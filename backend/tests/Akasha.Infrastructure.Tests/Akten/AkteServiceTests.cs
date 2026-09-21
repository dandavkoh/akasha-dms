using Akasha.Application.Akten;
using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Documents;
using Akasha.Application.Identity;
using Akasha.Application.Tenancy;
using Akasha.Domain.Akten;
using Akasha.Domain.Authorization;
using Akasha.Domain.Documents;
using Akasha.Domain.Identity;
using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Akten;
using Akasha.Infrastructure.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests.Akten;

[Collection(PostgresCollection.Name)]
public sealed class AkteServiceTests(PostgresFixture postgres)
{
    private sealed class Context : IDisposable
    {
        public Tenant Tenant { get; init; } = null!;
        public User User { get; init; } = null!;
        public Persistence.AkashaDbContext Db { get; init; } = null!;
        public AkteService Service { get; init; } = null!;

        public static async Task<Context> CreateAsync(
            PostgresFixture postgres,
            string slug,
            params PermissionAction[] typeLevelAkteGrants)
        {
            var tenant = new Tenant($"Tenant akte-{slug}", $"akte-{slug}");
            var user = new User(tenant.Id, $"User akte-{slug}");

            await using (var seeding = postgres.CreateContext())
            {
                seeding.Tenants.Add(tenant);
                await seeding.SaveChangesAsync();
            }

            await using (var seeding = postgres.CreateContext(tenant.Id))
            {
                seeding.Users.Add(user);
                foreach (var action in typeLevelAkteGrants)
                {
                    seeding.PermissionGrants.Add(new PermissionGrant(
                        tenant.Id, PrincipalType.User, user.Id, ResourceType.Akte, null, action));
                }

                await seeding.SaveChangesAsync();
            }

            var db = postgres.CreateContext(tenant.Id);
            var userContext = new CurrentUserContext();
            userContext.Establish(new CurrentUser(user.Id, tenant.Id, $"sub-{user.Id}", "internal", user.IsServiceAccount));
            var tenantContext = new TenantContext();
            tenantContext.Establish(tenant.Id);

            return new Context
            {
                Tenant = tenant,
                User = user,
                Db = db,
                Service = new AkteService(db, new AuthorizationService(db), userContext, tenantContext),
            };
        }

        /// <summary>Seeds a logical document directly; attachment needs an existing DocumentId.</summary>
        public async Task<Guid> SeedDocumentAsync(string externalId)
        {
            var document = new Document(Tenant.Id, new StorageReference(
                Tenant.Id, Guid.NewGuid(), externalId, "/somewhere", $"{externalId}.pdf"));
            Db.Documents.Add(document);
            await Db.SaveChangesAsync();
            return document.Id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static CreateAkteRequest Request(string name = "Customer ABC", string? description = "Contracts")
        => new(name, description);

    [Fact]
    public async Task Create_Generates_Stable_AkteId_And_Audits()
    {
        using var context = await Context.CreateAsync(postgres, "create", PermissionAction.Write, PermissionAction.Read);

        var created = await context.Service.CreateAsync(Request());

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Customer ABC", created.Name);
        Assert.Equal(AkteStatus.Active, created.Status);
        Assert.Empty(created.DocumentIds);
        Assert.True(created.RowVersion > 0);

        var audits = await context.Db.AuditEntries
            .Where(entry => entry.Action == "akte.created")
            .ToListAsync();
        Assert.Single(audits);
        Assert.Equal(context.Tenant.Id, audits[0].TenantId);
    }

    [Fact]
    public async Task Create_Without_Grant_Is_Denied()
    {
        using var context = await Context.CreateAsync(postgres, "noauthz");

        await Assert.ThrowsAsync<AccessDeniedException>(() => context.Service.CreateAsync(Request()));
    }

    [Fact]
    public async Task Get_And_List_Return_Own_Tenants_Akten_Only()
    {
        using var owner = await Context.CreateAsync(postgres, "owner", PermissionAction.Write, PermissionAction.Read);
        var akte = await owner.Service.CreateAsync(Request());

        using var outsider = await Context.CreateAsync(postgres, "outsider", PermissionAction.Read, PermissionAction.Admin);

        // even a tenant-wide admin of B cannot see A's Akte
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Service.GetAsync(akte.Id));

        var fetched = await owner.Service.GetAsync(akte.Id);
        Assert.Equal(akte.Id, fetched.Id);

        var list = await owner.Service.ListAsync(0, 10);
        Assert.Equal(1, list.Total);
        Assert.Equal(akte.Id, list.Items[0].Id);
    }

    [Fact]
    public async Task List_Requires_Type_Level_Read_And_Paginates()
    {
        using var context = await Context.CreateAsync(postgres, "list", PermissionAction.Write, PermissionAction.Read);
        for (var index = 0; index < 3; index++)
        {
            await context.Service.CreateAsync(Request($"Akte {index}"));
        }

        var page1 = await context.Service.ListAsync(0, 2);
        var page2 = await context.Service.ListAsync(2, 2);

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);

        using var noGrant = await Context.CreateAsync(postgres, "list-denied");
        await Assert.ThrowsAsync<AccessDeniedException>(() => noGrant.Service.ListAsync(0, 10));
        await Assert.ThrowsAsync<ValidationException>(() => context.Service.ListAsync(-1, 10));
        await Assert.ThrowsAsync<ValidationException>(() => context.Service.ListAsync(0, 101));
    }

    [Fact]
    public async Task Update_With_Stale_RowVersion_Conflicts_Without_Overwriting()
    {
        using var context = await Context.CreateAsync(postgres, "conc",
            PermissionAction.Write, PermissionAction.Read);
        var created = await context.Service.CreateAsync(Request());

        await context.Service.UpdateAsync(
            created.Id, new UpdateAkteRequest("one", null, created.RowVersion));

        await Assert.ThrowsAsync<ConflictException>(() => context.Service.UpdateAsync(
            created.Id, new UpdateAkteRequest("two", null, created.RowVersion)));

        // the failed update must not have reached the database
        await using var fresh = postgres.CreateContext(context.Tenant.Id);
        var persisted = await fresh.Akten.AsNoTracking().SingleAsync(akte => akte.Id == created.Id);
        Assert.Equal("one", persisted.Name);
    }

    [Fact]
    public async Task Archive_Closes_The_Akte_Reactivate_Opens_It_Again()
    {
        using var context = await Context.CreateAsync(postgres, "life",
            PermissionAction.Write, PermissionAction.Read);
        var created = await context.Service.CreateAsync(Request());

        var archived = await context.Service.ArchiveAsync(created.Id, created.RowVersion);
        Assert.Equal(AkteStatus.Archived, archived.Status);

        // an archived Akte refuses changes with a conflict (409), not a server error
        await Assert.ThrowsAsync<ConflictException>(() => context.Service.UpdateAsync(
            created.Id, new UpdateAkteRequest("x", null, archived.RowVersion)));

        var reactivated = await context.Service.ReactivateAsync(created.Id, archived.RowVersion);
        Assert.Equal(AkteStatus.Active, reactivated.Status);

        var updated = await context.Service.UpdateAsync(
            created.Id, new UpdateAkteRequest("ok again", null, reactivated.RowVersion));
        Assert.Equal("ok again", updated.Name);

        var actions = await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync();
        Assert.Contains("akte.archived", actions);
        Assert.Contains("akte.reactivated", actions);
        Assert.Contains("akte.updated", actions);
    }

    [Fact]
    public async Task Attach_And_Detach_Link_Documents_By_Logical_Id()
    {
        using var context = await Context.CreateAsync(postgres, "link",
            PermissionAction.Write, PermissionAction.Read);
        var akte = await context.Service.CreateAsync(Request());
        var documentA = await context.SeedDocumentAsync("a");
        var documentB = await context.SeedDocumentAsync("b");

        // attachment requires Read on the document as well
        await context.Db.PermissionGrants.AddAsync(new PermissionGrant(
            context.Tenant.Id, PrincipalType.User, context.User.Id,
            ResourceType.Document, null, PermissionAction.Read));
        await context.Db.SaveChangesAsync();

        await context.Service.AttachAsync(akte.Id, documentA);
        await context.Service.AttachAsync(akte.Id, documentB);

        var fetched = await context.Service.GetAsync(akte.Id);
        Assert.Equal([documentA, documentB], fetched.DocumentIds.OrderBy(id => id).ToList());

        await Assert.ThrowsAsync<ConflictException>(() => context.Service.AttachAsync(akte.Id, documentA));

        await context.Service.DetachAsync(akte.Id, documentA);
        var afterDetach = await context.Service.GetAsync(akte.Id);
        Assert.Equal([documentB], afterDetach.DocumentIds);

        await Assert.ThrowsAsync<NotFoundException>(() => context.Service.DetachAsync(akte.Id, documentA));

        var actions = await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync();
        Assert.Contains("akte.document.attached", actions);
        Assert.Contains("akte.document.detached", actions);
    }

    [Fact]
    public async Task Attach_Cross_Tenant_Document_Is_NotFound()
    {
        using var owner = await Context.CreateAsync(postgres, "xdoc-owner", PermissionAction.Write, PermissionAction.Read);
        var foreignDocumentId = await owner.SeedDocumentAsync("foreign");

        using var attacker = await Context.CreateAsync(postgres, "xdoc-attacker",
            PermissionAction.Write, PermissionAction.Read);
        var akte = await attacker.Service.CreateAsync(Request());
        await attacker.Db.PermissionGrants.AddAsync(new PermissionGrant(
            attacker.Tenant.Id, PrincipalType.User, attacker.User.Id,
            ResourceType.Document, null, PermissionAction.Read));
        await attacker.Db.SaveChangesAsync();

        // a document id of another tenant must not be linkable - it does not exist here
        await Assert.ThrowsAsync<NotFoundException>(() => attacker.Service.AttachAsync(akte.Id, foreignDocumentId));
        Assert.Empty((await attacker.Service.GetAsync(akte.Id)).DocumentIds);
    }

    [Fact]
    public async Task Document_Deletion_Removes_The_Membership_But_Keeps_The_Akte()
    {
        using var context = await Context.CreateAsync(postgres, "del", PermissionAction.Write, PermissionAction.Read);
        var akte = await context.Service.CreateAsync(Request());
        var documentId = await context.SeedDocumentAsync("gone");

        await context.Db.PermissionGrants.AddAsync(new PermissionGrant(
            context.Tenant.Id, PrincipalType.User, context.User.Id,
            ResourceType.Document, null, PermissionAction.Read));
        await context.Db.SaveChangesAsync();
        await context.Service.AttachAsync(akte.Id, documentId);

        // logical document deletion cascades the link only
        var document = await context.Db.Documents.SingleAsync(doc => doc.Id == documentId);
        context.Db.Documents.Remove(document);
        await context.Db.SaveChangesAsync();

        var fetched = await context.Service.GetAsync(akte.Id);
        Assert.Empty(fetched.DocumentIds);
        Assert.Equal(AkteStatus.Active, fetched.Status); // the Akte itself is unaffected
    }
}

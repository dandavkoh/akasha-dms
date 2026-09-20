using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Documents;
using Akasha.Application.Identity;
using Akasha.Application.Tenancy;
using Akasha.Domain.Audit;
using Akasha.Domain.Authorization;
using Akasha.Domain.Identity;
using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Authorization;
using Akasha.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests.Documents;

[Collection(PostgresCollection.Name)]
public sealed class DocumentServiceTests(PostgresFixture postgres)
{
    private sealed class Context : IDisposable
    {
        public Tenant Tenant { get; init; } = null!;
        public User User { get; init; } = null!;
        public Persistence.AkashaDbContext Db { get; init; } = null!;
        public DocumentService Service { get; init; } = null!;

        public static async Task<Context> CreateAsync(
            PostgresFixture postgres,
            string slug,
            params PermissionAction[] typeLevelGrants)
        {
            var tenant = new Tenant($"Tenant {slug}", slug);
            var user = new User(tenant.Id, $"User {slug}");

            await using (var seeding = postgres.CreateContext())
            {
                seeding.Tenants.Add(tenant);
                await seeding.SaveChangesAsync();
            }

            await using (var seeding = postgres.CreateContext(tenant.Id))
            {
                seeding.Users.Add(user);
                foreach (var action in typeLevelGrants)
                {
                    seeding.PermissionGrants.Add(new PermissionGrant(
                        tenant.Id, PrincipalType.User, user.Id, ResourceType.Document, null, action));
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
                Service = new DocumentService(db, new AuthorizationService(db), userContext, tenantContext),
            };
        }

        public void Dispose() => Db.Dispose();
    }

    private static CreateDocumentRequest Request(
        string externalId = "ext-1",
        string name = "contract.pdf",
        string path = "/akte-1")
        => new(Guid.NewGuid(), externalId, path, name, Version: "1", ETag: "etag-1",
            LastKnownModifiedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public async Task Create_Generates_Stable_DocumentId_And_Audits()
    {
        using var context = await Context.CreateAsync(postgres, "doc-create",
            PermissionAction.Upload, PermissionAction.Read);

        var created = await context.Service.CreateAsync(Request());

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.NotEqual(created.Storage.ConnectorId, created.Id);
        Assert.Equal("contract.pdf", created.Name);
        Assert.Equal(StorageSyncState.Pending, created.State);
        Assert.True(created.RowVersion > 0);

        var audits = await context.Db.AuditEntries
            .Where(entry => entry.Action == "document.created")
            .ToListAsync();
        Assert.Single(audits);
        Assert.Equal(context.Tenant.Id, audits[0].TenantId);
    }

    [Fact]
    public async Task Create_Without_Grant_Is_Denied()
    {
        using var context = await Context.CreateAsync(postgres, "doc-noauthz");

        await Assert.ThrowsAsync<AccessDeniedException>(() => context.Service.CreateAsync(Request()));
    }

    [Fact]
    public async Task Duplicate_External_Id_In_Same_Connector_Conflicts()
    {
        using var context = await Context.CreateAsync(postgres, "doc-dup",
            PermissionAction.Upload);
        var request = Request(externalId: "ext-dup");

        await context.Service.CreateAsync(request);
        var conflict = await Assert.ThrowsAsync<ConflictException>(() => context.Service.CreateAsync(request));

        Assert.Contains("external id", conflict.Message);
    }

    [Fact]
    public async Task Same_External_Id_In_Another_Tenant_Is_Allowed()
    {
        using var contextA = await Context.CreateAsync(postgres, "doc-xa", PermissionAction.Upload);
        using var contextB = await Context.CreateAsync(postgres, "doc-xb", PermissionAction.Upload);
        var connectorId = Guid.NewGuid();

        await contextA.Service.CreateAsync(Request() with { ConnectorId = connectorId });
        var b = await contextB.Service.CreateAsync(Request() with { ConnectorId = connectorId });

        Assert.NotEqual(Guid.Empty, b.Id);
    }

    [Fact]
    public async Task Other_Tenants_Document_Is_Invisible_NotFound()
    {
        using var owner = await Context.CreateAsync(postgres, "doc-owner",
            PermissionAction.Upload, PermissionAction.Read);
        var document = await owner.Service.CreateAsync(Request());

        using var outsider = await Context.CreateAsync(postgres, "doc-outsider",
            PermissionAction.Read, PermissionAction.Admin);

        // even a tenant-wide admin of B cannot see A's document - it does not exist for B
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Service.GetAsync(document.Id));
    }

    [Fact]
    public async Task Rename_Requires_Resource_Grant_And_RowVersion()
    {
        using var context = await Context.CreateAsync(postgres, "doc-rename",
            PermissionAction.Upload, PermissionAction.Read, PermissionAction.Rename);
        var created = await context.Service.CreateAsync(Request());

        var renamed = await context.Service.RenameAsync(
            created.Id, new RenameDocumentRequest("vertrag.pdf", created.RowVersion));

        Assert.Equal("vertrag.pdf", renamed.Name);
        Assert.Equal(created.Id, renamed.Id);
        Assert.NotEqual(created.RowVersion, renamed.RowVersion);
    }

    [Fact]
    public async Task Rename_With_Stale_RowVersion_Conflicts_Without_Overwriting()
    {
        using var context = await Context.CreateAsync(postgres, "doc-conc",
            PermissionAction.Upload, PermissionAction.Read, PermissionAction.Rename);
        var created = await context.Service.CreateAsync(Request());

        // first rename succeeds and bumps xmin
        await context.Service.RenameAsync(created.Id, new RenameDocumentRequest("one.pdf", created.RowVersion));

        // a caller holding the stale row version must conflict, not overwrite
        await Assert.ThrowsAsync<ConflictException>(() => context.Service.RenameAsync(
            created.Id, new RenameDocumentRequest("two.pdf", created.RowVersion)));

        // fresh session: the failed update must not have reached the database
        await using var fresh = postgres.CreateContext(context.Tenant.Id);
        var persisted = await fresh.Documents.AsNoTracking().SingleAsync(document => document.Id == created.Id);
        Assert.Equal("one.pdf", persisted.Name);
    }

    [Fact]
    public async Task Delete_Removes_Logical_Document_But_Keeps_Audit()
    {
        using var context = await Context.CreateAsync(postgres, "doc-del",
            PermissionAction.Upload, PermissionAction.Read, PermissionAction.Delete);
        var created = await context.Service.CreateAsync(Request());

        await context.Service.DeleteAsync(created.Id);

        await Assert.ThrowsAsync<NotFoundException>(() => context.Service.GetAsync(created.Id));
        Assert.False(await context.Db.Documents.AnyAsync(document => document.Id == created.Id));
        Assert.False(await context.Db.StorageReferences.AnyAsync(reference =>
            EF.Property<Guid>(reference, "DocumentId") == created.Id)); // 1:1 row gone with the document

        var audits = await context.Db.AuditEntries.ToListAsync();
        Assert.Contains(audits, entry => entry.Action == "document.deleted");
        Assert.Contains(audits, entry => entry.Action == "document.created");
    }

    [Fact]
    public async Task List_Requires_Type_Level_Read_And_Paginates()
    {
        using var context = await Context.CreateAsync(postgres, "doc-list",
            PermissionAction.Upload, PermissionAction.Read);
        for (var index = 0; index < 3; index++)
        {
            await context.Service.CreateAsync(Request(externalId: $"ext-{index}", name: $"doc-{index}.pdf"));
        }

        var page1 = await context.Service.ListAsync(0, 2);
        var page2 = await context.Service.ListAsync(2, 2);

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
        Assert.Equal([], page1.Items.Select(item => item.Id).Intersect(page2.Items.Select(item => item.Id)));

        using var noGrant = await Context.CreateAsync(postgres, "doc-list-denied");
        await Assert.ThrowsAsync<AccessDeniedException>(() => noGrant.Service.ListAsync(0, 10));
    }

    [Fact]
    public async Task Pagination_Bounds_Are_Enforced()
    {
        using var context = await Context.CreateAsync(postgres, "doc-page", PermissionAction.Read);

        await Assert.ThrowsAsync<ValidationException>(() => context.Service.ListAsync(-1, 50));
        await Assert.ThrowsAsync<ValidationException>(() => context.Service.ListAsync(0, 0));
        await Assert.ThrowsAsync<ValidationException>(() => context.Service.ListAsync(0, 101));
    }

    [Fact]
    public async Task Get_Without_Read_Grant_Is_Denied_Before_Data_Access()
    {
        using var owner = await Context.CreateAsync(postgres, "doc-get-owner", PermissionAction.Upload);
        var document = await owner.Service.CreateAsync(Request());

        using var reader = await Context.CreateAsync(postgres, "doc-get-denied");
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.Service.GetAsync(document.Id));

        // resource-level grant allows this one document only
        await using (var seeding = postgres.CreateContext(reader.Tenant.Id))
        {
            seeding.PermissionGrants.Add(new PermissionGrant(
                reader.Tenant.Id, PrincipalType.User, reader.User.Id,
                ResourceType.Document, document.Id, PermissionAction.Read));
            await seeding.SaveChangesAsync();
        }

        // the document belongs to the owner tenant - the grant cannot unlock it
        await Assert.ThrowsAsync<NotFoundException>(() => reader.Service.GetAsync(document.Id));
    }
}

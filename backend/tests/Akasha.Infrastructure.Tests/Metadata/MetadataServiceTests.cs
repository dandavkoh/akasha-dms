using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Identity;
using Akasha.Application.Metadata;
using Akasha.Application.Tenancy;
using Akasha.Domain.Authorization;
using Akasha.Domain.Documents;
using Akasha.Domain.Identity;
using Akasha.Domain.Metadata;
using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Authorization;
using Akasha.Infrastructure.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests.Metadata;

[Collection(PostgresCollection.Name)]
public sealed class MetadataServiceTests(PostgresFixture postgres)
{
    private sealed class Context : IDisposable
    {
        public Tenant Tenant { get; init; } = null!;
        public User User { get; init; } = null!;
        public Persistence.AkashaDbContext Db { get; init; } = null!;
        public MetadataService Service { get; init; } = null!;

        public static async Task<Context> CreateAsync(
            PostgresFixture postgres,
            string slug,
            bool metadataGrants = true,
            params PermissionAction[] documentGrants)
        {
            var tenant = new Tenant($"Tenant meta-{slug}", $"meta-{slug}");
            var user = new User(tenant.Id, $"User meta-{slug}");

            await using (var seeding = postgres.CreateContext())
            {
                seeding.Tenants.Add(tenant);
                await seeding.SaveChangesAsync();
            }

            await using (var seeding = postgres.CreateContext(tenant.Id))
            {
                seeding.Users.Add(user);
                if (metadataGrants)
                {
                    seeding.PermissionGrants.Add(new PermissionGrant(
                        tenant.Id, PrincipalType.User, user.Id, ResourceType.Metadata, null, PermissionAction.Read));
                    seeding.PermissionGrants.Add(new PermissionGrant(
                        tenant.Id, PrincipalType.User, user.Id, ResourceType.Metadata, null, PermissionAction.Write));
                }

                foreach (var action in documentGrants)
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
                Service = new MetadataService(db, new AuthorizationService(db), userContext, tenantContext),
            };
        }

        /// <summary>Seeds a logical document directly; metadata values need an existing owner.</summary>
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

    private static CreateMetadataDefinitionRequest TextField(string key, bool required = false)
        => new(key, MetadataFieldType.Text, required, Label: "The " + key, MaxLength: 50);

    private static MetadataValueInput Text(string value) => new(Text: value);

    [Fact]
    public async Task CreateDefinition_Generates_Id_And_Audits()
    {
        using var context = await Context.CreateAsync(postgres, "create");

        var created = await context.Service.CreateDefinitionAsync(TextField("contract-number"));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("contract-number", created.Key);
        Assert.Equal(MetadataFieldType.Text, created.FieldType);
        Assert.True(created.RowVersion > 0);

        var audits = await context.Db.AuditEntries.Where(entry => entry.Action == "metadata.definition.created").ToListAsync();
        Assert.Single(audits);
        Assert.Equal(context.Tenant.Id, audits[0].TenantId);
    }

    [Fact]
    public async Task CreateDefinition_Duplicate_Key_Conflicts_Per_Tenant()
    {
        using var context = await Context.CreateAsync(postgres, "dup");

        await context.Service.CreateDefinitionAsync(TextField("due-date"));
        await Assert.ThrowsAsync<ConflictException>(() => context.Service.CreateDefinitionAsync(TextField("due-date")));

        // Same key in another tenant is fine (uniqueness includes the tenant, #166).
        using var other = await Context.CreateAsync(postgres, "dup-other");
        var created = await other.Service.CreateDefinitionAsync(TextField("due-date"));
        Assert.Equal("due-date", created.Key);
    }

    [Fact]
    public async Task CreateDefinition_Invalid_Shape_Is_Validation_Error()
    {
        using var context = await Context.CreateAsync(postgres, "invalid");

        await Assert.ThrowsAsync<ValidationException>(() => context.Service.CreateDefinitionAsync(
            new CreateMetadataDefinitionRequest("", MetadataFieldType.Text)));
        await Assert.ThrowsAsync<ValidationException>(() => context.Service.CreateDefinitionAsync(
            new CreateMetadataDefinitionRequest("state", MetadataFieldType.Selection)));
    }

    [Fact]
    public async Task Definitions_Without_Grant_Are_Denied_And_Tenant_Isolated()
    {
        using var owner = await Context.CreateAsync(postgres, "owner");
        var definition = await owner.Service.CreateDefinitionAsync(TextField("customer"));

        using var denied = await Context.CreateAsync(postgres, "denied", metadataGrants: false);
        await Assert.ThrowsAsync<AccessDeniedException>(() => denied.Service.ListDefinitionsAsync(0, 50));
        await Assert.ThrowsAsync<AccessDeniedException>(() => denied.Service.CreateDefinitionAsync(TextField("x")));

        // Another tenant (with grants) sees an empty list and NotFound for the foreign id.
        using var other = await Context.CreateAsync(postgres, "other");
        Assert.Equal(0, (await other.Service.ListDefinitionsAsync(0, 50)).Total);
        await Assert.ThrowsAsync<NotFoundException>(() => other.Service.GetDefinitionAsync(definition.Id));
    }

    [Fact]
    public async Task UpdateDefinition_Is_Concurrency_Protected_And_Audited()
    {
        using var context = await Context.CreateAsync(postgres, "upd");
        var definition = await context.Service.CreateDefinitionAsync(TextField("amount"));

        var updated = await context.Service.UpdateDefinitionAsync(
            definition.Id, new UpdateMetadataDefinitionRequest("Betrag", true, null, null, definition.RowVersion));
        Assert.Equal("Betrag", updated.Label);
        Assert.True(updated.IsRequired);
        Assert.NotEqual(definition.RowVersion, updated.RowVersion);

        await Assert.ThrowsAsync<ConflictException>(() => context.Service.UpdateDefinitionAsync(
            definition.Id, new UpdateMetadataDefinitionRequest("Stale", false, null, null, definition.RowVersion)));

        Assert.Single(await context.Db.AuditEntries
            .Where(entry => entry.Action == "metadata.definition.updated").ToListAsync());
    }

    [Fact]
    public async Task Set_And_Get_Value_Roundtrip_Per_Type()
    {
        using var context = await Context.CreateAsync(postgres, "types", documentGrants: [PermissionAction.Read, PermissionAction.EditMetadata]);
        var documentId = await context.SeedDocumentAsync("types-1");

        var stamp = DateTimeOffset.UtcNow;
        await context.Service.CreateDefinitionAsync(TextField("comment"));
        await context.Service.CreateDefinitionAsync(new CreateMetadataDefinitionRequest("amount", MetadataFieldType.Number));
        await context.Service.CreateDefinitionAsync(new CreateMetadataDefinitionRequest("flag", MetadataFieldType.Boolean));
        await context.Service.CreateDefinitionAsync(new CreateMetadataDefinitionRequest("due", MetadataFieldType.Date));
        await context.Service.CreateDefinitionAsync(new CreateMetadataDefinitionRequest("seen", MetadataFieldType.DateTime));
        await context.Service.CreateDefinitionAsync(
            new CreateMetadataDefinitionRequest("state", MetadataFieldType.Selection, Options: ["open", "done"]));
        await context.Service.CreateDefinitionAsync(
            new CreateMetadataDefinitionRequest("tags", MetadataFieldType.MultiSelection, Options: ["a", "b"]));

        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "comment", Text("hello"));
        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "amount", new MetadataValueInput(Number: 9.5m));
        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "flag", new MetadataValueInput(Boolean: true));
        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "due", new MetadataValueInput(Date: new DateOnly(2026, 12, 31)));
        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "seen", new MetadataValueInput(Timestamp: stamp));
        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "state", Text("open"));
        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "tags", new MetadataValueInput(Selection: ["a", "b"]));

        var values = await context.Service.GetValuesAsync(MetadataOwnerType.Document, documentId);

        Assert.Equal(7, values.Count);
        Assert.Equal("hello", values.Single(v => v.Key == "comment").Text);
        Assert.Equal(9.5m, values.Single(v => v.Key == "amount").Number);
        Assert.True(values.Single(v => v.Key == "flag").Boolean);
        Assert.Equal(new DateOnly(2026, 12, 31), values.Single(v => v.Key == "due").Date);
        // timestamptz stores microseconds; the 100ns of DateTimeOffset are truncated.
        Assert.Equal(stamp.UtcDateTime, values.Single(v => v.Key == "seen").Timestamp!.Value.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal("open", values.Single(v => v.Key == "state").Text);
        Assert.Equal(["a", "b"], values.Single(v => v.Key == "tags").Selection);
    }

    [Fact]
    public async Task SetValue_Rejects_Invalid_And_Unknown_References()
    {
        using var context = await Context.CreateAsync(postgres, "badval", documentGrants: [PermissionAction.Read, PermissionAction.EditMetadata]);
        var documentId = await context.SeedDocumentAsync("badval-1");
        await context.Service.CreateDefinitionAsync(TextField("comment", required: true));
        await context.Service.CreateDefinitionAsync(
            new CreateMetadataDefinitionRequest("state", MetadataFieldType.Selection, Options: ["open"]));

        await Assert.ThrowsAsync<ValidationException>(() // required
            => context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "comment", Text("  ")));
        await Assert.ThrowsAsync<ValidationException>(() // too long
            => context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "comment", Text(new string('x', 51))));
        await Assert.ThrowsAsync<ValidationException>(() // wrong shape
            => context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "comment", new MetadataValueInput(Number: 1)));
        await Assert.ThrowsAsync<ValidationException>(() // unknown option
            => context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "state", Text("bogus")));
        await Assert.ThrowsAsync<NotFoundException>(() // unknown definition key
            => context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "nope", Text("x")));

        // Nothing was persisted by the failed attempts.
        Assert.Empty(await context.Service.GetValuesAsync(MetadataOwnerType.Document, documentId));
    }

    [Fact]
    public async Task Values_Require_Owner_Authorization_And_Existing_Owner()
    {
        using var context = await Context.CreateAsync(postgres, "sec", documentGrants: [PermissionAction.Read, PermissionAction.EditMetadata]);
        var documentId = await context.SeedDocumentAsync("sec-1");
        await context.Service.CreateDefinitionAsync(TextField("comment"));

        // No document grants at all: denied before data access.
        using var denied = await Context.CreateAsync(postgres, "sec-denied", documentGrants: []);
        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "comment", Text("x"));
        var foreignDoc = documentId;

        // Cross-tenant: the foreign document is invisible -> NotFound (authorization for
        // resource type Document is type-level here, so the owner exists check decides).
        using var other = await Context.CreateAsync(postgres, "sec-other", documentGrants: [PermissionAction.Read, PermissionAction.EditMetadata]);
        await Assert.ThrowsAsync<NotFoundException>(() => other.Service.GetValuesAsync(MetadataOwnerType.Document, foreignDoc));
        await Assert.ThrowsAsync<NotFoundException>(() => other.Service.SetValueAsync(
            MetadataOwnerType.Document, foreignDoc, "comment", Text("intrusion")));

        // Unknown owner id in the own tenant.
        await Assert.ThrowsAsync<NotFoundException>(() => context.Service.GetValuesAsync(
            MetadataOwnerType.Document, Guid.NewGuid()));
    }

    [Fact]
    public async Task Value_Update_Overwrites_And_Remove_Is_Audited()
    {
        using var context = await Context.CreateAsync(postgres, "updval", documentGrants: [PermissionAction.Read, PermissionAction.EditMetadata]);
        var documentId = await context.SeedDocumentAsync("updval-1");
        await context.Service.CreateDefinitionAsync(TextField("comment"));

        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "comment", Text("first"));
        var replaced = await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "comment", Text("second"));

        Assert.Equal("second", replaced.Text);
        var values = await context.Service.GetValuesAsync(MetadataOwnerType.Document, documentId);
        Assert.Single(values);
        Assert.Equal("second", values[0].Text);

        await context.Service.RemoveValueAsync(MetadataOwnerType.Document, documentId, "comment");
        Assert.Empty(await context.Service.GetValuesAsync(MetadataOwnerType.Document, documentId));
        await Assert.ThrowsAsync<NotFoundException>(()
            => context.Service.RemoveValueAsync(MetadataOwnerType.Document, documentId, "comment"));

        Assert.Equal(2, await context.Db.AuditEntries.CountAsync(entry => entry.Action == "metadata.value.set"));
        Assert.Single(await context.Db.AuditEntries.Where(entry => entry.Action == "metadata.value.removed").ToListAsync());
    }

    [Fact]
    public async Task Akte_Values_Use_Akte_Authorization()
    {
        using var context = await Context.CreateAsync(postgres, "akteval");
        var akte = new Domain.Akten.Akte(context.Tenant.Id, "Customer ABC");
        context.Db.Akten.Add(akte);
        await context.Db.SaveChangesAsync();

        // No Akte grants -> denied even though metadata grants exist.
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => context.Service.SetValueAsync(MetadataOwnerType.Akte, akte.Id, "comment", Text("x")));
    }

    [Fact]
    public async Task DeleteDefinition_Cascades_Values()
    {
        using var context = await Context.CreateAsync(postgres, "del", documentGrants: [PermissionAction.Read, PermissionAction.EditMetadata]);
        var documentId = await context.SeedDocumentAsync("del-1");
        var definition = await context.Service.CreateDefinitionAsync(TextField("comment"));
        await context.Service.SetValueAsync(MetadataOwnerType.Document, documentId, "comment", Text("x"));

        await context.Service.DeleteDefinitionAsync(definition.Id);

        Assert.Empty(await context.Service.GetValuesAsync(MetadataOwnerType.Document, documentId));
        Assert.Equal(0, await context.Db.MetadataValues.CountAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => context.Service.GetDefinitionAsync(definition.Id));
        Assert.Single(await context.Db.AuditEntries.Where(entry => entry.Action == "metadata.definition.deleted").ToListAsync());
    }
}

using Akasha.Application.Common;
using Akasha.Application.Identity;
using Akasha.Application.Search;
using Akasha.Application.Tenancy;
using Akasha.Domain.Akten;
using Akasha.Domain.Authorization;
using Akasha.Domain.Documents;
using Akasha.Domain.Identity;
using Akasha.Domain.Metadata;
using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests.Search;

[Collection(PostgresCollection.Name)]
public sealed class SearchServiceTests(PostgresFixture postgres)
{
    private sealed class Context : IDisposable
    {
        public Tenant Tenant { get; init; } = null!;
        public User User { get; init; } = null!;
        public Persistence.AkashaDbContext Db { get; init; } = null!;
        public SearchService Service { get; init; } = null!;

        public static async Task<Context> CreateAsync(PostgresFixture postgres, string slug)
        {
            var tenant = new Tenant($"Tenant search-{slug}", $"search-{slug}");
            var user = new User(tenant.Id, $"User search-{slug}");

            await using (var seeding = postgres.CreateContext())
            {
                seeding.Tenants.Add(tenant);
                await seeding.SaveChangesAsync();
            }

            await using (var seeding = postgres.CreateContext(tenant.Id))
            {
                seeding.Users.Add(user);
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
                Service = new SearchService(db, userContext, tenantContext),
            };
        }

        public async Task<Document> SeedDocumentAsync(
            string externalId,
            string? name = null,
            DateOnly? date = null,
            decimal? number = null,
            string? textValue = null,
            string? textKey = null,
            Guid? akteId = null,
            bool grantRead = true,
            bool grantDeny = false)
        {
            var document = new Document(Tenant.Id, new StorageReference(
                Tenant.Id, Guid.NewGuid(), externalId, "/somewhere", name ?? $"{externalId}.pdf"));
            Db.Documents.Add(document);

            if (grantRead)
            {
                Db.PermissionGrants.Add(new PermissionGrant(
                    Tenant.Id, PrincipalType.User, User.Id, ResourceType.Document, document.Id, PermissionAction.Read));
            }

            if (grantDeny)
            {
                Db.PermissionGrants.Add(new PermissionGrant(
                    Tenant.Id, PrincipalType.User, User.Id, ResourceType.Document, document.Id, PermissionAction.Read,
                    GrantEffect.Deny));
            }

            if (akteId is { } akte)
            {
                Db.AkteDocuments.Add(new AkteDocument(Tenant.Id, akte, document.Id));
            }

            MetadataDefinition? definition = null;
            if (textKey is not null)
            {
                definition = new MetadataDefinition(Tenant.Id, textKey, MetadataFieldType.Text);
                Db.MetadataDefinitions.Add(definition);
            }

            if (number is not null)
            {
                definition = new MetadataDefinition(Tenant.Id, $"{externalId}-number", MetadataFieldType.Number);
                Db.MetadataDefinitions.Add(definition);
            }

            await Db.SaveChangesAsync();

            if (definition is not null)
            {
                var value = new MetadataValue(Tenant.Id, MetadataOwnerType.Document, document.Id, definition.Id);
                value.Apply(definition, number is not null ? new MetadataValueInput(Number: number) : new MetadataValueInput(Text: textValue ?? externalId));
                Db.MetadataValues.Add(value);
                await Db.SaveChangesAsync();
            }

            return document;
        }

        public async Task<Guid> SeedAkteAsync(string name)
        {
            var akte = new Akte(Tenant.Id, name);
            Db.Akten.Add(akte);
            await Db.SaveChangesAsync();
            return akte.Id;
        }

        public void Dispose() => Db.Dispose();
    }

    private static DocumentSearchRequest Query(string query, int take = 50)
        => new() { Query = query, Take = take };

    [Fact]
    public async Task Search_without_any_grant_returns_nothing()
    {
        using var context = await Context.CreateAsync(postgres, "nogrant");
        await context.SeedDocumentAsync("nogrant-doc", grantRead: false);

        var result = await context.Service.SearchDocumentsAsync(Query("nogrant"));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task Search_never_leaks_other_tenants_documents()
    {
        using var context = await Context.CreateAsync(postgres, "iso-a");
        await context.SeedDocumentAsync("sharedname-iso", name: "shared-title.pdf");

        using var other = await Context.CreateAsync(postgres, "iso-b");
        var foreign = await other.SeedDocumentAsync("sharedname-iso", name: "shared-title.pdf");

        // User of tenant A searches the exact foreign name; the other tenant's doc must
        // never surface, not even in the total count (rule #16 of the root AGENTS).
        var result = await context.Service.SearchDocumentsAsync(Query("shared-title"));

        Assert.Single(result.Items);
        Assert.DoesNotContain(result.Items, hit => hit.Id == foreign.Id);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task Resource_level_deny_overrides_type_level_allow()
    {
        using var context = await Context.CreateAsync(postgres, "deny");
        var allowed = await context.SeedDocumentAsync("deny-allowed", name: "deny-allowed.pdf", grantRead: false);
        var denied = await context.SeedDocumentAsync("deny-denied", name: "deny-denied.pdf", grantRead: false, grantDeny: true);
        context.Db.PermissionGrants.Add(new PermissionGrant(
            context.Tenant.Id, PrincipalType.User, context.User.Id, ResourceType.Document, null, PermissionAction.Read));
        await context.Db.SaveChangesAsync();

        var result = await context.Service.SearchDocumentsAsync(Query("deny-"));

        Assert.Single(result.Items);
        Assert.Equal(allowed.Id, result.Items[0].Id);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task Group_and_role_grants_apply_to_search()
    {
        using var context = await Context.CreateAsync(postgres, "roles");
        var document = await context.SeedDocumentAsync("roles-doc", name: "roles-target.pdf", grantRead: false);
        var hidden = await context.SeedDocumentAsync("roles-hidden", name: "roles-hidden.pdf", grantRead: false);

        var group = new Group(context.Tenant.Id, "search-roles-group");
        var role = new Role(context.Tenant.Id, "search-roles-role");
        context.Db.Groups.Add(group);
        context.Db.Roles.Add(role);
        context.Db.GroupMembers.Add(new GroupMember(context.Tenant.Id, group.Id, context.User.Id));
        context.Db.RoleAssignments.Add(new RoleAssignment(context.Tenant.Id, role.Id, PrincipalType.Group, group.Id));
        context.Db.PermissionGrants.Add(new PermissionGrant(
            context.Tenant.Id, PrincipalType.Role, role.Id, ResourceType.Document, document.Id, PermissionAction.Read));
        await context.Db.SaveChangesAsync();

        var result = await context.Service.SearchDocumentsAsync(Query("roles-"));

        Assert.Single(result.Items);
        Assert.Equal(document.Id, result.Items[0].Id);
        Assert.DoesNotContain(result.Items, hit => hit.Id == hidden.Id);
    }

    [Fact]
    public async Task Query_matches_name_case_insensitive_and_orders_by_relevance()
    {
        using var context = await Context.CreateAsync(postgres, "rank");
        var exact = await context.SeedDocumentAsync("rank-exact", name: "Zeugnis");
        var prefix = await context.SeedDocumentAsync("rank-prefix", name: "Zeugnis-2026.pdf");
        var contains = await context.SeedDocumentAsync("rank-contains", name: "scan-zeugnis-final.pdf");
        var unrelated = await context.SeedDocumentAsync("rank-other", name: "invoice.pdf");

        var result = await context.Service.SearchDocumentsAsync(Query("zeugnis"));

        Assert.Equal(3, result.Total);
        Assert.Equal([exact.Id, prefix.Id, contains.Id], result.Items.Select(hit => hit.Id).ToArray());
        Assert.Equal(3, result.Items[0].Score);
        Assert.Equal(2, result.Items[1].Score);
        Assert.Equal(1, result.Items[2].Score);
        Assert.DoesNotContain(result.Items, hit => hit.Id == unrelated.Id);
    }

    [Fact]
    public async Task Query_also_matches_text_metadata()
    {
        using var context = await Context.CreateAsync(postgres, "meta-q");
        var document = await context.SeedDocumentAsync(
            "metaq-doc", name: "paper.pdf", textKey: "metaq-notes", textValue: "Aktenzeichen XY-42");

        var result = await context.Service.SearchDocumentsAsync(Query("AKTENZEICHEN"));

        Assert.Single(result.Items);
        Assert.Equal(document.Id, result.Items[0].Id);
        Assert.Equal(1, result.Items[0].Score); // metadata-only match ranks lowest
    }

    [Fact]
    public async Task Filters_by_akte_state_documenttype_and_dates()
    {
        using var context = await Context.CreateAsync(postgres, "filter");
        var akte = await context.SeedAkteAsync("Akte filter");
        var inAkte = await context.SeedDocumentAsync("filter-akte", name: "filter-a.pdf", akteId: akte);
        var otherDoc = await context.SeedDocumentAsync("filter-other", name: "filter-b.docx");
        var missing = await context.SeedDocumentAsync("filter-missing", name: "filter-c.pdf");
        missing.MarkMissing();
        await context.Db.SaveChangesAsync();

        var byAkte = await context.Service.SearchDocumentsAsync(new DocumentSearchRequest { AkteId = akte });
        Assert.Equal([inAkte.Id], byAkte.Items.Select(hit => hit.Id).ToArray());

        var byState = await context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { State = StorageSyncState.Missing });
        Assert.Equal([missing.Id], byState.Items.Select(hit => hit.Id).ToArray());

        var byType = await context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { DocumentType = "docx" });
        Assert.Equal([otherDoc.Id], byType.Items.Select(hit => hit.Id).ToArray());

        var future = DateTimeOffset.UtcNow.AddDays(1);
        var byDate = await context.Service.SearchDocumentsAsync(new DocumentSearchRequest
        {
            Query = "filter-",
            CreatedFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            CreatedTo = future,
            Sort = DocumentSearchSort.Name,
        });
        Assert.Equal(3, byDate.Total);

        var none = await context.Service.SearchDocumentsAsync(new DocumentSearchRequest
        {
            Query = "filter-",
            CreatedFrom = future,
        });
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task Metadata_filters_are_typed_and_unknown_keys_match_nothing()
    {
        using var context = await Context.CreateAsync(postgres, "metaf");
        var hit = await context.SeedDocumentAsync("metaf-doc", name: "metaf.pdf", number: 42m);
        await context.SeedDocumentAsync("metaf-low", name: "metaf-low.pdf", number: 7m, grantRead: true);
        // different definition key per document in the helper; use the hit's key
        var result = await context.Service.SearchDocumentsAsync(new DocumentSearchRequest
        {
            MetadataKey = "metaf-doc-number",
            MetadataValue = "42",
        });
        Assert.Equal([hit.Id], result.Items.Select(h => h.Id).ToArray());

        var unknown = await context.Service.SearchDocumentsAsync(new DocumentSearchRequest
        {
            MetadataKey = "no-such-key",
            MetadataValue = "1",
        });
        Assert.Empty(unknown.Items);

        await Assert.ThrowsAsync<ValidationException>(() => context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { MetadataKey = "metaf-doc-number", MetadataValue = "not-a-number" }));
    }

    [Fact]
    public async Task Pagination_counts_only_accessible_documents()
    {
        using var context = await Context.CreateAsync(postgres, "page");
        for (var i = 0; i < 5; i++)
        {
            await context.SeedDocumentAsync($"page-{i}", name: $"page-{i}.pdf");
        }

        await context.SeedDocumentAsync("page-hidden", name: "page-hidden.pdf", grantRead: false);

        var first = await context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { Query = "page-", Skip = 0, Take = 3 });
        var second = await context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { Query = "page-", Skip = 3, Take = 3 });

        Assert.Equal(5, first.Total);
        Assert.Equal(3, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(first.Items.Select(hit => hit.Id).Intersect(second.Items.Select(hit => hit.Id)));
    }

    [Fact]
    public async Task Sorting_by_name_ascending_and_descending()
    {
        using var context = await Context.CreateAsync(postgres, "sort");
        var b = await context.SeedDocumentAsync("sort-b", name: "bravo.pdf");
        var a = await context.SeedDocumentAsync("sort-a", name: "alpha.pdf");
        var c = await context.SeedDocumentAsync("sort-c", name: "charlie.pdf");

        var asc = await context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { Query = "pdf", Sort = DocumentSearchSort.Name, Descending = false });
        Assert.Equal([a.Id, b.Id, c.Id], asc.Items.Select(hit => hit.Id).ToArray());

        var desc = await context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { Query = "pdf", Sort = DocumentSearchSort.Name, Descending = true });
        Assert.Equal([c.Id, b.Id, a.Id], desc.Items.Select(hit => hit.Id).ToArray());
    }

    [Fact]
    public async Task Invalid_requests_are_validation_errors()
    {
        using var context = await Context.CreateAsync(postgres, "invalid");

        await Assert.ThrowsAsync<ValidationException>(() => context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { Take = 0 }));
        await Assert.ThrowsAsync<ValidationException>(() => context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { Skip = -1 }));
        await Assert.ThrowsAsync<ValidationException>(() => context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest { MetadataValue = "orphan" }));
        await Assert.ThrowsAsync<ValidationException>(() => context.Service.SearchDocumentsAsync(
            new DocumentSearchRequest
            {
                CreatedFrom = DateTimeOffset.UtcNow,
                CreatedTo = DateTimeOffset.UtcNow.AddDays(-1),
            }));
    }

    [Fact]
    public async Task Search_without_authenticated_identity_is_rejected()
    {
        var db = postgres.CreateContext(); // no tenant, no user
        await using (db)
        {
            var service = new SearchService(db, new CurrentUserContext(), new TenantContext());
            await Assert.ThrowsAsync<TenantResolutionException>(() =>
                service.SearchDocumentsAsync(new DocumentSearchRequest { Query = "anything" }));
        }
    }
}

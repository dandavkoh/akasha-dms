using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Akasha.Application.Tenancy;
using Akasha.Domain.Authorization;
using Akasha.Domain.Identity;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Api.IntegrationTests;

public sealed class AktenApiTests(ApiFixture factory) : IClassFixture<ApiFixture>
{
    private const string Base = "/api/v1/akten";

    private sealed class Subject
    {
        public required string Id { get; init; }
        public required Tenant Tenant { get; init; }
        public required User User { get; init; }
    }

    private AkashaDbContext CreateContext(Guid? tenantId = null)
    {
        var tenantContext = new TenantContext();
        if (tenantId is { } tenant)
        {
            tenantContext.Establish(tenant);
        }

        return new AkashaDbContext(
            new DbContextOptionsBuilder<AkashaDbContext>()
                .UseAkashaNpgsql(factory.ConnectionString, 30)
                .Options,
            tenantContext);
    }

    private async Task<Subject> SeedUserAsync(string slug, params PermissionAction[] typeLevelAkteGrants)
    {
        _ = factory.CreateClient(); // ensure host + startup migrations

        var subject = $"sub-akten-{slug}";
        var tenant = new Tenant($"Tenant akten-{slug}", $"akten-{slug}");
        var user = new User(tenant.Id, $"User akten-{slug}");

        await using (var context = CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(tenant.Id))
        {
            context.Users.Add(user);
            context.UserIdentities.Add(new UserIdentity(tenant.Id, user.Id, "internal", subject));
            foreach (var action in typeLevelAkteGrants)
            {
                context.PermissionGrants.Add(new PermissionGrant(
                    tenant.Id, PrincipalType.User, user.Id, ResourceType.Akte, null, action));
            }

            await context.SaveChangesAsync();
        }

        return new Subject { Id = subject, Tenant = tenant, User = user };
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? subject)
    {
        var request = new HttpRequestMessage(method, url);
        if (subject is not null)
        {
            request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
        }

        return request;
    }

    private static JsonElement Body(HttpResponseMessage response)
        => JsonDocument.Parse(response.Content.ReadAsStringAsync().Result).RootElement;

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().SendAsync(Request(HttpMethod.Get, Base, null))).StatusCode);
    }

    [Fact]
    public async Task Requests_without_grants_are_forbidden()
    {
        var user = await SeedUserAsync("forbidden");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await factory.CreateClient().SendAsync(Request(HttpMethod.Get, Base, user.Id))).StatusCode);
    }

    [Fact]
    public async Task Full_crud_and_lifecycle_roundtrip()
    {
        var user = await SeedUserAsync("crud",
            PermissionAction.Write, PermissionAction.Read);
        var client = factory.CreateClient();

        // create
        var create = Request(HttpMethod.Post, Base, user.Id);
        create.Content = JsonContent.Create(new { name = "Customer ABC", description = "Contracts" });
        var created = Body(await client.SendAsync(create));
        var id = created.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("Customer ABC", created.GetProperty("name").GetString());
        Assert.Equal("Active", created.GetProperty("status").GetString());
        var rowVersion = created.GetProperty("rowVersion").GetUInt32();
        Assert.True(rowVersion > 0);

        // read
        var fetched = Body(await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{id}", user.Id)));
        Assert.Equal(id, fetched.GetProperty("id").GetGuid());
        Assert.Equal(0, fetched.GetProperty("documentIds").GetArrayLength());

        // list
        var list = Body(await client.SendAsync(Request(HttpMethod.Get, Base, user.Id)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());

        // update with stale row version conflicts
        var stale = Request(HttpMethod.Patch, $"{Base}/{id}", user.Id);
        stale.Content = JsonContent.Create(new { name = "x", description = (string?)null, rowVersion = rowVersion + 1 });
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(stale)).StatusCode);

        // update
        var update = Request(HttpMethod.Patch, $"{Base}/{id}", user.Id);
        update.Content = JsonContent.Create(new { name = "Customer ABC v2", description = (string?)null, rowVersion });
        var updated = Body(await client.SendAsync(update));
        Assert.Equal("Customer ABC v2", updated.GetProperty("name").GetString());
        var version2 = updated.GetProperty("rowVersion").GetUInt32();

        // archive, then update is a 409
        var archive = Request(HttpMethod.Post, $"{Base}/{id}/archive", user.Id);
        archive.Content = JsonContent.Create(new { rowVersion = version2 });
        var archived = Body(await client.SendAsync(archive));
        Assert.Equal("Archived", archived.GetProperty("status").GetString());

        var whileArchived = Request(HttpMethod.Patch, $"{Base}/{id}", user.Id);
        whileArchived.Content = JsonContent.Create(new { name = "nope", description = (string?)null, rowVersion = archived.GetProperty("rowVersion").GetUInt32() });
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(whileArchived)).StatusCode);

        // reactivate
        var reactivate = Request(HttpMethod.Post, $"{Base}/{id}/reactivate", user.Id);
        reactivate.Content = JsonContent.Create(new { rowVersion = archived.GetProperty("rowVersion").GetUInt32() });
        var reactivated = Body(await client.SendAsync(reactivate));
        Assert.Equal("Active", reactivated.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Attach_and_detach_document_by_logical_id()
    {
        var user = await SeedUserAsync("link",
            PermissionAction.Write, PermissionAction.Read);
        var client = factory.CreateClient();

        // seed a logical document directly (its storage reference is irrelevant here:
        // the Akte references the logical DocumentId only)
        var documentId = Guid.NewGuid();
        await using (var context = CreateContext(user.Tenant.Id))
        {
            var document = new Akasha.Domain.Documents.Document(user.Tenant.Id, new Akasha.Domain.Storage.StorageReference(
                user.Tenant.Id, Guid.NewGuid(), "ext-link", "/some/where", "file.pdf"));
            context.Documents.Add(document);
            context.PermissionGrants.Add(new PermissionGrant(
                user.Tenant.Id, PrincipalType.User, user.User.Id,
                ResourceType.Document, null, PermissionAction.Read));
            await context.SaveChangesAsync();
            documentId = document.Id;
        }

        var create = Request(HttpMethod.Post, Base, user.Id);
        create.Content = JsonContent.Create(new { name = "Akte" });
        var id = Body(await client.SendAsync(create)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(Request(HttpMethod.Put, $"{Base}/{id}/documents/{documentId}", user.Id))).StatusCode);
        // duplicate link conflicts
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(Request(HttpMethod.Put, $"{Base}/{id}/documents/{documentId}", user.Id))).StatusCode);

        var fetched = Body(await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{id}", user.Id)));
        Assert.Equal(documentId, fetched.GetProperty("documentIds")[0].GetGuid());

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(Request(HttpMethod.Delete, $"{Base}/{id}/documents/{documentId}", user.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Request(HttpMethod.Delete, $"{Base}/{id}/documents/{documentId}", user.Id))).StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_akte_and_document_are_not_found()
    {
        var owner = await SeedUserAsync("x-owner", PermissionAction.Write, PermissionAction.Read);
        var outsider = await SeedUserAsync("x-outsider", PermissionAction.Write, PermissionAction.Read);
        var client = factory.CreateClient();

        var create = Request(HttpMethod.Post, Base, owner.Id);
        create.Content = JsonContent.Create(new { name = "Owner Akte" });
        var akteId = Body(await client.SendAsync(create)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{akteId}", outsider.Id))).StatusCode);
        // outsider has type-level grants, so authorization passes - but the tenant query
        // filter makes the other tenant's Akte invisible (404, not a data leak)
        var outsiderPatch = Request(HttpMethod.Patch, $"{Base}/{akteId}", outsider.Id);
        outsiderPatch.Content = JsonContent.Create(new { name = "x", description = (string?)null, rowVersion = 1u });
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(outsiderPatch)).StatusCode);

        // outsider's own Akte + owner's document id must not be linkable
        var outsiderAkteRequest = Request(HttpMethod.Post, Base, outsider.Id);
        outsiderAkteRequest.Content = JsonContent.Create(new { name = "Outsider Akte" });
        var outsiderAkte = Body(await client.SendAsync(outsiderAkteRequest)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request(HttpMethod.Put, $"{Base}/{outsiderAkte}/documents/{Guid.NewGuid()}", outsider.Id))).StatusCode);
    }

    [Fact]
    public async Task Validation_errors_are_bad_request()
    {
        var user = await SeedUserAsync("bad", PermissionAction.Write, PermissionAction.Read);
        var client = factory.CreateClient();

        var create = Request(HttpMethod.Post, Base, user.Id);
        create.Content = JsonContent.Create(new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(create)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(Request(HttpMethod.Get, $"{Base}?take=0", user.Id))).StatusCode);
    }
}

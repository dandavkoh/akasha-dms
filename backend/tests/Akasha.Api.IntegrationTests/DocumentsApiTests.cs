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

public sealed class DocumentsApiTests(ApiFixture factory) : IClassFixture<ApiFixture>
{
    private const string Base = "/api/v1/documents";

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

    private async Task<Subject> SeedUserAsync(string slug, params PermissionAction[] typeLevelDocumentGrants)
    {
        // Ensure the host is up so startup migrations ran before we touch the database.
        _ = factory.CreateClient();

        var subject = $"sub-{slug}";
        var tenant = new Tenant($"Tenant {slug}", slug);
        var user = new User(tenant.Id, $"User {slug}");

        await using (var context = CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(tenant.Id))
        {
            context.Users.Add(user);
            context.UserIdentities.Add(new UserIdentity(tenant.Id, user.Id, "internal", subject));
            foreach (var action in typeLevelDocumentGrants)
            {
                context.PermissionGrants.Add(new PermissionGrant(
                    tenant.Id, PrincipalType.User, user.Id, ResourceType.Document, null, action));
            }

            await context.SaveChangesAsync();
        }

        return new Subject { Id = subject, Tenant = tenant, User = user };
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string? subject)
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
        var user = await SeedUserAsync("docs-forbidden");
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request(HttpMethod.Get, Base, user.Id))).StatusCode);
    }

    [Fact]
    public async Task Full_crud_roundtrip()
    {
        var user = await SeedUserAsync("docs-crud",
            PermissionAction.Upload, PermissionAction.Read, PermissionAction.Rename, PermissionAction.Delete);
        var client = factory.CreateClient();

        // create
        var createRequest = Request(HttpMethod.Post, Base, user.Id);
        var connectorId = Guid.NewGuid();
        createRequest.Content = JsonContent.Create(new
        {
            connectorId,
            externalId = "ext-docs-crud",
            path = "/akte-1",
            name = "contract.pdf",
            version = "1",
            eTag = "etag-1",
        });
        var created = Body(await client.SendAsync(createRequest));
        var id = created.GetProperty("id").GetGuid();

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("contract.pdf", created.GetProperty("name").GetString());
        Assert.Equal("Pending", created.GetProperty("state").GetString());
        Assert.Equal(connectorId, created.GetProperty("storage").GetProperty("connectorId").GetGuid());
        var rowVersion = created.GetProperty("rowVersion").GetUInt32();
        Assert.True(rowVersion > 0);

        // read
        var fetched = Body(await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{id}", user.Id)));
        Assert.Equal(id, fetched.GetProperty("id").GetGuid());

        // list
        var list = Body(await client.SendAsync(Request(HttpMethod.Get, Base, user.Id)));
        Assert.Equal(1, list.GetProperty("total").GetInt32());

        // rename with the correct row version
        var rename = Request(HttpMethod.Patch, $"{Base}/{id}", user.Id);
        rename.Content = JsonContent.Create(new { name = "contract-v2.pdf", rowVersion });
        var renamed = Body(await client.SendAsync(rename));
        Assert.Equal("contract-v2.pdf", renamed.GetProperty("name").GetString());

        // rename with the now-stale row version conflicts
        var stale = Request(HttpMethod.Patch, $"{Base}/{id}", user.Id);
        stale.Content = JsonContent.Create(new { name = "contract-v3.pdf", rowVersion });
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(stale)).StatusCode);

        // delete
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(Request(HttpMethod.Delete, $"{Base}/{id}", user.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{id}", user.Id))).StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_document_is_not_found()
    {
        var owner = await SeedUserAsync("docs-x-owner",
            PermissionAction.Upload, PermissionAction.Read, PermissionAction.Delete);
        var outsider = await SeedUserAsync("docs-x-outsider",
            PermissionAction.Read, PermissionAction.Delete);
        var client = factory.CreateClient();

        var createRequest = Request(HttpMethod.Post, Base, owner.Id);
        createRequest.Content = JsonContent.Create(new
        {
            connectorId = Guid.NewGuid(),
            externalId = "ext-docs-x",
            path = "/a",
            name = "a.pdf",
        });
        var id = Body(await client.SendAsync(createRequest)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{id}", outsider.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Request(HttpMethod.Delete, $"{Base}/{id}", outsider.Id))).StatusCode);
    }

    [Fact]
    public async Task Validation_errors_are_bad_request()
    {
        var user = await SeedUserAsync("docs-bad",
            PermissionAction.Upload, PermissionAction.Read);
        var client = factory.CreateClient();

        var create = Request(HttpMethod.Post, Base, user.Id);
        create.Content = JsonContent.Create(new { connectorId = Guid.NewGuid(), externalId = "", path = "/a", name = "a.pdf" });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(create)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(Request(HttpMethod.Get, $"{Base}?take=0", user.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(Request(HttpMethod.Get, $"{Base}?take=500", user.Id))).StatusCode);
    }
}

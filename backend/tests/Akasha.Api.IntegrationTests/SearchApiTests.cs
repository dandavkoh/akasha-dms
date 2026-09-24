using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Akasha.Application.Tenancy;
using Akasha.Domain.Authorization;
using Akasha.Domain.Documents;
using Akasha.Domain.Identity;
using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Api.IntegrationTests;

public sealed class SearchApiTests(ApiFixture factory) : IClassFixture<ApiFixture>
{
    private const string Base = "/api/v1/search/documents";

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

    private async Task<(string Subject, Tenant Tenant, User User)> SeedUserAsync(string slug)
    {
        _ = factory.CreateClient(); // ensure startup migrations ran

        var subject = $"sub-search-{slug}";
        var tenant = new Tenant($"Tenant search-{slug}", slug);
        var user = new User(tenant.Id, $"User search-{slug}");

        await using (var context = CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(tenant.Id))
        {
            context.Users.Add(user);
            context.UserIdentities.Add(new UserIdentity(tenant.Id, user.Id, "internal", subject));
            await context.SaveChangesAsync();
        }

        return (subject, tenant, user);
    }

    private async Task<Guid> SeedDocumentAsync(Tenant tenant, User user, string name, bool grantRead)
    {
        var document = new Document(tenant.Id, new StorageReference(
            tenant.Id, Guid.NewGuid(), $"ext-{name}", "/somewhere", name));

        await using var context = CreateContext(tenant.Id);
        context.Documents.Add(document);
        if (grantRead)
        {
            context.PermissionGrants.Add(new PermissionGrant(
                tenant.Id, PrincipalType.User, user.Id, ResourceType.Document, document.Id, PermissionAction.Read));
        }

        await context.SaveChangesAsync();
        return document.Id;
    }

    private static HttpRequestMessage Request(string url, string? subject)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
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
            (await factory.CreateClient().SendAsync(Request(Base, null))).StatusCode);
    }

    [Fact]
    public async Task Search_returns_only_documents_the_user_may_read()
    {
        var (subject, tenant, user) = await SeedUserAsync("api-visible");
        var visible = await SeedDocumentAsync(tenant, user, "search-api-visible.pdf", grantRead: true);
        var hidden = await SeedDocumentAsync(tenant, user, "search-api-hidden.pdf", grantRead: false);

        var response = await factory.CreateClient().SendAsync(
            Request($"{Base}?query=search-api", subject));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = Body(response);
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(visible.ToString(), body.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Cross_tenant_documents_are_invisible()
    {
        var (subjectA, tenantA, userA) = await SeedUserAsync("api-cross-a");
        await SeedDocumentAsync(tenantA, userA, "shared-api-name.pdf", grantRead: true);
        var (subjectB, tenantB, userB) = await SeedUserAsync("api-cross-b");
        await SeedDocumentAsync(tenantB, userB, "shared-api-name.pdf", grantRead: true);

        var client = factory.CreateClient();
        var fromA = Body(await client.SendAsync(Request($"{Base}?query=shared-api-name", subjectA)));
        var fromB = Body(await client.SendAsync(Request($"{Base}?query=shared-api-name", subjectB)));

        Assert.Equal(1, fromA.GetProperty("total").GetInt32());
        Assert.Equal(1, fromB.GetProperty("total").GetInt32());
        Assert.NotEqual(
            fromA.GetProperty("items")[0].GetProperty("id").GetString(),
            fromB.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Invalid_pagination_is_a_bad_request()
    {
        var (subject, _, _) = await SeedUserAsync("api-invalid");

        Assert.Equal(HttpStatusCode.BadRequest,
            (await factory.CreateClient().SendAsync(Request($"{Base}?take=0", subject))).StatusCode);
    }
}

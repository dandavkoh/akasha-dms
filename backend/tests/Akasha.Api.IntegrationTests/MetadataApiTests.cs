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

public sealed class MetadataApiTests(ApiFixture factory) : IClassFixture<ApiFixture>
{
    private const string Base = "/api/v1/metadata";

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

    private async Task<Subject> SeedUserAsync(string slug, params (ResourceType Type, PermissionAction Action)[] grants)
    {
        _ = factory.CreateClient(); // ensure host + startup migrations

        var subject = $"sub-meta-{slug}";
        var tenant = new Tenant($"Tenant meta-{slug}", $"meta-{slug}");
        var user = new User(tenant.Id, $"User meta-{slug}");

        await using (var context = CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(tenant.Id))
        {
            context.Users.Add(user);
            context.UserIdentities.Add(new UserIdentity(tenant.Id, user.Id, "internal", subject));
            foreach (var (type, action) in grants)
            {
                context.PermissionGrants.Add(new PermissionGrant(tenant.Id, PrincipalType.User, user.Id, type, null, action));
            }

            await context.SaveChangesAsync();
        }

        return new Subject { Id = subject, Tenant = tenant, User = user };
    }

    private async Task<Guid> SeedDocumentAsync(Subject owner, string externalId)
    {
        await using var context = CreateContext(owner.Tenant.Id);
        var document = new Document(owner.Tenant.Id, new StorageReference(
            owner.Tenant.Id, ApiFixture.StorageConnectorId, externalId, "/seed", $"{externalId}.pdf"));
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        return document.Id;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? subject, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (subject is not null)
        {
            request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
        }

        return request;
    }

    private static readonly (ResourceType, PermissionAction)[] FullGrants =
    [
        (ResourceType.Metadata, PermissionAction.Read),
        (ResourceType.Metadata, PermissionAction.Write),
        (ResourceType.Document, PermissionAction.Read),
        (ResourceType.Document, PermissionAction.EditMetadata),
    ];

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().SendAsync(Request(HttpMethod.Get, $"{Base}/definitions", null))).StatusCode);
    }

    [Fact]
    public async Task Requests_without_grants_are_forbidden()
    {
        var user = await SeedUserAsync("api-denied");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await factory.CreateClient().SendAsync(Request(HttpMethod.Get, $"{Base}/definitions", user.Id))).StatusCode);
    }

    [Fact]
    public async Task Definition_and_value_roundtrip()
    {
        var user = await SeedUserAsync("api-crud", FullGrants);
        var client = factory.CreateClient();
        var documentId = await SeedDocumentAsync(user, "api-1");

        // Create definition (text, required, max 20 chars).
        var create = await client.SendAsync(Request(HttpMethod.Post, $"{Base}/definitions", user.Id,
            JsonContent.Create(new
            {
                key = "comment",
                fieldType = "Text",
                isRequired = true,
                label = "Comment",
                maxLength = 20,
            })));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // Invalid value -> 400, valid value -> 200, read back -> value present.
        var invalid = await client.SendAsync(Request(HttpMethod.Put, $"{Base}/documents/{documentId}/comment", user.Id,
            JsonContent.Create(new { text = "   " })));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var setResponse = await client.SendAsync(Request(HttpMethod.Put, $"{Base}/documents/{documentId}/comment", user.Id,
            JsonContent.Create(new { text = "approved" })));
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

        var values = await client.SendAsync(Request(HttpMethod.Get, $"{Base}/documents/{documentId}", user.Id));
        var body = JsonDocument.Parse(await values.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("approved", body[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Other_tenant_document_is_not_found()
    {
        var owner = await SeedUserAsync("api-a", FullGrants);
        var stranger = await SeedUserAsync("api-b", FullGrants);
        var documentId = await SeedDocumentAsync(owner, "api-iso");

        var response = await factory.CreateClient().SendAsync(
            Request(HttpMethod.Get, $"{Base}/documents/{documentId}", stranger.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

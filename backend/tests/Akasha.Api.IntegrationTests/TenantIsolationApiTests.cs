using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Akasha.Application.Tenancy;
using Akasha.Domain.Identity;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Api.IntegrationTests;

public sealed class TenantIsolationApiTests(ApiFixture factory) : IClassFixture<ApiFixture>
{
    private async Task<Tenant> SeedUserAsync(string slug, string subject, UserKind kind = UserKind.Person)
    {
        var tenant = new Tenant($"Tenant {slug}", slug);

        await using (var context = CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(tenant.Id))
        {
            var user = new User(tenant.Id, $"User {slug}", kind);
            context.Users.Add(user);
            context.UserIdentities.Add(new UserIdentity(tenant.Id, user.Id, "internal", subject));
            await context.SaveChangesAsync();
        }

        return tenant;
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

    [Fact]
    public async Task Authenticated_identity_resolves_its_tenant()
    {
        var subject = Guid.NewGuid().ToString("N");
        var tenant = await SeedUserAsync($"api-{subject[..8]}", subject);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/current");
        request.Headers.Add(TestAuthHandler.SubjectHeader, subject);

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(tenant.Id, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Spoofed_tenant_id_claim_is_rejected()
    {
        var subject = Guid.NewGuid().ToString("N");
        var own = await SeedUserAsync($"api-a-{subject[..8]}", subject);
        var foreign = await SeedUserAsync($"api-b-{subject[..8]}", "someone-else-" + subject[..8]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/current");
        request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
        request.Headers.Add(TestAuthHandler.TenantHeader, foreign.Id.ToString());

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(foreign.Id, own.Id);
    }

    [Fact]
    public async Task Identity_without_membership_is_forbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/current");
        request.Headers.Add(TestAuthHandler.SubjectHeader, "no-membership-" + Guid.NewGuid().ToString("N"));

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_have_no_tenant_context()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/tenants/current");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Multiple_memberships_require_valid_selection()
    {
        var subject = Guid.NewGuid().ToString("N");
        var tenantA = await SeedUserAsync($"api-m1-{subject[..8]}", subject);
        await SeedUserAsync($"api-m2-{subject[..8]}", subject);

        using var ambiguous = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/current");
        ambiguous.Headers.Add(TestAuthHandler.SubjectHeader, subject);

        var ambiguousResponse = await factory.CreateClient().SendAsync(ambiguous);

        Assert.Equal(HttpStatusCode.Conflict, ambiguousResponse.StatusCode);

        using var selected = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/current");
        selected.Headers.Add(TestAuthHandler.SubjectHeader, subject);
        selected.Headers.Add(TestAuthHandler.TenantHeader, tenantA.Id.ToString());

        var selectedResponse = await factory.CreateClient().SendAsync(selected);

        Assert.Equal(HttpStatusCode.OK, selectedResponse.StatusCode);
        var body = await selectedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(tenantA.Id, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Service_identity_uses_the_same_validation_path()
    {
        var subject = Guid.NewGuid().ToString("N");
        var tenant = await SeedUserAsync($"api-svc-{subject[..8]}", subject, UserKind.ServiceAccount);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/current");
        request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
        request.Headers.Add(TestAuthHandler.ClientHeader, Guid.NewGuid().ToString("N"));

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(tenant.Id, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Service_identity_is_not_exempt_from_tenant_validation()
    {
        var subject = Guid.NewGuid().ToString("N");
        var foreign = await SeedUserAsync($"api-svcf-{subject[..8]}", "someone-else-" + subject[..8]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants/current");
        request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
        request.Headers.Add(TestAuthHandler.ClientHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.TenantHeader, foreign.Id.ToString());

        var response = await factory.CreateClient().SendAsync(request);

        // Unknown identity (no user seeded for this subject) and spoofed tenant give no bypass.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}


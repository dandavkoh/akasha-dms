using System.Security.Claims;
using Akasha.Application.Tenancy;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Tenancy;

namespace Akasha.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TenantResolutionTests(PostgresFixture postgres)
{
    private async Task<Tenant> AddMembershipAsync(string slug, string subject)
    {
        var tenant = new Tenant($"Tenant {slug}", slug);
        await using (var context = postgres.CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using (var context = postgres.CreateContext(tenant.Id))
        {
            context.TenantMemberships.Add(new TenantMembership(tenant.Id, subject));
            await context.SaveChangesAsync();
        }

        return tenant;
    }

    private static ClaimsPrincipal Principal(string? subject, Guid? tenantId = null)
    {
        if (subject is null)
        {
            return new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "Test"));
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
        if (tenantId is { } tenant)
        {
            claims.Add(new Claim("tenant_id", tenant.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task Resolves_tenant_from_membership_of_authenticated_subject()
    {
        var subject = Guid.NewGuid().ToString("N");
        var tenant = await AddMembershipAsync("resolve", subject);

        var resolver = new TenantResolver(postgres.CreateContext());

        Assert.Equal(tenant.Id, await resolver.ResolveTenantIdAsync(Principal(subject)));
    }

    [Fact]
    public async Task Client_supplied_tenant_is_honored_only_with_valid_membership()
    {
        var subject = Guid.NewGuid().ToString("N");
        var tenantA = await AddMembershipAsync("multi-a-" + subject[..8], subject);
        await AddMembershipAsync("multi-b-" + subject[..8], subject);
        var other = new Tenant("Not a member", "not-a-member-" + subject[..8]);
        await using (var context = postgres.CreateContext())
        {
            context.Tenants.Add(other);
            await context.SaveChangesAsync();
        }

        var resolver = new TenantResolver(postgres.CreateContext());

        // Valid membership selection works.
        Assert.Equal(tenantA.Id, await resolver.ResolveTenantIdAsync(Principal(subject, tenantA.Id)));

        // Spoofed tenant_id is rejected.
        var spoofed = await Assert.ThrowsAsync<TenantResolutionException>(
            () => resolver.ResolveTenantIdAsync(Principal(subject, other.Id)));
        Assert.Equal(TenantResolutionFailure.Forbidden, spoofed.Failure);

        // Unknown tenant ids are rejected, too.
        await Assert.ThrowsAsync<TenantResolutionException>(
            () => resolver.ResolveTenantIdAsync(Principal(subject, Guid.NewGuid())));
    }

    [Fact]
    public async Task Multiple_memberships_without_selection_are_ambiguous()
    {
        var subject = Guid.NewGuid().ToString("N");
        await AddMembershipAsync("amb-a-" + subject[..8], subject);
        await AddMembershipAsync("amb-b-" + subject[..8], subject);

        var resolver = new TenantResolver(postgres.CreateContext());

        var failure = await Assert.ThrowsAsync<TenantResolutionException>(
            () => resolver.ResolveTenantIdAsync(Principal(subject)));
        Assert.Equal(TenantResolutionFailure.Ambiguous, failure.Failure);
    }

    [Fact]
    public async Task Identity_without_membership_is_rejected()
    {
        var resolver = new TenantResolver(postgres.CreateContext());

        var failure = await Assert.ThrowsAsync<TenantResolutionException>(
            () => resolver.ResolveTenantIdAsync(Principal("nobody-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(TenantResolutionFailure.NoMembership, failure.Failure);
    }

    [Fact]
    public async Task Principal_without_subject_is_rejected()
    {
        var resolver = new TenantResolver(postgres.CreateContext());

        var failure = await Assert.ThrowsAsync<TenantResolutionException>(
            () => resolver.ResolveTenantIdAsync(Principal(subject: null)));
        Assert.Equal(TenantResolutionFailure.Unauthenticated, failure.Failure);
    }

    [Fact]
    public void Tenant_context_cannot_be_switched_once_established()
    {
        var context = new TenantContext();
        var first = Guid.NewGuid();

        context.Establish(first);
        context.Establish(first); // idempotent re-establish is fine

        Assert.Throws<TenantResolutionException>(() => context.Establish(Guid.NewGuid()));
        Assert.Equal(first, context.Require());
    }

    [Fact]
    public void Tenant_cache_keys_are_tenant_prefixed()
    {
        var tenantId = Guid.NewGuid();

        Assert.Equal($"tenant:{tenantId:N}:permissions:u1", TenantCacheKey.For(tenantId, "permissions:u1"));
        Assert.Throws<ArgumentException>(() => TenantCacheKey.For(Guid.Empty, "key"));
        Assert.Throws<ArgumentException>(() => TenantCacheKey.For(tenantId, "tenant:spoofed:key"));
    }
}

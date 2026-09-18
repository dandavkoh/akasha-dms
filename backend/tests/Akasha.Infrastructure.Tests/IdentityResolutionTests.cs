using System.Security.Claims;
using Akasha.Application.Identity;
using Akasha.Application.Tenancy;
using Akasha.Domain.Identity;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class IdentityResolutionTests(PostgresFixture postgres)
{
    private async Task<Tenant> SeedUserAsync(
        string slug,
        string subject,
        UserKind kind = UserKind.Person,
        string provider = InternalIdentityProvider.ProviderName,
        bool active = true)
    {
        var tenant = new Tenant($"Tenant {slug}", slug);
        await using (var context = postgres.CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using (var context = postgres.CreateContext(tenant.Id))
        {
            var user = new User(tenant.Id, $"User {slug}", kind);
            if (!active)
            {
                user.Deactivate();
            }

            context.Users.Add(user);
            context.UserIdentities.Add(new UserIdentity(tenant.Id, user.Id, provider, subject));
            await context.SaveChangesAsync();
        }

        return tenant;
    }

    private IdentityResolver CreateResolver()
        => new(postgres.CreateContext(), [new InternalIdentityProvider()]);

    private static ClaimsPrincipal Principal(string? subject, Guid? tenantId = null, bool serviceAccount = false)
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

        if (serviceAccount)
        {
            claims.Add(new Claim("client_id", Guid.NewGuid().ToString("N")));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task Authenticated_identity_resolves_user_and_tenant()
    {
        var subject = Guid.NewGuid().ToString("N");
        var tenant = await SeedUserAsync("resolve", subject);

        var user = await CreateResolver().ResolveAsync(Principal(subject));

        Assert.Equal(tenant.Id, user.TenantId);
        Assert.NotEqual(Guid.Empty, user.UserId);
        Assert.Equal(subject, user.Subject);
        Assert.False(user.IsServiceAccount);
    }

    [Fact]
    public async Task Selected_tenant_is_honored_only_with_valid_association()
    {
        var subject = Guid.NewGuid().ToString("N");
        var tenantA = await SeedUserAsync("multi-a-" + subject[..8], subject);
        await SeedUserAsync("multi-b-" + subject[..8], subject);
        var other = new Tenant("Not mine", "not-mine-" + subject[..8]);
        await using (var context = postgres.CreateContext())
        {
            context.Tenants.Add(other);
            await context.SaveChangesAsync();
        }

        var resolver = CreateResolver();

        Assert.Equal(tenantA.Id, (await resolver.ResolveAsync(Principal(subject, tenantA.Id))).TenantId);

        var spoofed = await Assert.ThrowsAsync<TenantResolutionException>(
            () => resolver.ResolveAsync(Principal(subject, other.Id)));
        Assert.Equal(TenantResolutionFailure.Forbidden, spoofed.Failure);

        // Unknown tenant ids are rejected, too.
        var unknown = await Assert.ThrowsAsync<TenantResolutionException>(
            () => resolver.ResolveAsync(Principal(subject, Guid.NewGuid())));
        Assert.Equal(TenantResolutionFailure.Forbidden, unknown.Failure);
    }

    [Fact]
    public async Task Multiple_tenants_without_selection_are_ambiguous()
    {
        var subject = Guid.NewGuid().ToString("N");
        await SeedUserAsync("amb-a-" + subject[..8], subject);
        await SeedUserAsync("amb-b-" + subject[..8], subject);

        var failure = await Assert.ThrowsAsync<TenantResolutionException>(
            () => CreateResolver().ResolveAsync(Principal(subject)));
        Assert.Equal(TenantResolutionFailure.Ambiguous, failure.Failure);
    }

    [Fact]
    public async Task Unknown_identity_is_rejected()
    {
        var failure = await Assert.ThrowsAsync<TenantResolutionException>(
            () => CreateResolver().ResolveAsync(Principal("nobody-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(TenantResolutionFailure.NoMembership, failure.Failure);
    }

    [Fact]
    public async Task Principal_without_subject_is_unauthenticated()
    {
        var failure = await Assert.ThrowsAsync<TenantResolutionException>(
            () => CreateResolver().ResolveAsync(Principal(subject: null)));
        Assert.Equal(TenantResolutionFailure.Unauthenticated, failure.Failure);
    }

    [Fact]
    public async Task Unauthenticated_principal_cannot_be_mapped()
    {
        // Identity without an authentication type is not considered authenticated.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "x")]));

        Assert.Null(new InternalIdentityProvider().Map(principal));

        var failure = await Assert.ThrowsAsync<TenantResolutionException>(
            () => CreateResolver().ResolveAsync(principal));
        Assert.Equal(TenantResolutionFailure.Unauthenticated, failure.Failure);
    }

    [Fact]
    public async Task Inactive_user_is_rejected()
    {
        var subject = Guid.NewGuid().ToString("N");
        await SeedUserAsync("inactive-" + subject[..8], subject, active: false);

        var failure = await Assert.ThrowsAsync<TenantResolutionException>(
            () => CreateResolver().ResolveAsync(Principal(subject)));
        Assert.Equal(TenantResolutionFailure.NoMembership, failure.Failure);
    }

    [Fact]
    public async Task Service_identity_resolves_with_service_account_flag()
    {
        var subject = Guid.NewGuid().ToString("N");
        await SeedUserAsync("svc-" + subject[..8], subject, UserKind.ServiceAccount);

        var user = await CreateResolver().ResolveAsync(Principal(subject, serviceAccount: true));

        Assert.True(user.IsServiceAccount);
    }

    [Fact]
    public async Task App_only_principal_of_a_person_user_is_flagged_by_claim()
    {
        var subject = Guid.NewGuid().ToString("N");
        await SeedUserAsync("apponly-" + subject[..8], subject);

        var user = await CreateResolver().ResolveAsync(Principal(subject, serviceAccount: true));

        Assert.True(user.IsServiceAccount);
    }

    [Fact]
    public async Task Identity_links_are_unique_per_provider_subject_and_user()
    {
        var subject = Guid.NewGuid().ToString("N");
        var tenant = await SeedUserAsync("unique-" + subject[..8], subject);

        await using var context = postgres.CreateContext(tenant.Id);
        var user = await context.Users.SingleAsync(u => u.TenantId == tenant.Id);
        context.UserIdentities.Add(new UserIdentity(tenant.Id, user.Id, InternalIdentityProvider.ProviderName, subject));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public void Current_user_context_cannot_be_switched_once_established()
    {
        var context = new CurrentUserContext();
        var user = new CurrentUser(Guid.NewGuid(), Guid.NewGuid(), "sub", "internal", false);
        Assert.False(context.IsAuthenticated);
        Assert.Throws<TenantResolutionException>(() => context.Require());

        context.Establish(user);
        context.Establish(user); // idempotent re-establish is fine

        Assert.True(context.IsAuthenticated);
        Assert.Throws<TenantResolutionException>(() =>
            context.Establish(new CurrentUser(Guid.NewGuid(), Guid.NewGuid(), "sub", "internal", false)));
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
}

using System.Security.Claims;
using Akasha.Application.Identity;
using Akasha.Application.Tenancy;
using Akasha.Domain.Identity;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Identity;

/// <summary>
/// Maps the authenticated principal to an external identity via the registered identity
/// providers, then validates it against server-side user identities. The trust root is
/// the database, never a client claim. Resolves both the user (identity) and the tenant
/// (association); permission decisions are left to the authorization layer.
/// </summary>
public sealed class IdentityResolver(AkashaDbContext dbContext, IEnumerable<IIdentityProvider> providers) : IIdentityResolver
{
    public async Task<CurrentUser> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var external = providers
            .Select(provider => provider.Map(principal))
            .FirstOrDefault(identity => identity is not null);

        if (external is null)
        {
            throw new TenantResolutionException(
                TenantResolutionFailure.Unauthenticated,
                "The principal could not be mapped to a known identity provider.");
        }

        // Identity lookup is the trust root: at this point no tenant context exists,
        // so the lookup must explicitly bypass the tenant query filters.
        var associations = await (
            from identity in dbContext.UserIdentities.IgnoreQueryFilters()
            join user in dbContext.Users.IgnoreQueryFilters() on identity.UserId equals user.Id
            where identity.Provider == external.Provider && identity.ExternalSubject == external.Subject
            select new Association(user.TenantId, user.Id, user.IsActive, user.Kind))
            .ToListAsync(cancellationToken);

        var active = associations.Where(association => association.IsActive).ToList();
        if (active.Count == 0)
        {
            throw new TenantResolutionException(
                TenantResolutionFailure.NoMembership,
                "Identity is not registered for any tenant.");
        }

        var requested = principal.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (Guid.TryParse(requested, out var requestedTenantId))
            {
            var selected = active.Find(association => association.TenantId == requestedTenantId);
            if (selected is not null)
            {
                return ToCurrentUser(external, selected);
            }
            }

            throw new TenantResolutionException(
                TenantResolutionFailure.Forbidden,
                "Identity has no association for the requested tenant.");
        }

        if (active.Count > 1)
        {
            throw new TenantResolutionException(
                TenantResolutionFailure.Ambiguous,
                "Identity belongs to multiple tenants; a valid tenant_id claim is required.");
        }

        return ToCurrentUser(external, active[0]);
    }

    private sealed record Association(Guid TenantId, Guid UserId, bool IsActive, UserKind Kind);

    private static CurrentUser ToCurrentUser(ExternalIdentity external, Association association)
        => new(
            association.UserId,
            association.TenantId,
            external.Subject,
            external.Provider,
            external.IsServiceAccount || association.Kind == UserKind.ServiceAccount);
}

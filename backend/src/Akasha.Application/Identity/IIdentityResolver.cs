using System.Security.Claims;
using Akasha.Application.Tenancy;

namespace Akasha.Application.Identity;

/// <summary>
/// Resolves an authenticated principal to the server-side user and tenant. The user
/// association (tenant membership) is validated against persisted user identities;
/// a client-supplied tenant selection is honored only with a valid association.
/// </summary>
public interface IIdentityResolver
{
    /// <exception cref="TenantResolutionException">On any resolution failure (unauthenticated, unknown, forbidden, ambiguous).</exception>
    Task<CurrentUser> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

using System.Security.Claims;
using Akasha.Application.Tenancy;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tenancy;

/// <summary>
/// Resolves the tenant exclusively from the authenticated identity, validated against
/// server-side <see cref="Akasha.Domain.Tenants.TenantMembership"/> records.
/// A client-supplied tenant selection (claim) is only honored when a matching membership exists.
/// </summary>
public sealed class TenantResolver(AkashaDbContext dbContext) : ITenantResolver
{
    public async Task<Guid> ResolveTenantIdAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new TenantResolutionException(TenantResolutionFailure.Unauthenticated, "Authenticated identity has no subject claim.");
        }

        // Membership lookup is itself the trust root and must not be tenant-filtered -
        // there is no tenant context yet at this point.
        var tenantIds = await dbContext.TenantMemberships
            .IgnoreQueryFilters()
            .Where(membership => membership.Subject == subject)
            .Select(membership => membership.TenantId)
            .ToListAsync(cancellationToken);

        if (tenantIds.Count == 0)
        {
            throw new TenantResolutionException(TenantResolutionFailure.NoMembership, "Identity has no tenant membership.");
        }

        var requested = principal.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (Guid.TryParse(requested, out var requestedTenantId) && tenantIds.Contains(requestedTenantId))
            {
                return requestedTenantId;
            }

            throw new TenantResolutionException(TenantResolutionFailure.Forbidden, "Identity has no membership for the requested tenant.");
        }

        if (tenantIds.Count > 1)
        {
            throw new TenantResolutionException(TenantResolutionFailure.Ambiguous, "Identity belongs to multiple tenants; a valid tenant_id claim is required.");
        }

        return tenantIds[0];
    }
}

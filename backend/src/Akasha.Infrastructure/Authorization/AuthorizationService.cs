using Akasha.Application.Authorization;
using Akasha.Application.Identity;
using Akasha.Domain.Authorization;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Authorization;

/// <summary>
/// PostgreSQL-backed authorization evaluation. Rules:
/// 1. Deny always wins over allow (explicit override).
/// 2. Allows are collected from the user's direct grants, the user's group grants and
///    the grants of every role assigned to the user or to one of the user's groups.
/// 3. Inheritance: a grant with ResourceId == null covers every resource of its type;
///    a tenant-wide grant (ResourceType.Tenant, ResourceId == null) covers the whole
///    tenant, and a tenant-wide ADMIN allow covers every action.
/// 4. No matching allow - denied (default deny).
/// Grant rows are tenant-scoped and protected by the global tenant query filters, so
/// principals of one tenant can never inherit grants of another tenant.
/// </summary>
public sealed class AuthorizationService(AkashaDbContext dbContext) : IAuthorizationService
{
    public async Task<bool> IsAuthorizedAsync(
        CurrentUser user,
        ResourceType resourceType,
        Guid? resourceId,
        PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var userId = user.UserId;

        var groupIds = await dbContext.GroupMembers
            .Where(member => member.UserId == userId)
            .Select(member => member.GroupId)
            .ToListAsync(cancellationToken);

        var roleIds = await dbContext.RoleAssignments
            .Where(assignment =>
                (assignment.PrincipalType == PrincipalType.User && assignment.PrincipalId == userId)
                || (assignment.PrincipalType == PrincipalType.Group && groupIds.Contains(assignment.PrincipalId)))
            .Select(assignment => assignment.RoleId)
            .ToListAsync(cancellationToken);

        var grants = await dbContext.PermissionGrants
            .Where(grant =>
                ((grant.PrincipalType == PrincipalType.User && grant.PrincipalId == userId)
                 || (grant.PrincipalType == PrincipalType.Group && groupIds.Contains(grant.PrincipalId))
                 || (grant.PrincipalType == PrincipalType.Role && roleIds.Contains(grant.PrincipalId)))
                && (
                    // resource-level or type-level grant for the requested action
                    (grant.ResourceType == resourceType
                        && grant.Action == action
                        && (grant.ResourceId == null || grant.ResourceId == resourceId))
                    // tenant-wide grant: direct action or tenant admin
                    || (grant.ResourceType == ResourceType.Tenant
                        && grant.ResourceId == null
                        && (grant.Action == action || grant.Action == PermissionAction.Admin))))
            .Select(grant => grant.Effect)
            .ToListAsync(cancellationToken);

        if (grants.Contains(GrantEffect.Deny))
        {
            return false;
        }

        return grants.Contains(GrantEffect.Allow);
    }
}

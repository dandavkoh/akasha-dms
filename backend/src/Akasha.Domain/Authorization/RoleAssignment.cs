using Akasha.Domain.Common;

namespace Akasha.Domain.Authorization;

/// <summary>Assigns a role to a user or a group. Roles are never assigned to other roles.</summary>
public sealed class RoleAssignment : ITenantScoped
{
    private RoleAssignment()
    {
    }

    public RoleAssignment(Guid tenantId, Guid roleId, PrincipalType principalType, Guid principalId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        if (principalType == PrincipalType.Role)
        {
            throw new ArgumentException("Roles cannot be assigned to roles.", nameof(principalType));
        }

        TenantId = tenantId;
        RoleId = roleId;
        PrincipalType = principalType;
        PrincipalId = principalId;
    }

    public Guid TenantId { get; private set; }

    public Guid RoleId { get; private set; }

    public PrincipalType PrincipalType { get; private set; }

    public Guid PrincipalId { get; private set; }
}

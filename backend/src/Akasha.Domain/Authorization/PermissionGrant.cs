using Akasha.Domain.Common;

namespace Akasha.Domain.Authorization;

public enum GrantEffect
{
    Allow = 0,
    /// <summary>Explicit override: a deny always wins over any allow.</summary>
    Deny = 1,
}

/// <summary>
/// A single permission grant from a principal (user, group or role) to a resource.
/// <see cref="ResourceId"/> == null means the grant applies to every resource of that
/// type in the tenant (type-level grant, inherited by individual resources). Grants on
/// <see cref="ResourceType.Tenant"/> with <see cref="ResourceId"/> null apply tenant-wide;
/// a tenant-wide <see cref="GrantEffect.Allow"/> on <see cref="PermissionAction.Admin"/>
/// grants every action in the tenant, unless an explicit deny overrides it.
/// </summary>
public sealed class PermissionGrant : ITenantScoped
{
    private PermissionGrant()
    {
    }

    public PermissionGrant(
        Guid tenantId,
        PrincipalType principalType,
        Guid principalId,
        ResourceType resourceType,
        Guid? resourceId,
        PermissionAction action,
        GrantEffect effect = GrantEffect.Allow)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        TenantId = tenantId;
        PrincipalType = principalType;
        PrincipalId = principalId;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Action = action;
        Effect = effect;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    public PrincipalType PrincipalType { get; private set; }

    public Guid PrincipalId { get; private set; }

    public ResourceType ResourceType { get; private set; }

    /// <summary>null = type-level grant applying to all resources of <see cref="ResourceType"/> in the tenant.</summary>
    public Guid? ResourceId { get; private set; }

    public PermissionAction Action { get; private set; }

    public GrantEffect Effect { get; private set; }
}

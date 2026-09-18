using Akasha.Domain.Common;

namespace Akasha.Domain.Authorization;

/// <summary>Membership of a user in a group (both must belong to the same tenant).</summary>
public sealed class GroupMember : ITenantScoped
{
    private GroupMember()
    {
    }

    public GroupMember(Guid tenantId, Guid groupId, Guid userId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        TenantId = tenantId;
        GroupId = groupId;
        UserId = userId;
    }

    public Guid TenantId { get; private set; }

    public Guid GroupId { get; private set; }

    public Guid UserId { get; private set; }
}

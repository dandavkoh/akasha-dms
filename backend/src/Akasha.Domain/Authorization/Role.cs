using Akasha.Domain.Common;

namespace Akasha.Domain.Authorization;

/// <summary>Tenant-scoped named role. Roles receive permission grants and are assigned to users or groups.</summary>
public sealed class Role : ITenantScoped
{
    public const int MaxNameLength = 100;

    private Role()
    {
    }

    public Role(Guid tenantId, string name)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name is required (max {MaxNameLength} chars).", nameof(name));
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Name = name;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name is required (max {MaxNameLength} chars).", nameof(name));
        }

        Name = name;
    }
}

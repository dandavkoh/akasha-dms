using Akasha.Domain.Common;

namespace Akasha.Domain.Authorization;

/// <summary>Tenant-scoped user group. Groups receive permission grants and role assignments.</summary>
public sealed class Group : ITenantScoped
{
    public const int MaxNameLength = 100;

    private Group()
    {
    }

    public Group(Guid tenantId, string name)
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

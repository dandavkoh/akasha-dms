using Akasha.Domain.Common;

namespace Akasha.Domain.Audit;

/// <summary>
/// Append-only audit record. Tenant-scoped: audit entries are never shared across tenants
/// and are not cascade-deleted with the audited resource.
/// </summary>
public sealed class AuditEntry : ITenantScoped
{
    public const int MaxActionLength = 100;
    public const int MaxResourceTypeLength = 100;

    private AuditEntry()
    {
    }

    public AuditEntry(Guid tenantId, string action, string? actorSubject = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(action) || action.Length > MaxActionLength)
        {
            throw new ArgumentException($"Action is required (max {MaxActionLength} chars).", nameof(action));
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Action = action;
        ActorSubject = actorSubject;
        OccurredAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    /// <summary>Identity subject of the actor, null for system operations.</summary>
    public string? ActorSubject { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }
}

using Akasha.Domain.Common;

namespace Akasha.Domain.Tenants;

/// <summary>
/// Server-side assignment of an authenticated identity (subject) to a tenant.
/// This is the authoritative source for tenant resolution - never a client claim alone.
/// </summary>
public sealed class TenantMembership : ITenantScoped
{
    public const int MaxSubjectLength = 200;

    private TenantMembership()
    {
    }

    public TenantMembership(Guid tenantId, string subject)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(subject) || subject.Length > MaxSubjectLength)
        {
            throw new ArgumentException($"Subject is required (max {MaxSubjectLength} chars).", nameof(subject));
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Subject = subject;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Stable identity-provider subject identifier (e.g. the "sub" claim).</summary>
    public string Subject { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }
}

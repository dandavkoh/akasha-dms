using Akasha.Domain.Common;

namespace Akasha.Domain.Akten;

/// <summary>
/// Logical business container ("Akte"). An Akte is NOT a physical folder in any storage
/// system (rule #42 / root #10): it groups logical documents by <see cref="AkteDocument"/>
/// relations that reference the stable DocumentId, so they survive external rename/move.
/// A physical storage folder can never express this and is not used here.
/// </summary>
public sealed class Akte : ITenantScoped
{
    public const int MaxNameLength = 512;
    public const int MaxDescriptionLength = 4000;

    private Akte()
    {
    }

    public Akte(Guid tenantId, string name, string? description = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        Name = CheckName(name);
        Description = CheckDescription(description);
        Status = AkteStatus.Active;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        UpdatedAtUtc = CreatedAtUtc;
    }

    /// <summary>Stable logical identity (the "AkteId"). Server-generated, never storage-derived.</summary>
    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public AkteStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool IsArchived => Status == AkteStatus.Archived;

    /// <summary>Archiving closes the Akte for changes until it is reactivated.</summary>
    public void Archive()
    {
        if (IsArchived)
        {
            return; // idempotent
        }

        Status = AkteStatus.Archived;
        Touch();
    }

    public void Reactivate()
    {
        if (!IsArchived)
        {
            return; // idempotent
        }

        Status = AkteStatus.Active;
        Touch();
    }

    /// <summary>Update name/description. Archived Akten are immutable (caller reactivates first).</summary>
    public void Update(string name, string? description)
    {
        if (IsArchived)
        {
            throw new InvalidOperationException("An archived Akte cannot be updated; reactivate it first.");
        }

        Name = CheckName(name);
        Description = CheckDescription(description);
        Touch();
    }

    /// <summary>Membership changes are only allowed on active Akten.</summary>
    public void EnsureActive()
    {
        if (IsArchived)
        {
            throw new InvalidOperationException("An archived Akte cannot be changed; reactivate it first.");
        }
    }

    private void Touch() => UpdatedAtUtc = DateTimeOffset.UtcNow;

    private static string CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name is required (max {MaxNameLength} chars).", nameof(name));
        }

        return name;
    }

    private static string? CheckDescription(string? description)
    {
        if (description is null)
        {
            return null;
        }

        if (description.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"Description is at most {MaxDescriptionLength} chars.", nameof(description));
        }

        return description;
    }
}

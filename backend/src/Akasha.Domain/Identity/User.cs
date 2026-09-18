using Akasha.Domain.Common;

namespace Akasha.Domain.Identity;

public enum UserKind
{
    Person = 0,
    /// <summary>Non-interactive identity (integrations, background jobs, API clients).</summary>
    ServiceAccount = 1,
}

/// <summary>
/// A user is a tenant-scoped identity with a stable internal id. Who the user is
/// (authentication) is linked via <see cref="UserIdentity"/>; what the user may do
/// (authorization) is a separate concern.
/// </summary>
public sealed class User : ITenantScoped
{
    public const int MaxDisplayNameLength = 200;

    private User()
    {
    }

    public User(Guid tenantId, string displayName, UserKind kind = UserKind.Person)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > MaxDisplayNameLength)
        {
            throw new ArgumentException($"Display name is required (max {MaxDisplayNameLength} chars).", nameof(displayName));
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        DisplayName = displayName;
        Kind = kind;
        IsActive = true;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public UserKind Kind { get; private set; }

    /// <summary>Inactive users cannot authenticate, but keep their identity and history.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public bool IsServiceAccount => Kind == UserKind.ServiceAccount;

    public void Deactivate() => IsActive = false;

    public void Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > MaxDisplayNameLength)
        {
            throw new ArgumentException($"Display name is required (max {MaxDisplayNameLength} chars).", nameof(displayName));
        }

        DisplayName = displayName;
    }
}

using Akasha.Application.Tenancy;

namespace Akasha.Application.Identity;

/// <summary>
/// The authenticated identity of the current operation. Carries who the caller is
/// (identity, tenant association, account kind) — never what the caller may do.
/// </summary>
public sealed record CurrentUser(
    Guid UserId,
    Guid TenantId,
    string Subject,
    string Provider,
    bool IsServiceAccount);

/// <summary>Ambient authentication context for the current request/job scope.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    CurrentUser? User { get; }

    /// <summary>Sets the resolved identity. Refuses to switch to another user once established.</summary>
    void Establish(CurrentUser user);

    CurrentUser Require();
}

/// <summary>Default ambient authentication context implementation (scoped per request/job).</summary>
public sealed class CurrentUserContext : ICurrentUser
{
    private CurrentUser? _user;

    public bool IsAuthenticated => _user is not null;

    public CurrentUser? User => _user;

    public void Establish(CurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (_user is not null && _user.UserId != user.UserId)
        {
            throw new TenantResolutionException(
                TenantResolutionFailure.Forbidden,
                "Authentication context is already established for a different user and cannot be switched.");
        }

        _user = user;
    }

    public CurrentUser Require()
        => _user ?? throw new TenantResolutionException(TenantResolutionFailure.Unauthenticated, "No authenticated identity.");
}

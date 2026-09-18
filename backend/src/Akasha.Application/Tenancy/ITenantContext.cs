using System.Security.Claims;

namespace Akasha.Application.Tenancy;

public enum TenantResolutionFailure
{
    None = 0,
    /// <summary>No authenticated identity available.</summary>
    Unauthenticated,
    /// <summary>The identity has no tenant membership.</summary>
    NoMembership,
    /// <summary>The requested tenant does not belong to the identity (spoofing attempt).</summary>
    Forbidden,
    /// <summary>The identity belongs to multiple tenants and none was selected.</summary>
    Ambiguous,
}

public sealed class TenantResolutionException : Exception
{
    public TenantResolutionException(TenantResolutionFailure failure, string message)
        : base(message)
        => Failure = failure;

    public TenantResolutionFailure Failure { get; }
}

/// <summary>Thrown when tenant-scoped data is accessed without a resolved tenant.</summary>
public sealed class MissingTenantContextException : Exception
{
    public MissingTenantContextException()
        : base("No tenant context is established for the current operation.")
    {
    }
}

/// <summary>Thrown when code attempts to write or delete data of another tenant.</summary>
public sealed class TenantIsolationViolationException : Exception
{
    public TenantIsolationViolationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Ambient tenant context for the current operation (request or background job).
/// Established once from the authenticated identity; must never be derived from
/// a raw client-supplied tenantId without server-side validation.
/// </summary>
public interface ITenantContext
{
    bool HasTenant { get; }

    /// <summary>The current tenant id, or <see cref="Guid.Empty"/> when unresolved.</summary>
    Guid TenantId { get; }

    /// <summary>Sets the tenant. Refuses to switch once a different tenant was established.</summary>
    void Establish(Guid tenantId);

    /// <summary>Returns the tenant id or throws <see cref="MissingTenantContextException"/>.</summary>
    Guid Require();
}

/// <summary>
/// Resolves the tenant for an authenticated principal by validating it against
/// server-side tenant memberships.
/// </summary>
public interface ITenantResolver
{
    Task<Guid> ResolveTenantIdAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

/// <summary>Default ambient tenant context implementation (scoped per request/job).</summary>
public sealed class TenantContext : ITenantContext
{
    private Guid _tenantId;

    public bool HasTenant => _tenantId != Guid.Empty;

    public Guid TenantId => _tenantId;

    public void Establish(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (HasTenant && _tenantId != tenantId)
        {
            throw new TenantResolutionException(
                TenantResolutionFailure.Forbidden,
                "Tenant context is already established for a different tenant and cannot be switched.");
        }

        _tenantId = tenantId;
    }

    public Guid Require()
        => HasTenant ? _tenantId : throw new MissingTenantContextException();
}

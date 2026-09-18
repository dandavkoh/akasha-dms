using Akasha.Domain.Common;

namespace Akasha.Domain.Identity;

/// <summary>
/// Links a <see cref="User"/> to an external identity provider subject. This is the
/// server-side trust root for mapping an authenticated principal to an Akasha user.
/// Provider-specific protocol details never enter the domain; only the provider key
/// and the stable external subject are stored.
/// </summary>
public sealed class UserIdentity : ITenantScoped
{
    public const int MaxProviderLength = 100;
    public const int MaxExternalSubjectLength = 300;

    private UserIdentity()
    {
    }

    public UserIdentity(Guid tenantId, Guid userId, string provider, string externalSubject)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(provider) || provider.Length > MaxProviderLength)
        {
            throw new ArgumentException($"Provider is required (max {MaxProviderLength} chars).", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(externalSubject) || externalSubject.Length > MaxExternalSubjectLength)
        {
            throw new ArgumentException($"External subject is required (max {MaxExternalSubjectLength} chars).", nameof(externalSubject));
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        UserId = userId;
        Provider = provider;
        ExternalSubject = externalSubject;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>Stable provider key, e.g. "internal", "entra", "oidc", "ldap", "saml".</summary>
    public string Provider { get; private set; } = string.Empty;

    /// <summary>Stable provider-specific subject (e.g. the "sub" claim). Never e.g. an e-mail that can change.</summary>
    public string ExternalSubject { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }
}

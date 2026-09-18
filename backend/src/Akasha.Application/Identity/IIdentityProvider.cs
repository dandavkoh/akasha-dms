using System.Security.Claims;

namespace Akasha.Application.Identity;

/// <summary>Normalized identity extracted from a principal, free of provider specifics.</summary>
public sealed record ExternalIdentity(string Provider, string Subject, bool IsServiceAccount);

/// <summary>
/// Maps an authenticated principal to a normalized external identity. Each identity
/// provider (internal, Entra ID, OIDC, SAML, LDAP...) contributes one implementation
/// with its provider-specific claim mapping; the mapping result must match
/// UserIdentity.Provider / ExternalSubject stored server-side.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>Stable provider key (e.g. "internal", "entra", "oidc").</summary>
    string Name { get; }

    /// <summary>Returns null when this provider cannot map the principal.</summary>
    ExternalIdentity? Map(ClaimsPrincipal principal);
}

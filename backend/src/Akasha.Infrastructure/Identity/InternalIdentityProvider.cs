using System.Security.Claims;
using Akasha.Application.Identity;

namespace Akasha.Infrastructure.Identity;

/// <summary>
/// Default provider for internal/test authentication: reads the standard subject claim
/// and treats app-only credentials (client credentials, Entra app-only tokens) as
/// service accounts. Real protocol integrations (Entra, OIDC, SAML, LDAP) add their
/// own provider; this one stays as the offline-capable fallback.
/// </summary>
public sealed class InternalIdentityProvider : IIdentityProvider
{
    public const string ProviderName = "internal";

    public string Name => ProviderName;

    public ExternalIdentity? Map(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        // App-only tokens carry a client id (or Entra "idtyp=app") instead of a delegated user.
        var isServiceAccount = !string.IsNullOrEmpty(principal.FindFirst("client_id")?.Value)
            || string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase);

        return new ExternalIdentity(ProviderName, subject, isServiceAccount);
    }
}

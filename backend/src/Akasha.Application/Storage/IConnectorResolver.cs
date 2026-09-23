using Akasha.Application.Common;

namespace Akasha.Application.Storage;

/// <summary>
/// Resolves a configured storage connector by its id. Tenant context is part of the
/// resolution: a connector id belonging to another tenant MUST surface as
/// <see cref="NotFoundException"/>, never as cross-tenant access.
/// </summary>
public interface IConnectorResolver
{
    /// <summary>Returns the connector for <paramref name="connectorId"/> of the current tenant.</summary>
    /// <exception cref="NotFoundException">Unknown connector id or connector of another tenant.</exception>
    Task<IStorageConnector> ResolveAsync(Guid connectorId, CancellationToken cancellationToken = default);
}

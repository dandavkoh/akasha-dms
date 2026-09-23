using System.Collections.Concurrent;
using Akasha.Application.Common;
using Akasha.Application.Storage;
using Akasha.Application.Tenancy;
using Microsoft.Extensions.Options;

namespace Akasha.Infrastructure.Storage;

/// <summary>One connector entry in configuration. See <see cref="ConfiguredConnectorResolver"/>.</summary>
public sealed class ConfiguredConnectorOptions
{
    public const string SectionName = "Storage:Connectors";

    public Guid Id { get; init; }

    /// <summary>
    /// Owning tenant. Empty = explicitly shared across tenants by the operator
    /// (self-hosted deployments may wire one physical store for several tenants;
    /// per-document authorization still applies). A non-empty value restricts the
    /// connector to exactly that tenant.
    /// </summary>
    public Guid TenantId { get; init; }

    /// <summary>Connector implementation key; currently "LocalFileSystem" (the only implementation).</summary>
    public string Type { get; init; } = "LocalFileSystem";

    public string? RootPath { get; init; }

    public long? MaxFileSizeBytes { get; init; }
}

/// <summary>
/// Resolves connectors from static application configuration.
/// ponytail: connectors are config-based, not database entities, until the connector
/// management module exists (Phase 7 P4). Upgrade path: a tenant-scoped connectors
/// table plus the same <see cref="IConnectorResolver"/> contract; nothing upstream changes.
/// A tenant-restricted connector id used by another tenant resolves to
/// <see cref="NotFoundException"/> — the existence of foreign connectors is never revealed.
/// </summary>
public sealed class ConfiguredConnectorResolver(
    IOptions<List<ConfiguredConnectorOptions>> options,
    ITenantContext tenantContext) : IConnectorResolver
{
    // Connector instances hold change-tracking state (scan snapshot/cursor); keep them
    // alive across request scopes. Static is safe here: instances are keyed by connector id,
    // which maps to exactly one tenant, and tenancy is verified on every ResolveAsync call.
    private static readonly ConcurrentDictionary<Guid, IStorageConnector> Instances = new();

    public Task<IStorageConnector> ResolveAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.Require();
        var entry = options.Value.FirstOrDefault(candidate => candidate.Id == connectorId);

        if (entry is null || (entry.TenantId != Guid.Empty && entry.TenantId != tenantId))
        {
            throw new NotFoundException("connector", connectorId);
        }

        return Task.FromResult(Instances.GetOrAdd(connectorId, _ => Create(entry)));
    }

    private static IStorageConnector Create(ConfiguredConnectorOptions entry)
        => entry.Type switch
        {
            "LocalFileSystem" => new LocalFileSystemConnector(new LocalFileSystemConnectorOptions
            {
                RootPath = entry.RootPath
                    ?? throw new InvalidOperationException($"Connector {entry.Id}: RootPath is required."),
                MaxFileSizeBytes = entry.MaxFileSizeBytes
                    ?? LocalFileSystemConnectorOptions.DefaultMaxFileSizeBytes,
            }),
            _ => throw new InvalidOperationException($"Connector {entry.Id}: unknown type '{entry.Type}'."),
        };
}

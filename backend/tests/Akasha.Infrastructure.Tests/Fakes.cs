using Akasha.Application.Common;
using Akasha.Application.Storage;

namespace Akasha.Infrastructure.Tests;

/// <summary>In-memory <see cref="IConnectorResolver"/> for tests: id → connector, everything else NotFound.</summary>
public sealed class FakeConnectorResolver(IReadOnlyDictionary<Guid, IStorageConnector> connectors) : IConnectorResolver
{
    public static readonly FakeConnectorResolver Empty = new(new Dictionary<Guid, IStorageConnector>());

    public Task<IStorageConnector> ResolveAsync(Guid connectorId, CancellationToken cancellationToken = default)
        => connectors.TryGetValue(connectorId, out var connector)
            ? Task.FromResult(connector)
            : throw new NotFoundException("connector", connectorId);
}

/// <summary>Connector stub whose operations fail with a configurable normalized error (or lack the capability entirely).</summary>
public sealed class FailingConnector(StorageCapability capabilities, StorageErrorKind failWith, bool isTransient = true)
    : StorageConnectorBase
{
    public override StorageCapability Capabilities => capabilities;

    public override Task<StorageItem> GetDocumentAsync(string externalId, CancellationToken cancellationToken = default)
        => Task.FromResult(new StorageItem { ExternalId = externalId, Path = "", Name = "stub.bin" });

    public override Task<Stream> DownloadAsync(string externalId, CancellationToken cancellationToken = default)
        => throw new StorageException(failWith, "Configured failure.", isTransient);

    public override Task<StorageItem> MoveDocumentAsync(string externalId, string targetFolderPath, string? expectedETag = null, CancellationToken cancellationToken = default)
        => throw new StorageException(failWith, "Configured failure.", isTransient);
}

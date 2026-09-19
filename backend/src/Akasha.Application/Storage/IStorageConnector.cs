namespace Akasha.Application.Storage;

/// <summary>
/// Normalized storage abstraction. All physical document operations go through
/// this interface; the rest of the application must never use provider SDKs or
/// provider conditionals. One connector instance represents one configured,
/// tenant-scoped connection to a storage system.
///
/// Methods accept the provider's stable <c>externalId</c> as object identity.
/// Content is always streamed. Write operations accept an optional
/// <c>expectedETag</c> for optimistic concurrency: when given and the current
/// version differs, connectors must fail with
/// <see cref="StorageErrorKind.ConcurrencyConflict"/> instead of overwriting.
/// </summary>
public interface IStorageConnector
{
    /// <summary>What this connector can do. Core logic branches on this, never on provider names.</summary>
    StorageCapability Capabilities { get; }

    Task<StorageItem> GetDocumentAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>Lists documents in a folder. Pass null for a full discovery scan of the connector root.</summary>
    IAsyncEnumerable<StorageItem> ListDocumentsAsync(string? folderPath, CancellationToken cancellationToken = default);

    Task<StorageItem> CreateDocumentAsync(string folderPath, string name, Stream content, CancellationToken cancellationToken = default);

    Task<StorageItem> UpdateDocumentAsync(string externalId, Stream content, string? expectedETag = null, CancellationToken cancellationToken = default);

    Task DeleteDocumentAsync(string externalId, string? expectedETag = null, CancellationToken cancellationToken = default);

    Task<StorageItem> MoveDocumentAsync(string externalId, string targetFolderPath, string? expectedETag = null, CancellationToken cancellationToken = default);

    Task<StorageItem> CopyDocumentAsync(string externalId, string targetFolderPath, string? newName = null, CancellationToken cancellationToken = default);

    Task<StorageItem> RenameDocumentAsync(string externalId, string newName, string? expectedETag = null, CancellationToken cancellationToken = default);

    /// <summary>Opens the content stream. Caller owns and disposes the returned stream.</summary>
    Task<Stream> DownloadAsync(string externalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StorageVersion>> GetVersionsAsync(string externalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>Updates provider-side metadata. Akasha-managed metadata lives in the DMS and is not overwritten by this.</summary>
    Task<IReadOnlyDictionary<string, string>> UpdateMetadataAsync(string externalId, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoragePermission>> GetPermissionsAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>Incremental changes since <paramref name="cursor"/> (null = first sync). Requires <see cref="StorageCapability.ChangeTracking"/>.</summary>
    Task<StorageChangePage> GetChangesAsync(string? cursor, CancellationToken cancellationToken = default);
}

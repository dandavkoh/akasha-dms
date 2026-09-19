using System.Runtime.CompilerServices;

namespace Akasha.Application.Storage;

/// <summary>
/// Convenience base for connector implementations: every operation defaults to
/// <see cref="StorageErrorKind.UnsupportedOperation"/>. A connector overrides
/// exactly the operations matching its declared <see cref="Capabilities"/>;
/// forgetting to override then cannot silently corrupt anything.
/// </summary>
public abstract class StorageConnectorBase : IStorageConnector
{
    public abstract StorageCapability Capabilities { get; }

    public virtual Task<StorageItem> GetDocumentAsync(string externalId, CancellationToken cancellationToken = default) => NotSupported<StorageItem>();

    public virtual IAsyncEnumerable<StorageItem> ListDocumentsAsync(string? folderPath, CancellationToken cancellationToken = default) => NotSupportedEnumerable();

    public virtual Task<StorageItem> CreateDocumentAsync(string folderPath, string name, Stream content, CancellationToken cancellationToken = default) => NotSupported<StorageItem>();

    public virtual Task<StorageItem> UpdateDocumentAsync(string externalId, Stream content, string? expectedETag = null, CancellationToken cancellationToken = default) => NotSupported<StorageItem>();

    public virtual Task DeleteDocumentAsync(string externalId, string? expectedETag = null, CancellationToken cancellationToken = default) => NotSupportedTask();

    public virtual Task<StorageItem> MoveDocumentAsync(string externalId, string targetFolderPath, string? expectedETag = null, CancellationToken cancellationToken = default) => NotSupported<StorageItem>();

    public virtual Task<StorageItem> CopyDocumentAsync(string externalId, string targetFolderPath, string? newName = null, CancellationToken cancellationToken = default) => NotSupported<StorageItem>();

    public virtual Task<StorageItem> RenameDocumentAsync(string externalId, string newName, string? expectedETag = null, CancellationToken cancellationToken = default) => NotSupported<StorageItem>();

    public virtual Task<Stream> DownloadAsync(string externalId, CancellationToken cancellationToken = default) => NotSupported<Stream>();

    public virtual Task<IReadOnlyList<StorageVersion>> GetVersionsAsync(string externalId, CancellationToken cancellationToken = default) => NotSupported<IReadOnlyList<StorageVersion>>();

    public virtual Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(string externalId, CancellationToken cancellationToken = default) => NotSupported<IReadOnlyDictionary<string, string>>();

    public virtual Task<IReadOnlyDictionary<string, string>> UpdateMetadataAsync(string externalId, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => NotSupported<IReadOnlyDictionary<string, string>>();

    public virtual Task<IReadOnlyList<StoragePermission>> GetPermissionsAsync(string externalId, CancellationToken cancellationToken = default) => NotSupported<IReadOnlyList<StoragePermission>>();

    public virtual Task<StorageChangePage> GetChangesAsync(string? cursor, CancellationToken cancellationToken = default) => NotSupported<StorageChangePage>();

    private static string Caller([CallerMemberName] string? name = null) => name ?? "operation";

    private static StorageException Unsupported(string operation)
        => new(StorageErrorKind.UnsupportedOperation, $"{operation} is not supported by this connector.", isTransient: false);

    private static Task NotSupportedTask() => Task.FromException(Unsupported(Caller()));

    private static Task<T> NotSupported<T>() => Task.FromException<T>(Unsupported(Caller()));

    private static async IAsyncEnumerable<StorageItem> NotSupportedEnumerable()
    {
        await NotSupportedTask();
#pragma warning disable CS0162 // unreachable: keeps the iterator shape
        yield break;
#pragma warning restore CS0162
    }
}

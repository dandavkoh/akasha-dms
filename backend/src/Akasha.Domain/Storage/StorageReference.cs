namespace Akasha.Domain.Storage;

/// <summary>
/// Points from a logical document to its current physical representation in
/// an external storage system. Never identifies a logical document by itself:
/// path and name change on external rename/move, the stable identity is
/// (ConnectorId, ExternalId) where the provider offers stable external IDs.
/// </summary>
public sealed class StorageReference
{
    public const int MaxExternalIdLength = 512;
    public const int MaxPathLength = 2048;
    public const int MaxNameLength = 512;
    public const int MaxVersionLength = 256;
    public const int MaxETagLength = 256;

    private StorageReference()
    {
    }

    public StorageReference(
        Guid connectorId,
        string externalId,
        string path,
        string name,
        string? version = null,
        string? eTag = null,
        DateTimeOffset? lastKnownModifiedAtUtc = null)
    {
        if (connectorId == Guid.Empty)
        {
            throw new ArgumentException("Connector id must not be empty.", nameof(connectorId));
        }

        ExternalId = Check(externalId, MaxExternalIdLength, nameof(externalId));
        Path = Check(path, MaxPathLength, nameof(path));
        Name = Check(name, MaxNameLength, nameof(name));
        ConnectorId = connectorId;
        Version = version is null ? null : Check(version, MaxVersionLength, nameof(version));
        ETag = eTag is null ? null : Check(eTag, MaxETagLength, nameof(eTag));
        LastKnownModifiedAtUtc = lastKnownModifiedAtUtc;
        SyncState = StorageSyncState.Pending;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid ConnectorId { get; private set; }

    /// <summary>Stable provider-side object identity (drive item id, inode...). Strongest link to the physical object.</summary>
    public string ExternalId { get; private set; } = string.Empty;

    /// <summary>Current folder path within the connector root. Not an identity.</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>Current file name. Not an identity.</summary>
    public string Name { get; private set; } = string.Empty;

    public string? Version { get; private set; }

    public string? ETag { get; private set; }

    public DateTimeOffset? LastKnownModifiedAtUtc { get; private set; }

    public StorageSyncState SyncState { get; private set; }

    /// <summary>External rename/move: physical location changed, identity (ConnectorId + ExternalId) stays.</summary>
    public void RecordRelocation(string newPath, string newName)
    {
        Path = Check(newPath, MaxPathLength, nameof(newPath));
        Name = Check(newName, MaxNameLength, nameof(newName));
    }

    /// <summary>Records the newest known version/concurrency tokens of the physical object.</summary>
    public void RecordVersion(string? version, string? eTag, DateTimeOffset? modifiedAtUtc)
    {
        Version = version is null ? null : Check(version, MaxVersionLength, nameof(version));
        ETag = eTag is null ? null : Check(eTag, MaxETagLength, nameof(eTag));
        LastKnownModifiedAtUtc = modifiedAtUtc;
    }

    public void MarkAvailable() => SyncState = StorageSyncState.Available;

    /// <summary>Physical file externally deleted; logical document and history survive.</summary>
    public void MarkMissing() => SyncState = StorageSyncState.Missing;

    public void MarkUnavailable() => SyncState = StorageSyncState.Unavailable;

    public void MarkSyncError() => SyncState = StorageSyncState.SyncError;

    public void MarkAuthenticationRequired() => SyncState = StorageSyncState.AuthenticationRequired;

    private static string Check(string value, int maxLength, string param)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentException($"Value is required (max {maxLength} chars).", param);
        }

        return value;
    }
}

namespace Akasha.Application.Storage;

/// <summary>
/// Declares what a storage connector can do. Core logic asks
/// <c>connector.Capabilities.HasFlag(StorageCapability.Move)</c>
/// instead of checking provider names.
/// </summary>
[Flags]
public enum StorageCapability
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Delete = 1 << 2,
    Move = 1 << 3,
    Copy = 1 << 4,
    Rename = 1 << 5,
    Versioning = 1 << 6,
    Metadata = 1 << 7,
    Permissions = 1 << 8,
    ChangeTracking = 1 << 9,
    WatchChanges = 1 << 10,
    Locking = 1 << 11,
    RecycleBin = 1 << 12,
    NativeSearch = 1 << 13,
}

public static class StorageCapabilityExtensions
{
    public static bool Supports(this IStorageConnector connector, StorageCapability capability)
        => (connector.Capabilities & capability) == capability;

    /// <summary>Throws <see cref="StorageException"/> (UnsupportedOperation) when the connector lacks the capability.</summary>
    public static void Require(this IStorageConnector connector, StorageCapability capability)
    {
        if (!connector.Supports(capability))
        {
            throw new StorageException(
                StorageErrorKind.UnsupportedOperation,
                $"The connector does not support the '{capability}' capability.",
                isTransient: false);
        }
    }
}

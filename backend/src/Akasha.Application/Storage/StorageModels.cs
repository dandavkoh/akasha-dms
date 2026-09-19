namespace Akasha.Application.Storage;

/// <summary>
/// Normalized description of one physical storage object, as returned by a
/// connector. Vendor-neutral on purpose: no Graph/Dropbox/SMB types here.
/// </summary>
public sealed record StorageItem
{
    /// <summary>Stable provider-side identity; strongest link across rename/move. May equal the path on weak providers.</summary>
    public required string ExternalId { get; init; }

    public required string Path { get; init; }

    public required string Name { get; init; }

    public long Size { get; init; }

    public string? ContentType { get; init; }

    public string? Version { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? ModifiedAtUtc { get; init; }
}

/// <summary>One provider-side version of a storage object. Semantics stay connector-specific.</summary>
public sealed record StorageVersion
{
    public required string VersionId { get; init; }

    public long Size { get; init; }

    public DateTimeOffset? ModifiedAtUtc { get; init; }
}

/// <summary>Effective permission of the underlying storage system for one principal.</summary>
public sealed record StoragePermission
{
    public required string PrincipalId { get; init; }

    public string? PrincipalName { get; init; }

    /// <summary>Normalized rights, e.g. "Read", "Write", "FullControl".</summary>
    public required IReadOnlyList<string> Rights { get; init; }

    public bool IsInherited { get; init; }
}

public enum StorageChangeType
{
    Created = 0,
    Modified = 1,
    Renamed = 2,
    Moved = 3,
    Deleted = 4,
    Restored = 5,
}

/// <summary>
/// One normalized external change. Identity resolution must prefer
/// <see cref="ExternalId"/> over path + name (backend AGENTS #101).
/// </summary>
public sealed record StorageChange
{
    public required StorageChangeType Type { get; init; }

    public required string ExternalId { get; init; }

    /// <summary>Current path after the change (null for Deleted when unknown).</summary>
    public string? Path { get; init; }

    /// <summary>Path before a rename/move, when the provider reports it.</summary>
    public string? PreviousPath { get; init; }

    public string? Name { get; init; }

    public string? PreviousName { get; init; }

    public string? Version { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? ModifiedAtUtc { get; init; }

    /// <summary>Stable provider change id for idempotency, when available.</summary>
    public string? ChangeId { get; init; }
}

/// <summary>
/// Incremental change batch plus the opaque cursor (delta token / change token)
/// the connector needs to resume. Tenant- and connector-specific.
/// </summary>
public sealed record StorageChangePage
{
    public required IReadOnlyList<StorageChange> Changes { get; init; }

    /// <summary>Cursor for the next poll; null = no incremental tracking.</summary>
    public string? NextCursor { get; init; }

    /// <summary>True when the provider demands a full rescan (token expired etc.).</summary>
    public bool RequiresFullRescan { get; init; }
}

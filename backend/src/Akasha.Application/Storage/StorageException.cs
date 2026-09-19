namespace Akasha.Application.Storage;

/// <summary>
/// Normalized storage failure categories. Connectors map provider-specific
/// exceptions to these kinds; raw provider exceptions never escape the
/// connector (backend AGENTS #59, #99).
/// </summary>
public enum StorageErrorKind
{
    /// <summary>The physical object does not exist.</summary>
    NotFound = 0,
    /// <summary>The storage system denied access. Akasha must never bypass this.</summary>
    AccessDenied = 1,
    /// <summary>Optimistic concurrency: expected ETag/version did not match the current one.</summary>
    ConcurrencyConflict = 2,
    /// <summary>Connector or provider unavailable, retryable.</summary>
    Unavailable = 3,
    /// <summary>Connector credentials expired or invalid; user action required.</summary>
    AuthenticationRequired = 4,
    /// <summary>Operation not supported by this connector (capability missing).</summary>
    UnsupportedOperation = 5,
    /// <summary>Invalid input (path traversal, invalid name, ...).</summary>
    Validation = 6,
    /// <summary>Provider-side conflict, e.g. destination already exists.</summary>
    Conflict = 7,
    /// <summary>Anything else; mapped from unclassified provider errors.</summary>
    Unknown = 8,
}

public sealed class StorageException : Exception
{
    public StorageException(StorageErrorKind kind, string message, bool isTransient, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        IsTransient = isTransient;
    }

    public StorageErrorKind Kind { get; }

    /// <summary>True for transient failures (network, outage, rate limit) that may be retried with backoff.</summary>
    public bool IsTransient { get; }
}

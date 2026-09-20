namespace Akasha.Domain.Documents;

/// <summary>
/// Processing status of a document (extraction, OCR, indexing...). Kept strictly
/// separate from the storage <c>SyncState</c>: a document can be physically Available
/// while its processing failed, and vice versa (backend rule #110).
/// </summary>
public enum ProcessingState
{
    /// <summary>No processing required or none performed yet.</summary>
    None = 0,
    /// <summary>Processing has been requested but not started.</summary>
    Pending = 1,
    /// <summary>Processing is currently running.</summary>
    Processing = 2,
    /// <summary>Processing finished successfully.</summary>
    Processed = 3,
    /// <summary>Processing failed; does NOT affect document availability.</summary>
    Failed = 4,
}

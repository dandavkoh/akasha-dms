using Akasha.Application.Documents;
using Akasha.Domain.Documents;
using Akasha.Domain.Storage;

namespace Akasha.Application.Search;

/// <summary>Sortable columns of a document search. Relevance applies when a query text is given.</summary>
public enum DocumentSearchSort
{
    Relevance = 0,
    Name = 1,
    Created = 2,
    Updated = 3,
}

/// <summary>
/// A document search request. All filters combine with AND; <see cref="Query"/> matches the
/// document name and text/selection metadata values (case-insensitive substring).
/// ponytail: full-text/OCR content comes from a future search index (#44); semantic/vector
/// search plugs in behind this same service contract later (#45).
/// </summary>
public sealed record DocumentSearchRequest
{
    /// <summary>Free text: document name or text-like metadata value (case-insensitive contains).</summary>
    public string? Query { get; init; }

    /// <summary>Only documents linked to this Akte (via logical DocumentId).</summary>
    public Guid? AkteId { get; init; }

    /// <summary>File extension filter, e.g. "pdf" (case-insensitive).</summary>
    public string? DocumentType { get; init; }

    /// <summary>Metadata definition key to filter by (requires <see cref="MetadataValue"/>).</summary>
    public string? MetadataKey { get; init; }

    /// <summary>
    /// Value for <see cref="MetadataKey"/>. Compared typed according to the definition's
    /// field type: contains for Text, equality for the other scalar types and Selection,
    /// element match for MultiSelection. An unknown key matches nothing (never leaks).
    /// </summary>
    public string? MetadataValue { get; init; }

    /// <summary>Inclusive lower bound of the document creation timestamp (UTC).</summary>
    public DateTimeOffset? CreatedFrom { get; init; }

    /// <summary>Inclusive upper bound of the document creation timestamp (UTC).</summary>
    public DateTimeOffset? CreatedTo { get; init; }

    /// <summary>Inclusive lower bound of the last document update (UTC).</summary>
    public DateTimeOffset? UpdatedFrom { get; init; }

    /// <summary>Inclusive upper bound of the last document update (UTC).</summary>
    public DateTimeOffset? UpdatedTo { get; init; }

    /// <summary>Filter by storage synchronization/availability state.</summary>
    public StorageSyncState? State { get; init; }

    public DocumentSearchSort Sort { get; init; } = DocumentSearchSort.Relevance;

    public bool Descending { get; init; } = true;

    public int Skip { get; init; }

    public int Take { get; init; } = 50;
}

/// <summary>
/// One search hit. Deliberately exposes NO storage path/location and no metadata content:
/// a hit must never reveal information about a document the caller may not read (rules #81/#82).
/// </summary>
public sealed record DocumentSearchHit(
    Guid Id,
    string Name,
    StorageSyncState State,
    ProcessingState Processing,
    Guid ConnectorId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    /// <summary>Relevance rank (3 exact name, 2 name prefix, 1 name substring/metadata). Null when sorted by a column.</summary>
    int? Score);

/// <summary>
/// Central server-side document search. Every query is executed with the caller's tenant
/// and authorization context folded into the query itself (rules #44, #81): only documents
/// the current identity may READ are searched, so a search can never reveal the existence
/// of an inaccessible document - not even via result count.
/// </summary>
public interface ISearchService
{
    Task<PagedResult<DocumentSearchHit>> SearchDocumentsAsync(DocumentSearchRequest request, CancellationToken cancellationToken = default);
}

using Akasha.Application.Documents;
using Akasha.Application.Search;
using Microsoft.AspNetCore.Mvc;

namespace Akasha.Api.Controllers;

/// <summary>
/// Thin REST shell for document search. Tenant isolation, authorization and the open-text
/// permission predicate all live in <see cref="ISearchService"/> - this controller only
/// binds query parameters.
/// </summary>
[ApiController]
[Route("api/v1/search")]
public sealed class SearchController(ISearchService search) : ControllerBase
{
    [HttpGet("documents")]
    [ProducesResponseType(typeof(PagedResult<DocumentSearchHit>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<PagedResult<DocumentSearchHit>> SearchDocuments(
        [FromQuery] string? query,
        [FromQuery] Guid? akteId,
        [FromQuery] string? documentType,
        [FromQuery] string? metadataKey,
        [FromQuery] string? metadataValue,
        [FromQuery] DateTimeOffset? createdFrom,
        [FromQuery] DateTimeOffset? createdTo,
        [FromQuery] DateTimeOffset? updatedFrom,
        [FromQuery] DateTimeOffset? updatedTo,
        [FromQuery] Akasha.Domain.Storage.StorageSyncState? state,
        [FromQuery] DocumentSearchSort sort = DocumentSearchSort.Relevance,
        [FromQuery] bool descending = true,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
        => search.SearchDocumentsAsync(new DocumentSearchRequest
        {
            Query = query,
            AkteId = akteId,
            DocumentType = documentType,
            MetadataKey = metadataKey,
            MetadataValue = metadataValue,
            CreatedFrom = createdFrom,
            CreatedTo = createdTo,
            UpdatedFrom = updatedFrom,
            UpdatedTo = updatedTo,
            State = state,
            Sort = sort,
            Descending = descending,
            Skip = skip,
            Take = take,
        }, cancellationToken);
}

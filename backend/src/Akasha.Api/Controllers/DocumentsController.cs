using Akasha.Application.Documents;
using Microsoft.AspNetCore.Mvc;

namespace Akasha.Api.Controllers;

/// <summary>
/// Thin REST shell for logical documents. All tenant resolution, authorization and
/// business rules live in <see cref="IDocumentService"/>. Content (upload/download)
/// operations are served by the storage connector stack, not this controller.
/// </summary>
[ApiController]
[Route("api/v1/documents")]
public sealed class DocumentsController(IDocumentService documents) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<DocumentView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<PagedResult<DocumentView>> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
        => documents.ListAsync(skip, take, cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DocumentView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<DocumentView> Get(Guid id, CancellationToken cancellationToken)
        => documents.GetAsync(id, cancellationToken);

    [HttpPost]
    [ProducesResponseType(typeof(DocumentView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateDocumentRequest request, CancellationToken cancellationToken)
    {
        var document = await documents.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = document.Id }, document);
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(DocumentView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<DocumentView> Rename(Guid id, [FromBody] RenameDocumentRequest request, CancellationToken cancellationToken)
        => documents.RenameAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await documents.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}

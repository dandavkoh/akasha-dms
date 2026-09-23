using Akasha.Application.Documents;
using Microsoft.AspNetCore.Mvc;

namespace Akasha.Api.Controllers;

/// <summary>
/// Thin REST shell for logical documents. All tenant resolution, authorization and
/// business rules live in <see cref="IDocumentService"/>; content endpoints stream
/// through the storage connector stack, never through the database.
/// </summary>
[ApiController]
[Route("api/v1/documents")]
public sealed class DocumentsController(IDocumentService documents) : ControllerBase
{
    private const long MaxUploadBytes = 512L * 1024 * 1024;
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

    /// <summary>Uploads a new file (raw request body) and registers it as a logical document.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(DocumentView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Upload(
        [FromQuery] Guid connectorId,
        [FromQuery] string folder,
        [FromQuery] string name,
        CancellationToken cancellationToken)
    {
        var document = await documents.UploadContentAsync(
            new UploadDocumentRequest(connectorId, folder, name), Request.Body, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = document.Id }, document);
    }

    /// <summary>Streams the physical file. Authorization runs before any byte is read.</summary>
    [HttpGet("{id:guid}/content")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var content = await documents.DownloadContentAsync(id, cancellationToken);
        return File(content.Content, content.ContentType ?? "application/octet-stream", content.FileName);
    }

    /// <summary>Replaces the physical content. Optional <c>If-Match</c> ETag; otherwise the last known version must still match.</summary>
    [HttpPut("{id:guid}/content")]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(DocumentView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<DocumentView> UpdateContent(
        Guid id,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
        => await documents.UpdateContentAsync(id, new UpdateContentRequest(ifMatch), Request.Body, cancellationToken);

    /// <summary>Physically deletes the file; the logical document becomes Missing (history survives).</summary>
    [HttpDelete("{id:guid}/content")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteContent(
        Guid id,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        await documents.DeleteContentAsync(id, ifMatch, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/move")]
    [ProducesResponseType(typeof(DocumentView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<DocumentView> Move(Guid id, [FromBody] MoveDocumentRequest request, CancellationToken cancellationToken)
        => documents.MoveFileAsync(id, request, cancellationToken);

    [HttpPost("{id:guid}/copy")]
    [ProducesResponseType(typeof(DocumentView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Copy(Guid id, [FromBody] CopyDocumentRequest request, CancellationToken cancellationToken)
    {
        var copy = await documents.CopyFileAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = copy.Id }, copy);
    }

    /// <summary>Physically renames the file; the logical document keeps its identity.</summary>
    [HttpPost("{id:guid}/rename-file")]
    [ProducesResponseType(typeof(DocumentView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<DocumentView> RenameFile(Guid id, [FromBody] RenameFileRequest request, CancellationToken cancellationToken)
        => documents.RenameFileAsync(id, request, cancellationToken);
}

using Akasha.Application.Akten;
using Akasha.Application.Documents;
using Microsoft.AspNetCore.Mvc;

namespace Akasha.Api.Controllers;

/// <summary>
/// Thin REST shell for Akten (logical business containers). All tenant resolution,
/// authorization, lifecycle and membership rules live in <see cref="IAkteService"/>.
/// Membership references logical DocumentIds only, never storage paths.
/// </summary>
[ApiController]
[Route("api/v1/akten")]
public sealed class AktenController(IAkteService akten) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<AkteView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<PagedResult<AkteView>> List(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
        => akten.ListAsync(skip, take, cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AkteView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<AkteView> Get(Guid id, CancellationToken cancellationToken)
        => akten.GetAsync(id, cancellationToken);

    [HttpPost]
    [ProducesResponseType(typeof(AkteView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateAkteRequest request, CancellationToken cancellationToken)
    {
        var akte = await akten.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = akte.Id }, akte);
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(AkteView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<AkteView> Update(Guid id, [FromBody] UpdateAkteRequest request, CancellationToken cancellationToken)
        => akten.UpdateAsync(id, request, cancellationToken);

    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(typeof(AkteView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<AkteView> Archive(Guid id, [FromBody] AkteRowVersionRequest request, CancellationToken cancellationToken)
        => akten.ArchiveAsync(id, request.RowVersion, cancellationToken);

    [HttpPost("{id:guid}/reactivate")]
    [ProducesResponseType(typeof(AkteView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<AkteView> Reactivate(Guid id, [FromBody] AkteRowVersionRequest request, CancellationToken cancellationToken)
        => akten.ReactivateAsync(id, request.RowVersion, cancellationToken);

    [HttpPut("{id:guid}/documents/{documentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Attach(Guid id, Guid documentId, CancellationToken cancellationToken)
    {
        await akten.AttachAsync(id, documentId, cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id:guid}/documents/{documentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Detach(Guid id, Guid documentId, CancellationToken cancellationToken)
    {
        await akten.DetachAsync(id, documentId, cancellationToken);
        return NoContent();
    }
}

public sealed record AkteRowVersionRequest(uint RowVersion);

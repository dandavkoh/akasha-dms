using Akasha.Application.Documents;
using Akasha.Application.Metadata;
using Akasha.Domain.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Akasha.Api.Controllers;

/// <summary>
/// Thin REST shell for configurable metadata. Definitions are tenant configuration;
/// values belong to a document or Akte and inherit its authorization (Read / EditMetadata).
/// All rules live in <see cref="IMetadataService"/>.
/// </summary>
[ApiController]
[Route("api/v1/metadata")]
public sealed class MetadataController(IMetadataService metadata) : ControllerBase
{
    [HttpGet("definitions")]
    [ProducesResponseType(typeof(PagedResult<MetadataDefinitionView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<PagedResult<MetadataDefinitionView>> ListDefinitions(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
        => metadata.ListDefinitionsAsync(skip, take, cancellationToken);

    [HttpGet("definitions/{id:guid}")]
    [ProducesResponseType(typeof(MetadataDefinitionView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<MetadataDefinitionView> GetDefinition(Guid id, CancellationToken cancellationToken)
        => metadata.GetDefinitionAsync(id, cancellationToken);

    [HttpPost("definitions")]
    [ProducesResponseType(typeof(MetadataDefinitionView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateDefinition(
        [FromBody] CreateMetadataDefinitionRequest request, CancellationToken cancellationToken)
    {
        var definition = await metadata.CreateDefinitionAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetDefinition), new { id = definition.Id }, definition);
    }

    [HttpPatch("definitions/{id:guid}")]
    [ProducesResponseType(typeof(MetadataDefinitionView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<MetadataDefinitionView> UpdateDefinition(
        Guid id, [FromBody] UpdateMetadataDefinitionRequest request, CancellationToken cancellationToken)
        => metadata.UpdateDefinitionAsync(id, request, cancellationToken);

    [HttpDelete("definitions/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDefinition(Guid id, CancellationToken cancellationToken)
    {
        await metadata.DeleteDefinitionAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpGet("{owner:regex(^(documents|akten)$)}/{ownerId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyList<MetadataValueView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IReadOnlyList<MetadataValueView>> GetValues(
        string owner, Guid ownerId, CancellationToken cancellationToken)
        => metadata.GetValuesAsync(ParseOwner(owner), ownerId, cancellationToken);

    [HttpPut("{owner:regex(^(documents|akten)$)}/{ownerId:guid}/{key}")]
    [ProducesResponseType(typeof(MetadataValueView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<MetadataValueView> SetValue(
        string owner, Guid ownerId, string key, [FromBody] MetadataValueInput value, CancellationToken cancellationToken)
        => metadata.SetValueAsync(ParseOwner(owner), ownerId, key, value, cancellationToken);

    [HttpDelete("{owner:regex(^(documents|akten)$)}/{ownerId:guid}/{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveValue(
        string owner, Guid ownerId, string key, CancellationToken cancellationToken)
    {
        await metadata.RemoveValueAsync(ParseOwner(owner), ownerId, key, cancellationToken);
        return NoContent();
    }

    private static MetadataOwnerType ParseOwner(string owner)
        => owner == "documents" ? MetadataOwnerType.Document : MetadataOwnerType.Akte;
}

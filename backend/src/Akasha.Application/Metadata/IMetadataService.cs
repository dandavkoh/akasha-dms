using Akasha.Application.Documents;
using Akasha.Domain.Metadata;

namespace Akasha.Application.Metadata;

public sealed record MetadataDefinitionView(
    Guid Id,
    string Key,
    string? Label,
    MetadataFieldType FieldType,
    bool IsRequired,
    int? MaxLength,
    IReadOnlyList<string> Options,
    /// <summary>Optimistic concurrency token (PostgreSQL xmin); required for updates.</summary>
    uint RowVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateMetadataDefinitionRequest(
    string Key,
    MetadataFieldType FieldType,
    bool IsRequired = false,
    string? Label = null,
    int? MaxLength = null,
    IReadOnlyList<string>? Options = null);

public sealed record UpdateMetadataDefinitionRequest(
    string? Label,
    bool IsRequired,
    int? MaxLength,
    IReadOnlyList<string>? Options,
    uint RowVersion);

/// <summary>A value together with the definition snapshot needed to render/validate it.</summary>
public sealed record MetadataValueView(
    Guid DefinitionId,
    string Key,
    string? Label,
    MetadataFieldType FieldType,
    bool IsRequired,
    string? Text,
    decimal? Number,
    bool? Boolean,
    DateOnly? Date,
    DateTimeOffset? Timestamp,
    IReadOnlyList<string> Selection,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Configurable metadata for documents and Akten. System metadata (name, timestamps,
/// sync state) stays as typed columns on the entities; this service only manages
/// tenant-configured definitions and their values. Authorization: definitions are
/// tenant configuration (ResourceType.Metadata, Read/Write); values follow the owner
/// resource (Document/Akte: Read to read, EditMetadata to change). All access runs
/// under the tenant-filtered DbContext - cross-tenant owners surface as NotFound.
/// </summary>
public interface IMetadataService
{
    Task<PagedResult<MetadataDefinitionView>> ListDefinitionsAsync(
        int skip, int take, CancellationToken cancellationToken = default);

    Task<MetadataDefinitionView> GetDefinitionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<MetadataDefinitionView> CreateDefinitionAsync(
        CreateMetadataDefinitionRequest request, CancellationToken cancellationToken = default);

    Task<MetadataDefinitionView> UpdateDefinitionAsync(
        Guid id, UpdateMetadataDefinitionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the definition and cascades to all its values.</summary>
    Task DeleteDefinitionAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>All configured values of one owner (unknown owner =&gt; NotFound).</summary>
    Task<IReadOnlyList<MetadataValueView>> GetValuesAsync(
        MetadataOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the value of one field. Validated server-side against the definition.</summary>
    Task<MetadataValueView> SetValueAsync(
        MetadataOwnerType ownerType,
        Guid ownerId,
        string definitionKey,
        MetadataValueInput value,
        CancellationToken cancellationToken = default);

    Task RemoveValueAsync(
        MetadataOwnerType ownerType, Guid ownerId, string definitionKey, CancellationToken cancellationToken = default);
}

using Akasha.Domain.Common;

namespace Akasha.Domain.Metadata;

/// <summary>
/// One metadata value of a document or Akte. Values live in typed relational columns
/// (exactly one set, matching the definition's field type) so they stay queryable and
/// indexable - no opaque JSON blobs (backend rule #41). The owning row is identified by
/// (<see cref="OwnerType"/>, <see cref="OwnerId"/>, <see cref="DefinitionId"/>); the
/// owner reference is polymorphic by design (Document or Akte), the service verifies
/// owner existence under the tenant filter.
/// </summary>
public sealed class MetadataValue : ITenantScoped
{
    private List<string> _selectionValues = [];

    private MetadataValue()
    {
    }

    public MetadataValue(Guid tenantId, MetadataOwnerType ownerType, Guid ownerId, Guid definitionId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        OwnerType = ownerType;
        OwnerId = ownerId;
        DefinitionId = definitionId;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    public MetadataOwnerType OwnerType { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid DefinitionId { get; private set; }

    public string? TextValue { get; private set; }

    public decimal? NumberValue { get; private set; }

    public bool? BooleanValue { get; private set; }

    public DateOnly? DateValue { get; private set; }

    public DateTimeOffset? TimestampValue { get; private set; }

    public IReadOnlyList<string> SelectionValues
    {
        get => _selectionValues;
        private set => _selectionValues = value.ToList();
    }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>
    /// Assigns a validated value. Callers MUST run <see cref="MetadataDefinition.Validate"/>
    /// first; this method only maps the input onto the matching typed column.
    /// </summary>
    public void Apply(MetadataDefinition definition, MetadataValueInput value)
    {
        TextValue = null;
        NumberValue = null;
        BooleanValue = null;
        DateValue = null;
        TimestampValue = null;
        _selectionValues = [];

        switch (definition.FieldType)
        {
            case MetadataFieldType.Text:
            case MetadataFieldType.Selection:
                TextValue = value.Text;
                break;
            case MetadataFieldType.Number:
                NumberValue = value.Number;
                break;
            case MetadataFieldType.Boolean:
                BooleanValue = value.Boolean;
                break;
            case MetadataFieldType.Date:
                DateValue = value.Date;
                break;
            case MetadataFieldType.DateTime:
                TimestampValue = value.Timestamp; // validated UTC
                break;
            case MetadataFieldType.MultiSelection:
                SelectionValues = value.Selection ?? [];
                break;
        }

        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}

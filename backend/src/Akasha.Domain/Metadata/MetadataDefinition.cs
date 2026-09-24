using Akasha.Domain.Common;

namespace Akasha.Domain.Metadata;

/// <summary>
/// A configurable metadata field of a tenant (e.g. "contract-number", "due-date").
/// Distinct from system metadata (name, timestamps, sync state...), which are typed
/// relational columns on the entities themselves. Definitions govern validation of
/// <see cref="MetadataValue"/>s; values are stored in typed relational columns, never
/// as opaque JSON (backend rule #41).
///
/// The <see cref="FieldType"/> is immutable: changing it would require migrating every
/// stored value. Recreate the field instead (documented limitation).
/// </summary>
public sealed class MetadataDefinition : ITenantScoped
{
    public const int MaxKeyLength = 100;
    public const int MaxLabelLength = 200;
    public const int MaxTextLength = 4000;
    public const int MaxOptions = 500;

    private List<string> _options = [];

    private MetadataDefinition()
    {
    }

    public MetadataDefinition(
        Guid tenantId,
        string key,
        MetadataFieldType fieldType,
        bool isRequired = false,
        string? label = null,
        int? maxLength = null,
        IReadOnlyList<string>? options = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        Key = CheckKey(key);
        FieldType = fieldType;
        IsRequired = isRequired;
        Label = CheckLabel(label);
        MaxLength = CheckMaxLength(fieldType, maxLength);
        Options = CheckOptionsList(fieldType, options);
        CreatedAtUtc = DateTimeOffset.UtcNow;
        UpdatedAtUtc = CreatedAtUtc;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    /// <summary>Stable, tenant-unique machine key (e.g. "contract-number").</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>Optional human-readable display name.</summary>
    public string? Label { get; private set; }

    public MetadataFieldType FieldType { get; private set; }

    /// <summary>
    /// A value for this field must not be empty when set. (Completeness enforcement
    /// across all required fields belongs to future validation/workflow steps.)
    /// </summary>
    public bool IsRequired { get; private set; }

    /// <summary>Text-only length limit.</summary>
    public int? MaxLength { get; private set; }

    /// <summary>Allowed options for Selection/MultiSelection.</summary>
    public IReadOnlyList<string> Options
    {
        get => _options;
        private set => _options = value.ToList();
    }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Update(string? label, bool isRequired, int? maxLength, IReadOnlyList<string>? options)
    {
        Label = CheckLabel(label);
        IsRequired = isRequired;
        MaxLength = CheckMaxLength(FieldType, maxLength);
        Options = CheckOptionsList(FieldType, options);
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Validates a proposed value against this definition. Returns an error message or
    /// null when valid. Pure domain validation; the service translates errors into 400s.
    /// </summary>
    public string? Validate(MetadataValueInput value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var setMembers = new[]
        {
            value.Text is not null, value.Number is not null, value.Boolean is not null,
            value.Date is not null, value.Timestamp is not null, value.Selection is not null,
        }.Count(set => set);

        return FieldType switch
        {
            MetadataFieldType.Text when setMembers != 1 || value.Text is null
                => "Expected a text value.",
            MetadataFieldType.Text when string.IsNullOrWhiteSpace(value.Text) && IsRequired
                => $"Field '{Key}' is required.",
            MetadataFieldType.Text when value.Text!.Length > (MaxLength ?? MaxTextLength)
                => $"Text exceeds the maximum length of {MaxLength ?? MaxTextLength}.",
            MetadataFieldType.Number when setMembers != 1 || value.Number is null
                => "Expected a number value.",
            MetadataFieldType.Boolean when setMembers != 1 || value.Boolean is null
                => "Expected a boolean value.",
            MetadataFieldType.Date when setMembers != 1 || value.Date is null
                => "Expected a date value.",
            MetadataFieldType.DateTime when setMembers != 1 || value.Timestamp is null
                => "Expected a datetime value.",
            MetadataFieldType.DateTime when value.Timestamp!.Value.Offset != TimeSpan.Zero
                => "Datetime values must be UTC.",
            MetadataFieldType.Selection when setMembers != 1 || value.Text is null
                => "Expected a selection value.",
            MetadataFieldType.Selection when !_options.Contains(value.Text)
                => $"'{value.Text}' is not an allowed option for '{Key}'.",
            MetadataFieldType.MultiSelection when setMembers != 1 || value.Selection is null
                => "Expected a multi-selection value.",
            MetadataFieldType.MultiSelection when IsRequired && value.Selection!.Count == 0
                => $"Field '{Key}' is required.",
            MetadataFieldType.MultiSelection
                when value.Selection!.Distinct(StringComparer.Ordinal).Count() != value.Selection!.Count
                => "Multi-selection values must be distinct.",
            MetadataFieldType.MultiSelection when value.Selection!.Any(option => !_options.Contains(option))
                => $"A value is not an allowed option for '{Key}'.",
            _ => null,
        };
    }

    private static string CheckKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
        {
            throw new ArgumentException($"Key is required (max {MaxKeyLength} chars).", nameof(key));
        }

        return key.Trim();
    }

    private static string? CheckLabel(string? label)
    {
        if (label is not null && label.Length > MaxLabelLength)
        {
            throw new ArgumentException($"Label is at most {MaxLabelLength} chars.", nameof(label));
        }

        return label;
    }

    private static int? CheckMaxLength(MetadataFieldType fieldType, int? maxLength)
    {
        if (maxLength is not null && (fieldType != MetadataFieldType.Text || maxLength is < 1 or > MaxTextLength))
        {
            throw new ArgumentException(
                $"MaxLength only applies to text fields (1..{MaxTextLength}).", nameof(maxLength));
        }

        return maxLength;
    }

    private static List<string> CheckOptionsList(MetadataFieldType fieldType, IReadOnlyList<string>? options)
    {
        var requiresOptions = fieldType is MetadataFieldType.Selection or MetadataFieldType.MultiSelection;

        if (!requiresOptions)
        {
            return options is { Count: > 0 }
                ? throw new ArgumentException("Options only apply to selection fields.", nameof(options))
                : [];
        }

        if (options is null || options.Count == 0 || options.Count > MaxOptions
            || options.Any(string.IsNullOrWhiteSpace)
            || options.Distinct(StringComparer.Ordinal).Count() != options.Count)
        {
            throw new ArgumentException(
                $"Selection fields require 1..{MaxOptions} distinct, non-empty options.", nameof(options));
        }

        return options.ToList();
    }
}

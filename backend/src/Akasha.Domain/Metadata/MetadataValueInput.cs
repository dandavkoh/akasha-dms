namespace Akasha.Domain.Metadata;

/// <summary>
/// A proposed metadata value. Exactly the member matching the definition's
/// <see cref="MetadataFieldType"/> must be set; all others must be null.
/// Selection uses <see cref="Text"/>, MultiSelection uses <see cref="Selection"/>.
/// </summary>
public sealed record MetadataValueInput(
    string? Text = null,
    decimal? Number = null,
    bool? Boolean = null,
    DateOnly? Date = null,
    DateTimeOffset? Timestamp = null,
    IReadOnlyList<string>? Selection = null);

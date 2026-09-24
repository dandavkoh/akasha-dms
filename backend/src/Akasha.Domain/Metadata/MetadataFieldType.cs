namespace Akasha.Domain.Metadata;

/// <summary>The value type of a configurable metadata field.</summary>
public enum MetadataFieldType
{
    Text = 0,
    Number = 1,
    Boolean = 2,
    /// <summary>Calendar date without time-of-day.</summary>
    Date = 3,
    /// <summary>Absolute point in time (UTC).</summary>
    DateTime = 4,
    /// <summary>Exactly one of the configured options.</summary>
    Selection = 5,
    /// <summary>Zero or more of the configured options.</summary>
    MultiSelection = 6,
}

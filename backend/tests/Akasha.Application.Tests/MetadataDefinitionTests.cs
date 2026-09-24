using Akasha.Domain.Metadata;

namespace Akasha.Application.Tests;

/// <summary>Pure domain validation for metadata definitions - no database involved.</summary>
public sealed class MetadataDefinitionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void Definition_Requires_Key_And_Valid_Shape()
    {
        Assert.Throws<ArgumentException>(() => new MetadataDefinition(Tenant, "", MetadataFieldType.Text));
        Assert.Throws<ArgumentException>(() => new MetadataDefinition(Tenant, "x", MetadataFieldType.Number, options: ["a"]));
        Assert.Throws<ArgumentException>(() => new MetadataDefinition(Tenant, "x", MetadataFieldType.Selection));
        Assert.Throws<ArgumentException>(() => new MetadataDefinition(Tenant, "x", MetadataFieldType.Selection, options: ["a", "a"]));
        Assert.Throws<ArgumentException>(() => new MetadataDefinition(Tenant, "x", MetadataFieldType.Number, maxLength: 10));
        Assert.Throws<ArgumentException>(() => new MetadataDefinition(Guid.Empty, "x", MetadataFieldType.Text));

        var definition = new MetadataDefinition(Tenant, "priority", MetadataFieldType.Selection, options: ["low", "high"]);
        Assert.Equal("priority", definition.Key);
        Assert.Equal(["low", "high"], definition.Options);
    }

    [Fact]
    public void Validate_Text_Enforces_Shape_Required_And_Length()
    {
        var definition = new MetadataDefinition(Tenant, "comment", MetadataFieldType.Text, isRequired: true, maxLength: 5);

        Assert.Null(definition.Validate(new MetadataValueInput(Text: "ok")));
        Assert.NotNull(definition.Validate(new MetadataValueInput(Text: "  "))); // required
        Assert.NotNull(definition.Validate(new MetadataValueInput(Text: "too long!")));
        Assert.NotNull(definition.Validate(new MetadataValueInput(Number: 1))); // wrong shape
        Assert.NotNull(definition.Validate(new MetadataValueInput(Text: "a", Number: 1))); // two members set
        Assert.NotNull(definition.Validate(new MetadataValueInput())); // nothing set
    }

    [Fact]
    public void Validate_Number_Boolean_Date_Datetime()
    {
        var number = new MetadataDefinition(Tenant, "amount", MetadataFieldType.Number);
        Assert.Null(number.Validate(new MetadataValueInput(Number: 12.5m)));
        Assert.NotNull(number.Validate(new MetadataValueInput(Text: "12")));

        var boolean = new MetadataDefinition(Tenant, "flag", MetadataFieldType.Boolean);
        Assert.Null(boolean.Validate(new MetadataValueInput(Boolean: false)));
        Assert.NotNull(boolean.Validate(new MetadataValueInput(Text: "true")));

        var date = new MetadataDefinition(Tenant, "due", MetadataFieldType.Date);
        Assert.Null(date.Validate(new MetadataValueInput(Date: new DateOnly(2026, 9, 23))));
        Assert.NotNull(date.Validate(new MetadataValueInput(Timestamp: DateTimeOffset.UtcNow)));

        var timestamp = new MetadataDefinition(Tenant, "seen", MetadataFieldType.DateTime);
        Assert.Null(timestamp.Validate(new MetadataValueInput(Timestamp: DateTimeOffset.UtcNow)));
        Assert.NotNull(timestamp.Validate(new MetadataValueInput(
            Timestamp: new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(2))))); // not UTC
        Assert.NotNull(timestamp.Validate(new MetadataValueInput(Date: new DateOnly(2026, 9, 23))));
    }

    [Fact]
    public void Validate_Selection_And_MultiSelection()
    {
        var selection = new MetadataDefinition(Tenant, "state", MetadataFieldType.Selection, options: ["open", "done"]);
        Assert.Null(selection.Validate(new MetadataValueInput(Text: "open")));
        Assert.NotNull(selection.Validate(new MetadataValueInput(Text: "bogus")));

        var multi = new MetadataDefinition(Tenant, "tags", MetadataFieldType.MultiSelection, isRequired: true, options: ["a", "b"]);
        Assert.Null(multi.Validate(new MetadataValueInput(Selection: ["a", "b"])));
        Assert.NotNull(multi.Validate(new MetadataValueInput(Selection: []))); // required
        Assert.NotNull(multi.Validate(new MetadataValueInput(Selection: ["a", "c"]))); // unknown option
        Assert.NotNull(multi.Validate(new MetadataValueInput(Selection: ["a", "a"]))); // duplicates
    }

    [Fact]
    public void Apply_Assigns_Exactly_One_Typed_Column()
    {
        var definition = new MetadataDefinition(Tenant, "amount", MetadataFieldType.Number);
        var value = new MetadataValue(Tenant, MetadataOwnerType.Document, Guid.NewGuid(), definition.Id);

        value.Apply(definition, new MetadataValueInput(Number: 42m));

        Assert.Equal(42m, value.NumberValue);
        Assert.Null(value.TextValue);
        Assert.Null(value.BooleanValue);
        Assert.Null(value.DateValue);
        Assert.Null(value.TimestampValue);
        Assert.Empty(value.SelectionValues);

        // Re-apply clears the previous type's column.
        var selection = new MetadataDefinition(Tenant, "state", MetadataFieldType.MultiSelection, options: ["x"]);
        value.Apply(selection, new MetadataValueInput(Selection: ["x"]));
        Assert.Null(value.NumberValue);
        Assert.Equal(["x"], value.SelectionValues);
    }
}

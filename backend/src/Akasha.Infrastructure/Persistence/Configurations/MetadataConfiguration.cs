using Akasha.Domain.Metadata;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class MetadataDefinitionConfiguration : IEntityTypeConfiguration<MetadataDefinition>
{
    public void Configure(EntityTypeBuilder<MetadataDefinition> builder)
    {
        builder.HasKey(definition => definition.Id);

        builder.Property(definition => definition.TenantId).IsRequired();

        builder.Property(definition => definition.Key)
            .HasMaxLength(MetadataDefinition.MaxKeyLength)
            .IsRequired();

        builder.Property(definition => definition.Label)
            .HasMaxLength(MetadataDefinition.MaxLabelLength);

        builder.Property(definition => definition.FieldType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(definition => definition.Options)
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(definition => definition.CreatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(definition => definition.UpdatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // The machine key is unique per tenant (#166).
        builder.HasIndex(definition => new { definition.TenantId, definition.Key }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(definition => definition.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}

internal sealed class MetadataValueConfiguration : IEntityTypeConfiguration<MetadataValue>
{
    public void Configure(EntityTypeBuilder<MetadataValue> builder)
    {
        builder.HasKey(value => value.Id);

        builder.Property(value => value.TenantId).IsRequired();
        builder.Property(value => value.OwnerId).IsRequired();
        builder.Property(value => value.DefinitionId).IsRequired();

        builder.Property(value => value.OwnerType)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        // Exactly one typed column per definition's field type; all stay queryable
        // relationally (#41). Typed, indexable search preparation without JSON blobs.
        builder.Property(value => value.TextValue)
            .HasMaxLength(MetadataDefinition.MaxTextLength);

        builder.Property(value => value.NumberValue)
            .HasColumnType("numeric");

        builder.Property(value => value.DateValue)
            .HasColumnType("date");

        builder.Property(value => value.TimestampValue)
            .HasColumnType("timestamp with time zone");

        builder.Property(value => value.SelectionValues)
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(value => value.UpdatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // One value per (owner, field) within the tenant.
        builder.HasIndex(value => new { value.TenantId, value.OwnerType, value.OwnerId, value.DefinitionId })
            .IsUnique();

        // Search/filter preparation: "all values of field X in this tenant".
        // ponytail: plain btree; selective per-type value indexes (or pg_trgm for text
        // LIKE) only when real query patterns demand them.
        builder.HasIndex(value => new { value.TenantId, value.DefinitionId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(value => value.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // The definition is the schema of the value: deleting it removes its values.
        builder.HasOne<MetadataDefinition>()
            .WithMany()
            .HasForeignKey(value => value.DefinitionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

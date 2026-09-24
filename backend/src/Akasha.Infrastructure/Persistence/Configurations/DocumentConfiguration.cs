using Akasha.Domain.Documents;
using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.HasKey(document => document.Id);

        builder.Property(document => document.TenantId).IsRequired();

        builder.Property(document => document.Name)
            .HasMaxLength(Document.MaxNameLength)
            .IsRequired();

        builder.Property(document => document.Processing)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(document => document.CreatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(document => document.UpdatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(document => new { document.TenantId, document.Name });

        // Trigram index so case-insensitive substring search (ILike '%q%') on the document
        // name uses an index instead of a sequential scan. Requires the pg_trgm extension.
        builder.HasIndex(document => document.Name)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(document => document.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(document => document.Storage)
            .WithOne()
            .HasForeignKey<StorageReference>("DocumentId")
            .OnDelete(DeleteBehavior.Cascade); // same lifetime as the document; audit survives

        builder.Navigation(document => document.Storage).IsRequired();

        // Optimistic concurrency via the PostgreSQL system column xmin.
        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}

internal sealed class StorageReferenceConfiguration : IEntityTypeConfiguration<StorageReference>
{
    public void Configure(EntityTypeBuilder<StorageReference> builder)
    {
        builder.HasKey(reference => reference.Id);

        builder.Property(reference => reference.TenantId).IsRequired();

        builder.Property(reference => reference.ConnectorId).IsRequired();

        builder.Property(reference => reference.ExternalId)
            .HasMaxLength(StorageReference.MaxExternalIdLength)
            .IsRequired();

        builder.Property(reference => reference.Path)
            .HasMaxLength(StorageReference.MaxPathLength)
            .IsRequired();

        builder.Property(reference => reference.Name)
            .HasMaxLength(StorageReference.MaxNameLength)
            .IsRequired();

        builder.Property(reference => reference.Version)
            .HasMaxLength(StorageReference.MaxVersionLength);

        builder.Property(reference => reference.ETag)
            .HasMaxLength(StorageReference.MaxETagLength);

        builder.Property(reference => reference.LastKnownModifiedAtUtc)
            .HasColumnType("timestamp with time zone");

        builder.Property(reference => reference.SyncState)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        // Strongest link to the physical object: unique per tenant (#166). When a connector
        // cannot supply a stable external id the connector degrades it (documented there),
        // never this column.
        builder.HasIndex(reference => new { reference.TenantId, reference.ConnectorId, reference.ExternalId })
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(reference => reference.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex("DocumentId").IsUnique();

        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}

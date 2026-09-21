using Akasha.Domain.Akten;
using Akasha.Domain.Documents;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class AkteConfiguration : IEntityTypeConfiguration<Akte>
{
    public void Configure(EntityTypeBuilder<Akte> builder)
    {
        builder.HasKey(akte => akte.Id);

        builder.Property(akte => akte.TenantId).IsRequired();

        builder.Property(akte => akte.Name)
            .HasMaxLength(Akte.MaxNameLength)
            .IsRequired();

        builder.Property(akte => akte.Description)
            .HasMaxLength(Akte.MaxDescriptionLength);

        builder.Property(akte => akte.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(akte => akte.CreatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(akte => akte.UpdatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(akte => new { akte.TenantId, akte.Name });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(akte => akte.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Optimistic concurrency via the PostgreSQL system column xmin.
        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}

internal sealed class AkteDocumentConfiguration : IEntityTypeConfiguration<AkteDocument>
{
    public void Configure(EntityTypeBuilder<AkteDocument> builder)
    {
        builder.HasKey(membership => membership.Id);

        builder.Property(membership => membership.TenantId).IsRequired();

        builder.Property(membership => membership.LinkedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // Same document linked twice to the same Akte is a conflict, not a second row (#166).
        builder.HasIndex(membership => new { membership.TenantId, membership.AkteId, membership.DocumentId })
            .IsUnique();

        builder.HasIndex(membership => new { membership.TenantId, membership.DocumentId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(membership => membership.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Memberships share the lifetime of both ends: deleting the Akte or the logical
        // document removes the link. Only the physical storage file is unaffected.
        builder.HasOne<Akte>()
            .WithMany()
            .HasForeignKey(membership => membership.AkteId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(membership => membership.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

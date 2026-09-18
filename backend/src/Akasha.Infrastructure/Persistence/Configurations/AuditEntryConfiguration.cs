using Akasha.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.TenantId).IsRequired();

        builder.Property(entry => entry.Action)
            .HasMaxLength(AuditEntry.MaxActionLength)
            .IsRequired();

        builder.Property(entry => entry.ActorSubject).HasMaxLength(200);

        builder.Property(entry => entry.OccurredAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(entry => new { entry.TenantId, entry.OccurredAtUtc });

        // Audit entries survive their tenant-related records; never cascade.
        builder.HasOne<Akasha.Domain.Tenants.Tenant>()
            .WithMany()
            .HasForeignKey(entry => entry.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

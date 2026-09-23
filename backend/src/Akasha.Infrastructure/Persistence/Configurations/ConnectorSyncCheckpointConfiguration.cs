using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class ConnectorSyncCheckpointConfiguration : IEntityTypeConfiguration<ConnectorSyncCheckpoint>
{
    public void Configure(EntityTypeBuilder<ConnectorSyncCheckpoint> builder)
    {
        builder.HasKey(checkpoint => checkpoint.Id);

        builder.Property(checkpoint => checkpoint.TenantId).IsRequired();

        builder.Property(checkpoint => checkpoint.ConnectorId).IsRequired();

        builder.Property(checkpoint => checkpoint.Cursor)
            .HasMaxLength(ConnectorSyncCheckpoint.MaxCursorLength);

        builder.Property(checkpoint => checkpoint.UpdatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // One resume point per (tenant, connector) pair (#103, #166).
        builder.HasIndex(checkpoint => new { checkpoint.TenantId, checkpoint.ConnectorId })
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(checkpoint => checkpoint.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

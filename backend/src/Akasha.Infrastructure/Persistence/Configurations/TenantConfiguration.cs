using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasKey(tenant => tenant.Id);

        builder.Property(tenant => tenant.Name)
            .HasMaxLength(Tenant.MaxNameLength)
            .IsRequired();

        builder.Property(tenant => tenant.Slug)
            .HasMaxLength(Tenant.MaxSlugLength)
            .IsRequired();

        builder.HasIndex(tenant => tenant.Slug).IsUnique();

        builder.Property(tenant => tenant.CreatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // Optimistic concurrency via the PostgreSQL system column xmin:
        // any row update increments it, so a SaveChanges from a stale snapshot fails.
        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}

using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> builder)
    {
        builder.HasKey(membership => membership.Id);

        builder.Property(membership => membership.TenantId).IsRequired();

        builder.Property(membership => membership.Subject)
            .HasMaxLength(TenantMembership.MaxSubjectLength)
            .IsRequired();

        builder.Property(membership => membership.CreatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // One membership per (tenant, subject); lookup of memberships by subject is the
        // tenant-resolution hot path.
        builder.HasIndex(membership => new { membership.TenantId, membership.Subject }).IsUnique();
        builder.HasIndex(membership => membership.Subject);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(membership => membership.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

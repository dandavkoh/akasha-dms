using Akasha.Domain.Identity;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(user => user.Id);

        builder.Property(user => user.TenantId).IsRequired();

        builder.Property(user => user.DisplayName)
            .HasMaxLength(User.MaxDisplayNameLength)
            .IsRequired();

        builder.Property(user => user.Kind)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(user => user.IsActive).IsRequired();

        builder.Property(user => user.CreatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // Optimistic concurrency via the PostgreSQL system column xmin (see TenantConfiguration).
        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .IsRowVersion();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(user => user.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

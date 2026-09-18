using Akasha.Domain.Identity;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class UserIdentityConfiguration : IEntityTypeConfiguration<UserIdentity>
{
    public void Configure(EntityTypeBuilder<UserIdentity> builder)
    {
        builder.HasKey(identity => identity.Id);

        builder.Property(identity => identity.TenantId).IsRequired();
        builder.Property(identity => identity.UserId).IsRequired();

        builder.Property(identity => identity.Provider)
            .HasMaxLength(UserIdentity.MaxProviderLength)
            .IsRequired();

        builder.Property(identity => identity.ExternalSubject)
            .HasMaxLength(UserIdentity.MaxExternalSubjectLength)
            .IsRequired();

        builder.Property(identity => identity.CreatedAtUtc)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // Natural key: one link per (provider, subject, user). The same external subject
        // may have a user in several tenants; resolution by (provider, subject) is the hot path.
        builder.HasIndex(identity => new { identity.Provider, identity.ExternalSubject, identity.UserId }).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(identity => identity.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

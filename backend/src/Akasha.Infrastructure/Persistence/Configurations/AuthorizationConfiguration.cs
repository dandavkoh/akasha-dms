using Akasha.Domain.Authorization;
using Akasha.Domain.Identity;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Akasha.Infrastructure.Persistence.Configurations;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.HasKey(role => role.Id);

        builder.Property(role => role.TenantId).IsRequired();

        builder.Property(role => role.Name)
            .HasMaxLength(Role.MaxNameLength)
            .IsRequired();

        builder.HasIndex(role => new { role.TenantId, role.Name }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(role => role.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.HasKey(group => group.Id);

        builder.Property(group => group.TenantId).IsRequired();

        builder.Property(group => group.Name)
            .HasMaxLength(Group.MaxNameLength)
            .IsRequired();

        builder.HasIndex(group => new { group.TenantId, group.Name }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(group => group.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GroupMemberConfiguration : IEntityTypeConfiguration<GroupMember>
{
    public void Configure(EntityTypeBuilder<GroupMember> builder)
    {
        builder.HasKey(member => new { member.GroupId, member.UserId });

        builder.Property(member => member.TenantId).IsRequired();

        builder.HasIndex(member => new { member.TenantId, member.UserId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(member => member.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Group>()
            .WithMany()
            .HasForeignKey(member => member.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(member => member.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.HasKey(assignment => new { assignment.RoleId, assignment.PrincipalType, assignment.PrincipalId });

        builder.Property(assignment => assignment.TenantId).IsRequired();

        builder.Property(assignment => assignment.PrincipalType)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.HasIndex(assignment => new { assignment.TenantId, assignment.PrincipalType, assignment.PrincipalId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(assignment => assignment.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(assignment => assignment.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PermissionGrantConfiguration : IEntityTypeConfiguration<PermissionGrant>
{
    public void Configure(EntityTypeBuilder<PermissionGrant> builder)
    {
        builder.HasKey(grant => grant.Id);

        builder.Property(grant => grant.TenantId).IsRequired();

        builder.Property(grant => grant.PrincipalType)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(grant => grant.ResourceType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(grant => grant.Action)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(grant => grant.Effect)
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsRequired();

        builder.HasIndex(grant => new { grant.TenantId, grant.ResourceType, grant.ResourceId, grant.Action });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(grant => grant.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

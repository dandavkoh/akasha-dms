using System.Linq.Expressions;
using Akasha.Application.Tenancy;
using Akasha.Domain.Audit;
using Akasha.Domain.Authorization;
using Akasha.Domain.Common;
using Akasha.Domain.Documents;
using Akasha.Domain.Identity;
using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Persistence;

/// <summary>
/// Enforces tenant isolation:
/// - global query filters restrict every <see cref="ITenantScoped"/> entity to the current tenant
///   (an unresolved tenant context yields an empty tenant id, so queries return nothing);
/// - SaveChanges refuses any tracked tenant-scoped entity that does not belong to the
///   current tenant, blocking cross-tenant writes/deletes even for entities attached
///   without going through a filtered query.
/// </summary>
public sealed class AkashaDbContext(DbContextOptions<AkashaDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    private readonly ITenantContext _tenantContext = tenantContext;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();

    public DbSet<PermissionGrant> PermissionGrants => Set<PermissionGrant>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<StorageReference> StorageReferences => Set<StorageReference>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AkashaDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                entityType.SetQueryFilter(BuildTenantFilter(entityType.ClrType));
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardTenantWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardTenantWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardTenantWrites()
    {
        var pending = ChangeTracker.Entries<ITenantScoped>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        if (!_tenantContext.HasTenant)
        {
            throw new MissingTenantContextException();
        }

        foreach (var entry in pending)
        {
            if (entry.Entity.TenantId != _tenantContext.TenantId)
            {
                throw new TenantIsolationViolationException(
                    $"Entity {entry.Entity.GetType().Name} belongs to tenant {entry.Entity.TenantId}, " +
                    $"but the current tenant context is {_tenantContext.TenantId}.");
            }
        }
    }

    private LambdaExpression BuildTenantFilter(Type entityType)
    {
        var parameter = Expression.Parameter(entityType, "entity");
        var body = Expression.Equal(
            Expression.Property(parameter, nameof(ITenantScoped.TenantId)),
            // Reference the context instance (not the tenant context object): EF Core
            // parameterizes per DbContext instance, so this survives model caching.
            Expression.Property(
                Expression.Field(Expression.Constant(this), nameof(_tenantContext)),
                nameof(ITenantContext.TenantId)));
        return Expression.Lambda(body, parameter);
    }
}

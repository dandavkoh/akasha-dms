using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Persistence;

public sealed class AkashaDbContext(DbContextOptions<AkashaDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AkashaDbContext).Assembly);
}

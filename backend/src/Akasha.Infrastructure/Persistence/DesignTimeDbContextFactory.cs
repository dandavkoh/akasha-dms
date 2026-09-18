using Akasha.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Akasha.Infrastructure.Persistence;

/// <summary>
/// Used by dotnet-ef tooling. The connection string is irrelevant for
/// migration generation; set Database__ConnectionString to target a real DB.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AkashaDbContext>
{
    public AkashaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AkashaDbContext>()
            .UseAkashaNpgsql(
                Environment.GetEnvironmentVariable("Database__ConnectionString")
                    ?? "Host=localhost;Database=akasha_design_time",
                30)
            .Options;

        return new AkashaDbContext(options, new DesignTimeTenantContext());
    }

    /// <summary>Migration tooling never executes queries, so a never-established context is safe here.</summary>
    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public bool HasTenant => false;

        public Guid TenantId => Guid.Empty;

        public void Establish(Guid tenantId)
            => throw new NotSupportedException("Design-time context cannot establish a tenant.");

        public Guid Require() => throw new NotSupportedException("Design-time context has no tenant.");
    }
}

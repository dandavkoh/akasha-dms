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

        return new AkashaDbContext(options);
    }
}

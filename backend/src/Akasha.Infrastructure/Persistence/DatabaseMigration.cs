using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Akasha.Infrastructure.Persistence;

public static class DatabaseMigration
{
    /// <summary>
    /// Applies all pending EF Core migrations. Safe to run on application startup
    /// (idempotent) and works offline, since migrations ship with the application.
    /// </summary>
    public static async Task MigrateAkashaDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }
}

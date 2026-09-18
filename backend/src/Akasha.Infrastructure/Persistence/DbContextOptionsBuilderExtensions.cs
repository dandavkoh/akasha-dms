using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Persistence;

public static class DbContextOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder UseAkashaNpgsql(
        this DbContextOptionsBuilder options,
        string connectionString,
        int commandTimeoutSeconds)
        => options
            .UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(commandTimeoutSeconds))
            .UseSnakeCaseNamingConvention();

    public static DbContextOptionsBuilder<T> UseAkashaNpgsql<T>(
        this DbContextOptionsBuilder<T> options,
        string connectionString,
        int commandTimeoutSeconds)
        where T : DbContext
    {
        return options
            .UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(commandTimeoutSeconds))
            .UseSnakeCaseNamingConvention();
    }
}

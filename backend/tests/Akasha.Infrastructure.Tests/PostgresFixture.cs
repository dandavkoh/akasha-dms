using Akasha.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using Akasha.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Akasha.Infrastructure.Tests;

/// <summary>
/// Spins up a real PostgreSQL container per test class collection and applies migrations.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public AkashaDbContext CreateContext(Guid? tenantId = null)
    {
        var tenantContext = new TenantContext();
        if (tenantId is { } tenant)
        {
            tenantContext.Establish(tenant);
        }

        return new AkashaDbContext(
            new DbContextOptionsBuilder<AkashaDbContext>()
                .UseAkashaNpgsql(ConnectionString, 30)
                .Options,
            tenantContext);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TenantPersistenceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Tenant_roundtrips()
    {
        var tenant = new Tenant("Muster GmbH", Unique("muster"));

        await using (var context = postgres.CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using (var context = postgres.CreateContext())
        {
            var loaded = await context.Tenants.SingleAsync(t => t.Id == tenant.Id);
            Assert.Equal("Muster GmbH", loaded.Name);
            Assert.Equal(tenant.Slug, loaded.Slug);
            Assert.True(loaded.CreatedAtUtc > DateTimeOffset.MinValue);
        }
    }

    [Fact]
    public async Task Duplicate_slug_is_rejected_by_unique_constraint()
    {
        var slug = Unique("dup");

        await using (var context = postgres.CreateContext())
        {
            context.Tenants.Add(new Tenant("First", slug));
            await context.SaveChangesAsync();
        }

        await using (var context = postgres.CreateContext())
        {
            context.Tenants.Add(new Tenant("Second", slug));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Name_length_is_rejected_by_column_constraint()
    {
        await using var context = postgres.CreateContext();
        var slug = Unique("long");

        context.Tenants.Add(new Tenant(new string('x', 200), slug));
        await context.SaveChangesAsync();

        // Directly violating the column limit must fail at the database level.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Database.ExecuteSqlRawAsync(
                "UPDATE tenants SET name = repeat('x', 201) WHERE slug = {0}", slug));
    }

    [Fact]
    public async Task Stale_update_is_rejected_by_xmin_concurrency_token()
    {
        var tenant = new Tenant("Concurrent", Unique("conc"));

        await using (var context = postgres.CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        // Two contexts load the same row snapshot.
        await using var context1 = postgres.CreateContext();
        await using var context2 = postgres.CreateContext();
        var copy1 = await context1.Tenants.SingleAsync(t => t.Id == tenant.Id);
        var copy2 = await context2.Tenants.SingleAsync(t => t.Id == tenant.Id);

        copy1.Rename("First writer");
        await context1.SaveChangesAsync();

        copy2.Rename("Second writer (stale)");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context2.SaveChangesAsync());
    }

    [Fact]
    public async Task Rolled_back_transaction_persists_nothing()
    {
        var slug = Unique("rollback");

        await using (var context = postgres.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Tenants.Add(new Tenant("Transient", slug));
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using (var context = postgres.CreateContext())
        {
            Assert.False(await context.Tenants.AnyAsync(t => t.Slug == slug));
        }
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

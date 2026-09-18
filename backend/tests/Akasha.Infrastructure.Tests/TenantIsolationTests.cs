using Akasha.Application.Tenancy;
using Akasha.Domain.Audit;
using Akasha.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TenantIsolationTests(PostgresFixture postgres)
{
    private async Task<Tenant> SeedTenantAsync(string slug)
    {
        var tenant = new Tenant($"Tenant {slug}", slug);
        await using var context = postgres.CreateContext();
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();
        return tenant;
    }

    [Fact]
    public async Task Tenant_can_read_and_write_own_data()
    {
        var tenant = await SeedTenantAsync("own");

        await using (var context = postgres.CreateContext(tenant.Id))
        {
            context.AuditEntries.Add(new AuditEntry(tenant.Id, "document.created"));
            await context.SaveChangesAsync();
        }

        await using (var context = postgres.CreateContext(tenant.Id))
        {
            Assert.Single(await context.AuditEntries.ToListAsync());
        }
    }

    [Fact]
    public async Task Tenant_cannot_read_other_tenants_data()
    {
        var tenantA = await SeedTenantAsync("a-read");
        var tenantB = await SeedTenantAsync("b-read");

        await using (var context = postgres.CreateContext(tenantA.Id))
        {
            context.AuditEntries.Add(new AuditEntry(tenantA.Id, "secret-action"));
            context.TenantMemberships.Add(new TenantMembership(tenantA.Id, "subject-a"));
            await context.SaveChangesAsync();
        }

        await using (var context = postgres.CreateContext(tenantB.Id))
        {
            Assert.Empty(await context.AuditEntries.ToListAsync());
            Assert.Empty(await context.TenantMemberships.ToListAsync());
        }
    }

    [Fact]
    public async Task Tenant_cannot_update_other_tenants_data()
    {
        var tenantA = await SeedTenantAsync("a-update");
        var tenantB = await SeedTenantAsync("b-update");

        var membership = new TenantMembership(tenantA.Id, "victim-subject");
        await using (var context = postgres.CreateContext(tenantA.Id))
        {
            context.TenantMemberships.Add(membership);
            await context.SaveChangesAsync();
        }

        // Simulates code that attached an entity of a foreign tenant without going
        // through the filtered query path.
        await using var foreign = postgres.CreateContext(tenantB.Id);
        foreign.TenantMemberships.Update(new TenantMembership(tenantA.Id, "forged-write"));

        await Assert.ThrowsAsync<TenantIsolationViolationException>(() => foreign.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_cannot_delete_other_tenants_data()
    {
        var tenantA = await SeedTenantAsync("a-delete");
        var tenantB = await SeedTenantAsync("b-delete");

        var membership = new TenantMembership(tenantA.Id, "victim-subject");
        await using (var context = postgres.CreateContext(tenantA.Id))
        {
            context.TenantMemberships.Add(membership);
            await context.SaveChangesAsync();
        }

        await using var foreign = postgres.CreateContext(tenantB.Id);
        foreign.TenantMemberships.Remove(membership);

        await Assert.ThrowsAsync<TenantIsolationViolationException>(() => foreign.SaveChangesAsync());

        await using (var context = postgres.CreateContext(tenantA.Id))
        {
            Assert.True(await context.TenantMemberships.AnyAsync());
        }
    }

    [Fact]
    public async Task Queries_without_tenant_context_return_nothing()
    {
        var tenant = await SeedTenantAsync("no-context");

        await using (var context = postgres.CreateContext(tenant.Id))
        {
            context.AuditEntries.Add(new AuditEntry(tenant.Id, "seeded"));
            await context.SaveChangesAsync();
        }

        await using var unscoped = postgres.CreateContext();
        Assert.Empty(await unscoped.AuditEntries.ToListAsync());
        Assert.Empty(await unscoped.TenantMemberships.ToListAsync());
    }

    [Fact]
    public async Task Writes_without_tenant_context_are_rejected()
    {
        var tenant = await SeedTenantAsync("no-context-write");

        await using var unscoped = postgres.CreateContext();
        unscoped.AuditEntries.Add(new AuditEntry(tenant.Id, "unscoped-write-attempt"));

        await Assert.ThrowsAsync<MissingTenantContextException>(() => unscoped.SaveChangesAsync());
    }
}

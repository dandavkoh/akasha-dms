using Akasha.Application.Tenants;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tenancy;

internal sealed class TenantQueries(AkashaDbContext dbContext) : ITenantQueries
{
    public async Task<TenantInfo?> FindAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => await dbContext.Tenants.AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => new TenantInfo(tenant.Id, tenant.Name, tenant.Slug))
            .SingleOrDefaultAsync(cancellationToken);
}

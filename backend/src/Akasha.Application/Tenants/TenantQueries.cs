namespace Akasha.Application.Tenants;

public sealed record TenantInfo(Guid Id, string Name, string Slug);

public interface ITenantQueries
{
    Task<TenantInfo?> FindAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

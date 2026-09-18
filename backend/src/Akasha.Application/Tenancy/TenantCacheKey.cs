namespace Akasha.Application.Tenancy;

/// <summary>
/// Security-sensitive cache keys must include the tenant so that no cache entry
/// can ever satisfy another tenant's request.
/// </summary>
public static class TenantCacheKey
{
    public static string For(Guid tenantId, string key)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant-scoped cache keys require a tenant id.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("tenant:", StringComparison.Ordinal))
        {
            throw new ArgumentException("Provide the tenant-relative key only.", nameof(key));
        }

        return $"tenant:{tenantId:N}:{key}";
    }
}

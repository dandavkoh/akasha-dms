namespace Akasha.Domain.Common;

/// <summary>
/// Marker for entities that belong to exactly one tenant.
/// Isolation is enforced by infrastructure (global query filters + save guards);
/// application code must not rely on manual Where(x => x.TenantId == ...) filters.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}

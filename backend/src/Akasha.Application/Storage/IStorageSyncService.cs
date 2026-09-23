namespace Akasha.Application.Storage;

/// <summary>Outcome of one synchronization cycle; purely informational.</summary>
public sealed class StorageSyncResult
{
    public int Created;
    public int Modified;
    public int Renamed;
    public int Moved;
    public int Deleted;
    public int Restored;
    public int Unchanged;
}

/// <summary>
/// Consumes normalized external storage changes and reconciles them with the logical
/// DMS state (rules #18-#26, #100-#104). Provider-specific change detection stays inside
/// the connector; this service only sees <see cref="StorageChange"/>s.
///
/// Tenant context must be established by the caller; the connector is resolved
/// tenant-safely via <see cref="IConnectorResolver"/>. One call = one (tenant, connector)
/// cycle: incremental via <c>GetChangesAsync</c>, falling back to a full reconciliation
/// scan on first sync or when the connector demands it (<c>RequiresFullRescan</c>).
/// </summary>
public interface IStorageSyncService
{
    Task<StorageSyncResult> SynchronizeAsync(Guid connectorId, CancellationToken cancellationToken = default);
}

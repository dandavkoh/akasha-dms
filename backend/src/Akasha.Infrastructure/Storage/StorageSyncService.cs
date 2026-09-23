using Akasha.Application.Storage;
using Akasha.Application.Tenancy;
using Akasha.Domain.Audit;
using Akasha.Domain.Documents;
using Akasha.Domain.Storage;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Storage;

/// <summary>
/// PostgreSQL-backed <see cref="IStorageSyncService"/>. One cycle:
/// Detect (connector.GetChangesAsync) → Normalize (StorageChange) → Reconcile (domain
/// transitions) → Persist (single transaction incl. checkpoint) → Process (documents
/// land in <see cref="ProcessingState.Pending"/> for the processing pipeline).
///
/// Safety properties (rules #25, #26, #102, #105):
/// - identity resolution uses (ConnectorId, ExternalId) only — never path/name (#101);
/// - external deletion marks Missing, nothing is ever removed;
/// - restoration only re-associates via the SAME stable external id (rule #172.25);
/// - staleness guards live in the domain (RecordExternalVersion drops out-of-order changes);
/// - a connector outage throws before any mutation, so known state is preserved;
/// - the whole cycle (incl. checkpoint) commits in one transaction — a failed cycle
///   leaves no partial state and is safe to retry (idempotent by replay).
/// A cross-service create race (upload + sync registering the same external id
/// concurrently) surfaces as the unique-key violation of
/// (TenantId, ConnectorId, ExternalId); the failed cycle is healed by the next poll.
/// </summary>
public sealed class StorageSyncService(
    AkashaDbContext dbContext,
    IConnectorResolver connectors,
    ITenantContext tenantContext) : IStorageSyncService
{
    public async Task<StorageSyncResult> SynchronizeAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.Require();
        var connector = await connectors.ResolveAsync(connectorId, cancellationToken);
        connector.Require(StorageCapability.ChangeTracking);

        var checkpoint = await dbContext.ConnectorSyncCheckpoints
            .SingleOrDefaultAsync(entry => entry.ConnectorId == connectorId, cancellationToken);

        // All known documents of this connector, keyed by their stable external identity.
        // Tenant isolation is enforced by the DbContext query filter.
        var documents = (await dbContext.Documents
                .Include(document => document.Storage)
                .Where(document => document.Storage.ConnectorId == connectorId)
                .ToListAsync(cancellationToken))
            .ToDictionary(document => document.Storage.ExternalId, StringComparer.Ordinal);

        var page = await connector.GetChangesAsync(checkpoint?.Cursor, cancellationToken);

        var result = new StorageSyncResult();
        if (checkpoint is null || page.RequiresFullRescan)
        {
            // First sync is discovery (rule #21): establish the baseline via a full scan.
            await ReconcileFullScanAsync(connector, connectorId, tenantId, documents, result, cancellationToken);
        }
        else
        {
            foreach (var change in page.Changes)
            {
                Apply(change, connectorId, tenantId, documents, result);
            }
        }

        checkpoint ??= dbContext.ConnectorSyncCheckpoints.Add(
            new ConnectorSyncCheckpoint(tenantId, connectorId)).Entity;
        checkpoint.RecordCursor(page.NextCursor);

        await dbContext.SaveChangesAsync(cancellationToken);
        return result;
    }

    private void Apply(
        StorageChange change,
        Guid connectorId,
        Guid tenantId,
        Dictionary<string, Document> documents,
        StorageSyncResult result)
    {
        documents.TryGetValue(change.ExternalId, out var document);

        switch (change.Type)
        {
            case StorageChangeType.Created:
                if (document is null)
                {
                    Register(change, connectorId, tenantId, documents);
                    result.Created++;
                    Audit(tenantId, "document.sync.created");
                }
                else if (document.State == StorageSyncState.Missing)
                {
                    // Same stable external id reappeared: restore the logical document (rule #26).
                    document.MarkRestored(change.Version, change.ETag, change.ModifiedAtUtc);
                    Relocate(document, change.Path, change.Name);
                    result.Restored++;
                    Audit(tenantId, "document.sync.restored");
                }
                else
                {
                    document.RecordExternalVersion(change.Version, change.ETag, change.ModifiedAtUtc);
                    result.Unchanged++;
                }
                break;

            case StorageChangeType.Restored:
                if (document is null)
                {
                    Register(change, connectorId, tenantId, documents);
                    result.Created++;
                    Audit(tenantId, "document.sync.created");
                }
                else
                {
                    document.MarkRestored(change.Version, change.ETag, change.ModifiedAtUtc);
                    Relocate(document, change.Path, change.Name);
                    result.Restored++;
                    Audit(tenantId, "document.sync.restored");
                }
                break;

            case StorageChangeType.Renamed:
            case StorageChangeType.Moved:
                if (document is null)
                {
                    result.Unchanged++; // unregistered object; full-scan discovery picks it up
                    break;
                }

                Relocate(document, change.Path, change.Name);
                document.RecordExternalVersion(change.Version, change.ETag, change.ModifiedAtUtc);
                if (change.Type == StorageChangeType.Renamed)
                {
                    result.Renamed++;
                    Audit(tenantId, "document.sync.external.renamed");
                }
                else
                {
                    result.Moved++;
                    Audit(tenantId, "document.sync.external.moved");
                }
                break;

            case StorageChangeType.Modified:
                if (document is null)
                {
                    result.Unchanged++;
                    break;
                }

                document.RecordExternalVersion(change.Version, change.ETag, change.ModifiedAtUtc);
                result.Modified++;
                Audit(tenantId, "document.sync.external.modified");
                break;

            case StorageChangeType.Deleted:
                if (document is null)
                {
                    result.Unchanged++;
                    break;
                }

                // Logical identity, Akte relationships, metadata and history survive (rule #25).
                document.MarkMissing();
                result.Deleted++;
                Audit(tenantId, "document.sync.external.deleted");
                break;
        }
    }

    /// <summary>Discovery/reconciliation: whatever exists now is authoritative; known objects absent from the scan become Missing (rule #104).</summary>
    private async Task ReconcileFullScanAsync(
        IStorageConnector connector,
        Guid connectorId,
        Guid tenantId,
        Dictionary<string, Document> documents,
        StorageSyncResult result,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in connector.ListDocumentsAsync(null, cancellationToken))
        {
            seen.Add(item.ExternalId);
            if (documents.TryGetValue(item.ExternalId, out var document))
            {
                if (document.State == StorageSyncState.Missing)
                {
                    document.MarkRestored(item.Version, item.ETag, item.ModifiedAtUtc);
                    result.Restored++;
                    Audit(tenantId, "document.sync.restored");
                }
                else
                {
                    var nameChanged = !string.Equals(document.Storage.Name, item.Name, StringComparison.Ordinal);
                    var pathChanged = Relocate(document, item.Path, item.Name);
                    if (nameChanged || pathChanged)
                    {
                        Audit(tenantId, nameChanged
                            ? "document.sync.external.renamed"
                            : "document.sync.external.moved");
                    }

                    document.RecordExternalVersion(item.Version, item.ETag, item.ModifiedAtUtc);
                    result.Unchanged++;
                }
            }
            else
            {
                Register(new StorageChange
                {
                    Type = StorageChangeType.Created,
                    ExternalId = item.ExternalId,
                    Path = item.Path,
                    Name = item.Name,
                    Version = item.Version,
                    ETag = item.ETag,
                    ModifiedAtUtc = item.ModifiedAtUtc,
                }, connectorId, tenantId, documents);
                result.Created++;
                Audit(tenantId, "document.sync.created");
            }
        }

        foreach (var (externalId, document) in documents)
        {
            if (!seen.Contains(externalId) && document.State != StorageSyncState.Missing)
            {
                document.MarkMissing();
                result.Deleted++;
                Audit(tenantId, "document.sync.external.deleted");
            }
        }
    }

    private void Register(StorageChange change, Guid connectorId, Guid tenantId, Dictionary<string, Document> documents)
    {
        var document = new Document(tenantId, new StorageReference(
            tenantId,
            connectorId,
            change.ExternalId,
            // StorageReference requires a non-empty path; connector root is represented as ".".
            string.IsNullOrWhiteSpace(change.Path) ? "." : change.Path,
            change.Name ?? change.ExternalId,
            change.Version,
            change.ETag));

        // Discovered objects exist physically: Available, and queued for processing.
        document.RecordExternalVersion(change.Version, change.ETag, change.ModifiedAtUtc);
        dbContext.Documents.Add(document);
        documents[change.ExternalId] = document;
    }

    /// <summary>
    /// Updates the storage reference when the change carries location info; identity stays
    /// untouched. Returns true when the location actually changed. The empty connector
    /// root path normalizes to "." so rounds trips through the connector are idempotent.
    /// </summary>
    private static bool Relocate(Document document, string? path, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalizedPath = string.IsNullOrWhiteSpace(path) ? "." : path;
        if (string.Equals(document.Storage.Path, normalizedPath, StringComparison.Ordinal)
            && string.Equals(document.Storage.Name, name, StringComparison.Ordinal))
        {
            return false;
        }

        document.RecordExternalRelocation(normalizedPath, name);
        return true;
    }

    private void Audit(Guid tenantId, string action)
        => dbContext.AuditEntries.Add(new AuditEntry(tenantId, action)); // actor null = system sync
}

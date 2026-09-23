using System.Text;
using Akasha.Application.Storage;
using Akasha.Application.Tenancy;
using Akasha.Domain.Akten;
using Akasha.Domain.Documents;
using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests.Storage;

/// <summary>
/// Phase 13: external change detection and synchronization. The connector is the real
/// <see cref="LocalFileSystemConnector"/> on a temp root; external changes are simulated
/// by direct filesystem manipulation (File.Move / File.WriteAllText / File.Delete),
/// i.e. exactly the changes users make outside the DMS. Persistence is real PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StorageSyncServiceTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("akasha-p13-").FullName;

    private sealed class Context : IDisposable
    {
        public required Tenant Tenant { get; init; }
        public required Persistence.AkashaDbContext Db { get; init; }
        public required LocalFileSystemConnector Connector { get; init; }
        public required StorageSyncService Sync { get; init; }
        public required Guid ConnectorId { get; init; }
        public required string Root { get; init; }

        public string FullPath(string folder, string name) => Path.Combine(Root, folder, name);

        public async Task<StorageItem> CreateExternalFileAsync(string folder, string name, string content)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            return await Connector.CreateDocumentAsync(folder, name, stream);
        }

        public static async Task<Context> CreateAsync(PostgresFixture postgres, string root, string slug)
        {
            var tenant = new Tenant($"Tenant {slug}", slug);
            await using (var seeding = postgres.CreateContext())
            {
                seeding.Tenants.Add(tenant);
                await seeding.SaveChangesAsync();
            }

            var db = postgres.CreateContext(tenant.Id);
            var connector = new LocalFileSystemConnector(new LocalFileSystemConnectorOptions { RootPath = root });
            var connectorId = Guid.NewGuid();
            var tenantContext = new TenantContext();
            tenantContext.Establish(tenant.Id);
            return new Context
            {
                Tenant = tenant,
                Db = db,
                Connector = connector,
                ConnectorId = connectorId,
                Root = root,
                Sync = new StorageSyncService(
                    db, new FakeConnectorResolver(new Dictionary<Guid, IStorageConnector> { [connectorId] = connector }), tenantContext),
            };
        }

        public async Task<Document> GetDocumentAsync(string externalId)
            => await Db.Documents.Include(d => d.Storage)
                .SingleAsync(d => d.Storage.ExternalId == externalId);

        public void Dispose() => Db.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------- Created / discovery ----------

    [Fact]
    public async Task FirstSync_Discovers_External_Files_As_New_Documents()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-discover");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");

        var result = await context.Sync.SynchronizeAsync(context.ConnectorId);

        Assert.Equal(1, result.Created);
        var document = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal(StorageSyncState.Available, document.State);
        Assert.Equal(ProcessingState.Pending, document.Processing); // queued for the pipeline
        Assert.Equal(item.ETag, document.Storage.ETag);
        Assert.True(await context.Db.AuditEntries.AnyAsync(e => e.Action == "document.sync.created"));

        // checkpoint persisted: a second cycle is incremental and changes nothing
        var checkpoint = await context.Db.ConnectorSyncCheckpoints.SingleAsync();
        Assert.NotNull(checkpoint.Cursor);
        var second = await context.Sync.SynchronizeAsync(context.ConnectorId);
        Assert.Equal(0, second.Created);
        Assert.Equal(0, second.Modified);
    }

    [Fact]
    public async Task Root_Level_Files_Are_Discovered_With_Normalized_Path()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-root");
        await File.WriteAllTextAsync(Path.Combine(_root, "loose.txt"), "x");

        var result = await context.Sync.SynchronizeAsync(context.ConnectorId);

        Assert.Equal(1, result.Created);
        var document = await context.Db.Documents.Include(d => d.Storage).SingleAsync();
        Assert.Equal(".", document.Storage.Path);
    }

    // ---------- Modified ----------

    [Fact]
    public async Task External_Modification_Updates_Version_And_Queues_Processing()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-modify");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");
        await context.Sync.SynchronizeAsync(context.ConnectorId);
        var documentId = (await context.GetDocumentAsync(item.ExternalId)).Id;

        await File.WriteAllTextAsync(context.FullPath("akte-1", "contract.pdf"), "v2 - externally changed");
        var result = await context.Sync.SynchronizeAsync(context.ConnectorId);

        Assert.Equal(1, result.Modified);
        var document = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal(documentId, document.Id); // logical identity preserved
        Assert.NotEqual(item.ETag, document.Storage.ETag);
        Assert.Equal(StorageSyncState.Available, document.State);
        Assert.Equal(ProcessingState.Pending, document.Processing); // reindexing prepared
        Assert.True(await context.Db.AuditEntries.AnyAsync(e => e.Action == "document.sync.external.modified"));
    }

    [Fact]
    public async Task Duplicate_Modified_Change_Is_Idempotent()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-dupmod");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");
        await context.Sync.SynchronizeAsync(context.ConnectorId);

        var change = new StorageChange
        {
            Type = StorageChangeType.Modified,
            ExternalId = item.ExternalId,
            ETag = "etag-v2",
            ModifiedAtUtc = DateTimeOffset.UtcNow,
        };
        var scripted = new ScriptedConnector(new StorageChangePage { Changes = [change], NextCursor = "1" },
            new StorageChangePage { Changes = [change], NextCursor = "2" }); // same change delivered twice
        var sync = new StorageSyncService(context.Db, new FakeConnectorResolver(
            new Dictionary<Guid, IStorageConnector> { [context.ConnectorId] = scripted }), NewTenantContext(context.Tenant.Id));

        await sync.SynchronizeAsync(context.ConnectorId);
        var second = await sync.SynchronizeAsync(context.ConnectorId);

        Assert.Equal(1, second.Modified); // still counted, but state is identical (idempotent replay)
        var document = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal("etag-v2", document.Storage.ETag);
    }

    [Fact]
    public async Task OutOfOrder_Older_Change_Does_Not_Regress_Known_State()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-stale");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");
        await context.Sync.SynchronizeAsync(context.ConnectorId);
        context.Db.ChangeTracker.Clear();
        var known = await context.GetDocumentAsync(item.ExternalId);
        var knownModifiedAt = known.Storage.LastKnownModifiedAtUtc;

        var stale = new StorageChange
        {
            Type = StorageChangeType.Modified,
            ExternalId = item.ExternalId,
            ETag = "stale-etag",
            ModifiedAtUtc = knownModifiedAt?.AddSeconds(-60), // arrives late, older than known
        };
        var scripted = new ScriptedConnector(new StorageChangePage { Changes = [stale], NextCursor = "1" });
        var sync = new StorageSyncService(context.Db, new FakeConnectorResolver(
            new Dictionary<Guid, IStorageConnector> { [context.ConnectorId] = scripted }), NewTenantContext(context.Tenant.Id));

        await sync.SynchronizeAsync(context.ConnectorId);

        context.Db.ChangeTracker.Clear();
        var after = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal(known.Storage.ETag, after.Storage.ETag); // not overwritten by the stale change
        Assert.Equal(knownModifiedAt, after.Storage.LastKnownModifiedAtUtc);
    }

    // ---------- Renamed / Moved ----------

    [Fact]
    public async Task External_Rename_Preserves_DocumentId_Name_And_Akte()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-rename");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");
        await context.Sync.SynchronizeAsync(context.ConnectorId);
        var documentId = (await context.GetDocumentAsync(item.ExternalId)).Id;
        var akte = await LinkToAkteAsync(context, documentId);

        File.Move(context.FullPath("akte-1", "contract.pdf"), context.FullPath("akte-1", "vertrag.pdf"));
        await context.Sync.SynchronizeAsync(context.ConnectorId);

        context.Db.ChangeTracker.Clear();
        var document = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal(documentId, document.Id);
        Assert.Equal("vertrag.pdf", document.Name);
        Assert.Equal("akte-1", document.Storage.Path);
        Assert.True(await context.Db.AkteDocuments.AnyAsync(l => l.AkteId == akte.Id && l.DocumentId == documentId));
        Assert.True(await context.Db.AuditEntries.AnyAsync(e => e.Action == "document.sync.external.renamed"));
    }

    [Fact]
    public async Task External_Move_Preserves_DocumentId_And_Akte()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-move");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");
        await context.Sync.SynchronizeAsync(context.ConnectorId);
        var documentId = (await context.GetDocumentAsync(item.ExternalId)).Id;
        var akte = await LinkToAkteAsync(context, documentId);

        Directory.CreateDirectory(Path.Combine(_root, "akte-2"));
        File.Move(context.FullPath("akte-1", "contract.pdf"), context.FullPath("akte-2", "contract.pdf"));
        await context.Sync.SynchronizeAsync(context.ConnectorId);

        context.Db.ChangeTracker.Clear();
        var document = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal(documentId, document.Id);
        Assert.Equal("akte-2", document.Storage.Path);
        Assert.True(await context.Db.AkteDocuments.AnyAsync(l => l.AkteId == akte.Id && l.DocumentId == documentId));
        Assert.True(await context.Db.AuditEntries.AnyAsync(e => e.Action == "document.sync.external.moved"));
    }

    // ---------- Deleted / Restored ----------

    [Fact]
    public async Task External_Deletion_Marks_Missing_And_Keeps_Logical_State()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-delete");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");
        await context.Sync.SynchronizeAsync(context.ConnectorId);
        var documentId = (await context.GetDocumentAsync(item.ExternalId)).Id;
        var akte = await LinkToAkteAsync(context, documentId);

        File.Delete(context.FullPath("akte-1", "contract.pdf"));
        var result = await context.Sync.SynchronizeAsync(context.ConnectorId);

        Assert.Equal(1, result.Deleted);
        context.Db.ChangeTracker.Clear();
        var document = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal(StorageSyncState.Missing, document.State);
        // logical identity, relationships and last known location all survive
        Assert.Equal("contract.pdf", document.Name);
        Assert.Equal("akte-1", document.Storage.Path);
        Assert.Equal(item.ExternalId, document.Storage.ExternalId);
        Assert.True(await context.Db.AkteDocuments.AnyAsync(l => l.AkteId == akte.Id && l.DocumentId == documentId));
        Assert.True(await context.Db.AuditEntries.AnyAsync(e => e.Action == "document.sync.external.deleted"));
    }

    [Fact]
    public async Task External_Restore_With_Same_ExternalId_Reactivates_Document()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-restore");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");
        await context.Sync.SynchronizeAsync(context.ConnectorId);
        var documentId = (await context.GetDocumentAsync(item.ExternalId)).Id;

        // file "lost" (moved out of the scanned root), then brought back: the FileId
        // identity survives the move, so the stable external id is re-detected.
        var quarantine = Path.Combine(_root, "..", $"akasha-p13-quarantine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(quarantine);
        File.Move(context.FullPath("akte-1", "contract.pdf"), Path.Combine(quarantine, "contract.pdf"));
        await context.Sync.SynchronizeAsync(context.ConnectorId);
        Assert.Equal(StorageSyncState.Missing, (await context.GetDocumentAsync(item.ExternalId)).State);

        File.Move(Path.Combine(quarantine, "contract.pdf"), context.FullPath("akte-1", "contract.pdf"));
        var result = await context.Sync.SynchronizeAsync(context.ConnectorId);

        context.Db.ChangeTracker.Clear();
        var document = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal(documentId, document.Id); // no duplicate document was created
        Assert.Equal(StorageSyncState.Available, document.State);
        Assert.Equal(ProcessingState.Pending, document.Processing);
        Assert.Equal(1, result.Restored);
        Assert.True(await context.Db.AuditEntries.AnyAsync(e => e.Action == "document.sync.restored"));
        Directory.Delete(quarantine, recursive: true);
    }

    // ---------- Rescan / failure semantics ----------

    [Fact]
    public async Task Lost_Checkpoint_Triggers_Full_Rescan_Reconciliation()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-rescan");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");
        await context.Sync.SynchronizeAsync(context.ConnectorId);
        var documentId = (await context.GetDocumentAsync(item.ExternalId)).Id;

        context.Db.ChangeTracker.Clear();

        // Process restart: a NEW connector instance has lost its in-memory snapshot, so
        // the stored cursor is unknown to it and it must demand a full rescan.
        var restarted = new LocalFileSystemConnector(new LocalFileSystemConnectorOptions { RootPath = _root });
        var sync = new StorageSyncService(context.Db, new FakeConnectorResolver(
            new Dictionary<Guid, IStorageConnector> { [context.ConnectorId] = restarted }), NewTenantContext(context.Tenant.Id));

        var result = await sync.SynchronizeAsync(context.ConnectorId);

        context.Db.ChangeTracker.Clear();
        var document = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal(documentId, document.Id); // reconciled, not re-created
        Assert.Equal(1, await context.Db.Documents.CountAsync());
        Assert.True(result.Unchanged >= 1);
    }

    [Fact]
    public async Task Full_Rescan_Marks_Vanished_Files_Missing_But_Keeps_Registered_Ones()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-rescandel");
        var kept = await context.CreateExternalFileAsync("akte-1", "kept.pdf", "a");
        var gone = await context.CreateExternalFileAsync("akte-1", "gone.pdf", "b");
        await context.Sync.SynchronizeAsync(context.ConnectorId);

        File.Delete(context.FullPath("akte-1", "gone.pdf"));
        context.Db.ChangeTracker.Clear();

        var restarted = new LocalFileSystemConnector(new LocalFileSystemConnectorOptions { RootPath = _root });
        var sync = new StorageSyncService(context.Db, new FakeConnectorResolver(
            new Dictionary<Guid, IStorageConnector> { [context.ConnectorId] = restarted }), NewTenantContext(context.Tenant.Id));
        var result = await sync.SynchronizeAsync(context.ConnectorId);

        Assert.Equal(1, result.Deleted);
        context.Db.ChangeTracker.Clear();
        Assert.Equal(StorageSyncState.Missing, (await context.GetDocumentAsync(gone.ExternalId)).State);
        Assert.Equal(StorageSyncState.Available, (await context.GetDocumentAsync(kept.ExternalId)).State);
    }

    [Fact]
    public async Task Unavailable_Connector_Preserves_All_Known_State()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-outage");
        var item = await context.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");
        await context.Sync.SynchronizeAsync(context.ConnectorId);
        context.Db.ChangeTracker.Clear();

        // Connector goes down HARD: a outage is not a mass deletion (#105).
        // Delete the file while it is down to prove the sync trusted the failure, not the disk.
        File.Delete(context.FullPath("akte-1", "contract.pdf"));
        var down = new OutageConnector();
        var sync = new StorageSyncService(context.Db, new FakeConnectorResolver(
            new Dictionary<Guid, IStorageConnector> { [context.ConnectorId] = down }), NewTenantContext(context.Tenant.Id));

        await Assert.ThrowsAsync<StorageException>(() => sync.SynchronizeAsync(context.ConnectorId));

        context.Db.ChangeTracker.Clear();
        var document = await context.GetDocumentAsync(item.ExternalId);
        Assert.Equal(StorageSyncState.Available, document.State); // not marked Missing
        Assert.Equal(item.ETag, document.Storage.ETag); // nothing overwritten
    }

    [Fact]
    public async Task Sync_Without_ChangeTracking_Capability_Fails_Fast()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p13-nocap");
        var incapable = new FailingConnector(StorageCapability.Read, StorageErrorKind.Unavailable);
        var sync = new StorageSyncService(context.Db, new FakeConnectorResolver(
            new Dictionary<Guid, IStorageConnector> { [context.ConnectorId] = incapable }), NewTenantContext(context.Tenant.Id));

        var exception = await Assert.ThrowsAsync<StorageException>(() => sync.SynchronizeAsync(context.ConnectorId));
        Assert.Equal(StorageErrorKind.UnsupportedOperation, exception.Kind);
    }

    // ---------- tenant isolation ----------

    [Fact]
    public async Task Sync_Never_Touches_Other_Tenants_Documents()
    {
        using var contextA = await Context.CreateAsync(postgres, _root, "p13-tenant-a");
        using var contextB = await Context.CreateAsync(postgres, _root, "p13-tenant-b");
        var item = await contextA.CreateExternalFileAsync("akte-1", "contract.pdf", "v1");

        // Tenant A discovers the file and registers it.
        await contextA.Sync.SynchronizeAsync(contextA.ConnectorId);
        var docA = await contextA.GetDocumentAsync(item.ExternalId);

        // Tenant B runs a sync against ITS connector view (shared physical root is a
        // valid shared-connector deployment; per-tenant logical documents stay separate).
        var resultB = await contextB.Sync.SynchronizeAsync(contextB.ConnectorId);

        Assert.Equal(1, resultB.Created); // B gets its OWN logical document
        var docB = await contextB.Db.Documents.Include(d => d.Storage).SingleAsync();
        Assert.NotEqual(docA.Id, docB.Id);
        Assert.NotEqual(docA.TenantId, docB.TenantId);
        // A's document unchanged and no cross-tenant visibility
        Assert.Equal(1, await contextA.Db.Documents.CountAsync());
        Assert.False(await contextB.Db.Documents.AnyAsync(d => d.Id == docA.Id));
    }

    // ---------- helpers / fakes ----------

    private static TenantContext NewTenantContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.Establish(tenantId);
        return tenantContext;
    }

    private static async Task<Akte> LinkToAkteAsync(Context context, Guid documentId)
    {
        var akte = new Akte(context.Tenant.Id, "Akte 2026-001");
        context.Db.Akten.Add(akte);
        context.Db.AkteDocuments.Add(new AkteDocument(context.Tenant.Id, akte.Id, documentId));
        await context.Db.SaveChangesAsync();
        return akte;
    }

    /// <summary>Connector with a scripted change feed; declares ChangeTracking.</summary>
    private sealed class ScriptedConnector(params StorageChangePage[] pages) : StorageConnectorBase
    {
        private int _index;

        public override StorageCapability Capabilities => StorageCapability.Read | StorageCapability.ChangeTracking;

        public override Task<StorageChangePage> GetChangesAsync(string? cursor, CancellationToken cancellationToken = default)
            => Task.FromResult(pages[Math.Min(_index++, pages.Length - 1)]);
    }

    /// <summary>Connector that is hard down; everything it offers throws Unavailable.</summary>
    private sealed class OutageConnector : StorageConnectorBase
    {
        public override StorageCapability Capabilities => StorageCapability.Read | StorageCapability.ChangeTracking;

        public override Task<StorageChangePage> GetChangesAsync(string? cursor, CancellationToken cancellationToken = default)
            => Task.FromException<StorageChangePage>(
                new StorageException(StorageErrorKind.Unavailable, "Connector is unreachable.", isTransient: true));
    }
}

using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Documents;
using Akasha.Application.Identity;
using Akasha.Application.Tenancy;
using Akasha.Domain.Audit;
using Akasha.Domain.Authorization;
using Akasha.Domain.Documents;
using Akasha.Application.Storage;
using Akasha.Domain.Storage;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Akasha.Infrastructure.Documents;

/// <summary>
/// PostgreSQL-backed <see cref="IDocumentService"/>. Tenant isolation is enforced by the
/// DbContext query filters and write guard; authorization runs before any data access
/// (rule #10). Updates use the PostgreSQL xmin row version for optimistic concurrency
/// (rule #28): a stale write conflicts instead of silently overwriting.
/// </summary>
public sealed class DocumentService(
    AkashaDbContext dbContext,
    IAuthorizationService authorization,
    ICurrentUser currentUser,
    ITenantContext tenantContext,
    IConnectorResolver connectors) : IDocumentService
{
    public async Task<PagedResult<DocumentView>> ListAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100)
        {
            throw new ValidationException("Invalid pagination: skip >= 0 and 1 <= take <= 100.");
        }

        var user = currentUser.Require();

        // Type-level read required: without it the caller must not even learn how many
        // documents exist. Per-document grants can still read single documents by id.
        // ponytail: per-row filtering (documents with resource-level grants only) is not
        // expressible without joining the grant table; add it when resource-level document
        // grants are actually assigned by the product.
        await authorization.RequireAsync(user, ResourceType.Document, null, PermissionAction.Read, cancellationToken);

        var query = dbContext.Documents.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(document => document.CreatedAtUtc)
            .Skip(skip)
            .Take(take)
            .Select(document => new { document, xmin = EF.Property<uint>(document, "xmin") })
            .ToListAsync(cancellationToken);

        // AsNoTracking + Include via projection
        var ids = rows.Select(row => row.document.Id).ToList();
        var storageRows = await dbContext.StorageReferences.AsNoTracking()
            .Where(reference => ids.Contains(EF.Property<Guid>(reference, "DocumentId")))
            .Select(reference => new { reference, documentId = EF.Property<Guid>(reference, "DocumentId") })
            .ToListAsync(cancellationToken);
        var storage = storageRows.ToDictionary(row => row.documentId, row => row.reference);

        var items = rows
            .Select(row =>
            {
                var reference = storage[row.document.Id];
                return new DocumentView(
                    row.document.Id,
                    row.document.Name,
                    reference.SyncState,
                    row.document.Processing,
                    ToView(reference),
                    row.xmin,
                    row.document.CreatedAtUtc,
                    row.document.UpdatedAtUtc);
            })
            .ToList();

        return new PagedResult<DocumentView>(items, total, skip, take);
    }

    public async Task<DocumentView> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, id, PermissionAction.Read, cancellationToken);

        var document = await LoadAsync(id, cancellationToken);
        return new DocumentView(
            document.Id,
            document.Name,
            document.State,
            document.Processing,
            ToView(document.Storage),
            Xmin(document),
            document.CreatedAtUtc,
            document.UpdatedAtUtc);
    }

    public async Task<DocumentView> CreateAsync(CreateDocumentRequest request, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(
            user, ResourceType.Document, null, PermissionAction.Upload, cancellationToken);

        Validate(nameof(request.ExternalId), request.ExternalId, StorageReference.MaxExternalIdLength);
        Validate(nameof(request.Path), request.Path, StorageReference.MaxPathLength);
        Validate(nameof(request.Name), request.Name, StorageReference.MaxNameLength);

        var tenantId = tenantContext.Require();
        var document = new Document(tenantId, new StorageReference(
            tenantId,
            request.ConnectorId,
            request.ExternalId,
            request.Path,
            request.Name,
            request.Version,
            request.ETag,
            request.LastKnownModifiedAtUtc));

        // Registration: the sync state starts Pending until a sync cycle confirms the object.
        dbContext.Documents.Add(document);
        dbContext.AuditEntries.Add(new AuditEntry(tenantId, "document.created", user.Subject));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new ConflictException(
                "A document for this storage object (connector + external id) already exists.");
        }

        return new DocumentView(
            document.Id,
            document.Name,
            document.State,
            document.Processing,
            ToView(document.Storage),
            Xmin(document),
            document.CreatedAtUtc,
            document.UpdatedAtUtc);
    }

    public async Task<DocumentView> RenameAsync(Guid id, RenameDocumentRequest request, CancellationToken cancellationToken = default)
    {
        if (request.RowVersion == 0)
        {
            throw new ValidationException("RowVersion is required.");
        }

        Validate(nameof(request.Name), request.Name, Document.MaxNameLength);

        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, id, PermissionAction.Rename, cancellationToken);

        var document = await LoadAsync(id, cancellationToken);

        // Optimistic concurrency: any concurrent save since the caller's read bumped xmin,
        // so this update conflicts instead of overwriting a newer state (rule #28).
        dbContext.Entry(document).Property("xmin").OriginalValue = request.RowVersion;

        document.Rename(request.Name);
        dbContext.AuditEntries.Add(new AuditEntry(document.TenantId, "document.renamed", user.Subject));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The document was modified by someone else; reload and retry.");
        }

        return new DocumentView(
            document.Id,
            document.Name,
            document.State,
            document.Processing,
            ToView(document.Storage),
            Xmin(document),
            document.CreatedAtUtc,
            document.UpdatedAtUtc);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, id, PermissionAction.Delete, cancellationToken);

        var document = await LoadAsync(id, cancellationToken);

        // Logical deletion only; the physical customer file is never touched here and
        // audit entries survive the document (rules #83/#84).
        dbContext.Documents.Remove(document);
        dbContext.AuditEntries.Add(new AuditEntry(document.TenantId, "document.deleted", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---------- physical file operations (connector-backed) ----------
    // Common pipeline: authorize → load (tenant-filtered) → resolve connector (tenant-checked)
    // → capability check → storage operation → THEN database update + audit (#29).
    // A failed storage operation never updates the database and never reports success.

    public async Task<DocumentView> UploadContentAsync(UploadDocumentRequest request, Stream content, CancellationToken cancellationToken = default)
    {
        Validate(nameof(request.Name), request.Name, StorageReference.MaxNameLength);
        Validate(nameof(request.FolderPath), request.FolderPath, StorageReference.MaxPathLength);

        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, null, PermissionAction.Upload, cancellationToken);

        var connector = await connectors.ResolveAsync(request.ConnectorId, cancellationToken);
        connector.Require(StorageCapability.Write);

        // Storage first (rule #118): no database transaction is held during the upload.
        var stored = await connector.CreateDocumentAsync(request.FolderPath, request.Name, content, cancellationToken);

        var tenantId = tenantContext.Require();
        var document = new Document(tenantId, new StorageReference(
            tenantId,
            request.ConnectorId,
            stored.ExternalId,
            stored.Path,
            stored.Name,
            stored.Version,
            stored.ETag,
            stored.ModifiedAtUtc));

        dbContext.Documents.Add(document);
        dbContext.AuditEntries.Add(new AuditEntry(tenantId, "document.uploaded", user.Subject));
        try
        {
            await MarkRegisteredAsync(document, stored, cancellationToken);
        }
        catch (ConflictException)
        {
            // Registration conflicted after the file was created: remove the orphan so the
            // partial failure leaves no unreferenced physical file behind.
            await TryDeletePhysicalAsync(connector, stored.ExternalId, cancellationToken);
            throw;
        }

        return ViewOf(document);
    }

    public async Task<DocumentContent> DownloadContentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Authorization BEFORE any content access (rule #10).
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, id, PermissionAction.Download, cancellationToken);

        var document = await LoadAsync(id, cancellationToken);
        var connector = await connectors.ResolveAsync(document.Storage.ConnectorId, cancellationToken);
        connector.Require(StorageCapability.Read);

        var (stream, item) = await GuardedAsync(document, async () =>
        {
            var opened = await connector.DownloadAsync(document.Storage.ExternalId, cancellationToken);
            var current = await connector.GetDocumentAsync(document.Storage.ExternalId, cancellationToken);
            return (opened, current);
        }, cancellationToken);

        dbContext.AuditEntries.Add(new AuditEntry(document.TenantId, "document.downloaded", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new DocumentContent(stream, item.Name, item.ContentType, item.Size);
    }

    public async Task<DocumentView> UpdateContentAsync(Guid id, UpdateContentRequest request, Stream content, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, id, PermissionAction.Write, cancellationToken);

        var document = await LoadAsync(id, cancellationToken);
        var connector = await connectors.ResolveAsync(document.Storage.ConnectorId, cancellationToken);
        connector.Require(StorageCapability.Write);

        var stored = await GuardedAsync(document, () => connector.UpdateDocumentAsync(
            document.Storage.ExternalId, content, ETagOrKnown(document, request.ETag), cancellationToken), cancellationToken);

        document.RecordExternalVersion(stored.Version, stored.ETag, stored.ModifiedAtUtc);
        dbContext.AuditEntries.Add(new AuditEntry(document.TenantId, "document.updated", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);
        return ViewOf(document);
    }

    public async Task DeleteContentAsync(Guid id, string? eTag, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, id, PermissionAction.Delete, cancellationToken);

        var document = await LoadAsync(id, cancellationToken);
        var connector = await connectors.ResolveAsync(document.Storage.ConnectorId, cancellationToken);
        connector.Require(StorageCapability.Delete);

        await GuardedAsync(document, async () =>
        {
            await connector.DeleteDocumentAsync(
                document.Storage.ExternalId, ETagOrKnown(document, eTag), cancellationToken);
            return true;
        }, cancellationToken);

        // The file is gone; the logical document, Akte relationships and history survive (#83).
        document.MarkMissing();
        dbContext.AuditEntries.Add(new AuditEntry(document.TenantId, "document.file.deleted", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<DocumentView> MoveFileAsync(Guid id, MoveDocumentRequest request, CancellationToken cancellationToken = default)
    {
        Validate(nameof(request.TargetFolderPath), request.TargetFolderPath, StorageReference.MaxPathLength);

        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, id, PermissionAction.Move, cancellationToken);

        var document = await LoadAsync(id, cancellationToken);
        var connector = await connectors.ResolveAsync(document.Storage.ConnectorId, cancellationToken);
        connector.Require(StorageCapability.Move);

        var stored = await GuardedAsync(document, () => connector.MoveDocumentAsync(
            document.Storage.ExternalId, request.TargetFolderPath, document.Storage.ETag, cancellationToken), cancellationToken);

        // Same DocumentId, new physical location; Akte relationships are untouched (#23).
        document.RecordExternalRelocation(stored.Path, stored.Name);
        document.RecordExternalVersion(stored.Version, stored.ETag, stored.ModifiedAtUtc);
        dbContext.AuditEntries.Add(new AuditEntry(document.TenantId, "document.moved", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);
        return ViewOf(document);
    }

    public async Task<DocumentView> CopyFileAsync(Guid id, CopyDocumentRequest request, CancellationToken cancellationToken = default)
    {
        Validate(nameof(request.TargetFolderPath), request.TargetFolderPath, StorageReference.MaxPathLength);

        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, id, PermissionAction.Copy, cancellationToken);
        // The copy becomes a new document: creating documents must also be allowed.
        await authorization.RequireAsync(user, ResourceType.Document, null, PermissionAction.Upload, cancellationToken);

        var document = await LoadAsync(id, cancellationToken);
        var connector = await connectors.ResolveAsync(document.Storage.ConnectorId, cancellationToken);
        connector.Require(StorageCapability.Copy);

        var stored = await GuardedAsync(document, () => connector.CopyDocumentAsync(
            document.Storage.ExternalId, request.TargetFolderPath, request.NewName, cancellationToken), cancellationToken);

        var tenantId = tenantContext.Require();
        var copy = new Document(tenantId, new StorageReference(
            tenantId,
            document.Storage.ConnectorId,
            stored.ExternalId,
            stored.Path,
            stored.Name,
            stored.Version,
            stored.ETag,
            stored.ModifiedAtUtc));

        dbContext.Documents.Add(copy);
        dbContext.AuditEntries.Add(new AuditEntry(tenantId, "document.copied", user.Subject));
        await MarkRegisteredAsync(copy, stored, cancellationToken);
        return ViewOf(copy);
    }

    public async Task<DocumentView> RenameFileAsync(Guid id, RenameFileRequest request, CancellationToken cancellationToken = default)
    {
        Validate(nameof(request.Name), request.Name, Document.MaxNameLength);

        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Document, id, PermissionAction.Rename, cancellationToken);

        var document = await LoadAsync(id, cancellationToken);
        var connector = await connectors.ResolveAsync(document.Storage.ConnectorId, cancellationToken);
        connector.Require(StorageCapability.Rename);

        var stored = await GuardedAsync(document, () => connector.RenameDocumentAsync(
            document.Storage.ExternalId, request.Name, ETagOrKnown(document, request.ETag), cancellationToken), cancellationToken);

        // Same DocumentId, new filename; Akte relationships are untouched (#22).
        document.RecordExternalRelocation(stored.Path, stored.Name);
        document.RecordExternalVersion(stored.Version, stored.ETag, stored.ModifiedAtUtc);
        dbContext.AuditEntries.Add(new AuditEntry(document.TenantId, "document.file.renamed", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);
        return ViewOf(document);
    }

    /// <summary>Optimistic concurrency token: the client's If-Match wins, otherwise the last known ETag. Never silently overwrite external changes (#9/#28).</summary>
    private static string? ETagOrKnown(Document document, string? requested)
        => requested ?? document.Storage.ETag;

    /// <summary>
    /// Runs a connector operation and keeps the document state honest on failure:
    /// the operation did not succeed, so it is never reported as such, <em>and</em>
    /// the known storage state is corrected where the failure proves it stale
    /// (NotFound → Missing, Unavailable/lock → Unavailable, expired credentials →
    /// AuthenticationRequired). State transitions are explicit and audited (#31,#105).
    /// </summary>
    private async Task<T> GuardedAsync<T>(Document document, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (StorageException exception)
        {
            switch (exception.Kind)
            {
                case StorageErrorKind.NotFound:
                    document.MarkMissing();
                    break;
                case StorageErrorKind.Unavailable:
                    document.MarkUnavailable();
                    break;
                case StorageErrorKind.AuthenticationRequired:
                    document.MarkAuthenticationRequired();
                    break;
                default:
                    throw; // authorization/concurrency/validation/conflict: no state change implied
            }

            dbContext.AuditEntries.Add(new AuditEntry(
                document.TenantId, $"document.storage.state.{document.State.ToString().ToLowerInvariant()}", currentUser.Require().Subject));
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>Persists a freshly uploaded/copied document as Available and processes the unique-key race.</summary>
    private async Task MarkRegisteredAsync(Document document, StorageItem stored, CancellationToken cancellationToken)
    {
        // No staleness guard here (RecordExternalVersion ignores same-timestamp writes):
        // this is the initial registration, the state transitions Pending → Available.
        document.Storage.RecordVersion(stored.Version, stored.ETag, stored.ModifiedAtUtc);
        document.Storage.MarkAvailable();
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new ConflictException(
                "A document for this storage object (connector + external id) already exists.");
        }
    }

    /// <summary>Best-effort physical cleanup after an aborted registration; never throws.</summary>
    private static async Task TryDeletePhysicalAsync(IStorageConnector connector, string externalId, CancellationToken cancellationToken)
    {
        try
        {
            await connector.DeleteDocumentAsync(externalId, cancellationToken: cancellationToken);
        }
        catch (StorageException)
        {
            // cleanup is best effort; the orphaned file is reconciled by synchronization
        }
    }

    private DocumentView ViewOf(Document document)
        => new(
            document.Id,
            document.Name,
            document.State,
            document.Processing,
            ToView(document.Storage),
            Xmin(document),
            document.CreatedAtUtc,
            document.UpdatedAtUtc);

    private async Task<Document> LoadAsync(Guid id, CancellationToken cancellationToken)
        => await dbContext.Documents
               .Include(document => document.Storage)
               .SingleOrDefaultAsync(document => document.Id == id, cancellationToken)
           ?? throw new NotFoundException("document", id);
    // Cross-tenant reads return nothing through the query filter, so a wrong-tenant id
    // surfaces as NotFound, never as another tenant's data.

    private uint Xmin(Document document)
        => (uint)dbContext.Entry(document).Property("xmin").CurrentValue!;

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static void Validate(string field, string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ValidationException($"{field} is required (max {maxLength} chars).");
        }
    }

    private static StorageReferenceView ToView(StorageReference reference)
        => new(
            reference.ConnectorId,
            reference.ExternalId,
            reference.Path,
            reference.Name,
            reference.Version,
            reference.ETag,
            reference.LastKnownModifiedAtUtc,
            reference.SyncState);
}

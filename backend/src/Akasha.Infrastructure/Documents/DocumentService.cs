using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Documents;
using Akasha.Application.Identity;
using Akasha.Application.Tenancy;
using Akasha.Domain.Audit;
using Akasha.Domain.Authorization;
using Akasha.Domain.Documents;
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
    ITenantContext tenantContext) : IDocumentService
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

using Akasha.Application.Akten;
using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Documents;
using Akasha.Application.Identity;
using Akasha.Application.Tenancy;
using Akasha.Domain.Akten;
using Akasha.Domain.Audit;
using Akasha.Domain.Authorization;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Akasha.Infrastructure.Akten;

/// <summary>
/// PostgreSQL-backed <see cref="IAkteService"/>. Same rules as documents: DbContext query
/// filters + write guard enforce tenant isolation, authorization runs before any data
/// access (#10), xmin is the optimistic concurrency token (#28), mutations are audited.
/// Membership references the logical DocumentId only - never storage paths (#42).
/// </summary>
public sealed class AkteService(
    AkashaDbContext dbContext,
    IAuthorizationService authorization,
    ICurrentUser currentUser,
    ITenantContext tenantContext) : IAkteService
{
    public async Task<PagedResult<AkteView>> ListAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100)
        {
            throw new ValidationException("Invalid pagination: skip >= 0 and 1 <= take <= 100.");
        }

        var user = currentUser.Require();

        // ponytail: type-level read only, same trade-off as documents; per-row grant
        // filtering joins the grant table once resource-level Akte grants are used.
        await authorization.RequireAsync(user, ResourceType.Akte, null, PermissionAction.Read, cancellationToken);

        var query = dbContext.Akten.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(akte => akte.CreatedAtUtc)
            .Skip(skip)
            .Take(take)
            .Select(akte => new { akte, xmin = EF.Property<uint>(akte, "xmin") })
            .ToListAsync(cancellationToken);

        // Membership ids are intentionally not loaded in lists (Get loads them); avoids
        // an N+1/aggregation query the list view does not need.
        var items = rows
            .Select(row => ToView(row.akte, row.xmin, []))
            .ToList();

        return new PagedResult<AkteView>(items, total, skip, take);
    }

    public async Task<AkteView> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Akte, id, PermissionAction.Read, cancellationToken);

        var akte = await LoadAsync(id, cancellationToken);
        var documentIds = await DocumentIdsAsync(id, cancellationToken);
        return ToView(akte, Xmin(akte), documentIds);
    }

    public async Task<AkteView> CreateAsync(CreateAkteRequest request, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Akte, null, PermissionAction.Write, cancellationToken);

        ValidateName(request.Name);
        ValidateDescription(request.Description);

        var tenantId = tenantContext.Require();
        var akte = new Akte(tenantId, request.Name, request.Description);
        dbContext.Akten.Add(akte);
        dbContext.AuditEntries.Add(new AuditEntry(tenantId, "akte.created", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToView(akte, Xmin(akte), []);
    }

    public async Task<AkteView> UpdateAsync(Guid id, UpdateAkteRequest request, CancellationToken cancellationToken = default)
    {
        if (request.RowVersion == 0)
        {
            throw new ValidationException("RowVersion is required.");
        }

        ValidateName(request.Name);
        ValidateDescription(request.Description);

        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Akte, id, PermissionAction.Write, cancellationToken);

        var akte = await LoadAsync(id, cancellationToken);
        RequireActive(akte);

        dbContext.Entry(akte).Property("xmin").OriginalValue = request.RowVersion;
        akte.Update(request.Name, request.Description);
        dbContext.AuditEntries.Add(new AuditEntry(akte.TenantId, "akte.updated", user.Subject));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The Akte was modified by someone else; reload and retry.");
        }

        return ToView(akte, Xmin(akte), await DocumentIdsAsync(id, cancellationToken));
    }

    public async Task<AkteView> ArchiveAsync(Guid id, uint rowVersion, CancellationToken cancellationToken = default)
        => await SetStatusAsync(id, rowVersion, archive: true, cancellationToken);

    public async Task<AkteView> ReactivateAsync(Guid id, uint rowVersion, CancellationToken cancellationToken = default)
        => await SetStatusAsync(id, rowVersion, archive: false, cancellationToken);

    public async Task AttachAsync(Guid akteId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        // Both resources are authorized before any data access (#10).
        await authorization.RequireAsync(user, ResourceType.Akte, akteId, PermissionAction.Write, cancellationToken);
        await authorization.RequireAsync(user, ResourceType.Document, documentId, PermissionAction.Read, cancellationToken);

        var akte = await LoadAsync(akteId, cancellationToken);
        RequireActive(akte);

        // Tenant filter makes cross-tenant documents invisible: they surface as NotFound,
        // never as another tenant's data or a leaked existence signal.
        if (!await dbContext.Documents.AnyAsync(document => document.Id == documentId, cancellationToken))
        {
            throw new NotFoundException("document", documentId);
        }

        var link = new AkteDocument(akte.TenantId, akteId, documentId);
        dbContext.AkteDocuments.Add(link);
        dbContext.AuditEntries.Add(new AuditEntry(akte.TenantId, "akte.document.attached", user.Subject));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Drop the failed insert from the tracker, otherwise the next SaveChanges on
            // this (possibly long-lived test/job) context would silently re-insert it.
            dbContext.Entry(link).State = EntityState.Detached;
            throw new ConflictException("The document is already linked to this Akte.");
        }
    }

    public async Task DetachAsync(Guid akteId, Guid documentId, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Akte, akteId, PermissionAction.Write, cancellationToken);

        var akte = await LoadAsync(akteId, cancellationToken);
        RequireActive(akte);

        var link = await dbContext.AkteDocuments
                .SingleOrDefaultAsync(membership => membership.AkteId == akteId && membership.DocumentId == documentId, cancellationToken)
            ?? throw new NotFoundException("akte document link", documentId);

        dbContext.AkteDocuments.Remove(link);
        dbContext.AuditEntries.Add(new AuditEntry(akte.TenantId, "akte.document.detached", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<AkteView> SetStatusAsync(Guid id, uint rowVersion, bool archive, CancellationToken cancellationToken)
    {
        if (rowVersion == 0)
        {
            throw new ValidationException("RowVersion is required.");
        }

        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Akte, id, PermissionAction.Write, cancellationToken);

        var akte = await LoadAsync(id, cancellationToken);

        dbContext.Entry(akte).Property("xmin").OriginalValue = rowVersion;
        if (archive)
        {
            akte.Archive();
            dbContext.AuditEntries.Add(new AuditEntry(akte.TenantId, "akte.archived", user.Subject));
        }
        else
        {
            akte.Reactivate();
            dbContext.AuditEntries.Add(new AuditEntry(akte.TenantId, "akte.reactivated", user.Subject));
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The Akte was modified by someone else; reload and retry.");
        }

        return ToView(akte, Xmin(akte), await DocumentIdsAsync(id, cancellationToken));
    }

    private async Task<Akte> LoadAsync(Guid id, CancellationToken cancellationToken)
        => await dbContext.Akten.SingleOrDefaultAsync(akte => akte.Id == id, cancellationToken)
            ?? throw new NotFoundException("akte", id);
    // Cross-tenant reads return nothing through the query filter, so a wrong-tenant id
    // surfaces as NotFound, never as another tenant's data.

    private async Task<IReadOnlyList<Guid>> DocumentIdsAsync(Guid akteId, CancellationToken cancellationToken)
        => await dbContext.AkteDocuments.AsNoTracking()
            .Where(membership => membership.AkteId == akteId)
            .OrderBy(membership => membership.LinkedAtUtc)
            .Select(membership => membership.DocumentId)
            .ToListAsync(cancellationToken);

    // An archived Akte is closed for changes: 409, since it conflicts with the current state.
    private static void RequireActive(Akte akte)
    {
        if (akte.IsArchived)
        {
            throw new ConflictException("The Akte is archived; reactivate it before making changes.");
        }
    }

    private uint Xmin(Akte akte)
        => (uint)dbContext.Entry(akte).Property("xmin").CurrentValue!;

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > Akte.MaxNameLength)
        {
            throw new ValidationException($"Name is required (max {Akte.MaxNameLength} chars).");
        }
    }

    private static void ValidateDescription(string? description)
    {
        if (description is not null && description.Length > Akte.MaxDescriptionLength)
        {
            throw new ValidationException($"Description is at most {Akte.MaxDescriptionLength} chars.");
        }
    }

    private static AkteView ToView(Akte akte, uint rowVersion, IReadOnlyList<Guid> documentIds)
        => new(
            akte.Id,
            akte.Name,
            akte.Description,
            akte.Status,
            documentIds,
            rowVersion,
            akte.CreatedAtUtc,
            akte.UpdatedAtUtc);
}

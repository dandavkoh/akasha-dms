using System.Globalization;
using Akasha.Application.Common;
using Akasha.Application.Documents;
using Akasha.Application.Identity;
using Akasha.Application.Search;
using Akasha.Application.Tenancy;
using Akasha.Domain.Authorization;
using Akasha.Domain.Documents;
using Akasha.Domain.Metadata;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Search;

/// <summary>
/// PostgreSQL-backed <see cref="ISearchService"/>. Tenant isolation comes from the DbContext
/// query filters; authorization is part of the query itself (rule #81 "search everything,
/// filter later" is forbidden): a document only matches when an effective READ allow exists
/// for the caller (user / groups / roles, resource- or type-level or tenant-wide) and no
/// matching deny. This mirrors <see cref="Authorization.AuthorizationService"/> exactly.
/// </summary>
public sealed class SearchService(
    AkashaDbContext dbContext,
    ICurrentUser currentUser,
    ITenantContext tenantContext) : ISearchService
{
    private const int MaxTake = 100;
    private const int MaxQueryLength = 200;
    private const int MaxDocumentTypeLength = 16;

    public async Task<PagedResult<DocumentSearchHit>> SearchDocumentsAsync(
        DocumentSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Skip < 0 || request.Take is < 1 or > MaxTake)
        {
            throw new ValidationException($"Invalid pagination: skip >= 0 and 1 <= take <= {MaxTake}.");
        }

        if (request.CreatedFrom > request.CreatedTo || request.UpdatedFrom > request.UpdatedTo)
        {
            throw new ValidationException("Invalid date range: From must not be after To.");
        }

        // Authentication and tenant context are mandatory (rule #7/#85): missing context is
        // a security boundary violation, never silently an empty page.
        var user = currentUser.Require();
        tenantContext.Require();

        var query = FilterVisible(dbContext.Documents.AsNoTracking(), user);
        query = await ApplyFiltersAsync(query, request, cancellationToken);

        var total = await query.CountAsync(cancellationToken);

        var rows = await ApplyOrder(query, request)
            .Skip(request.Skip)
            .Take(request.Take)
            .Select(document => new
            {
                document.Id,
                document.Name,
                document.Processing,
                document.CreatedAtUtc,
                document.UpdatedAtUtc,
                document.Storage.SyncState,
                document.Storage.ConnectorId,
            })
            .ToListAsync(cancellationToken);

        var relevance = request.Sort == DocumentSearchSort.Relevance && !string.IsNullOrWhiteSpace(request.Query);
        var needle = request.Query?.Trim();
        var hits = rows.Select(row => new DocumentSearchHit(
                row.Id,
                row.Name,
                row.SyncState,
                row.Processing,
                row.ConnectorId,
                row.CreatedAtUtc,
                row.UpdatedAtUtc,
                relevance ? Score(row.Name, needle!) : null))
            .ToList();

        return new PagedResult<DocumentSearchHit>(hits, total, request.Skip, request.Take);
    }

    /// <summary>
    /// Restricts the query to documents with an effective READ allow and no matching deny.
    /// Loaded principal ids (groups/roles) are two small queries; the rest runs in SQL.
    /// </summary>
    private IQueryable<Document> FilterVisible(IQueryable<Document> documents, CurrentUser user)
    {
        var groupIds = dbContext.GroupMembers
            .Where(member => member.UserId == user.UserId)
            .Select(member => member.GroupId);

        var roleIds = dbContext.RoleAssignments
            .Where(assignment =>
                (assignment.PrincipalType == PrincipalType.User && assignment.PrincipalId == user.UserId)
                || (assignment.PrincipalType == PrincipalType.Group && groupIds.Contains(assignment.PrincipalId)))
            .Select(assignment => assignment.RoleId);

        // Effective permission = EXISTS allow AND NOT EXISTS deny, identical to
        // AuthorizationService: deny always wins; tenant-wide ADMIN allow covers READ.
        return documents.Where(document =>
            dbContext.PermissionGrants.Any(grant =>
                ((grant.PrincipalType == PrincipalType.User && grant.PrincipalId == user.UserId)
                 || (grant.PrincipalType == PrincipalType.Group && groupIds.Contains(grant.PrincipalId))
                 || (grant.PrincipalType == PrincipalType.Role && roleIds.Contains(grant.PrincipalId)))
                && grant.Effect == GrantEffect.Allow
                && ((grant.ResourceType == ResourceType.Document
                        && (grant.ResourceId == null || grant.ResourceId == document.Id)
                        && grant.Action == PermissionAction.Read)
                    || (grant.ResourceType == ResourceType.Tenant
                        && grant.ResourceId == null
                        && (grant.Action == PermissionAction.Read || grant.Action == PermissionAction.Admin))))
            && !dbContext.PermissionGrants.Any(grant =>
                ((grant.PrincipalType == PrincipalType.User && grant.PrincipalId == user.UserId)
                 || (grant.PrincipalType == PrincipalType.Group && groupIds.Contains(grant.PrincipalId))
                 || (grant.PrincipalType == PrincipalType.Role && roleIds.Contains(grant.PrincipalId)))
                && grant.Effect == GrantEffect.Deny
                && ((grant.ResourceType == ResourceType.Document
                        && (grant.ResourceId == null || grant.ResourceId == document.Id)
                        && grant.Action == PermissionAction.Read)
                    || (grant.ResourceType == ResourceType.Tenant
                        && grant.ResourceId == null
                        && (grant.Action == PermissionAction.Read || grant.Action == PermissionAction.Admin)))));
    }

    private async Task<IQueryable<Document>> ApplyFiltersAsync(
        IQueryable<Document> query, DocumentSearchRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            if (request.Query.Length > MaxQueryLength)
            {
                throw new ValidationException($"Query is too long (max {MaxQueryLength} chars).");
            }

            var pattern = "%" + EscapeLike(request.Query.Trim()) + "%";
            query = query.Where(document =>
                EF.Functions.ILike(document.Name, pattern, "\\")
                // ponytail: query text also scans text-like metadata; full content search
                // (OCR/pdf body) lands in the future search index (#44), not here.
                || dbContext.MetadataValues.Any(value =>
                    value.OwnerType == MetadataOwnerType.Document
                    && value.OwnerId == document.Id
                    && value.TextValue != null
                    && EF.Functions.ILike(value.TextValue, pattern, "\\")));
        }

        if (request.AkteId is { } akteId)
        {
            // Logical Akte membership only - never a storage path (#42).
            query = query.Where(document =>
                dbContext.AkteDocuments.Any(link => link.AkteId == akteId && link.DocumentId == document.Id));
        }

        if (!string.IsNullOrWhiteSpace(request.DocumentType))
        {
            var type = request.DocumentType.Trim().TrimStart('.');
            if (type.Length is < 1 or > MaxDocumentTypeLength
                || !type.All(c => char.IsLetterOrDigit(c) || c is '+' or '#'))
            {
                throw new ValidationException("DocumentType must be a file extension like \"pdf\".");
            }

            query = query.Where(document => EF.Functions.ILike(document.Name, "%." + EscapeLike(type), "\\"));
        }

        if (request.State is { } state)
        {
            query = query.Where(document => document.Storage.SyncState == state);
        }

        if (request.CreatedFrom is { } createdFrom)
        {
            query = query.Where(document => document.CreatedAtUtc >= createdFrom);
        }

        if (request.CreatedTo is { } createdTo)
        {
            query = query.Where(document => document.CreatedAtUtc <= createdTo);
        }

        if (request.UpdatedFrom is { } updatedFrom)
        {
            query = query.Where(document => document.UpdatedAtUtc >= updatedFrom);
        }

        if (request.UpdatedTo is { } updatedTo)
        {
            query = query.Where(document => document.UpdatedAtUtc <= updatedTo);
        }

        if (!string.IsNullOrWhiteSpace(request.MetadataKey))
        {
            query = await ApplyMetadataFilterAsync(query, request, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(request.MetadataValue))
        {
            throw new ValidationException("MetadataValue requires MetadataKey.");
        }

        return query;
    }

    /// <summary>
    /// Typed metadata comparison against the value column matching the definition's field
    /// type. An unknown key or an unparsable value matches nothing or is a validation error -
    /// either way it never leaks document existence.
    /// </summary>
    private async Task<IQueryable<Document>> ApplyMetadataFilterAsync(
        IQueryable<Document> query, DocumentSearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MetadataValue))
        {
            throw new ValidationException("MetadataKey requires MetadataValue.");
        }

        var key = request.MetadataKey!.Trim();
        var definition = await dbContext.MetadataDefinitions
            .SingleOrDefaultAsync(candidate => candidate.Key == key, cancellationToken);

        // Unknown key: match nothing (zero results), never reveal whether the key exists.
        if (definition is null)
        {
            return query.Where(document => false);
        }

        var raw = request.MetadataValue!.Trim();
        var definitionId = definition.Id;
        var values = dbContext.MetadataValues.Where(value =>
            value.OwnerType == MetadataOwnerType.Document
            && value.DefinitionId == definitionId);

        IQueryable<MetadataValue> matching = definition.FieldType switch
        {
            MetadataFieldType.Text => values.Where(value =>
                value.TextValue != null && EF.Functions.ILike(value.TextValue, "%" + EscapeLike(raw) + "%", "\\")),
            MetadataFieldType.Selection => values.Where(value => value.TextValue == raw),
            MetadataFieldType.MultiSelection => values.Where(value => value.SelectionValues.Contains(raw)),
            MetadataFieldType.Number => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? values.Where(value => value.NumberValue == number)
                : throw new ValidationException($"Metadata '{key}' expects a number."),
            MetadataFieldType.Boolean => bool.TryParse(raw, out var boolean)
                ? values.Where(value => value.BooleanValue == boolean)
                : throw new ValidationException($"Metadata '{key}' expects true or false."),
            MetadataFieldType.Date => DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? values.Where(value => value.DateValue == date)
                : throw new ValidationException($"Metadata '{key}' expects a date (yyyy-MM-dd)."),
            MetadataFieldType.DateTime => DateTimeOffset.TryParse(
                                       raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                                   && time.Offset == TimeSpan.Zero
                ? values.Where(value => value.TimestampValue == time)
                : throw new ValidationException($"Metadata '{key}' expects a UTC datetime."),
            _ => throw new ValidationException($"Unsupported metadata field type for '{key}'."),
        };

        return query.Where(document => matching.Any(value => value.OwnerId == document.Id));
    }

    private IOrderedQueryable<Document> ApplyOrder(IQueryable<Document> query, DocumentSearchRequest request)
    {
        var descending = request.Descending;
        IOrderedQueryable<Document> ordered = request.Sort switch
        {
            DocumentSearchSort.Name => descending
                ? query.OrderByDescending(document => document.Name.ToLower())
                : query.OrderBy(document => document.Name.ToLower()),
            DocumentSearchSort.Created => descending
                ? query.OrderByDescending(document => document.CreatedAtUtc)
                : query.OrderBy(document => document.CreatedAtUtc),
            DocumentSearchSort.Updated => descending
                ? query.OrderByDescending(document => document.UpdatedAtUtc)
                : query.OrderBy(document => document.UpdatedAtUtc),
            _ => OrderByRelevance(query, request.Query),
        };

        // Stable secondary key so pagination never returns duplicate/absent rows between pages.
        return ordered.ThenBy(document => document.Id);
    }

    /// <summary>Name matches beat metadata matches; exact > prefix > substring > metadata-only.</summary>
    private static IOrderedQueryable<Document> OrderByRelevance(IQueryable<Document> query, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return query.OrderByDescending(document => document.UpdatedAtUtc);
        }

        var lowered = needle.Trim().ToLowerInvariant();
        var prefix = EscapeLike(lowered) + "%";
        return query
            .OrderByDescending(document => document.Name.ToLower() == lowered)
            .ThenByDescending(document => EF.Functions.ILike(document.Name, prefix, "\\"))
            .ThenByDescending(document => document.Name.ToLower().Contains(lowered))
            .ThenByDescending(document => document.UpdatedAtUtc);
    }

    private static int Score(string name, string needle)
        => string.Equals(name, needle, StringComparison.OrdinalIgnoreCase) ? 3
            : name.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 2
            : 1;

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

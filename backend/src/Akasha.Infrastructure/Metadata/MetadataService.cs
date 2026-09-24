using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Documents;
using Akasha.Application.Identity;
using Akasha.Application.Metadata;
using Akasha.Application.Tenancy;
using Akasha.Domain.Audit;
using Akasha.Domain.Authorization;
using Akasha.Domain.Metadata;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Akasha.Infrastructure.Metadata;

/// <summary>
/// PostgreSQL-backed <see cref="IMetadataService"/>. Authorization runs before any data
/// access (#10): definitions use type-level Metadata grants, values use the owner
/// resource (Document/Akte). Tenant isolation comes from the DbContext query filters;
/// a foreign owner or definition is invisible and surfaces as NotFound. Definition
/// updates use xmin optimistic concurrency (#28); value upserts are protected by the
/// (TenantId, OwnerType, OwnerId, DefinitionId) unique index.
/// </summary>
public sealed class MetadataService(
    AkashaDbContext dbContext,
    IAuthorizationService authorization,
    ICurrentUser currentUser,
    ITenantContext tenantContext) : IMetadataService
{
    public async Task<PagedResult<MetadataDefinitionView>> ListDefinitionsAsync(
        int skip, int take, CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100)
        {
            throw new ValidationException("Invalid pagination: skip >= 0 and 1 <= take <= 100.");
        }

        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Metadata, null, PermissionAction.Read, cancellationToken);

        var query = dbContext.MetadataDefinitions.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(definition => definition.Key)
            .Skip(skip)
            .Take(take)
            .Select(definition => new { definition, xmin = EF.Property<uint>(definition, "xmin") })
            .ToListAsync(cancellationToken);

        var items = rows.Select(row => ToView(row.definition, row.xmin)).ToList();
        return new PagedResult<MetadataDefinitionView>(items, total, skip, take);
    }

    public async Task<MetadataDefinitionView> GetDefinitionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Metadata, null, PermissionAction.Read, cancellationToken);

        var definition = await LoadDefinitionAsync(id, cancellationToken);
        return ToView(definition, Xmin(definition));
    }

    public async Task<MetadataDefinitionView> CreateDefinitionAsync(
        CreateMetadataDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Metadata, null, PermissionAction.Write, cancellationToken);

        var tenantId = tenantContext.Require();
        MetadataDefinition definition;
        try
        {
            definition = new MetadataDefinition(
                tenantId, request.Key, request.FieldType, request.IsRequired, request.Label, request.MaxLength, request.Options);
        }
        catch (ArgumentException exception)
        {
            throw new ValidationException(exception.Message);
        }

        dbContext.MetadataDefinitions.Add(definition);
        dbContext.AuditEntries.Add(new AuditEntry(tenantId, "metadata.definition.created", user.Subject));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(definition).State = EntityState.Detached;
            throw new ConflictException($"A metadata definition with key '{request.Key}' already exists.");
        }

        return ToView(definition, Xmin(definition));
    }

    public async Task<MetadataDefinitionView> UpdateDefinitionAsync(
        Guid id, UpdateMetadataDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.RowVersion == 0)
        {
            throw new ValidationException("RowVersion is required.");
        }

        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Metadata, null, PermissionAction.Write, cancellationToken);

        var definition = await LoadDefinitionAsync(id, cancellationToken);

        dbContext.Entry(definition).Property("xmin").OriginalValue = request.RowVersion;
        try
        {
            definition.Update(request.Label, request.IsRequired, request.MaxLength, request.Options);
        }
        catch (ArgumentException exception)
        {
            throw new ValidationException(exception.Message);
        }

        dbContext.AuditEntries.Add(new AuditEntry(definition.TenantId, "metadata.definition.updated", user.Subject));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The metadata definition was modified by someone else; reload and retry.");
        }

        return ToView(definition, Xmin(definition));
    }

    public async Task DeleteDefinitionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await authorization.RequireAsync(user, ResourceType.Metadata, null, PermissionAction.Write, cancellationToken);

        var definition = await LoadDefinitionAsync(id, cancellationToken);

        // Values cascade via FK: a definition is the schema of its values, both share
        // the same tenant lifetime. Audit survives (#169).
        dbContext.MetadataDefinitions.Remove(definition);
        dbContext.AuditEntries.Add(new AuditEntry(definition.TenantId, "metadata.definition.deleted", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MetadataValueView>> GetValuesAsync(
        MetadataOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await RequireOwnerAccessAsync(user, ownerType, ownerId, PermissionAction.Read, cancellationToken);

        return await ValueQuery(ownerType, ownerId).ToListAsync(cancellationToken);
    }

    public async Task<MetadataValueView> SetValueAsync(
        MetadataOwnerType ownerType,
        Guid ownerId,
        string definitionKey,
        MetadataValueInput value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(definitionKey))
        {
            throw new ValidationException("Definition key is required.");
        }

        var user = currentUser.Require();
        // Both the owner and the change itself are authorized before any data access (#10).
        await RequireOwnerAccessAsync(user, ownerType, ownerId, PermissionAction.EditMetadata, cancellationToken);

        var definition = await dbContext.MetadataDefinitions
            .SingleOrDefaultAsync(candidate => candidate.Key == definitionKey, cancellationToken)
            ?? throw new NotFoundException("metadata definition", ownerId);

        if (definition.Validate(value) is { } error)
        {
            throw new ValidationException(error);
        }

        var existing = await dbContext.MetadataValues
            .SingleOrDefaultAsync(candidate
                => candidate.OwnerType == ownerType && candidate.OwnerId == ownerId && candidate.DefinitionId == definition.Id,
                cancellationToken);

        if (existing is null)
        {
            existing = new MetadataValue(definition.TenantId, ownerType, ownerId, definition.Id);
            existing.Apply(definition, value);
            dbContext.MetadataValues.Add(existing);
        }
        else
        {
            existing.Apply(definition, value);
        }

        dbContext.AuditEntries.Add(new AuditEntry(definition.TenantId, "metadata.value.set", user.Subject));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Concurrent first insert for the same (owner, definition): nothing was overwritten (#28).
            dbContext.Entry(existing).State = EntityState.Detached;
            throw new ConflictException("The metadata value was modified concurrently; retry.");
        }

        return ToView(definition, existing);
    }

    public async Task RemoveValueAsync(
        MetadataOwnerType ownerType, Guid ownerId, string definitionKey, CancellationToken cancellationToken = default)
    {
        var user = currentUser.Require();
        await RequireOwnerAccessAsync(user, ownerType, ownerId, PermissionAction.EditMetadata, cancellationToken);

        var definitionId = await dbContext.MetadataDefinitions.AsNoTracking()
            .Where(candidate => candidate.Key == definitionKey)
            .Select(candidate => candidate.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (definitionId == Guid.Empty)
        {
            throw new NotFoundException("metadata definition", ownerId);
        }

        var value = await dbContext.MetadataValues
            .SingleOrDefaultAsync(candidate
                => candidate.OwnerType == ownerType && candidate.OwnerId == ownerId && candidate.DefinitionId == definitionId,
                cancellationToken)
            ?? throw new NotFoundException("metadata value", ownerId);

        dbContext.MetadataValues.Remove(value);
        dbContext.AuditEntries.Add(new AuditEntry(value.TenantId, "metadata.value.removed", user.Subject));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Authorizes the owner resource AND verifies the owner exists in this tenant.</summary>
    private async Task RequireOwnerAccessAsync(
        CurrentUser user, MetadataOwnerType ownerType, Guid ownerId,
        PermissionAction action, CancellationToken cancellationToken)
    {
        var resourceType = ownerType switch
        {
            MetadataOwnerType.Document => ResourceType.Document,
            MetadataOwnerType.Akte => ResourceType.Akte,
            _ => throw new ValidationException($"Unknown owner type '{ownerType}'."),
        };

        await authorization.RequireAsync(user, resourceType, ownerId, action, cancellationToken);

        // Tenant filter applies: a foreign owner's id surfaces as NotFound, never as
        // another tenant's data or a leaked existence signal.
        var exists = ownerType switch
        {
            MetadataOwnerType.Document => await dbContext.Documents.AnyAsync(document => document.Id == ownerId, cancellationToken),
            _ => await dbContext.Akten.AnyAsync(akte => akte.Id == ownerId, cancellationToken),
        };

        if (!exists)
        {
            throw new NotFoundException(ownerType.ToString().ToLowerInvariant(), ownerId);
        }
    }

    private IQueryable<MetadataValueView> ValueQuery(MetadataOwnerType ownerType, Guid ownerId)
        => from value in dbContext.MetadataValues.AsNoTracking()
           where value.OwnerType == ownerType && value.OwnerId == ownerId
           join definition in dbContext.MetadataDefinitions.AsNoTracking()
               on value.DefinitionId equals definition.Id
           orderby definition.Key
           select new MetadataValueView(
               definition.Id,
               definition.Key,
               definition.Label,
               definition.FieldType,
               definition.IsRequired,
               value.TextValue,
               value.NumberValue,
               value.BooleanValue,
               value.DateValue,
               value.TimestampValue,
               value.SelectionValues,
               value.UpdatedAtUtc);

    private async Task<MetadataDefinition> LoadDefinitionAsync(Guid id, CancellationToken cancellationToken)
        => await dbContext.MetadataDefinitions.SingleOrDefaultAsync(definition => definition.Id == id, cancellationToken)
            ?? throw new NotFoundException("metadata definition", id);
    // Cross-tenant reads return nothing through the query filter, so a wrong-tenant id
    // surfaces as NotFound, never as another tenant's data.

    private uint Xmin(MetadataDefinition definition)
        => (uint)dbContext.Entry(definition).Property("xmin").CurrentValue!;

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static MetadataDefinitionView ToView(MetadataDefinition definition, uint rowVersion)
        => new(
            definition.Id,
            definition.Key,
            definition.Label,
            definition.FieldType,
            definition.IsRequired,
            definition.MaxLength,
            definition.Options,
            rowVersion,
            definition.CreatedAtUtc,
            definition.UpdatedAtUtc);

    private static MetadataValueView ToView(MetadataDefinition definition, MetadataValue value)
        => new(
            definition.Id,
            definition.Key,
            definition.Label,
            definition.FieldType,
            definition.IsRequired,
            value.TextValue,
            value.NumberValue,
            value.BooleanValue,
            value.DateValue,
            value.TimestampValue,
            value.SelectionValues,
            value.UpdatedAtUtc);
}

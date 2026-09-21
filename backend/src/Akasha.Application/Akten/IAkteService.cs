using Akasha.Application.Documents;
using Akasha.Domain.Akten;

namespace Akasha.Application.Akten;

public sealed record AkteView(
    Guid Id,
    string Name,
    string? Description,
    AkteStatus Status,
    /// <summary>Logical DocumentIds contained in this Akte; populated by Get, empty on List.</summary>
    IReadOnlyList<Guid> DocumentIds,
    /// <summary>Optimistic concurrency token (PostgreSQL xmin); required for updates.</summary>
    uint RowVersion,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateAkteRequest(string Name, string? Description = null);

public sealed record UpdateAkteRequest(string Name, string? Description, uint RowVersion);

/// <summary>
/// Application service for Akten - the logical business container, independent of any
/// physical storage location. Tenant context, authorization-before-data-access (#10),
/// xmin optimistic concurrency and audit follow the same rules as documents.
/// Document membership references logical DocumentIds only (#42).
/// </summary>
public interface IAkteService
{
    Task<PagedResult<AkteView>> ListAsync(int skip, int take, CancellationToken cancellationToken = default);

    Task<AkteView> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AkteView> CreateAsync(CreateAkteRequest request, CancellationToken cancellationToken = default);

    Task<AkteView> UpdateAsync(Guid id, UpdateAkteRequest request, CancellationToken cancellationToken = default);

    Task<AkteView> ArchiveAsync(Guid id, uint rowVersion, CancellationToken cancellationToken = default);

    Task<AkteView> ReactivateAsync(Guid id, uint rowVersion, CancellationToken cancellationToken = default);

    /// <summary>Links a logical document (by DocumentId) into the Akte. Idempotent-ish: duplicates conflict.</summary>
    Task AttachAsync(Guid akteId, Guid documentId, CancellationToken cancellationToken = default);

    Task DetachAsync(Guid akteId, Guid documentId, CancellationToken cancellationToken = default);
}

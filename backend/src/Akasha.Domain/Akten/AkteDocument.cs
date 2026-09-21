using Akasha.Domain.Common;

namespace Akasha.Domain.Akten;

/// <summary>
/// Membership of a logical document in an Akte (rules #11, #42). References the stable
/// DocumentId - never a path, filename or provider URL - so the relation survives
/// external rename/move and works across different storage connectors within one Akte.
/// </summary>
public sealed class AkteDocument : ITenantScoped
{
    private AkteDocument()
    {
    }

    public AkteDocument(Guid tenantId, Guid akteId, Guid documentId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (akteId == Guid.Empty)
        {
            throw new ArgumentException("Akte id must not be empty.", nameof(akteId));
        }

        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document id must not be empty.", nameof(documentId));
        }

        TenantId = tenantId;
        AkteId = akteId;
        DocumentId = documentId;
        LinkedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    public Guid AkteId { get; private set; }

    /// <summary>Logical document identity - never resolved through storage paths.</summary>
    public Guid DocumentId { get; private set; }

    public DateTimeOffset LinkedAtUtc { get; private set; }
}

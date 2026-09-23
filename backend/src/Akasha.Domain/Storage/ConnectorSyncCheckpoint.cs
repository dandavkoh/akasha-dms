using Akasha.Domain.Common;

namespace Akasha.Domain.Storage;

/// <summary>
/// Resume state of the incremental change tracking for one (tenant, connector) pair
/// (rule #103). The cursor is opaque — only the owning connector understands it.
/// </summary>
public sealed class ConnectorSyncCheckpoint : ITenantScoped
{
    public const int MaxCursorLength = 1024;

    private ConnectorSyncCheckpoint()
    {
    }

    public ConnectorSyncCheckpoint(Guid tenantId, Guid connectorId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (connectorId == Guid.Empty)
        {
            throw new ArgumentException("Connector id must not be empty.", nameof(connectorId));
        }

        TenantId = tenantId;
        ConnectorId = connectorId;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    public Guid ConnectorId { get; private set; }

    /// <summary>Opaque resume token from the connector's last <c>GetChangesAsync</c>.</summary>
    public string? Cursor { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void RecordCursor(string? cursor)
    {
        if (cursor is { Length: > MaxCursorLength })
        {
            throw new ArgumentException($"Cursor must not exceed {MaxCursorLength} chars.", nameof(cursor));
        }

        Cursor = cursor;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}

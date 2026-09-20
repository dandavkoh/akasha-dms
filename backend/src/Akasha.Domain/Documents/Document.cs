using Akasha.Domain.Common;
using Akasha.Domain.Storage;

namespace Akasha.Domain.Documents;

/// <summary>
/// The central logical document of Akasha. Its identity (<see cref="Id"/>, a server-generated
/// "DocumentId") never depends on filename, path, URL or any provider-specific identifier.
/// Physical rename/move/external deletion only ever mutate the <see cref="Storage"/> reference,
/// never the logical identity.
///
/// State model (backend rules #31, #109, #110):
/// - availability/sync: <see cref="State"/> (delegated to <see cref="StorageReference.SyncState"/>)
/// - processing: <see cref="Processing"/> (separate dimension, never conflated)
/// </summary>
public sealed class Document : ITenantScoped
{
    public const int MaxNameLength = StorageReference.MaxNameLength;

    private Document()
    {
    }

    public Document(Guid tenantId, StorageReference storage)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        ArgumentNullException.ThrowIfNull(storage);
        if (storage.TenantId != tenantId)
        {
            throw new ArgumentException("Storage reference belongs to a different tenant.", nameof(storage));
        }

        TenantId = tenantId;
        Storage = storage;
        Name = storage.Name;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        UpdatedAtUtc = CreatedAtUtc;
    }

    /// <summary>Stable logical identity (the "DocumentId"). Never derived from storage.</summary>
    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid TenantId { get; private set; }

    /// <summary>Current logical display name; tracks external renames but is not an identity.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Where and how the physical file is currently represented in external storage.</summary>
    public StorageReference Storage { get; private set; } = null!;

    /// <summary>Availability/sync relationship with the physical object.</summary>
    public StorageSyncState State => Storage.SyncState;

    public ProcessingState Processing { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Logical rename initiated inside Akasha (physical rename happens via connector).</summary>
    public void Rename(string name)
    {
        Name = CheckName(name);
        Touch();
    }

    /// <summary>
    /// External rename/move (rule #22/#23): the physical location changed, the logical
    /// document and all its relationships stay untouched.
    /// </summary>
    public void RecordExternalRelocation(string newPath, string newName)
    {
        Storage.RecordRelocation(newPath, newName);
        Name = CheckName(newName);
        Touch();
    }

    /// <summary>
    /// External modification detected (rule #24): records the newest known provider version.
    /// Stale changes (older or equal modification timestamp) are ignored (rule #102) instead
    /// of overwriting a newer known state.
    /// </summary>
    public void RecordExternalVersion(string? version, string? eTag, DateTimeOffset? modifiedAtUtc)
    {
        if (modifiedAtUtc is not null
            && Storage.LastKnownModifiedAtUtc is not null
            && modifiedAtUtc <= Storage.LastKnownModifiedAtUtc)
        {
            return; // out-of-order or duplicate change
        }

        Storage.RecordVersion(version, eTag, modifiedAtUtc);
        Storage.MarkAvailable();
        Processing = ProcessingState.Pending;
        Touch();
    }

    /// <summary>Physical file externally deleted: logical identity, metadata and history survive (rule #25).</summary>
    public void MarkMissing()
    {
        Storage.MarkMissing();
        Touch();
    }

    /// <summary>
    /// A previously missing physical object reappeared and was reliably associated via its
    /// stable external ID (rule #26): the document becomes available again. Ambiguous
    /// restoration must NOT call this; it requires explicit reconciliation.
    /// </summary>
    public void MarkRestored(string? version, string? eTag, DateTimeOffset? modifiedAtUtc)
    {
        Storage.RecordVersion(version, eTag, modifiedAtUtc);
        Storage.MarkAvailable();
        Processing = ProcessingState.Pending;
        Touch();
    }

    /// <summary>The whole connector is unreachable; last known state is preserved (rule #105).</summary>
    public void MarkConnectorUnavailable()
    {
        Storage.MarkConnectorUnavailable();
        Touch();
    }

    /// <summary>Single object temporarily unreachable.</summary>
    public void MarkUnavailable()
    {
        Storage.MarkUnavailable();
        Touch();
    }

    /// <summary>Synchronization failed; needs retry or administrative attention.</summary>
    public void MarkSyncError()
    {
        Storage.MarkSyncError();
        Touch();
    }

    /// <summary>Connector credentials expired; not a document problem (rule #138).</summary>
    public void MarkAuthenticationRequired()
    {
        Storage.MarkAuthenticationRequired();
        Touch();
    }

    public void BeginProcessing()
    {
        Processing = ProcessingState.Processing;
        Touch();
    }

    public void MarkProcessed()
    {
        Processing = ProcessingState.Processed;
        Touch();
    }

    /// <summary>Processing failed. Availability is NOT affected (rule #46/#110).</summary>
    public void MarkProcessingFailed()
    {
        Processing = ProcessingState.Failed;
        Touch();
    }

    private void Touch() => UpdatedAtUtc = DateTimeOffset.UtcNow;

    private static string CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Name is required (max {MaxNameLength} chars).", nameof(name));
        }

        return name;
    }
}

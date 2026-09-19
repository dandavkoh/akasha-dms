using Akasha.Domain.Storage;

namespace Akasha.Application.Tests;

public sealed class StorageReferenceTests
{
    private static StorageReference CreateReference() =>
        new(Guid.NewGuid(), "ext-1", "/akte-1", "contract.pdf", version: "5", eTag: "etag-5",
            lastKnownModifiedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void External_Rename_Or_Move_Updates_Location_But_Never_Identity()
    {
        var reference = CreateReference();
        var (id, connectorId, externalId) = (reference.Id, reference.ConnectorId, reference.ExternalId);

        reference.RecordRelocation("/akte-2/sub", "vertrag.pdf");

        Assert.Equal(id, reference.Id);                    // logical identity stable
        Assert.Equal(connectorId, reference.ConnectorId);  // storage identity stable
        Assert.Equal(externalId, reference.ExternalId);
        Assert.Equal("/akte-2/sub", reference.Path);
        Assert.Equal("vertrag.pdf", reference.Name);
    }

    [Fact]
    public void External_Modification_Updates_Version_And_ETag()
    {
        var reference = CreateReference();
        var now = DateTimeOffset.UtcNow;

        reference.RecordVersion("6", "etag-6", now);

        Assert.Equal("6", reference.Version);
        Assert.Equal("etag-6", reference.ETag);
        Assert.Equal(now, reference.LastKnownModifiedAtUtc);
    }

    [Fact]
    public void External_Deletion_Marks_Missing_And_Preserves_Last_Known_State()
    {
        var reference = CreateReference();

        reference.MarkMissing();

        Assert.Equal(StorageSyncState.Missing, reference.SyncState);
        Assert.Equal("ext-1", reference.ExternalId);
        Assert.Equal("contract.pdf", reference.Name);
        Assert.Equal("5", reference.Version); // last known version retained
    }

    [Fact]
    public void Restoration_After_Missing_Marks_Available()
    {
        var reference = CreateReference();
        reference.MarkMissing();

        reference.RecordVersion("7", "etag-7", DateTimeOffset.UtcNow);
        reference.MarkAvailable();

        Assert.Equal(StorageSyncState.Available, reference.SyncState);
        Assert.Equal("7", reference.Version);
    }

    [Fact]
    public void Validation_Rejects_Empty_Connector_And_Blank_Identity()
    {
        Assert.Throws<ArgumentException>(() =>
            new StorageReference(Guid.Empty, "ext", "/p", "n"));
        Assert.Throws<ArgumentException>(() =>
            new StorageReference(Guid.NewGuid(), "", "/p", "n"));
        Assert.Throws<ArgumentException>(() =>
            new StorageReference(Guid.NewGuid(), "ext", "/p", " "));
    }
}

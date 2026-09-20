using Akasha.Domain.Documents;
using Akasha.Domain.Storage;

namespace Akasha.Application.Tests;

public sealed class DocumentTests
{
    private static Document CreateDocument()
        => new(Guid.NewGuid(), new StorageReference(
            Guid.NewGuid(), // mismatch with tenant below is tested separately
            Guid.NewGuid(), "ext-1", "/akte-1", "contract.pdf",
            version: "5", eTag: "etag-5", lastKnownModifiedAtUtc: DateTimeOffset.UtcNow));

    private static (Guid TenantId, Document Document) CreateMatching()
    {
        var tenantId = Guid.NewGuid();
        var storage = new StorageReference(
            tenantId, Guid.NewGuid(), "ext-1", "/akte-1", "contract.pdf",
            version: "5", eTag: "etag-5", lastKnownModifiedAtUtc: DateTimeOffset.UtcNow);
        return (tenantId, new Document(tenantId, storage));
    }

    [Fact]
    public void Document_Id_Is_Server_Generated_And_Independent_Of_Storage()
    {
        var (_, document) = CreateMatching();

        Assert.NotEqual(Guid.Empty, document.Id);
        Assert.NotEqual(document.Storage.Id, document.Id);
        Assert.NotEqual(document.Storage.ConnectorId, document.Id);
        Assert.Equal("contract.pdf", document.Name);
        Assert.Equal(StorageSyncState.Pending, document.State);
        Assert.Equal(ProcessingState.None, document.Processing);
    }

    [Fact]
    public void Constructor_Rejects_Storage_Reference_Of_Another_Tenant()
    {
        Assert.Throws<ArgumentException>(() => CreateDocument());
    }

    [Fact]
    public void External_Rename_Or_Move_Keeps_Document_Id_And_Updates_Location()
    {
        var (_, document) = CreateMatching();
        var id = document.Id;

        document.RecordExternalRelocation("/akte-2/sub", "vertrag.pdf");

        Assert.Equal(id, document.Id);
        Assert.Equal("vertrag.pdf", document.Name);
        Assert.Equal("/akte-2/sub", document.Storage.Path);
        Assert.Equal("vertrag.pdf", document.Storage.Name);
        Assert.Equal("ext-1", document.Storage.ExternalId);
    }

    [Fact]
    public void External_Modification_Updates_Version_And_Schedules_Processing()
    {
        var (_, document) = CreateMatching();
        document.Storage.MarkAvailable();
        var now = DateTimeOffset.UtcNow;

        document.RecordExternalVersion("6", "etag-6", now);

        Assert.Equal(StorageSyncState.Available, document.State);
        Assert.Equal("6", document.Storage.Version);
        Assert.Equal("etag-6", document.Storage.ETag);
        Assert.Equal(ProcessingState.Pending, document.Processing);
    }

    [Fact]
    public void Out_Of_Order_Or_Duplicate_Change_Is_Ignored()
    {
        var (_, document) = CreateMatching();
        var now = DateTimeOffset.UtcNow;
        document.RecordExternalVersion("6", "etag-6", now);
        document.MarkProcessed();

        document.RecordExternalVersion("7", "etag-stale", now.AddMinutes(-5)); // older
        document.RecordExternalVersion("6", "etag-6", now);                    // duplicate

        Assert.Equal("6", document.Storage.Version);
        Assert.Equal("etag-6", document.Storage.ETag);
        Assert.Equal(ProcessingState.Processed, document.Processing);
    }

    [Fact]
    public void External_Deletion_Marks_Missing_And_Preserves_Last_Known_State()
    {
        var (_, document) = CreateMatching();

        document.MarkMissing();

        Assert.Equal(StorageSyncState.Missing, document.State);
        Assert.Equal("contract.pdf", document.Name);
        Assert.Equal("ext-1", document.Storage.ExternalId);
        Assert.Equal("5", document.Storage.Version);
    }

    [Fact]
    public void Restoration_After_Missing_Marks_Available_Again()
    {
        var (_, document) = CreateMatching();
        document.MarkMissing();

        document.MarkRestored("7", "etag-7", DateTimeOffset.UtcNow);

        Assert.Equal(StorageSyncState.Available, document.State);
        Assert.Equal("7", document.Storage.Version);
        Assert.Equal(ProcessingState.Pending, document.Processing);
    }

    [Fact]
    public void Connector_Outage_Keeps_Last_Known_State_And_Marks_ConnectorUnavailable()
    {
        var (_, document) = CreateMatching();
        document.Storage.MarkAvailable();

        document.MarkConnectorUnavailable();

        Assert.Equal(StorageSyncState.ConnectorUnavailable, document.State);
        Assert.Equal("5", document.Storage.Version);
    }

    [Fact]
    public void Processing_Failure_Does_Not_Affect_Availability()
    {
        var (_, document) = CreateMatching();
        document.Storage.MarkAvailable();

        document.BeginProcessing();
        document.MarkProcessingFailed();

        Assert.Equal(StorageSyncState.Available, document.State);
        Assert.Equal(ProcessingState.Failed, document.Processing);
    }

    [Fact]
    public void Logical_Rename_Validates_And_Updates_Name()
    {
        var (_, document) = CreateMatching();

        document.Rename("vertrag.pdf");

        Assert.Equal("vertrag.pdf", document.Name);
        Assert.Throws<ArgumentException>(() => document.Rename(""));
        Assert.Throws<ArgumentException>(() => document.Rename(new string('x', Document.MaxNameLength + 1)));
    }
}

using System.Text;
using Akasha.Application.Storage;

namespace Akasha.Application.Tests;

/// <summary>
/// Contract-style tests against the abstraction itself, using an in-memory fake.
/// No provider-specific types involved — the point of the phase.
/// </summary>
public sealed class StorageConnectorTests
{
    /// <summary>Minimal fake connector: read/write/rename/change-tracking, nothing else.</summary>
    private sealed class FakeConnector : StorageConnectorBase
    {
        private readonly Dictionary<string, (string Path, string Name, byte[] Content, string ETag)> _files = new();
        private readonly List<StorageChange> _changes = new();

        public override StorageCapability Capabilities { get; } =
            StorageCapability.Read | StorageCapability.Write | StorageCapability.Rename | StorageCapability.ChangeTracking;

        public override Task<StorageItem> GetDocumentAsync(string externalId, CancellationToken cancellationToken = default)
        {
            if (!_files.TryGetValue(externalId, out var f))
            {
                throw new StorageException(StorageErrorKind.NotFound, "not found", isTransient: false);
            }

            return Task.FromResult(new StorageItem
            {
                ExternalId = externalId,
                Path = f.Path,
                Name = f.Name,
                Size = f.Content.Length,
                ETag = f.ETag,
            });
        }

        public override Task<StorageItem> CreateDocumentAsync(string folderPath, string name, Stream content, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            var id = Guid.NewGuid().ToString("N");
            _files[id] = (folderPath, name, ms.ToArray(), "v1");
            _changes.Add(new StorageChange { Type = StorageChangeType.Created, ExternalId = id, Path = folderPath, Name = name, ETag = "v1" });
            return Task.FromResult(new StorageItem { ExternalId = id, Path = folderPath, Name = name, Size = ms.Length, ETag = "v1" });
        }

        public override async Task<StorageItem> RenameDocumentAsync(string externalId, string newName, string? expectedETag = null, CancellationToken cancellationToken = default)
        {
            var item = await GetDocumentAsync(externalId, cancellationToken);
            if (expectedETag is not null && expectedETag != item.ETag)
            {
                throw new StorageException(StorageErrorKind.ConcurrencyConflict, "etag mismatch", isTransient: false);
            }

            var f = _files[externalId];
            _files[externalId] = f with { Name = newName, ETag = f.ETag + "+" };
            _changes.Add(new StorageChange { Type = StorageChangeType.Renamed, ExternalId = externalId, Name = newName, PreviousName = f.Name, ETag = _files[externalId].ETag });
            return await GetDocumentAsync(externalId, cancellationToken);
        }

        public override Task<Stream> DownloadAsync(string externalId, CancellationToken cancellationToken = default)
        {
            if (!_files.TryGetValue(externalId, out var f))
            {
                throw new StorageException(StorageErrorKind.NotFound, "not found", isTransient: false);
            }

            return Task.FromResult<Stream>(new MemoryStream(f.Content, writable: false));
        }

        public override Task<StorageChangePage> GetChangesAsync(string? cursor, CancellationToken cancellationToken = default)
        {
            var from = cursor is null ? 0 : int.Parse(cursor);
            var page = _changes.Skip(from).ToList();
            return Task.FromResult(new StorageChangePage { Changes = page, NextCursor = (from + page.Count).ToString() });
        }
    }

    [Fact]
    public async Task Base_Defaults_To_UnsupportedOperation_For_Missing_Capabilities()
    {
        var connector = new FakeConnector();

        var move = await Assert.ThrowsAsync<StorageException>(
            () => connector.MoveDocumentAsync("x", "/target"));
        Assert.Equal(StorageErrorKind.UnsupportedOperation, move.Kind);
        Assert.False(move.IsTransient);

        await Assert.ThrowsAsync<StorageException>(() => connector.GetPermissionsAsync("x"));
        await Assert.ThrowsAsync<StorageException>(() => connector.GetVersionsAsync("x"));
    }

    [Fact]
    public void Supports_And_Require_Reflect_Declared_Capabilities()
    {
        IStorageConnector connector = new FakeConnector();

        Assert.True(connector.Supports(StorageCapability.Read));
        Assert.True(connector.Supports(StorageCapability.Rename));
        Assert.False(connector.Supports(StorageCapability.Move));
        Assert.False(connector.Supports(StorageCapability.Versioning));

        connector.Require(StorageCapability.Read);
        var ex = Assert.Throws<StorageException>(() => connector.Require(StorageCapability.Locking));
        Assert.Equal(StorageErrorKind.UnsupportedOperation, ex.Kind);
    }

    [Fact]
    public async Task Create_Download_Rename_Roundtrip_Keeps_ExternalId()
    {
        IStorageConnector connector = new FakeConnector();

        var created = await connector.CreateDocumentAsync("/akte-1", "contract.pdf", new MemoryStream(Encoding.UTF8.GetBytes("hello")));

        using var download = await connector.DownloadAsync(created.ExternalId);
        using var reader = new StreamReader(download);
        Assert.Equal("hello", await reader.ReadToEndAsync());

        var renamed = await connector.RenameDocumentAsync(created.ExternalId, "vertrag.pdf");
        Assert.Equal(created.ExternalId, renamed.ExternalId);
        Assert.Equal("vertrag.pdf", renamed.Name);
    }

    [Fact]
    public async Task Rename_With_Stale_ETag_Fails_With_ConcurrencyConflict()
    {
        IStorageConnector connector = new FakeConnector();
        var created = await connector.CreateDocumentAsync("/", "a.txt", new MemoryStream([1]));

        var ex = await Assert.ThrowsAsync<StorageException>(
            () => connector.RenameDocumentAsync(created.ExternalId, "b.txt", expectedETag: "stale"));
        Assert.Equal(StorageErrorKind.ConcurrencyConflict, ex.Kind);
    }

    [Fact]
    public async Task GetChanges_Uses_Opaque_Cursor_Incrementally()
    {
        IStorageConnector connector = new FakeConnector();
        await connector.CreateDocumentAsync("/", "a.txt", new MemoryStream([1]));
        var first = await connector.GetChangesAsync(cursor: null);
        Assert.Single(first.Changes);

        var second = await connector.GetChangesAsync(first.NextCursor);
        Assert.Empty(second.Changes);

        var created = await connector.CreateDocumentAsync("/", "b.txt", new MemoryStream([2]));
        await connector.RenameDocumentAsync(created.ExternalId, "c.txt");
        var third = await connector.GetChangesAsync(second.NextCursor);
        Assert.Equal(
            [StorageChangeType.Created, StorageChangeType.Renamed],
            third.Changes.Select(c => c.Type));
        Assert.Equal(created.ExternalId, third.Changes[1].ExternalId);
        Assert.Equal("b.txt", third.Changes[1].PreviousName);
    }
}

using Akasha.Application.Storage;
using Akasha.Infrastructure.Storage;

namespace Akasha.Infrastructure.Tests.Storage;

public sealed class LocalFileSystemConnectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "akasha-lfs-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalFileSystemConnector _connector;

    public LocalFileSystemConnectorTests()
    {
        Directory.CreateDirectory(_root);
        _connector = new LocalFileSystemConnector(new LocalFileSystemConnectorOptions { RootPath = _root });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private async Task<StorageItem> CreateAsync(string folder, string name, string content)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return await _connector.CreateDocumentAsync(folder, name, stream);
    }

    private static async Task<string> DownloadTextAsync(LocalFileSystemConnector connector, string externalId)
    {
        await using var stream = await connector.DownloadAsync(externalId);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task CreateDownloadRoundtrip()
    {
        var item = await CreateAsync("docs", "vertrag.pdf", "hello");
        Assert.Equal("vertrag.pdf", item.Name);
        Assert.Equal("docs", item.Path);
        Assert.NotNull(item.ExternalId);
        Assert.Equal("hello", await DownloadTextAsync(_connector, item.ExternalId));
    }

    [Fact]
    public async Task GetList_Roundtrip()
    {
        var created = await CreateAsync("a/b", "f.txt", "x");
        var fetched = await _connector.GetDocumentAsync(created.ExternalId);
        Assert.Equal(created.ExternalId, fetched.ExternalId);
        Assert.Equal("f.txt", fetched.Name);

        var scan = new List<StorageItem>();
        await foreach (var item in _connector.ListDocumentsAsync(null))
        {
            scan.Add(item);
        }

        Assert.Contains(scan, i => i.ExternalId == created.ExternalId);
    }

    [Fact]
    public async Task Update_WithMatchingETag_Succeeds()
    {
        var item = await CreateAsync("", "f.txt", "v1");
        using var stream = new MemoryStream("v2"u8.ToArray());
        var updated = await _connector.UpdateDocumentAsync(item.ExternalId, stream, item.ETag);
        Assert.NotEqual(item.ETag, updated.ETag);
        Assert.Equal("v2", await DownloadTextAsync(_connector, item.ExternalId));
    }

    [Fact]
    public async Task Update_WithStaleETag_ConcurrencyConflict()
    {
        var item = await CreateAsync("", "f.txt", "v1");
        File.WriteAllText(Path.Combine(_root, "f.txt"), "external"); // external modification
        using var stream = new MemoryStream("v2"u8.ToArray());
        var ex = await Assert.ThrowsAsync<StorageException>(() => _connector.UpdateDocumentAsync(item.ExternalId, stream, item.ETag));
        Assert.Equal(StorageErrorKind.ConcurrencyConflict, ex.Kind);
        Assert.Equal("external", File.ReadAllText(Path.Combine(_root, "f.txt"))); // not silently overwritten
    }

    [Fact]
    public async Task Rename_PreservesExternalId()
    {
        var item = await CreateAsync("f", "old.txt", "x");
        var renamed = await _connector.RenameDocumentAsync(item.ExternalId, "new.txt");
        Assert.Equal(item.ExternalId, renamed.ExternalId);
        Assert.Equal("new.txt", renamed.Name);
        Assert.False(File.Exists(Path.Combine(_root, "f", "old.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "f", "new.txt")));
    }

    [Fact]
    public async Task Move_PreservesExternalId()
    {
        var item = await CreateAsync("from", "f.txt", "x");
        var moved = await _connector.MoveDocumentAsync(item.ExternalId, "to");
        Assert.Equal(item.ExternalId, moved.ExternalId);
        Assert.Equal("to", moved.Path);
        Assert.True(File.Exists(Path.Combine(_root, "to", "f.txt")));
    }

    [Fact]
    public async Task Copy_CreatesSecondItem()
    {
        var item = await CreateAsync("a", "f.txt", "content");
        var copy = await _connector.CopyDocumentAsync(item.ExternalId, "b", "f-copy.txt");
        Assert.NotEqual(item.ExternalId, copy.ExternalId);
        Assert.Equal("content", await DownloadTextAsync(_connector, copy.ExternalId));
    }

    [Fact]
    public async Task Delete_RemovesFile_GetFails()
    {
        var item = await CreateAsync("", "f.txt", "x");
        await _connector.DeleteDocumentAsync(item.ExternalId, item.ETag);
        Assert.False(File.Exists(Path.Combine(_root, "f.txt")));
        var ex = await Assert.ThrowsAsync<StorageException>(() => _connector.GetDocumentAsync(item.ExternalId));
        Assert.Equal(StorageErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Create_Duplicate_Conflict()
    {
        await CreateAsync("", "f.txt", "x");
        var ex = await Assert.ThrowsAsync<StorageException>(() => CreateAsync("", "f.txt", "y"));
        Assert.Equal(StorageErrorKind.Conflict, ex.Kind);
    }

    [Fact]
    public async Task MissingRead_NotFound()
    {
        var item = await CreateAsync("", "f.txt", "x");
        File.Delete(Path.Combine(_root, "f.txt")); // external deletion
        var ex = await Assert.ThrowsAsync<StorageException>(() => _connector.DownloadAsync(item.ExternalId));
        Assert.Equal(StorageErrorKind.NotFound, ex.Kind);
    }

    // ---------------- security ----------------

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("ok/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    public async Task PathTraversal_Rejected(string path)
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => _connector.CreateDocumentAsync(path, "f.txt", Stream.Null));
        Assert.Equal(StorageErrorKind.Validation, ex.Kind);
        Assert.False(File.Exists(Path.Combine(_root, "..", "escape.txt")));
    }

    [Theory]
    [InlineData("C:/absolute/f.txt")]
    [InlineData("/absolute/f.txt")]
    public async Task AbsolutePaths_Rejected(string path)
    {
        await Assert.ThrowsAsync<StorageException>(() => _connector.CreateDocumentAsync(path, "f.txt", Stream.Null));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("aux")]
    [InlineData("LPT1.log")]
    public async Task ReservedNames_Rejected(string name)
    {
        await Assert.ThrowsAsync<StorageException>(() => _connector.CreateDocumentAsync("", name, Stream.Null));
    }

    [Theory]
    [InlineData("evil?.txt")]
    [InlineData("evil<.txt")]
    [InlineData("evil:.txt")]
    [InlineData("trail.")]
    [InlineData("trail ")]
    public async Task InvalidNames_Rejected(string name)
    {
        await Assert.ThrowsAsync<StorageException>(() => _connector.CreateDocumentAsync("", name, Stream.Null));
    }

    [Fact]
    public async Task OversizedUpload_Rejected()
    {
        var connector = new LocalFileSystemConnector(new LocalFileSystemConnectorOptions { RootPath = _root, MaxFileSizeBytes = 8 });
        using var stream = new MemoryStream("this is way too large"u8.ToArray());
        var ex = await Assert.ThrowsAsync<StorageException>(() => connector.CreateDocumentAsync("", "big.bin", stream));
        Assert.Equal(StorageErrorKind.Validation, ex.Kind);
        Assert.False(File.Exists(Path.Combine(_root, "big.bin")));
        Assert.Empty(Directory.GetFiles(_root, "*.akasha-part", SearchOption.AllDirectories)); // temp cleaned
    }

    [Fact]
    public async Task RootEscape_RootIsBoundary()
    {
        // file outside the root must be invisible
        var outside = Path.Combine(Directory.GetParent(_root)!.FullName, $"outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            await Assert.ThrowsAsync<StorageException>(() => _connector.CreateDocumentAsync("..", Path.GetFileName(outside), Stream.Null));
            var scan = new List<StorageItem>();
            await foreach (var item in _connector.ListDocumentsAsync(null))
            {
                scan.Add(item);
            }

            Assert.DoesNotContain(scan, i => i.Name == Path.GetFileName(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task ReparsePoint_Rejected_WhenWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var junction = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(junction, Path.GetTempPath());
        }
        catch (Exception)
        {
            return; // no privilege to create symlinks; nothing to test
        }

        await Assert.ThrowsAsync<StorageException>(() => _connector.CreateDocumentAsync("link", "f.txt", Stream.Null));
    }

    // ---------------- concurrency / parallelism ----------------

    [Fact]
    public async Task ParallelCreates_AllSucceed()
    {
        var tasks = Enumerable.Range(0, 16)
            .Select(async i => await CreateAsync("parallel", $"f{i}.txt", $"content{i}"))
            .ToArray();
        var items = await Task.WhenAll(tasks);
        Assert.Equal(16, items.Select(i => i.ExternalId).Distinct().Count());
    }

    [Fact]
    public async Task LargeFile_StreamsRoundtrip()
    {
        var connector = new LocalFileSystemConnector(new LocalFileSystemConnectorOptions { RootPath = _root, MaxFileSizeBytes = 64 * 1024 * 1024 });
        var bytes = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        var item = await connector.CreateDocumentAsync("", "large.bin", new MemoryStream(bytes));
        await using var stream = await connector.DownloadAsync(item.ExternalId);
        using var result = new MemoryStream();
        await stream.CopyToAsync(result);
        Assert.True(bytes.AsSpan().SequenceEqual(result.ToArray()));
    }

    // ---------------- metadata ----------------

    [Fact]
    public async Task Metadata_ReturnsBasics()
    {
        var item = await CreateAsync("", "f.txt", "12345");
        var metadata = await _connector.GetMetadataAsync(item.ExternalId);
        Assert.Equal("5", metadata["size"]);
        Assert.Equal(".txt", metadata["extension"]);
    }

    // ---------------- change detection ----------------

    [Fact]
    public async Task Changes_DetectCreatedModifiedDeleted()
    {
        var seed = await CreateAsync("", "a.txt", "1");
        var first = await _connector.GetChangesAsync(null); // baseline
        Assert.Empty(first.Changes);
        Assert.NotNull(first.NextCursor);

        await CreateAsync("", "b.txt", "2");                                   // external create
        File.WriteAllText(Path.Combine(_root, "a.txt"), "1-modified");         // external modify
        var page = await _connector.GetChangesAsync(first.NextCursor);

        Assert.Contains(page.Changes, c => c.Type == StorageChangeType.Created && c.Name == "b.txt");
        Assert.Contains(page.Changes, c => c.Type == StorageChangeType.Modified && c.ExternalId == seed.ExternalId);

        File.Delete(Path.Combine(_root, "a.txt"));                             // external delete
        var page2 = await _connector.GetChangesAsync(page.NextCursor);
        Assert.Contains(page2.Changes, c => c.Type == StorageChangeType.Deleted && c.ExternalId == seed.ExternalId);
    }

    [Fact]
    public async Task Changes_Rename_ReportsRenamed()
    {
        var item = await CreateAsync("", "old.txt", "x");
        var first = await _connector.GetChangesAsync(null);
        File.Move(Path.Combine(_root, "old.txt"), Path.Combine(_root, "new.txt"));
        var page = await _connector.GetChangesAsync(first.NextCursor);

        // On Windows (file-id identity) this is one Renamed change; on path-identity platforms
        // a rename appears as Deleted+Created (documented connector ceiling).
        if (OperatingSystem.IsWindows())
        {
            var change = Assert.Single(page.Changes);
            Assert.Equal(StorageChangeType.Renamed, change.Type);
            Assert.Equal(item.ExternalId, change.ExternalId);
            Assert.Equal("old.txt", change.PreviousName);
            Assert.Equal("new.txt", change.Name);
        }
    }

    [Fact]
    public async Task Changes_UnknownCursor_RequiresRescan()
    {
        var page = await _connector.GetChangesAsync("999");
        Assert.True(page.RequiresFullRescan);
    }

    // ---------------- capabilities ----------------

    [Fact]
    public void Capabilities_DoNotDeclareUnsupported()
    {
        Assert.True(_connector.Supports(StorageCapability.Move));
        Assert.True(_connector.Supports(StorageCapability.Rename));
        Assert.True(_connector.Supports(StorageCapability.ChangeTracking));
        Assert.False(_connector.Supports(StorageCapability.Versioning));
        Assert.False(_connector.Supports(StorageCapability.Permissions));
        Assert.False(_connector.Supports(StorageCapability.RecycleBin));
    }

    [Fact]
    public async Task UnsupportedOperations_Throw()
    {
        var item = await CreateAsync("", "f.txt", "x");
        var ex = await Assert.ThrowsAsync<StorageException>(() => _connector.GetVersionsAsync(item.ExternalId));
        Assert.Equal(StorageErrorKind.UnsupportedOperation, ex.Kind);
    }
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Akasha.Application.Storage;

namespace Akasha.Infrastructure.Storage;

/// <summary>
/// Storage connector for a customer-controlled local filesystem tree. All
/// filesystem logic lives here and only here (backend AGENTS #16, #141).
/// Access is confined to the configured root: path traversal, absolute paths,
/// reserved device names, invalid characters and symlink/reparse-point escapes
/// are rejected before any I/O happens.
///
/// ExternalId = stable NTFS file reference number on Windows (survives
/// rename/move within the volume); relative path on other platforms.
/// ETag = "lastWriteTicks-size"; optimistic concurrency via expectedETag.
/// </summary>
public sealed class LocalFileSystemConnector : StorageConnectorBase
{
    // ponytail: non-Windows fallback uses the relative path as ExternalId, so an
    // external rename looks like Deleted+Created. Upgrade: use inode/dev via
    // Mono.Posix or stat P/Invoke when self-hosted Linux becomes a requirement.
    private static readonly bool UseFileId = OperatingSystem.IsWindows();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] Separators = ['/', '\\'];

    private readonly string _root;
    private readonly long _maxFileSizeBytes;
    private readonly Lock _scanLock = new();
    private long _cursor;
    private Dictionary<string, ScanEntry>? _snapshot;

    private sealed record ScanEntry(string Path, string Name, string ETag);

    public LocalFileSystemConnector(LocalFileSystemConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            throw new ArgumentException("RootPath is required.", nameof(options));
        }

        var root = Path.GetFullPath(options.RootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Storage root '{root}' does not exist.");
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("Storage root must not be a reparse point/junction.", nameof(options));
        }

        _root = EnsureTrailingSeparator(root);
        _maxFileSizeBytes = options.MaxFileSizeBytes;
    }

    public override StorageCapability Capabilities =>
        StorageCapability.Read
        | StorageCapability.Write
        | StorageCapability.Delete
        | StorageCapability.Move
        | StorageCapability.Copy
        | StorageCapability.Rename
        | StorageCapability.Metadata
        | StorageCapability.ChangeTracking;

    // ---------- path security ----------

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static void ValidateSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or "..")
        {
            throw new StorageException(StorageErrorKind.Validation, $"Invalid path segment '{segment}'.", isTransient: false);
        }

        if (segment[^1] is '.' or ' ')
        {
            throw new StorageException(StorageErrorKind.Validation, $"Path segment '{segment}' must not end with a dot or space.", isTransient: false);
        }

        if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new StorageException(StorageErrorKind.Validation, $"Path segment '{segment}' contains invalid characters.", isTransient: false);
        }

        var stem = segment.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            throw new StorageException(StorageErrorKind.Validation, $"Reserved device name '{segment}' is not allowed.", isTransient: false);
        }
    }

    /// <summary>Resolves a relative storage path into a full path inside the root, validating every segment.</summary>
    private string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || Path.IsPathFullyQualified(relativePath))
        {
            throw new StorageException(StorageErrorKind.Validation, $"Absolute paths are not allowed: '{relativePath}'.", isTransient: false);
        }

        foreach (var segment in relativePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateSegment(segment);
        }

        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        // StringComparison from the OS: Windows paths are case-insensitive.
        if (!full.StartsWith(_root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new StorageException(StorageErrorKind.Validation, $"Path '{relativePath}' escapes the storage root.", isTransient: false);
        }

        ThrowOnReparsePoints(full);
        return full;
    }

    /// <summary>Rejects any reparse point/junction from the root down to (but excluding the leaf's existence) so symlinks cannot escape the root.</summary>
    private void ThrowOnReparsePoints(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath.AsSpan().TrimEnd(Path.DirectorySeparatorChar).ToString());
        var chain = new Stack<string>();
        for (var current = dir; current is not null && current.Length >= _root.Length - 1; current = Path.GetDirectoryName(current))
        {
            chain.Push(current);
        }

        foreach (var part in chain)
        {
            if (Directory.Exists(part) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StorageException(StorageErrorKind.AccessDenied, "Reparse points/junctions inside the storage root are not followed.", isTransient: false);
            }
        }

        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StorageException(StorageErrorKind.AccessDenied, "The file itself is a reparse point and is not accessible.", isTransient: false);
        }
    }

    // ---------- identity / mapping ----------

    private string RelativePathOf(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        return relative == "." ? string.Empty : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private StorageItem ToItem(string fullPath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                throw new StorageException(StorageErrorKind.NotFound, "The file does not exist.", isTransient: false);
            }
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw Wrap(ex, fullPath);
        }

        // ponytail: version number not tracked (no NTFS object-id index); ETag carries change detection.
        var relative = RelativePathOf(fullPath);
        return new StorageItem
        {
            ExternalId = UseFileId ? GetFileId(fullPath) : relative,
            Path = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty,
            Name = info.Name,
            Size = info.Length,
            ModifiedAtUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            ETag = MakeETag(info),
        };
    }

    private static string MakeETag(FileInfo info) => $"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}";

    private static string GetFileId(string fullPath)
    {
        using var handle = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileId(handle.SafeFileHandle);
    }

    private static string GetFileId(SafeFileHandle handle)
    {
        var info = new FILE_ID_INFO();
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileIdInfo, ref info, (uint)Marshal.SizeOf<FILE_ID_INFO>()))
        {
            // e.g. volumes without file id support: fall back to a hash-free distinct token.
            throw new StorageException(StorageErrorKind.Unavailable, "The volume does not expose stable file IDs.", isTransient: false);
        }

        return $"{info.VolumeSerialNumber:x}-{info.FileIdLow:x}-{info.FileIdHigh:x}";
    }

    private void CheckETag(string fullPath, string? expectedETag)
    {
        if (expectedETag is null)
        {
            return;
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new StorageException(StorageErrorKind.NotFound, "The file does not exist.", isTransient: false);
        }

        if (!string.Equals(MakeETag(info), expectedETag, StringComparison.Ordinal))
        {
            throw new StorageException(StorageErrorKind.ConcurrencyConflict, "The file was modified since the expected version.", isTransient: false);
        }
    }

    // ---------- read ----------

    public override Task<StorageItem> GetDocumentAsync(string externalId, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveByExternalId(externalId);
        return Task.FromResult(ToItem(fullPath));
    }

    public override Task<Stream> DownloadAsync(string externalId, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveByExternalId(externalId);
        try
        {
            return Task.FromResult<Stream>(new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        catch (Exception ex)
        {
            throw Wrap(ex, fullPath);
        }
    }

    public override IAsyncEnumerable<StorageItem> ListDocumentsAsync(string? folderPath, CancellationToken cancellationToken = default)
    {
        var startDir = folderPath is null
            ? _root
            : Path.GetDirectoryName(ResolvePath(Path.Combine(folderPath, "placeholder")))!;

        return Enumerate(startDir, recursive: folderPath is null, cancellationToken);
    }

    private async IAsyncEnumerable<StorageItem> Enumerate(string startDir, bool recursive, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(startDir))
        {
            throw new StorageException(StorageErrorKind.NotFound, $"Folder '{RelativePathOf(startDir)}' does not exist.", isTransient: false);
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(startDir, "*", option);
        }
        catch (Exception ex)
        {
            throw Wrap(ex, startDir);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageItem? item = null;
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                {
                    item = ToItem(file);
                }
            }
            catch (StorageException)
            {
                // skip inaccessible entries instead of failing the whole scan
            }

            if (item is not null)
            {
                yield return item;
            }

            await Task.Yield();
        }
    }

    // ---------- write ----------

    public override async Task<StorageItem> CreateDocumentAsync(string folderPath, string name, Stream content, CancellationToken cancellationToken = default)
    {
        ValidateSegment(name);
        var folder = Path.GetDirectoryName(ResolvePath(Path.Combine(folderPath ?? string.Empty, "placeholder")))!;
        var target = ResolvePath(Path.Combine(RelativePathOf(folder), name));
        if (File.Exists(target))
        {
            throw new StorageException(StorageErrorKind.Conflict, $"'{name}' already exists.", isTransient: false);
        }

        Directory.CreateDirectory(folder);
        await WriteAtomically(target, content, overwrite: false, existingExpectedETag: null, cancellationToken);
        return ToItem(target);
    }

    public override async Task<StorageItem> UpdateDocumentAsync(string externalId, Stream content, string? expectedETag = null, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveByExternalId(externalId);
        CheckETag(fullPath, expectedETag);
        try
        {
            // In-place truncate+write preserves the NTFS file reference number (the ExternalId);
            // temp+replace would hand the file a new identity (backend AGENTS #27).
            // ponytail: not crash-atomic — a killed process may leave a truncated file; acceptable
            // for local storage, ETag/reconciliation detects the inconsistency.
            await using var output = new FileStream(fullPath, FileMode.Truncate, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            await CopyLimited(content, output, cancellationToken);
        }
        catch (Exception ex) when (ex is not StorageException)
        {
            throw Wrap(ex, fullPath);
        }

        return ToItem(fullPath);
    }

    private async Task WriteAtomically(string target, Stream content, bool overwrite, string? existingExpectedETag, CancellationToken cancellationToken)
    {
        // ponytail: temp file beside the target keeps partial writes invisible; cleaned up on failure (#68).
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Guid.NewGuid():N}.akasha-part");
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await CopyLimited(content, output, cancellationToken);
            }

            if (overwrite && existingExpectedETag is not null)
            {
                CheckETag(target, existingExpectedETag);
            }

            File.Move(temp, target, overwrite);
        }
        catch (StorageException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            throw Wrap(ex, target);
        }
    }

    public override Task DeleteDocumentAsync(string externalId, string? expectedETag = null, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveByExternalId(externalId);
        CheckETag(fullPath, expectedETag);
        try
        {
            File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            throw Wrap(ex, fullPath);
        }

        return Task.CompletedTask;
    }

    public override Task<StorageItem> MoveDocumentAsync(string externalId, string targetFolderPath, string? expectedETag = null, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveByExternalId(externalId);
        var targetFolder = Path.GetDirectoryName(ResolvePath(Path.Combine(targetFolderPath, "placeholder")))!;
        var target = ResolvePath(Path.Combine(RelativePathOf(targetFolder), Path.GetFileName(fullPath)));
        Move(fullPath, target, expectedETag);
        return Task.FromResult(ToItem(target));
    }

    public override Task<StorageItem> CopyDocumentAsync(string externalId, string targetFolderPath, string? newName = null, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveByExternalId(externalId);
        if (newName is not null)
        {
            ValidateSegment(newName);
        }

        var targetFolder = Path.GetDirectoryName(ResolvePath(Path.Combine(targetFolderPath, "placeholder")))!;
        var target = ResolvePath(Path.Combine(RelativePathOf(targetFolder), newName ?? Path.GetFileName(fullPath)));
        try
        {
            Directory.CreateDirectory(targetFolder);
            File.Copy(fullPath, target, overwrite: false);
        }
        catch (Exception ex)
        {
            throw Wrap(ex, target);
        }

        return Task.FromResult(ToItem(target));
    }

    public override Task<StorageItem> RenameDocumentAsync(string externalId, string newName, string? expectedETag = null, CancellationToken cancellationToken = default)
    {
        ValidateSegment(newName);
        var fullPath = ResolveByExternalId(externalId);
        var target = ResolvePath(Path.Combine(RelativePathOf(Path.GetDirectoryName(fullPath)!), newName));
        Move(fullPath, target, expectedETag);
        return Task.FromResult(ToItem(target));
    }

    private void Move(string source, string target, string? expectedETag)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && source == target)
        {
            throw new StorageException(StorageErrorKind.Validation, "Source and target are identical.", isTransient: false);
        }

        CheckETag(source, expectedETag);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target, overwrite: false);
        }
        catch (Exception ex)
        {
            throw Wrap(ex, target);
        }
    }

    // ---------- metadata ----------

    public override Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(string externalId, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveByExternalId(externalId);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new StorageException(StorageErrorKind.NotFound, "The file does not exist.", isTransient: false);
        }

        IReadOnlyDictionary<string, string> metadata = new Dictionary<string, string>
        {
            ["size"] = info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["createdUtc"] = info.CreationTimeUtc.ToString("O"),
            ["modifiedUtc"] = info.LastWriteTimeUtc.ToString("O"),
            ["extension"] = info.Extension,
        };

        return Task.FromResult(metadata);
    }

    // ---------- change detection ----------

    public override async Task<StorageChangePage> GetChangesAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        // ponytail: reconciliation scan over the whole tree on each poll. In-memory snapshot
        // dies with the process (unknown cursor -> RequiresFullRescan, caller does a full
        // ListDocuments pass). Upgrade: persisted checkpoint or USN journal when volumes get large.
        var current = new Dictionary<string, ScanEntry>();
        await foreach (var item in ListDocumentsAsync(null, cancellationToken))
        {
            current[item.ExternalId] = new ScanEntry(item.Path, item.Name, item.ETag ?? string.Empty);
        }

        lock (_scanLock)
        {
            if (cursor is not null && (!long.TryParse(cursor, out var known) || known != _cursor))
            {
                return new StorageChangePage { Changes = [], NextCursor = _cursor.ToString(), RequiresFullRescan = true };
            }

            var changes = new List<StorageChange>();
            var previous = _snapshot;
            if (previous is not null)
            {
                foreach (var (id, now) in current)
                {
                    if (!previous.TryGetValue(id, out var before))
                    {
                        changes.Add(ChangeOf(StorageChangeType.Created, id, now));
                    }
                    else if (before.Path != now.Path)
                    {
                        changes.Add(ChangeOf(StorageChangeType.Moved, id, now, previous: before));
                    }
                    else if (before.Name != now.Name)
                    {
                        changes.Add(ChangeOf(StorageChangeType.Renamed, id, now, previous: before));
                    }
                    else if (before.ETag != now.ETag)
                    {
                        changes.Add(ChangeOf(StorageChangeType.Modified, id, now));
                    }
                }

                foreach (var (id, before) in previous)
                {
                    if (!current.ContainsKey(id))
                    {
                        changes.Add(new StorageChange { Type = StorageChangeType.Deleted, ExternalId = id, Path = before.Path, Name = before.Name });
                    }
                }
            }

            _snapshot = current;
            _cursor++;
            return new StorageChangePage { Changes = changes, NextCursor = _cursor.ToString() };
        }
    }

    private static StorageChange ChangeOf(StorageChangeType type, string id, ScanEntry now, ScanEntry? previous = null)
        => new()
        {
            Type = type,
            ExternalId = id,
            Path = now.Path,
            Name = now.Name,
            ETag = now.ETag,
            PreviousPath = previous?.Path,
            PreviousName = previous?.Name,
        };

    // ---------- external id resolution ----------

    private string ResolveByExternalId(string externalId)
    {
        if (!UseFileId)
        {
            var path = ResolvePath(externalId);
            if (!File.Exists(path))
            {
                throw new StorageException(StorageErrorKind.NotFound, "The file does not exist.", isTransient: false);
            }

            return path;
        }

        // Id-first lookup would need an index; resolve via scan instead.
        // ponytail: O(tree) scan per id lookup. Fine for local DMS volumes; add an
        // externalId->path index (refreshed by GetChanges) when scans get expensive.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 && string.Equals(GetFileId(file), externalId, StringComparison.Ordinal))
                {
                    return file;
                }
            }
            catch (IOException) { /* skip inaccessible */ }
            catch (UnauthorizedAccessException) { /* skip inaccessible */ }
        }

        throw new StorageException(StorageErrorKind.NotFound, "No file with this id exists.", isTransient: false);
    }

    // ---------- error mapping ----------

    private async Task CopyLimited(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > _maxFileSizeBytes)
            {
                throw new StorageException(StorageErrorKind.Validation, $"File exceeds the size limit of {_maxFileSizeBytes} bytes.", isTransient: false);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static StorageException Wrap(Exception ex, string path) => ex switch
    {
        StorageException se => se,
        FileNotFoundException or DirectoryNotFoundException
            => new(StorageErrorKind.NotFound, "The file or folder does not exist.", isTransient: false, inner: ex),
        UnauthorizedAccessException
            => new(StorageErrorKind.AccessDenied, "The filesystem denied access. Storage permissions must be fixed outside Akasha.", isTransient: false, inner: ex),
        PathTooLongException or ArgumentException or NotSupportedException
            => new(StorageErrorKind.Validation, "The path is invalid.", isTransient: false, inner: ex),
        IOException io when io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) // sharing/lock violation
            => new(StorageErrorKind.Unavailable, "The file is locked by another process.", isTransient: true, inner: ex),
        IOException io when io.HResult == unchecked((int)0x80070050) // ERROR_FILE_EXISTS
            => new(StorageErrorKind.Conflict, "Destination already exists.", isTransient: false, inner: ex),
        IOException io
            => new(StorageErrorKind.Unavailable, "A filesystem error occurred.", isTransient: true, inner: io),
        _ => new StorageException(StorageErrorKind.Unknown, "An unexpected filesystem error occurred.", isTransient: false, inner: ex),
    };

    // ---------- interop ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;

        public ulong FileIdLow;

        public ulong FileIdHigh;
    }

    private static class NativeMethods
    {
        public const int FileIdInfo = 18; // FILE_INFO_BY_HANDLE_CLASS.FileIdInfo

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, ref FILE_ID_INFO info, uint dwBufferSize);
    }
}

/// <summary>Configuration for <see cref="LocalFileSystemConnector"/>. Root is the security boundary.</summary>
public sealed class LocalFileSystemConnectorOptions
{
    public const long DefaultMaxFileSizeBytes = 512L * 1024 * 1024;

    /// <summary>Absolute directory the connector is confined to.</summary>
    public required string RootPath { get; init; }

    /// <summary>Rejects larger uploads; protects against oversized files (#28).</summary>
    public long MaxFileSizeBytes { get; init; } = DefaultMaxFileSizeBytes;
}

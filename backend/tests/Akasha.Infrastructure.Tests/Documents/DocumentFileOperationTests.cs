using System.Text;
using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Documents;
using Akasha.Application.Identity;
using Akasha.Application.Storage;
using Akasha.Application.Tenancy;
using Akasha.Domain.Authorization;
using Akasha.Domain.Identity;
using Akasha.Domain.Storage;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Authorization;
using Akasha.Infrastructure.Documents;
using Akasha.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests.Documents;

/// <summary>
/// Phase 12: physical file operations. Every operation must run the full pipeline
/// (tenant → authorization → document → connector → capability → operation → database →
/// audit) and must report failures honestly. Connector is the real
/// <see cref="LocalFileSystemConnector"/> on a temp root; persistence is real PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DocumentFileOperationTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("akasha-p12-").FullName;

    private sealed class Context : IDisposable
    {
        public required Tenant Tenant { get; init; }
        public required User User { get; init; }
        public required Persistence.AkashaDbContext Db { get; init; }
        public required CurrentUserContext UserContext { get; init; }
        public required TenantContext TenantContext { get; init; }
        public required DocumentService Service { get; init; }
        public required Guid ConnectorId { get; init; }
        public required string Root { get; init; }

        public string FullPath(DocumentView document)
            => Path.Combine(Root, document.Storage.Path, document.Storage.Name);

        public DocumentService ServiceWith(IReadOnlyDictionary<Guid, IStorageConnector> connectors)
            => new(Db, new AuthorizationService(Db), UserContext, TenantContext,
                new FakeConnectorResolver(connectors));

        public static async Task<Context> CreateAsync(
            PostgresFixture postgres, string root, string slug, params PermissionAction[] typeLevelGrants)
        {
            var tenant = new Tenant($"Tenant {slug}", slug);
            var user = new User(tenant.Id, $"User {slug}");

            await using (var seeding = postgres.CreateContext())
            {
                seeding.Tenants.Add(tenant);
                await seeding.SaveChangesAsync();
            }

            await using (var seeding = postgres.CreateContext(tenant.Id))
            {
                seeding.Users.Add(user);
                foreach (var action in typeLevelGrants)
                {
                    seeding.PermissionGrants.Add(new PermissionGrant(
                        tenant.Id, PrincipalType.User, user.Id, ResourceType.Document, null, action));
                }

                await seeding.SaveChangesAsync();
            }

            var db = postgres.CreateContext(tenant.Id);
            var userContext = new CurrentUserContext();
            userContext.Establish(new CurrentUser(user.Id, tenant.Id, $"sub-{user.Id}", "internal", user.IsServiceAccount));
            var tenantContext = new TenantContext();
            tenantContext.Establish(tenant.Id);

            var connectorId = Guid.NewGuid();
            var connector = new LocalFileSystemConnector(new LocalFileSystemConnectorOptions { RootPath = root });

            return new Context
            {
                Tenant = tenant,
                User = user,
                Db = db,
                UserContext = userContext,
                TenantContext = tenantContext,
                ConnectorId = connectorId,
                Root = root,
                Service = new DocumentService(db, new AuthorizationService(db), userContext, tenantContext,
                    new FakeConnectorResolver(new Dictionary<Guid, IStorageConnector> { [connectorId] = connector })),
            };
        }

        public void Dispose() => Db.Dispose();
    }

    private static MemoryStream Bytes(string content) => new(Encoding.UTF8.GetBytes(content));

    private static Task<DocumentView> UploadAsync(Context context, string name = "contract.pdf", string folder = "akte-1", string content = "v1")
        => context.Service.UploadContentAsync(new UploadDocumentRequest(context.ConnectorId, folder, name), Bytes(content));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Upload_Creates_Available_Document_Writes_File_And_Audits()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-upload", PermissionAction.Upload);

        var document = await UploadAsync(context);

        Assert.Equal(StorageSyncState.Available, document.State);
        Assert.Equal("contract.pdf", document.Name);
        Assert.Equal("akte-1", document.Storage.Path);
        Assert.NotNull(document.Storage.ETag);
        Assert.Equal("v1", await File.ReadAllTextAsync(context.FullPath(document)));

        var audits = await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync();
        Assert.Contains("document.uploaded", audits);
    }

    [Fact]
    public async Task Upload_With_Traversal_Name_Is_Rejected_And_Nothing_Is_Written()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-trav", PermissionAction.Upload);

        await Assert.ThrowsAsync<StorageException>(() => context.Service.UploadContentAsync(
            new UploadDocumentRequest(context.ConnectorId, "akte-1", "../evil.txt"), Bytes("x")));

        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(_root)!.FullName, "evil.txt")));
        Assert.Equal(0, await context.Db.Documents.CountAsync());
        Assert.Equal(0, await context.Db.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Upload_To_Unknown_Connector_Is_NotFound_And_Nothing_Is_Created()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-ncon", PermissionAction.Upload);

        await Assert.ThrowsAsync<NotFoundException>(() => context.Service.UploadContentAsync(
            new UploadDocumentRequest(Guid.NewGuid(), "akte-1", "a.pdf"), Bytes("x")));

        Assert.Equal(0, await context.Db.Documents.CountAsync());
    }

    [Fact]
    public async Task Upload_Without_Grant_Is_Denied_And_Nothing_Touches_Storage()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-updeny");

        await Assert.ThrowsAsync<AccessDeniedException>(() => UploadAsync(context));
        Assert.Equal(0, await context.Db.Documents.CountAsync());
        Assert.False(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Upload_Same_Name_Twice_Conflicts_Instead_Of_Overwriting()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-dup", PermissionAction.Upload);

        await UploadAsync(context, name: "dup.pdf", content: "first");
        var conflict = await Assert.ThrowsAsync<StorageException>(() => UploadAsync(context, name: "dup.pdf", content: "second"));

        Assert.Equal(StorageErrorKind.Conflict, conflict.Kind);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(_root, "akte-1", "dup.pdf")));
        Assert.Equal(1, await context.Db.Documents.CountAsync());
    }

    [Fact]
    public async Task Download_Streams_Content_And_Audits()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-dl",
            PermissionAction.Upload, PermissionAction.Download);
        var document = await UploadAsync(context, content: "secret-contents");

        var download = await context.Service.DownloadContentAsync(document.Id);
        using var reader = new StreamReader(download.Content);

        Assert.Equal("secret-contents", await reader.ReadToEndAsync());
        Assert.Equal("contract.pdf", download.FileName);
        Assert.Contains("document.downloaded",
            await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync());
    }

    [Fact]
    public async Task Download_Without_Grant_Is_Denied_Before_Content_Access()
    {
        using var owner = await Context.CreateAsync(postgres, _root, "p12-dlo", PermissionAction.Upload);
        var document = await UploadAsync(owner);

        using var denied = await Context.CreateAsync(postgres, _root, "p12-dld", PermissionAction.Read);
        await Assert.ThrowsAsync<AccessDeniedException>(() => denied.Service.DownloadContentAsync(document.Id));
    }

    [Fact]
    public async Task Download_Of_Externally_Deleted_File_Marks_Missing_And_Fails()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-dlm",
            PermissionAction.Upload, PermissionAction.Download, PermissionAction.Read);
        var document = await UploadAsync(context);
        File.Delete(context.FullPath(document));

        var error = await Assert.ThrowsAsync<StorageException>(() => context.Service.DownloadContentAsync(document.Id));

        Assert.Equal(StorageErrorKind.NotFound, error.Kind);
        Assert.Equal(StorageSyncState.Missing, (await context.Service.GetAsync(document.Id)).State);
        Assert.Contains("document.storage.state.missing",
            await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync());
    }

    [Fact]
    public async Task UpdateContent_Refreshes_Content_And_ETag()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-upd",
            PermissionAction.Upload, PermissionAction.Write);
        var document = await UploadAsync(context, content: "v1");

        var updated = await context.Service.UpdateContentAsync(
            document.Id, new UpdateContentRequest(), Bytes("v2-is-longer"));

        Assert.Equal(document.Id, updated.Id);
        Assert.NotNull(updated.Storage.ETag);
        Assert.Equal("v2-is-longer", await File.ReadAllTextAsync(context.FullPath(updated)));
        Assert.Contains("document.updated",
            await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync());
    }

    [Fact]
    public async Task UpdateContent_After_External_Modification_Conflicts_Without_Overwriting()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-conc",
            PermissionAction.Upload, PermissionAction.Write);
        var document = await UploadAsync(context, content: "akasha-v1");

        // external actor modifies the file outside Akasha
        await File.WriteAllTextAsync(context.FullPath(document), "external-v2");

        // the known ETag is stale: this must conflict, never silently overwrite (#9)
        var conflict = await Assert.ThrowsAsync<StorageException>(() => context.Service.UpdateContentAsync(
            document.Id, new UpdateContentRequest(), Bytes("akasha-v3")));

        Assert.Equal(StorageErrorKind.ConcurrencyConflict, conflict.Kind);
        Assert.Equal("external-v2", await File.ReadAllTextAsync(context.FullPath(document)));
    }

    [Fact]
    public async Task UpdateContent_With_Wrong_IfMatch_ETag_Conflicts()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-ifm",
            PermissionAction.Upload, PermissionAction.Write);
        var document = await UploadAsync(context);

        var conflict = await Assert.ThrowsAsync<StorageException>(() => context.Service.UpdateContentAsync(
            document.Id, new UpdateContentRequest("wrong-etag"), Bytes("v2")));

        Assert.Equal(StorageErrorKind.ConcurrencyConflict, conflict.Kind);
    }

    [Fact]
    public async Task RenameFile_Preserves_Document_Id_And_Renames_Physically()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-ren",
            PermissionAction.Upload, PermissionAction.Rename);
        var document = await UploadAsync(context, name: "contract.pdf");
        var oldPath = context.FullPath(document);

        var renamed = await context.Service.RenameFileAsync(document.Id, new RenameFileRequest("vertrag.pdf"));

        Assert.Equal(document.Id, renamed.Id);
        Assert.Equal("vertrag.pdf", renamed.Name);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(context.FullPath(renamed)));
        Assert.Contains("document.file.renamed",
            await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync());
    }

    [Fact]
    public async Task MoveFile_Preserves_Id_And_Updates_Storage_Path()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-move",
            PermissionAction.Upload, PermissionAction.Move);
        var document = await UploadAsync(context);

        var moved = await context.Service.MoveFileAsync(document.Id, new MoveDocumentRequest("akte-2/sub"));

        Assert.Equal(document.Id, moved.Id);
        Assert.Equal("akte-2/sub", moved.Storage.Path);
        Assert.Equal("contract.pdf", moved.Name);
        Assert.True(File.Exists(context.FullPath(moved)));
        Assert.Contains("document.moved",
            await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync());
    }

    [Fact]
    public async Task CopyFile_Creates_A_New_Logical_Document()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-copy",
            PermissionAction.Upload, PermissionAction.Copy);
        var source = await UploadAsync(context, name: "source.pdf");

        var copy = await context.Service.CopyFileAsync(source.Id, new CopyDocumentRequest("akte-2", "copy.pdf"));

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("copy.pdf", copy.Name);
        Assert.Equal(StorageSyncState.Available, copy.State);
        Assert.Equal(2, await context.Db.Documents.CountAsync());
        Assert.True(File.Exists(context.FullPath(copy)));
        Assert.Contains("document.copied",
            await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync());
    }

    [Fact]
    public async Task Copy_Requires_Upload_Grant_For_The_New_Document()
    {
        using var owner = await Context.CreateAsync(postgres, _root, "p12-cpo",
            PermissionAction.Upload);
        var source = await UploadAsync(owner);

        // same user has Copy on the source but no Upload permission to register the copy
        using var noUpload = await Context.CreateAsync(postgres, _root, "p12-cpx", PermissionAction.Copy);

        await Assert.ThrowsAsync<AccessDeniedException>(() => noUpload.Service.CopyFileAsync(
            source.Id, new CopyDocumentRequest("akte-2")));
    }

    [Fact]
    public async Task DeleteContent_Removes_File_And_Keeps_Logical_Document_As_Missing()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-pdel",
            PermissionAction.Upload, PermissionAction.Delete, PermissionAction.Read);
        var document = await UploadAsync(context);

        await context.Service.DeleteContentAsync(document.Id, eTag: null);

        Assert.False(File.Exists(context.FullPath(document)));
        var logical = await context.Service.GetAsync(document.Id);
        Assert.Equal(StorageSyncState.Missing, logical.State);
        Assert.Contains("document.file.deleted",
            await context.Db.AuditEntries.Select(entry => entry.Action).ToListAsync());
    }

    [Fact]
    public async Task Move_Without_Move_Grant_Is_Denied_Before_Storage_Call()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-mvdeny",
            PermissionAction.Upload);
        var document = await UploadAsync(context);

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            context.Service.MoveFileAsync(document.Id, new MoveDocumentRequest("akte-2")));
        Assert.True(File.Exists(context.FullPath(document)));
    }

    [Fact]
    public async Task Missing_Capability_Is_Rejected_Before_Any_Storage_Call()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-cap",
            PermissionAction.Upload, PermissionAction.Move);
        var document = await UploadAsync(context);

        // swap in a connector without the Move capability
        var service = context.ServiceWith(new Dictionary<Guid, IStorageConnector>
        {
            [context.ConnectorId] = new FailingConnector(StorageCapability.Read, StorageErrorKind.Unavailable),
        });

        var error = await Assert.ThrowsAsync<StorageException>(() =>
            service.MoveFileAsync(document.Id, new MoveDocumentRequest("elsewhere")));
        Assert.Equal(StorageErrorKind.UnsupportedOperation, error.Kind);
    }

    [Fact]
    public async Task Expired_Connector_Credentials_Propagate_And_State_Is_Recorded()
    {
        using var context = await Context.CreateAsync(postgres, _root, "p12-auth",
            PermissionAction.Upload, PermissionAction.Read, PermissionAction.Download);
        var document = await UploadAsync(context);

        var service = context.ServiceWith(new Dictionary<Guid, IStorageConnector>
        {
            [context.ConnectorId] = new FailingConnector(StorageCapability.Read | StorageCapability.Write,
                StorageErrorKind.AuthenticationRequired, isTransient: false),
        });

        var error = await Assert.ThrowsAsync<StorageException>(() => service.DownloadContentAsync(document.Id));

        Assert.Equal(StorageErrorKind.AuthenticationRequired, error.Kind);
        Assert.Equal(StorageSyncState.AuthenticationRequired, (await context.Service.GetAsync(document.Id)).State);
    }

    [Fact]
    public async Task Cross_Tenant_File_Operations_Are_NotFound()
    {
        using var owner = await Context.CreateAsync(postgres, _root, "p12-xo", PermissionAction.Upload);
        var document = await UploadAsync(owner);

        using var outsider = await Context.CreateAsync(postgres, _root, "p12-xx",
            PermissionAction.Download, PermissionAction.Move, PermissionAction.Delete, PermissionAction.Rename,
            PermissionAction.Write, PermissionAction.Copy, PermissionAction.Upload, PermissionAction.Read);

        // the outsider has every permission - the document simply does not exist for them
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Service.DownloadContentAsync(document.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Service.MoveFileAsync(document.Id, new MoveDocumentRequest("x")));
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Service.RenameFileAsync(document.Id, new RenameFileRequest("x.pdf")));
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Service.DeleteContentAsync(document.Id, null));
        await Assert.ThrowsAsync<NotFoundException>(() => outsider.Service.UpdateContentAsync(document.Id, new(), Bytes("x")));
    }
}

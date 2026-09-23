using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Akasha.Application.Tenancy;
using Akasha.Domain.Authorization;
using Akasha.Domain.Identity;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP plus real filesystem connector plus real PostgreSQL for the
/// Phase 12 physical file operations. The shared fixture connector points at a temp root.
/// </summary>
public sealed class DocumentFileApiTests(ApiFixture factory) : IClassFixture<ApiFixture>
{
    private const string Base = "/api/v1/documents";

    private AkashaDbContext CreateContext(Guid? tenantId = null)
    {
        var tenantContext = new TenantContext();
        if (tenantId is { } tenant)
        {
            tenantContext.Establish(tenant);
        }

        return new AkashaDbContext(
            new DbContextOptionsBuilder<AkashaDbContext>()
                .UseAkashaNpgsql(factory.ConnectionString, 30)
                .Options,
            tenantContext);
    }

    private async Task<(string subject, Guid tenantId)> SeedAsync(string slug, params PermissionAction[] grants)
    {
        _ = factory.CreateClient();

        var subject = $"sub-{slug}";
        var tenant = new Tenant($"Tenant {slug}", slug);
        var user = new User(tenant.Id, $"User {slug}");

        await using (var context = CreateContext())
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(tenant.Id))
        {
            context.Users.Add(user);
            context.UserIdentities.Add(new UserIdentity(tenant.Id, user.Id, "internal", subject));
            foreach (var action in grants)
            {
                context.PermissionGrants.Add(new PermissionGrant(
                    tenant.Id, PrincipalType.User, user.Id, ResourceType.Document, null, action));
            }

            await context.SaveChangesAsync();
        }

        return (subject, tenant.Id);
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string subject, string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        }

        return request;
    }

    private static JsonElement Body(HttpResponseMessage response)
        => JsonDocument.Parse(response.Content.ReadAsStringAsync().Result).RootElement;

    private string UploadUrl(string name, string folder = "akte-1")
        => $"{Base}/upload?connectorId={ApiFixture.StorageConnectorId}&folder={Uri.EscapeDataString(folder)}&name={Uri.EscapeDataString(name)}";

    [Fact]
    public async Task Upload_Download_Update_Rename_Move_Copy_Delete_roundtrip()
    {
        var (subject, _) = await SeedAsync("fapi-full",
            PermissionAction.Upload, PermissionAction.Read, PermissionAction.Download, PermissionAction.Write,
            PermissionAction.Rename, PermissionAction.Move, PermissionAction.Copy, PermissionAction.Delete);
        var client = factory.CreateClient();

        // upload
        var upload = await client.SendAsync(Request(HttpMethod.Post, UploadUrl("contract.pdf"), subject, "content-v1"));
        Assert.True(upload.StatusCode == HttpStatusCode.Created,
            $"expected 201, got {upload.StatusCode}: {await upload.Content.ReadAsStringAsync()}");
        var document = Body(upload);
        var id = document.GetProperty("id").GetGuid();
        Assert.Equal("Available", document.GetProperty("state").GetString());
        Assert.Equal("contract.pdf", document.GetProperty("name").GetString());

        // download roundtrip
        var download = await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{id}/content", subject));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("content-v1", await download.Content.ReadAsStringAsync());
        Assert.Equal("contract.pdf", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        // update without If-Match: known ETag still matches
        var update = await client.SendAsync(Request(HttpMethod.Put, $"{Base}/{id}/content", subject, "content-v2"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var etag = Body(update).GetProperty("storage").GetProperty("eTag").GetString();
        Assert.False(string.IsNullOrEmpty(etag));

        // update with a wrong If-Match conflicts, nothing is overwritten
        var stale = Request(HttpMethod.Put, $"{Base}/{id}/content", subject, "content-v3");
        stale.Headers.TryAddWithoutValidation("If-Match", "\"bogus\"");
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(stale)).StatusCode);

        // physical rename keeps identity
        var rename = Request(HttpMethod.Post, $"{Base}/{id}/rename-file", subject);
        rename.Content = JsonContent.Create(new { name = "vertrag.pdf" });
        var renamed = Body(await client.SendAsync(rename));
        Assert.Equal(id, renamed.GetProperty("id").GetGuid());
        Assert.Equal("vertrag.pdf", renamed.GetProperty("name").GetString());

        // move keeps identity
        var move = Request(HttpMethod.Post, $"{Base}/{id}/move", subject);
        move.Content = JsonContent.Create(new { targetFolderPath = "akte-2" });
        var moved = Body(await client.SendAsync(move));
        Assert.Equal(id, moved.GetProperty("id").GetGuid());
        Assert.Equal("akte-2", moved.GetProperty("storage").GetProperty("path").GetString());

        // copy creates a new document
        var copy = Request(HttpMethod.Post, $"{Base}/{id}/copy", subject);
        copy.Content = JsonContent.Create(new { targetFolderPath = "akte-3", newName = "copy.pdf" });
        var copyResponse = await client.SendAsync(copy);
        Assert.Equal(HttpStatusCode.Created, copyResponse.StatusCode);
        var copyId = Body(copyResponse).GetProperty("id").GetGuid();
        Assert.NotEqual(id, copyId);

        // physical delete keeps the logical document as Missing
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(Request(HttpMethod.Delete, $"{Base}/{id}/content", subject))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{id}/content", subject))).StatusCode);
        var logical = Body(await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{id}", subject)));
        Assert.Equal("Missing", logical.GetProperty("state").GetString());

        // cleanup the copy
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(Request(HttpMethod.Delete, $"{Base}/{copyId}/content", subject))).StatusCode);
    }

    [Fact]
    public async Task Upload_with_traversal_name_is_bad_request()
    {
        var (subject, _) = await SeedAsync("fapi-trav", PermissionAction.Upload);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(Request(HttpMethod.Post, UploadUrl("../evil.txt"), subject, "x"))).StatusCode);
    }

    [Fact]
    public async Task Upload_to_unknown_connector_is_not_found()
    {
        var (subject, _) = await SeedAsync("fapi-ncon", PermissionAction.Upload);
        var client = factory.CreateClient();

        var url = $"{Base}/upload?connectorId={Guid.NewGuid()}&folder=a&name=a.pdf";
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Request(HttpMethod.Post, url, subject, "x"))).StatusCode);
    }

    [Fact]
    public async Task Duplicate_upload_conflicts()
    {
        var (subject, _) = await SeedAsync("fapi-dup", PermissionAction.Upload);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created,
            (await client.SendAsync(Request(HttpMethod.Post, UploadUrl("dup.pdf", "fapi-dup"), subject, "one"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(Request(HttpMethod.Post, UploadUrl("dup.pdf", "fapi-dup"), subject, "two"))).StatusCode);
    }

    [Fact]
    public async Task Content_operations_without_grants_are_forbidden()
    {
        var (owner, _) = await SeedAsync("fapi-own", PermissionAction.Upload, PermissionAction.Read);
        var (reader, _) = await SeedAsync("fapi-reader", PermissionAction.Read);
        var client = factory.CreateClient();

        var upload = await client.SendAsync(Request(HttpMethod.Post, UploadUrl("private.pdf", "fapi-own"), owner, "secret"));
        var id = Body(upload).GetProperty("id").GetGuid();

        // no Download grant
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request(HttpMethod.Get, $"{Base}/{id}/content", reader))).StatusCode);
        // no Move grant
        var move = Request(HttpMethod.Post, $"{Base}/{id}/move", reader);
        move.Content = JsonContent.Create(new { targetFolderPath = "elsewhere" });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(move)).StatusCode);
        // no Delete grant
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request(HttpMethod.Delete, $"{Base}/{id}/content", reader))).StatusCode);
    }
}

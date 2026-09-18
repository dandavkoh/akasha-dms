using Akasha.Application.Authorization;
using Akasha.Application.Identity;
using Akasha.Domain.Authorization;
using Akasha.Domain.Identity;
using Akasha.Domain.Tenants;
using Akasha.Infrastructure.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AuthorizationTests(PostgresFixture postgres)
{
    private CurrentUser User(Akasha.Domain.Identity.User user)
        => new(user.Id, user.TenantId, $"sub-{user.Id}", "internal", user.IsServiceAccount);

    private static IAuthorizationService Service(Akasha.Infrastructure.Persistence.AkashaDbContext context)
        => new AuthorizationService(context);

    private async Task<(Tenant Tenant, Akasha.Domain.Identity.User User)> SeedUserAsync(string slug, UserKind kind = UserKind.Person)
    {
        await using var context = postgres.CreateContext();
        var tenant = new Tenant($"Tenant {slug}", slug);
        var user = new Akasha.Domain.Identity.User(tenant.Id, $"User {slug}", kind);
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        await using var scoped = postgres.CreateContext(tenant.Id);
        scoped.Users.Add(user);
        await scoped.SaveChangesAsync();
        return (tenant, user);
    }

    [Fact]
    public async Task No_grant_means_denied()
    {
        var (_, user) = await SeedUserAsync("authz-none");
        await using var context = postgres.CreateContext(user.TenantId);

        Assert.False(await Service(context).IsAuthorizedAsync(
            User(user), ResourceType.Document, Guid.NewGuid(), PermissionAction.Read));
    }

    [Fact]
    public async Task Direct_user_grant_allows()
    {
        var (_, user) = await SeedUserAsync("authz-user");
        var documentId = Guid.NewGuid();
        await using var context = postgres.CreateContext(user.TenantId);
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.User, user.Id, ResourceType.Document, documentId, PermissionAction.Read));
        await context.SaveChangesAsync();

        var service = Service(context);
        Assert.True(await service.IsAuthorizedAsync(User(user), ResourceType.Document, documentId, PermissionAction.Read));
        // Other action and other resource stay denied.
        Assert.False(await service.IsAuthorizedAsync(User(user), ResourceType.Document, documentId, PermissionAction.Delete));
        Assert.False(await service.IsAuthorizedAsync(User(user), ResourceType.Document, Guid.NewGuid(), PermissionAction.Read));
    }

    [Fact]
    public async Task Group_grant_applies_to_members()
    {
        var (_, user) = await SeedUserAsync("authz-group-member");
        var (_, outsider) = await SeedUserAsync("authz-group-outsider");
        var documentId = Guid.NewGuid();

        await using var context = postgres.CreateContext(user.TenantId);
        var group = new Group(user.TenantId, "Readers");
        context.Groups.Add(group);
        context.GroupMembers.Add(new GroupMember(user.TenantId, group.Id, user.Id));
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.Group, group.Id, ResourceType.Document, documentId, PermissionAction.Download));
        await context.SaveChangesAsync();

        var service = Service(context);
        Assert.True(await service.IsAuthorizedAsync(User(user), ResourceType.Document, documentId, PermissionAction.Download));

        // different tenant user in no group: denied
        await using var otherContext = postgres.CreateContext(outsider.TenantId);
        Assert.False(await Service(otherContext).IsAuthorizedAsync(
            User(outsider), ResourceType.Document, documentId, PermissionAction.Download));
    }

    private async Task<Akasha.Domain.Identity.User> AddUserAsync(Guid tenantId, string name)
    {
        await using var context = postgres.CreateContext(tenantId);
        var user = new Akasha.Domain.Identity.User(tenantId, name);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Role_grant_applies_to_assigned_users_and_groups()
    {
        var (_, viaUser) = await SeedUserAsync("authz-role-user");
        var viaGroup = await AddUserAsync(viaUser.TenantId, "via group");
        var documentId = Guid.NewGuid();

        await using var context = postgres.CreateContext(viaUser.TenantId);
        var role = new Role(viaUser.TenantId, "Reviewer");
        var group = new Group(viaUser.TenantId, "ReviewTeam");
        context.Roles.Add(role);
        context.Groups.Add(group);
        context.RoleAssignments.Add(new RoleAssignment(viaUser.TenantId, role.Id, PrincipalType.User, viaUser.Id));
        context.RoleAssignments.Add(new RoleAssignment(viaUser.TenantId, role.Id, PrincipalType.Group, group.Id));
        context.GroupMembers.Add(new GroupMember(viaUser.TenantId, group.Id, viaGroup.Id));
        context.PermissionGrants.Add(new PermissionGrant(
            viaUser.TenantId, PrincipalType.Role, role.Id, ResourceType.Document, documentId, PermissionAction.Approve));
        await context.SaveChangesAsync();

        var service = Service(context);
        Assert.True(await service.IsAuthorizedAsync(User(viaUser), ResourceType.Document, documentId, PermissionAction.Approve));
        Assert.True(await service.IsAuthorizedAsync(User(viaGroup), ResourceType.Document, documentId, PermissionAction.Approve));
    }

    [Fact]
    public async Task Type_level_grant_is_inherited_by_all_resources_of_that_type()
    {
        var (_, user) = await SeedUserAsync("authz-typelevel");
        await using var context = postgres.CreateContext(user.TenantId);
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.User, user.Id, ResourceType.Akte, null, PermissionAction.Read));
        await context.SaveChangesAsync();

        var service = Service(context);
        Assert.True(await service.IsAuthorizedAsync(User(user), ResourceType.Akte, Guid.NewGuid(), PermissionAction.Read));
        Assert.True(await service.IsAuthorizedAsync(User(user), ResourceType.Akte, Guid.NewGuid(), PermissionAction.Read));
        Assert.False(await service.IsAuthorizedAsync(User(user), ResourceType.Document, Guid.NewGuid(), PermissionAction.Read));
    }

    [Fact]
    public async Task Explicit_deny_overrides_allow()
    {
        var (_, user) = await SeedUserAsync("authz-deny");
        var documentId = Guid.NewGuid();
        await using var context = postgres.CreateContext(user.TenantId);
        // broad allow via type-level grant, specific deny on the resource
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.User, user.Id, ResourceType.Document, null, PermissionAction.Read));
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.User, user.Id, ResourceType.Document, documentId, PermissionAction.Read, GrantEffect.Deny));
        await context.SaveChangesAsync();

        var service = Service(context);
        Assert.False(await service.IsAuthorizedAsync(User(user), ResourceType.Document, documentId, PermissionAction.Read));
        Assert.True(await service.IsAuthorizedAsync(User(user), ResourceType.Document, Guid.NewGuid(), PermissionAction.Read));
    }

    [Fact]
    public async Task Deny_via_group_beats_direct_user_allow()
    {
        var (_, user) = await SeedUserAsync("authz-deny-group");
        var documentId = Guid.NewGuid();
        await using var context = postgres.CreateContext(user.TenantId);
        var group = new Group(user.TenantId, "Restricted");
        context.Groups.Add(group);
        context.GroupMembers.Add(new GroupMember(user.TenantId, group.Id, user.Id));
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.User, user.Id, ResourceType.Document, documentId, PermissionAction.Delete));
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.Group, group.Id, ResourceType.Document, documentId, PermissionAction.Delete, GrantEffect.Deny));
        await context.SaveChangesAsync();

        Assert.False(await Service(context).IsAuthorizedAsync(
            User(user), ResourceType.Document, documentId, PermissionAction.Delete));
    }

    [Fact]
    public async Task Tenant_admin_grant_allows_everything_in_tenant()
    {
        var (_, user) = await SeedUserAsync("authz-admin");
        await using var context = postgres.CreateContext(user.TenantId);
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.User, user.Id, ResourceType.Tenant, null, PermissionAction.Admin));
        await context.SaveChangesAsync();

        var service = Service(context);
        Assert.True(await service.IsAuthorizedAsync(User(user), ResourceType.Document, Guid.NewGuid(), PermissionAction.Delete));
        Assert.True(await service.IsAuthorizedAsync(User(user), ResourceType.Connector, null, PermissionAction.Admin));
        Assert.True(await service.IsAuthorizedAsync(User(user), ResourceType.AdminSettings, null, PermissionAction.Admin));
    }

    [Fact]
    public async Task Explicit_deny_overrides_tenant_admin()
    {
        var (_, user) = await SeedUserAsync("authz-admin-deny");
        var documentId = Guid.NewGuid();
        await using var context = postgres.CreateContext(user.TenantId);
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.User, user.Id, ResourceType.Tenant, null, PermissionAction.Admin));
        context.PermissionGrants.Add(new PermissionGrant(
            user.TenantId, PrincipalType.User, user.Id, ResourceType.Document, documentId, PermissionAction.Share, GrantEffect.Deny));
        await context.SaveChangesAsync();

        Assert.False(await Service(context).IsAuthorizedAsync(
            User(user), ResourceType.Document, documentId, PermissionAction.Share));
    }

    [Fact]
    public async Task Cross_tenant_grants_are_never_inherited()
    {
        var (tenantA, userA) = await SeedUserAsync("authz-cross-a");
        var (tenantB, userB) = await SeedUserAsync("authz-cross-b");
        var documentId = Guid.NewGuid();

        // tenant A grants its user READ on a document id that "exists" in tenant B
        await using (var contextA = postgres.CreateContext(tenantA.Id))
        {
            contextA.PermissionGrants.Add(new PermissionGrant(
                tenantA.Id, PrincipalType.User, userA.Id, ResourceType.Document, documentId, PermissionAction.Read));
            await contextA.SaveChangesAsync();
        }

        // tenant B's context must not see tenant A's grants - for neither user
        await using var contextB = postgres.CreateContext(tenantB.Id);
        var service = Service(contextB);
        Assert.False(await service.IsAuthorizedAsync(User(userB), ResourceType.Document, documentId, PermissionAction.Read));

        // even if a B-user record pointed at A's grant, evaluation under B returns nothing
        Assert.Empty(await contextB.PermissionGrants.ToListAsync());
    }

    [Fact]
    public async Task Service_accounts_use_the_same_authorization_path()
    {
        var (_, service) = await SeedUserAsync("authz-svc", UserKind.ServiceAccount);
        var documentId = Guid.NewGuid();
        await using var context = postgres.CreateContext(service.TenantId);

        var authService = Service(context);
        Assert.False(await authService.IsAuthorizedAsync(
            User(service), ResourceType.Document, documentId, PermissionAction.Read));

        context.PermissionGrants.Add(new PermissionGrant(
            service.TenantId, PrincipalType.User, service.Id, ResourceType.Document, documentId, PermissionAction.Read));
        await context.SaveChangesAsync();

        Assert.True(await authService.IsAuthorizedAsync(
            User(service), ResourceType.Document, documentId, PermissionAction.Read));
    }

    [Fact]
    public async Task RequireAsync_throws_AccessDenied_when_denied()
    {
        var (_, user) = await SeedUserAsync("authz-require");
        await using var context = postgres.CreateContext(user.TenantId);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Service(context).RequireAsync(
            User(user), ResourceType.Workflow, null, PermissionAction.StartWorkflow));
    }

    [Fact]
    public async Task Role_names_are_unique_per_tenant()
    {
        var (tenant, _) = await SeedUserAsync("authz-unique");
        await using var context = postgres.CreateContext(tenant.Id);
        context.Roles.Add(new Role(tenant.Id, "Admin"));
        await context.SaveChangesAsync();

        context.Roles.Add(new Role(tenant.Id, "Admin"));
        await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync());
    }
}

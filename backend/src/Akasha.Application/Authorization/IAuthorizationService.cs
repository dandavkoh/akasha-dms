using Akasha.Application.Identity;
using Akasha.Domain.Authorization;

namespace Akasha.Application.Authorization;

/// <summary>Thrown when the current identity is not allowed to perform an action on a resource.</summary>
public sealed class AccessDeniedException : Exception
{
    public AccessDeniedException(ResourceType resourceType, Guid? resourceId, PermissionAction action)
        : base($"Access denied: {action} on {resourceType} {(resourceId?.ToString() ?? "(type-level)")}.")
    {
        ResourceType = resourceType;
        ResourceId = resourceId;
        Action = action;
    }

    public ResourceType ResourceType { get; }

    public Guid? ResourceId { get; }

    public PermissionAction Action { get; }
}

/// <summary>
/// Central server-side authorization service. Every security-relevant operation MUST
/// be authorized here or through a caller of this service - never ad-hoc in controllers,
/// UIs or scattered role checks.
///
/// This service evaluates Akasha (DMS) permissions only. For documents in external
/// storage the effective permission is DMS permission INTERSECT storage permission;
/// storage connectors will contribute the storage side when they are implemented, and
/// must never be bypassed by a DMS allow.
/// </summary>
public interface IAuthorizationService
{
    Task<bool> IsAuthorizedAsync(
        CurrentUser user,
        ResourceType resourceType,
        Guid? resourceId,
        PermissionAction action,
        CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="IsAuthorizedAsync"/>, but throws <see cref="AccessDeniedException"/> when denied.</summary>
    async Task RequireAsync(
        CurrentUser user,
        ResourceType resourceType,
        Guid? resourceId,
        PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedAsync(user, resourceType, resourceId, action, cancellationToken))
        {
            throw new AccessDeniedException(resourceType, resourceId, action);
        }
    }
}

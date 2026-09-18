using Akasha.Application.Identity;
using Akasha.Application.Tenancy;

namespace Akasha.Api.Middleware;

/// <summary>
/// Establishes the authentication and tenant context for authenticated requests from the
/// authenticated identity, validated server-side against persisted user identities.
/// Client-supplied tenant values are only honored after validation.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IIdentityResolver identityResolver,
        ICurrentUser currentUser,
        ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                var identity = await identityResolver.ResolveAsync(context.User, context.RequestAborted);
                currentUser.Establish(identity);
                tenantContext.Establish(identity.TenantId);
            }
            catch (TenantResolutionException resolution)
            {
                context.Response.StatusCode = resolution.Failure switch
                {
                    TenantResolutionFailure.Unauthenticated => StatusCodes.Status401Unauthorized,
                    TenantResolutionFailure.Ambiguous => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status403Forbidden,
                };
                return;
            }
        }

        await next(context);
    }
}

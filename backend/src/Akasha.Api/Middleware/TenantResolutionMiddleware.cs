using Akasha.Application.Tenancy;

namespace Akasha.Api.Middleware;

/// <summary>
/// Establishes the tenant context for authenticated requests from the authenticated
/// identity, validated server-side against tenant memberships. Client-supplied tenant
/// values are only honored after membership validation.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantResolver tenantResolver, ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                tenantContext.Establish(await tenantResolver.ResolveTenantIdAsync(
                    context.User, context.RequestAborted));
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

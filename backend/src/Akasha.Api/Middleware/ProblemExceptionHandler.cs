using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Tenancy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Akasha.Api.Middleware;

/// <summary>
/// Maps application-layer exceptions to HTTP problem responses. Raw internal exceptions
/// are never exposed (rule #59); unknown exceptions fall through to the default handler.
/// </summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, detail) = exception switch
        {
            AccessDeniedException => (StatusCodes.Status403Forbidden, "Access denied."),
            TenantIsolationViolationException => (StatusCodes.Status403Forbidden, "Access denied."),
            TenantResolutionException resolution when resolution.Failure == TenantResolutionFailure.Unauthenticated
                => (StatusCodes.Status401Unauthorized, "Authentication required."),
            TenantResolutionException => (StatusCodes.Status403Forbidden, "Forbidden."),
            NotFoundException notFound => (StatusCodes.Status404NotFound, notFound.Message),
            ConflictException conflict => (StatusCodes.Status409Conflict, conflict.Message),
            ValidationException validation => (StatusCodes.Status400BadRequest, validation.Message),
            _ => (0, null),
        };

        if (status == 0)
        {
            return false;
        }

        context.Response.StatusCode = status;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = detail,
            },
        });
        return true;
    }
}

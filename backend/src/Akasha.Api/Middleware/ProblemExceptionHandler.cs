using Akasha.Application.Authorization;
using Akasha.Application.Common;
using Akasha.Application.Storage;
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
            StorageException storage => StorageStatus(storage),
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

    /// <summary>Maps normalized storage failures. Unknown kinds fall through to the generic 500 handler.</summary>
    private static (int Status, string Detail) StorageStatus(StorageException exception)
        => exception.Kind switch
        {
            StorageErrorKind.NotFound => (StatusCodes.Status404NotFound, "The physical file was not found."),
            StorageErrorKind.AccessDenied => (StatusCodes.Status403Forbidden, "The storage system denied access."),
            StorageErrorKind.ConcurrencyConflict => (StatusCodes.Status409Conflict, "The file changed since the known version; reload and retry."),
            StorageErrorKind.Conflict => (StatusCodes.Status409Conflict, "Storage conflict: the destination may already exist."),
            StorageErrorKind.Validation => (StatusCodes.Status400BadRequest, exception.Message),
            StorageErrorKind.Unavailable => (StatusCodes.Status503ServiceUnavailable, "The storage system is currently unavailable."),
            StorageErrorKind.AuthenticationRequired => (StatusCodes.Status503ServiceUnavailable, "The storage connector requires re-authentication."),
            StorageErrorKind.UnsupportedOperation => (StatusCodes.Status400BadRequest, exception.Message),
            _ => (0, null!),
        };
}

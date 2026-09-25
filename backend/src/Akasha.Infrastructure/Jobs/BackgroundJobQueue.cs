using Akasha.Application.Common;
using Akasha.Application.Identity;
using Akasha.Application.Jobs;
using Akasha.Application.Tenancy;
using Akasha.Domain.Jobs;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Akasha.Infrastructure.Jobs;

/// <summary>
/// PostgreSQL-backed <see cref="IBackgroundJobQueue"/>. Captures tenant, initiating
/// identity and correlation id into the job row at enqueue time, so execution never
/// depends on ambient caller state (rule #33). Enqueue requires an established tenant
/// context; dedupe is enforced by the unique (TenantId, Type, IdempotencyKey) index.
/// </summary>
public sealed class BackgroundJobQueue(
    AkashaDbContext dbContext,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    ICorrelationContext correlationContext,
    IOptions<BackgroundJobOptions> options) : IBackgroundJobQueue
{
    public async Task<Guid> EnqueueAsync(EnqueueBackgroundJob request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Type))
        {
            throw new ValidationException("Job type is required.");
        }

        var tenantId = tenantContext.Require();
        var operationId = Guid.NewGuid();
        var correlationId = correlationContext.CorrelationId ?? operationId.ToString("N");
        var initiatedBy = currentUser.User;

        var job = new BackgroundJob(
            tenantId,
            request.Type,
            request.Payload,
            correlationId,
            options.Value.MaxAttempts,
            operationId,
            request.IdempotencyKey,
            initiatedBy?.UserId,
            initiatedBy?.Subject,
            initiatedBy?.Provider,
            initiatedBy?.IsServiceAccount ?? false,
            request.NotBeforeUtc);

        dbContext.BackgroundJobs.Add(job);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent or repeated enqueue of the same logical operation (#34):
            // return the already-existing job instead of executing twice.
            dbContext.Entry(job).State = EntityState.Detached;
            return await dbContext.BackgroundJobs
                .Where(existing => existing.Type == request.Type && existing.IdempotencyKey == request.IdempotencyKey)
                .Select(existing => existing.Id)
                .SingleAsync(cancellationToken);
        }

        return job.Id;
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        // Tenant query filter: another tenant's job is invisible and cannot be cancelled.
        var job = await dbContext.BackgroundJobs
                      .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken)
                  ?? throw new NotFoundException("background job", jobId);

        job.Cancel();
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

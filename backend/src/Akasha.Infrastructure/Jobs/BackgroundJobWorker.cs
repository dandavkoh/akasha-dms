using Akasha.Application.Common;
using Akasha.Application.Identity;
using Akasha.Application.Jobs;
using Akasha.Application.Tenancy;
using Akasha.Domain.Jobs;
using Akasha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Akasha.Infrastructure.Jobs;

/// <summary>
/// Polls the PostgreSQL job table and executes due jobs. Safety properties:
/// - the poller intentionally sees all tenants (IgnoreQueryFilters); every claimed job
///   is then executed in its own DI scope with that job's tenant, identity and
///   correlation context established (rules #33, #158, #159) — a job can never leak
///   into another tenant's scope;
/// - claiming is atomic (conditional UPDATE), so two workers never run the same job twice;
/// - On startup, Running jobs are requeued: a crashed worker never leaves work stuck.
///   ponytail: assumes a single worker per database. Upgrade path for multiple workers:
///   heartbeat column + requeue-stale-by-timeout instead of requeue-all-on-startup.
/// - transient failures retry with exponential backoff + jitter (#35, #137); permanent
///   failures and exhausted retries move to the dead state for administrative resolution;
/// - a running job is cancelled cooperatively: the worker polls the flag and cancels
///   the job's execution token (rule #135). On host shutdown the job stays Running and
///   is recovered by the next startup, so nothing is silently lost.
/// </summary>
public sealed class BackgroundJobWorker(
    IServiceProvider services,
    IOptions<BackgroundJobOptions> options,
    ILogger<BackgroundJobWorker> logger) : BackgroundService
{
    /// <summary>Service identity for jobs without an initiating user. No object grants by
    /// default: a system job only gets what the authorization model explicitly allows (#79).</summary>
    public static readonly Guid SystemUserId = Guid.Empty;

    private readonly BackgroundJobOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAbandonedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAvailableAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Background job poll cycle failed.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }
    }

    /// <summary>Requeues jobs left in Running by a crashed/stopped worker (restart-resume).</summary>
    public async Task RecoverAbandonedJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
        var requeued = await dbContext.BackgroundJobs
            .IgnoreQueryFilters()
            .Where(job => job.Status == BackgroundJobStatus.Running)
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, BackgroundJobStatus.Queued)
                .SetProperty(job => job.ScheduledAtUtc, DateTimeOffset.UtcNow),
                cancellationToken);

        if (requeued > 0)
        {
            logger.LogWarning("Requeued {Count} background job(s) abandoned by a previous worker.", requeued);
        }
    }

    /// <summary>Claims and executes all currently due jobs. Called by the host loop; also the deterministic test driver.</summary>
    public async Task ProcessAvailableAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();

        // Compare against a client-side 'now' (EF parameterizes a local variable):
        // ScheduledAtUtc is always written from application time, so comparing with the
        // database server's now() could pick up jobs slightly early when clocks drift.
        var now = DateTimeOffset.UtcNow;
        var dueJobIds = await dbContext.BackgroundJobs
            .IgnoreQueryFilters()
            .Where(job => job.Status == BackgroundJobStatus.Queued && job.ScheduledAtUtc <= now)
            .OrderBy(job => job.ScheduledAtUtc)
            .Select(job => job.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var jobId in dueJobIds)
        {
            // Atomic claim: only the UPDATE that transitions Queued → Running wins.
            var claimed = await dbContext.BackgroundJobs
                .IgnoreQueryFilters()
                .Where(job => job.Id == jobId && job.Status == BackgroundJobStatus.Queued)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(job => job.Status, BackgroundJobStatus.Running)
                    .SetProperty(job => job.StartedAtUtc, DateTimeOffset.UtcNow),
                    cancellationToken);

            if (claimed == 1)
            {
                await ExecuteClaimedJobAsync(jobId, cancellationToken);
            }
        }
    }

    private async Task ExecuteClaimedJobAsync(Guid jobId, CancellationToken shutdownToken)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var dbContext = provider.GetRequiredService<AkashaDbContext>();

        var job = await dbContext.BackgroundJobs
            .IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == jobId, shutdownToken);

        // Establish the job's own context — never the poller's, never ambient (rules #158, #159).
        provider.GetRequiredService<ITenantContext>().Establish(job.TenantId);
        provider.GetRequiredService<ICurrentUser>().Establish(job.InitiatedByUserId is { } userId
            ? new CurrentUser(userId, job.TenantId, job.InitiatedBySubject!, job.InitiatedByProvider!, job.InitiatedByIsServiceAccount)
            : new CurrentUser(SystemUserId, job.TenantId, "background-worker", "background", IsServiceAccount: true));
        provider.GetRequiredService<ICorrelationContext>().Establish(job.CorrelationId);

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["TenantId"] = job.TenantId,
            ["JobId"] = job.Id,
            ["JobType"] = job.Type,
            ["OperationId"] = job.OperationId,
            ["CorrelationId"] = job.CorrelationId,
        });

        var handler = provider.GetServices<IBackgroundJobHandler>()
            .FirstOrDefault(candidate => candidate.Type == job.Type);

        if (handler is null)
        {
            job.MoveToDead($"No background job handler registered for type '{job.Type}'.");
            await dbContext.SaveChangesAsync(shutdownToken);
            logger.LogError("Background job {JobId} of type {JobType} dead: no handler registered.", job.Id, job.Type);
            return;
        }

        var context = new BackgroundJobContext(
            job.Id, job.TenantId, job.OperationId, job.CorrelationId, job.Payload, job.AttemptCount + 1);

        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        var cancellationMonitor = CancellationMonitorAsync(job.Id, jobCancellation);
        try
        {
            await handler.ExecuteAsync(context, jobCancellation.Token);
            job.Complete();
            logger.LogInformation("Background job {JobId} of type {JobType} completed.", job.Id, job.Type);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Host shutdown: leave the row Running without persisting; the next worker
            // startup requeues it (RecoverAbandonedJobsAsync). Nothing is silently lost.
            throw;
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
            logger.LogInformation("Background job {JobId} of type {JobType} was cancelled.", job.Id, job.Type);
        }
        catch (PermanentJobFailureException exception)
        {
            job.MoveToDead(exception.Message);
            logger.LogError(exception, "Background job {JobId} of type {JobType} failed permanently and is dead.", job.Id, job.Type);
        }
        catch (Exception exception)
        {
            var attempt = job.AttemptCount + 1;
            if (attempt >= job.MaxAttempts)
            {
                job.MoveToDead($"Attempts exhausted ({job.MaxAttempts}). Last error: {exception.Message}");
                logger.LogError(exception,
                    "Background job {JobId} of type {JobType} exhausted all attempts and is dead.", job.Id, job.Type);
            }
            else
            {
                var retryAt = DateTimeOffset.UtcNow + ComputeRetryDelay(attempt);
                job.ScheduleRetry($"{exception.GetType().Name}: {exception.Message}", retryAt);
                logger.LogWarning(exception,
                    "Background job {JobId} of type {JobType} failed (attempt {Attempt}/{MaxAttempts}), retry at {RetryAtUtc}.",
                    job.Id, job.Type, attempt, job.MaxAttempts, retryAt);
            }
        }
        finally
        {
            await jobCancellation.CancelAsync();
            try
            {
                await cancellationMonitor;
            }
            catch (Exception)
            {
                // The monitor never decides the outcome; its failures are logged inside.
            }
        }

        await dbContext.SaveChangesAsync(shutdownToken);
    }

    /// <summary>Polls the cancellation flag while the handler runs and cancels the job token cooperatively.</summary>
    private async Task CancellationMonitorAsync(Guid jobId, CancellationTokenSource jobCancellation)
    {
        while (!jobCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CancellationCheckInterval, jobCancellation.Token);
                await using var scope = services.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
                var requested = await dbContext.BackgroundJobs
                    .IgnoreQueryFilters()
                    .Where(job => job.Id == jobId)
                    .Select(job => job.CancellationRequested)
                    .SingleAsync(jobCancellation.Token);
                if (requested)
                {
                    await jobCancellation.CancelAsync();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Cancellation check for background job {JobId} failed; retrying.", jobId);
            }
        }
    }

    /// <summary>Exponential backoff capped at the configured maximum, with ±20% jitter against thundering herds.</summary>
    private TimeSpan ComputeRetryDelay(int failedAttempt)
    {
        var exponential = _options.InitialRetryDelay * Math.Pow(2, failedAttempt - 1);
        var capped = exponential > _options.MaxRetryDelay ? _options.MaxRetryDelay : exponential;
        return capped * (1 + (Random.Shared.NextDouble() * 0.4 - 0.2));
    }
}

namespace Akasha.Application.Jobs;

/// <summary>
/// What to enqueue. <paramref name="Type"/> routes to the registered handler,
/// <paramref name="Payload"/> is module-defined serialized context (never credentials),
/// <paramref name="IdempotencyKey"/> deduplicates the same logical operation
/// (e.g. a provider change id): an already-known key returns the existing job without
/// executing it twice.
/// </summary>
public sealed record EnqueueBackgroundJob(
    string Type,
    string Payload,
    string? IdempotencyKey = null,
    DateTimeOffset? NotBeforeUtc = null);

/// <summary>
/// Enqueues and cancels tenant-scoped background work. The queue captures the current
/// tenant, initiating identity and correlation id at enqueue time — a job never loses
/// its tenant context (rule #33). Tenant context is mandatory: enqueue without one throws.
/// </summary>
public interface IBackgroundJobQueue
{
    /// <summary>Returns the job id. With an idempotency key that was already enqueued,
    /// returns the id of the existing job instead of creating a duplicate.</summary>
    Task<Guid> EnqueueAsync(EnqueueBackgroundJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a job of the current tenant (cross-tenant jobs are invisible).
    /// Queued jobs are cancelled immediately; a running job is cancelled cooperatively.
    /// </summary>
    Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default);
}

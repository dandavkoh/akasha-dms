using Akasha.Domain.Common;

namespace Akasha.Domain.Jobs;

public enum BackgroundJobStatus
{
    /// <summary>Waiting to be claimed (possibly scheduled for the future = pending retry).</summary>
    Queued,

    /// <summary>Claimed by a worker and currently executing.</summary>
    Running,

    /// <summary>Terminal: cancelled before completion.</summary>
    Cancelled,

    /// <summary>Terminal: the handler finished successfully.</summary>
    Completed,

    /// <summary>Terminal dead state: retries exhausted or a permanent failure. Administrative resolution required.</summary>
    Dead,
}

/// <summary>
/// Durable background job carrying its full execution context: tenant, initiating
/// identity (null for system jobs), operation and correlation id (rules #33, #79).
/// The database row is the queue — no broker, no lost context, restart-safe:
/// a job stuck in Running after a crash is requeued by the worker on startup.
/// Idempotent enqueue is enforced by the unique (TenantId, Type, IdempotencyKey) index.
/// </summary>
public sealed class BackgroundJob : ITenantScoped
{
    public const int MaxTypeLength = 100;
    public const int MaxIdempotencyKeyLength = 200;
    public const int MaxErrorLength = 2000;

    private BackgroundJob()
    {
    }

    public BackgroundJob(
        Guid tenantId,
        string type,
        string payload,
        string correlationId,
        int maxAttempts,
        Guid operationId,
        string? idempotencyKey = null,
        Guid? initiatedByUserId = null,
        string? initiatedBySubject = null,
        string? initiatedByProvider = null,
        bool initiatedByIsServiceAccount = false,
        DateTimeOffset? notBeforeUtc = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(type) || type.Length > MaxTypeLength)
        {
            throw new ArgumentException($"Type is required (max {MaxTypeLength} chars).", nameof(type));
        }

        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        }

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one attempt is required.");
        }

        if (idempotencyKey is { Length: > MaxIdempotencyKeyLength })
        {
            throw new ArgumentException(
                $"Idempotency key must not exceed {MaxIdempotencyKeyLength} chars.", nameof(idempotencyKey));
        }

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Type = type;
        Payload = payload;
        CorrelationId = correlationId;
        OperationId = operationId;
        MaxAttempts = maxAttempts;
        IdempotencyKey = idempotencyKey;
        InitiatedByUserId = initiatedByUserId;
        InitiatedBySubject = initiatedBySubject;
        InitiatedByProvider = initiatedByProvider;
        InitiatedByIsServiceAccount = initiatedByIsServiceAccount;
        Status = BackgroundJobStatus.Queued;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        ScheduledAtUtc = notBeforeUtc ?? CreatedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Handler routing key, e.g. "storage.sync", "document.ocr".</summary>
    public string Type { get; private set; } = string.Empty;

    /// <summary>Opaque context (module-defined serialization). Never contains credentials (#80).</summary>
    public string Payload { get; private set; } = string.Empty;

    public string? IdempotencyKey { get; private set; }

    public Guid OperationId { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public Guid? InitiatedByUserId { get; private set; }

    public string? InitiatedBySubject { get; private set; }

    public string? InitiatedByProvider { get; private set; }

    public bool InitiatedByIsServiceAccount { get; private set; }

    public BackgroundJobStatus Status { get; private set; }

    /// <summary>Number of failed attempts so far (incremented on each failure).</summary>
    public int AttemptCount { get; private set; }

    public int MaxAttempts { get; private set; }

    public bool CancellationRequested { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Earliest execution time; doubles as the retry backoff checkpoint.</summary>
    public DateTimeOffset ScheduledAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// Cancels the job. Queued jobs transition to Cancelled immediately; a running job is
    /// flagged and its execution token is cancelled cooperatively by the worker.
    /// Terminal jobs are unaffected.
    /// </summary>
    public void Cancel()
    {
        if (Status == BackgroundJobStatus.Queued)
        {
            Status = BackgroundJobStatus.Cancelled;
            CompletedAtUtc = DateTimeOffset.UtcNow;
        }
        else if (Status == BackgroundJobStatus.Running)
        {
            CancellationRequested = true;
        }
    }

    public void Complete()
    {
        if (Status != BackgroundJobStatus.Running)
        {
            throw new InvalidOperationException($"Cannot complete a job in state {Status}.");
        }

        Status = BackgroundJobStatus.Completed;
        CompletedAtUtc = DateTimeOffset.UtcNow;
        LastError = null;
    }

    /// <summary>Terminal cancellation of a running job after cooperative cancellation succeeded.</summary>
    public void MarkCancelled()
    {
        if (Status != BackgroundJobStatus.Running)
        {
            throw new InvalidOperationException($"Cannot cancel a job in state {Status}.");
        }

        Status = BackgroundJobStatus.Cancelled;
        CompletedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Transient failure: requeue with a backoff checkpoint. Retries exhausted use <see cref="MoveToDead"/>.</summary>
    public void ScheduleRetry(string error, DateTimeOffset retryAtUtc)
    {
        if (Status != BackgroundJobStatus.Running)
        {
            throw new InvalidOperationException($"Cannot retry a job in state {Status}.");
        }

        AttemptCount++;
        Status = BackgroundJobStatus.Queued;
        ScheduledAtUtc = retryAtUtc;
        LastError = Truncate(error);
    }

    /// <summary>Permanent failure or retries exhausted: dead state for administrative resolution (#35).</summary>
    public void MoveToDead(string error)
    {
        if (Status != BackgroundJobStatus.Running)
        {
            throw new InvalidOperationException($"Cannot fail a job in state {Status}.");
        }

        // A job can also land here without ever being retried (permanent failure),
        // so a dead job may have fewer attempts than MaxAttempts.
        AttemptCount++;
        Status = BackgroundJobStatus.Dead;
        CompletedAtUtc = DateTimeOffset.UtcNow;
        LastError = Truncate(error);
    }

    private static string Truncate(string error)
        => error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}

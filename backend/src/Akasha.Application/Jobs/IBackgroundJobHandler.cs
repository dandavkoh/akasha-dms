namespace Akasha.Application.Jobs;

/// <summary>
/// Execution context of a claimed job. Tenant and user scopes are already established
/// by the worker before the handler runs — handlers must not widen them.
/// </summary>
public sealed record BackgroundJobContext(
    Guid JobId,
    Guid TenantId,
    Guid OperationId,
    string CorrelationId,
    string Payload,
    /// <summary>1-based attempt number.</summary>
    int Attempt);

/// <summary>
/// Handler for one job <see cref="Type"/>. Implementations are resolved from the
/// job's scope (scoped DbContext sees only the job's tenant). Any exception means a
/// transient failure and is retried with backoff; throw <see cref="PermanentJobFailureException"/>
/// to move the job straight to the dead state without retry.
/// </summary>
public interface IBackgroundJobHandler
{
    string Type { get; }

    Task ExecuteAsync(BackgroundJobContext context, CancellationToken cancellationToken);
}

/// <summary>A failure that retry can never fix (invalid input, missing configuration,
/// permanent denial — rule #35). The job moves directly to the dead state.</summary>
public sealed class PermanentJobFailureException : Exception
{
    public PermanentJobFailureException(string message)
        : base(message)
    {
    }
}

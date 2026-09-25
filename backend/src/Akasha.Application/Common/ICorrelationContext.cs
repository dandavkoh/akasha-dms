namespace Akasha.Application.Common;

/// <summary>
/// Ambient correlation id for the current scope (HTTP request or background job).
/// Established at the boundary (middleware / job worker) and captured into audit
/// records and enqueued background jobs so a logical operation stays traceable
/// across async boundaries (#29, #90 repo rules).
/// </summary>
public interface ICorrelationContext
{
    string? CorrelationId { get; }

    void Establish(string correlationId);
}

/// <summary>Default ambient correlation context (scoped per request/job).</summary>
public sealed class CorrelationContext : ICorrelationContext
{
    private string? _correlationId;

    public string? CorrelationId => _correlationId;

    public void Establish(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        }

        _correlationId ??= correlationId;
    }
}

namespace Akasha.Application.Common;

/// <summary>The requested resource does not exist (in the caller's tenant).</summary>
public sealed class NotFoundException(string resourceType, Guid resourceId)
    : Exception($"{resourceType} '{resourceId}' was not found.")
{
    public string ResourceType { get; } = resourceType;

    public Guid ResourceId { get; } = resourceId;
}

/// <summary>Optimistic concurrency conflict or uniqueness violation; nothing was overwritten.</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>Invalid request input. Never trusted from the client without server-side validation.</summary>
public sealed class ValidationException(string message) : Exception(message);

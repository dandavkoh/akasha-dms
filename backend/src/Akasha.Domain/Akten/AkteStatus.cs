namespace Akasha.Domain.Akten;

/// <summary>Lifecycle of an Akte. Archived Akten are read-only business-wise.</summary>
public enum AkteStatus
{
    Active = 0,
    Archived = 1,
}

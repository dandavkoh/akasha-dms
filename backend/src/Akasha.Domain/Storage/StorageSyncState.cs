namespace Akasha.Domain.Storage;

/// <summary>
/// Synchronization/availability relationship between the logical document and
/// the physical storage object. Processing status is a separate concern and
/// must not be encoded here.
/// </summary>
public enum StorageSyncState
{
    /// <summary>Initial state before the first successful sync.</summary>
    Pending = 0,
    /// <summary>Physical object exists and matches the known state.</summary>
    Available = 1,
    /// <summary>Physical object was externally deleted or cannot be found.</summary>
    Missing = 2,
    /// <summary>Object or connector temporarily unreachable; known state preserved.</summary>
    Unavailable = 3,
    /// <summary>Synchronization failed; needs retry or administrative attention.</summary>
    SyncError = 4,
    /// <summary>The whole connector is unreachable; documents keep their known state (distinct from a missing object).</summary>
    ConnectorUnavailable = 6,
    /// <summary>Connector credentials expired; not a document problem.</summary>
    AuthenticationRequired = 5,
}

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>
/// What a receiver answered for a hint, either when it was offered or when it finished with it.
/// </summary>
public enum HintCode
{
    /// <summary>The receiver stored the hint and applies it in turn.</summary>
    Queued,

    /// <summary>The item is not on the receiver yet, so the hint waits until it arrives.</summary>
    NoLocalItem,

    /// <summary>The receiver already holds that version.</summary>
    AlreadyCurrent,

    /// <summary>The sync type is off on the receiver.</summary>
    ModuleOff,

    /// <summary>The path or user is not mapped on the receiver.</summary>
    NotMapped,

    /// <summary>The library mapping's Library Filter leaves the path out.</summary>
    Filtered,

    /// <summary>The key or origin is malformed.</summary>
    Invalid,

    /// <summary>The version is dated further ahead than the receiver's clock allows.</summary>
    FutureVersion,

    /// <summary>The receiver does not apply that kind of hint live.</summary>
    Unsupported,

    /// <summary>The receiver applied the change.</summary>
    Applied,

    /// <summary>Applying the change left the receiver as it was.</summary>
    Unchanged,

    /// <summary>The receiver holds a newer edit.</summary>
    Superseded,

    /// <summary>The item is locked on the receiver.</summary>
    Locked,

    /// <summary>The change waits for approval on the receiver.</summary>
    AwaitingApproval,

    /// <summary>The receiver is set to leave the item alone.</summary>
    Ignored,

    /// <summary>The file is no longer on the origin.</summary>
    SourceGone,

    /// <summary>The origin did not say where the file is.</summary>
    Unreadable,

    /// <summary>Another server provides the file.</summary>
    Priority,

    /// <summary>The receiver stopped trying after repeated failures.</summary>
    GaveUp
}

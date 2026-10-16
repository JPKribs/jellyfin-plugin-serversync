namespace Jellyfin.Plugin.ServerSync.Models.Queue;

public enum HintCode
{
    Queued,
    NoLocalItem,
    AlreadyCurrent,
    ModuleOff,
    NotMapped,
    Filtered,
    Invalid,
    FutureVersion,
    Unsupported,
    Applied,
    Unchanged,
    Superseded,
    Locked,
    AwaitingApproval,
    Ignored,
    SourceGone,
    Unreadable,
    Priority,
    GaveUp
}

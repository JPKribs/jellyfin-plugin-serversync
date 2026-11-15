namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>How an inbound hint ended.</summary>
public enum HintApplyOutcome
{
    /// <summary>The local object was written.</summary>
    Applied,

    /// <summary>The local object already matched, or this server keeps its own value. Nothing was written.</summary>
    Unchanged,

    /// <summary>The hint cannot apply here, for example an unmapped path. It is done and the origin is told.</summary>
    Dropped,

    /// <summary>Something failed that may pass later. The row stays and is tried again.</summary>
    Retry,

    /// <summary>No local item matches yet. The hint waits under a park key until one appears.</summary>
    Parked
}

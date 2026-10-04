namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>How an inbound hint ended.</summary>
public enum HintApplyOutcome
{
    /// <summary>The local object was written.</summary>
    Applied,

    /// <summary>The local object already matched. Nothing was written.</summary>
    Unchanged,

    /// <summary>The hint cannot apply here, for example an unmapped path. It is done and the origin is told.</summary>
    Dropped,

    /// <summary>Something failed that may pass later. The row stays and is tried again.</summary>
    Retry
}

/// <summary>The outcome of one inbound apply, with the reason when it did not simply apply.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Why, for everything but a plain apply.</param>
public readonly record struct HintApplyResult(HintApplyOutcome Outcome, string? Reason)
{
    /// <summary>A plain apply.</summary>
    public static HintApplyResult Applied => new(HintApplyOutcome.Applied, null);

    /// <summary>Nothing to write.</summary>
    public static HintApplyResult Unchanged => new(HintApplyOutcome.Unchanged, null);

    /// <summary>Builds a dropped result.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The result.</returns>
    public static HintApplyResult Dropped(string reason) => new(HintApplyOutcome.Dropped, reason);

    /// <summary>Builds a retry result.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>The result.</returns>
    public static HintApplyResult RetryLater(string reason) => new(HintApplyOutcome.Retry, reason);
}

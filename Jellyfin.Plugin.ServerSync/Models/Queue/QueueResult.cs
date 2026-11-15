using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>What the receiver did with one offered hint.</summary>
public class QueueResult
{
    /// <summary>Gets or sets the hint id, echoed.</summary>
    public string HintId { get; set; } = string.Empty;

    /// <summary>Gets or sets what the receiver did.</summary>
    public HintCode Code { get; set; }

    /// <summary>Gets or sets the reason, when the code needs one.</summary>
    public string? Reason { get; set; }
}

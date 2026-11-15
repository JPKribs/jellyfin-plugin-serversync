using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>One hint a receiver finished with.</summary>
public class CompletedHint
{
    /// <summary>Gets or sets the hint id.</summary>
    public string HintId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the version the receiver applied. The origin only removes its row when the row still
    /// carries this version, so an edit made after delivery is not lost to a late completion.
    /// </summary>
    public DateTime VersionTimestamp { get; set; }

    /// <summary>Gets or sets how the hint ended.</summary>
    public HintCode Code { get; set; } = HintCode.Applied;

    /// <summary>Gets or sets the reason, when the code needs one.</summary>
    public string? Reason { get; set; }
}

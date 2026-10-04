using System;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>
/// Where and when the value an object holds was last edited by a person. A local edit sets the
/// version to this server and the time of the edit. A copy that came from a hint keeps the version
/// the hint carried, so a copy can never look newer than the edit it came from. Saved dates from
/// Jellyfin are never used for this.
/// </summary>
public class ObjectVersion
{
    /// <summary>Gets or sets the kind of object.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets this server's key for the object.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin server id of the server where the edit was made.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets when the edit was made, in UTC.</summary>
    public DateTime Timestamp { get; set; }
}

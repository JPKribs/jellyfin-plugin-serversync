using System;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>
/// The Stamp, the server and time of the edit a person made that a value came from.
/// A copy that came from a hint keeps the hint's stamp, so it never looks newer than the edit, and Jellyfin's saved dates are never used.
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

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>Delivery state of one peer as shown to the operator.</summary>
public class PeerHintStateDto
{
    /// <summary>Gets or sets the entry key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets when delivery was last tried.</summary>
    public DateTime? LastAttempt { get; set; }

    /// <summary>Gets or sets until when the peer is paused, or null.</summary>
    public DateTime? PausedUntil { get; set; }

    /// <summary>Gets or sets why the last delivery did not go through.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the kinds this server sends to the peer, which are the modules selected there, or
    /// null when the peer has not said yet, in which case everything is sent.
    /// </summary>
    public List<string>? Sends { get; set; }
}

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>What the dashboard shows about delivery to one peer.</summary>
public sealed class PeerDeliveryState
{
    /// <summary>Gets or sets when the last delivery was tried, in UTC.</summary>
    public DateTime? LastAttempt { get; set; }

    /// <summary>Gets or sets until when the peer is left alone, in UTC, or null when it is not paused.</summary>
    public DateTime? PausedUntil { get; set; }

    /// <summary>Gets or sets why the last delivery did not go through, or null after a success.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the settings of the peer's entry when it was paused, or null when not paused over a delivery.</summary>
    public string? PausedFor { get; set; }

    /// <summary>Gets or sets what the peer last said it wants, or null when not known.</summary>
    public PeerInterest? Interest { get; set; }

    /// <summary>Gets or sets a value indicating whether the peer runs a protocol this server cannot talk to.</summary>
    public bool VersionBlocked { get; set; }

    /// <summary>Gets the kinds the peer applies, or null when not known.</summary>
    public HashSet<HintKind>? Accepts => Interest is null ? null : new HashSet<HintKind>(Interest.Kinds);

    /// <summary>Gets or sets when the peer's interest was last asked for, in UTC.</summary>
    public DateTime InterestReadAt { get; set; }
}

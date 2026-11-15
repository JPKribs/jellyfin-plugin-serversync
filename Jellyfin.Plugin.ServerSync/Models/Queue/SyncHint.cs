using System;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>
/// One hint, sent from the server where a change happened to a peer, saying what changed and never the value.
/// Paths, names, and IDs are the origin's own, so the receiver maps them through its mappings and never uses the IDs as keys.
/// </summary>
public class SyncHint
{
    /// <summary>Gets or sets the id of this hint, which is the origin's server id and a sequence number.</summary>
    public string HintId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin server id of the server where the change happened.</summary>
    public string OriginServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets what kind of object changed.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets the origin's key for the object. For history this is the origin's user id and item id.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's path on the origin, when the kind concerns an item.</summary>
    public string? ItemPath { get; set; }

    /// <summary>Gets or sets the origin's item id, when the kind concerns an item.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the item's Jellyfin type, such as Movie or Episode, so a queue view can show it in the right shape.</summary>
    public string? ItemType { get; set; }

    /// <summary>Gets or sets the origin's user id, when the kind concerns a user.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the username on the origin, when the kind concerns a user.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets the server id of the edit this hint carries. See <see cref="ObjectVersion"/>.</summary>
    public string VersionServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the time of the edit this hint carries, in UTC.</summary>
    public DateTime VersionTimestamp { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a person made the change, which is <c>true</c> when a peer leaves it out.
    /// A provider's change is marked <c>false</c>, so it only fills in where the receiver recorded no edit of its own.
    /// </summary>
    public bool Recorded { get; set; } = true;
}

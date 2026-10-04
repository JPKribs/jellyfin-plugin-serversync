using System;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>
/// One change notice sent from the server where a change happened to a peer that pulls it. A hint says
/// what changed and where. It never carries the value, the receiver reads the live state from the
/// origin. Paths and names are the origin's own and the receiver maps them through its mappings for
/// that origin. The origin's ids are included so the receiver can fetch the object, but they are never
/// used as keys on the receiver.
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
    /// Gets or sets a value indicating whether the change was made by hand. A provider's work, such as
    /// a poster fetched during a scan, is announced too but marked false: it fills in where the
    /// receiver has recorded no edit of its own and never replaces one. Absent from older peers, which
    /// is read as true.
    /// </summary>
    public bool Recorded { get; set; } = true;
}

using System;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>Where an outbound row is in its life.</summary>
public enum OutboundState
{
    /// <summary>Waiting to be delivered, or waiting for its next attempt.</summary>
    Pending = 0,

    /// <summary>The peer answered 200 and holds the hint. The row waits for the peer to report it done.</summary>
    Sent = 1,

    /// <summary>The peer rejected the hint as malformed. Nothing will retry it. Shown to the operator.</summary>
    Failed = 2
}

/// <summary>
/// One hint waiting to reach one peer. Rows coalesce on peer, kind, and key, so several edits before
/// delivery are one row carrying the newest version. The row is deleted when the peer reports the
/// work complete.
/// </summary>
public class OutboundHint
{
    /// <summary>Gets or sets the row id.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the hint id sent on the wire.</summary>
    public string HintId { get; set; } = string.Empty;

    /// <summary>Gets or sets the configured entry key of the peer this row goes to.</summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of object.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets this server's key for the object, which is what the peer receives.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's local path, when the kind concerns an item.</summary>
    public string? ItemPath { get; set; }

    /// <summary>Gets or sets the local item id, when the kind concerns an item.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the local user id, when the kind concerns a user.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the local username, when the kind concerns a user.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets the server id of the edit the row carries.</summary>
    public string VersionServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the time of the edit the row carries, in UTC.</summary>
    public DateTime VersionTimestamp { get; set; }

    /// <summary>Gets or sets the delivery state.</summary>
    public OutboundState State { get; set; }

    /// <summary>Gets or sets how many deliveries have been tried since the row was last pending.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when the next delivery may be tried, in UTC.</summary>
    public DateTime NextAttempt { get; set; }

    /// <summary>Gets or sets when the peer accepted the hint, in UTC. Null until sent.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>Gets or sets what went wrong on the last attempt.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets when the row was created, in UTC.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Builds the wire shape of this row.</summary>
    /// <param name="originServerId">This server's id.</param>
    /// <returns>The hint.</returns>
    public SyncHint ToHint(string originServerId) => new()
    {
        HintId = HintId,
        OriginServerId = originServerId,
        Kind = Kind,
        Key = Key,
        ItemPath = ItemPath,
        ItemId = ItemId,
        UserId = UserId,
        UserName = UserName,
        VersionServerId = VersionServerId,
        VersionTimestamp = VersionTimestamp
    };
}

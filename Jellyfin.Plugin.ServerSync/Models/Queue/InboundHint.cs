using System;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>
/// One hint received from a peer and not yet applied. The row stays until the work is done, then it
/// is removed and the origin is told. Rows coalesce on origin, kind, and key, so a second notice for
/// the same object before the first is applied just refreshes the version.
/// </summary>
public class InboundHint
{
    /// <summary>Gets or sets the row id, which is what the operator removes a poisoned row by.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the hint id the origin sent.</summary>
    public string HintId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin server id of the origin.</summary>
    public string OriginServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of object.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets the origin's key for the object.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's path on the origin.</summary>
    public string? ItemPath { get; set; }

    /// <summary>Gets or sets the origin's item id.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the origin's user id.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the username on the origin.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets the server id of the edit the hint carries.</summary>
    public string VersionServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the time of the edit the hint carries, in UTC.</summary>
    public DateTime VersionTimestamp { get; set; }

    /// <summary>Gets or sets when the hint arrived, in UTC.</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>Gets or sets how many applies have been tried.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when the next apply may be tried, in UTC.</summary>
    public DateTime NextAttempt { get; set; }

    /// <summary>Gets or sets what went wrong on the last attempt.</summary>
    public string? LastError { get; set; }

    /// <summary>Builds a row from a received hint.</summary>
    /// <param name="hint">The hint.</param>
    /// <param name="utcNow">The time of receipt.</param>
    /// <returns>The row, not yet stored.</returns>
    public static InboundHint FromHint(SyncHint hint, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(hint);
        return new InboundHint
        {
            HintId = hint.HintId,
            OriginServerId = hint.OriginServerId,
            Kind = hint.Kind,
            Key = hint.Key,
            ItemPath = hint.ItemPath,
            ItemId = hint.ItemId,
            UserId = hint.UserId,
            UserName = hint.UserName,
            VersionServerId = hint.VersionServerId,
            VersionTimestamp = hint.VersionTimestamp,
            ReceivedAt = utcNow,
            NextAttempt = utcNow
        };
    }
}

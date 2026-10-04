using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

/// <summary>A batch of hints offered to a peer.</summary>
public class QueueRequest
{
    /// <summary>Gets or sets the sending server's id. Every hint in the batch originates there.</summary>
    public string SenderServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the hints.</summary>
    public List<SyncHint> Items { get; set; } = new();
}

/// <summary>What the receiver did with one offered hint.</summary>
public class QueueResult
{
    /// <summary>Gets or sets the hint id, echoed.</summary>
    public string HintId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the hint was stored. A hint for something the receiver
    /// cannot map is reported as not accepted with a reason, and the sender treats that as done, since
    /// the sender has no knowledge of the receiver's mappings.
    /// </summary>
    public bool Accepted { get; set; }

    /// <summary>Gets or sets why the hint was not accepted.</summary>
    public string? Reason { get; set; }
}

/// <summary>The receiver's answers for a batch of hints.</summary>
public class QueueResponse
{
    /// <summary>Gets or sets one result per hint, in request order.</summary>
    public List<QueueResult> Items { get; set; } = new();
}

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
}

/// <summary>A receiver telling an origin that hints are done.</summary>
public class CompleteRequest
{
    /// <summary>Gets or sets the reporting server's id.</summary>
    public string SenderServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the finished hints.</summary>
    public List<CompletedHint> Items { get; set; } = new();
}

/// <summary>One inbound row as shown to a peer or the operator.</summary>
public class InboundHintDto
{
    /// <summary>Gets or sets the row id.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the hint id.</summary>
    public string HintId { get; set; } = string.Empty;

    /// <summary>Gets or sets the origin's server id.</summary>
    public string OriginServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets the origin's key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's path on the origin.</summary>
    public string? ItemPath { get; set; }

    /// <summary>Gets or sets the origin's item id, for a poster through the image proxy.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the item's Jellyfin type.</summary>
    public string? ItemType { get; set; }

    /// <summary>Gets or sets the username on the origin.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets when the hint arrived, in UTC.</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>Gets or sets how many applies have been tried.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the last error.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets a value indicating whether the change was made by hand rather than by a provider.</summary>
    public bool Recorded { get; set; } = true;

    /// <summary>Builds the DTO for a row.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The DTO.</returns>
    public static InboundHintDto From(InboundHint row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new InboundHintDto
        {
            Id = row.Id,
            HintId = row.HintId,
            OriginServerId = row.OriginServerId,
            Kind = row.Kind,
            Key = row.Key,
            ItemPath = row.ItemPath,
            ItemId = row.ItemId,
            ItemType = row.ItemType,
            UserName = row.UserName,
            ReceivedAt = row.ReceivedAt,
            Attempts = row.Attempts,
            LastError = row.LastError,
            Recorded = row.Recorded
        };
    }
}

/// <summary>This server's inbound queue, as a peer sees it when checking on work it sent.</summary>
public class QueueStatusResponse
{
    /// <summary>Gets or sets this server's id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the inbound rows.</summary>
    public List<InboundHintDto> Inbound { get; set; } = new();

    /// <summary>
    /// Gets or sets the ids of hints finished recently. An origin whose completion reports this server
    /// could not deliver, because it holds a standard user's key for the origin, reads them from here
    /// with its own key and removes its rows.
    /// </summary>
    public List<string> Completed { get; set; } = new();

    /// <summary>
    /// Gets or sets the hints finished recently with the version each one applied. An origin only treats
    /// a hint as done when the finished version is at least the one its row now carries, so a hint
    /// re-sent with a newer edit is never closed by an older completion.
    /// </summary>
    public List<CompletedHint> CompletedHints { get; set; } = new();
}

/// <summary>Asks a peer for the versions it holds for a batch of its own keys.</summary>
public class VersionsRequest
{
    /// <summary>Gets or sets the kind.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets the peer's keys.</summary>
    public List<string> Keys { get; set; } = new();
}

/// <summary>The versions a peer holds. Keys with no version are left out.</summary>
public class VersionsResponse
{
    /// <summary>Gets or sets the versions.</summary>
    public List<ObjectVersion> Items { get; set; } = new();
}

using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>One outbound row as shown to the operator.</summary>
public class OutboundHintDto
{
    /// <summary>Gets or sets the row id.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets Jellyfin's title for the item, or the person or user.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the second line: the episode, the year, or null.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets the item's Jellyfin type, so the view can pick the poster's shape.</summary>
    public string? ItemType { get; set; }

    /// <summary>Gets or sets the local item id, for its poster.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the local user id, for the avatar on a watch history change.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the hint id.</summary>
    public string HintId { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's entry key.</summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the peer's display name.</summary>
    public string PeerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets this server's key for the object.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's local path.</summary>
    public string? ItemPath { get; set; }

    /// <summary>Gets or sets the local username.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets the state.</summary>
    public OutboundState State { get; set; }

    /// <summary>Gets or sets how many deliveries have been tried.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when the next delivery may be tried.</summary>
    public DateTime NextAttempt { get; set; }

    /// <summary>Gets or sets when the peer accepted the hint.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>Gets or sets the last error.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets when the row was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets a value indicating whether the change was made by hand rather than by a provider.</summary>
    public bool Recorded { get; set; } = true;
}

using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>One change still gathering, as shown to the operator.</summary>
public class GatheringDto
{
    /// <summary>Gets or sets the sync type the change belongs to.</summary>
    public HintKind Kind { get; set; }

    /// <summary>Gets or sets what changed, named.</summary>
    public string Change { get; set; } = string.Empty;

    /// <summary>Gets or sets Jellyfin's title for the item, or the person or user.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the second line, such as the episode or the year, or null.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Gets or sets the item's Jellyfin type, so the view can pick the poster's shape.</summary>
    public string? ItemType { get; set; }

    /// <summary>Gets or sets the local item id, for its poster.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the local user id, for the avatar on a History change.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the local username.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets when it was last edited, in UTC.</summary>
    public DateTime EditedAt { get; set; }

    /// <summary>Gets or sets when it is sent unless edited again, in UTC.</summary>
    public DateTime DueAt { get; set; }

    /// <summary>Gets or sets a value indicating whether a hand made edit is among the gathered changes.</summary>
    public bool Recorded { get; set; } = true;
}

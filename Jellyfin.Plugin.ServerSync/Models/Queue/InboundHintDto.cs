using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Queue;

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

    /// <summary>Gets or sets a value indicating whether the hint is parked rather than waiting to apply.</summary>
    public bool Parked { get; set; }

    /// <summary>Builds the DTO for a row.</summary>
    /// <param name="row">The stored inbound row.</param>
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
            Recorded = row.Recorded,
            Parked = row.ParkedPath is not null
        };
    }
}

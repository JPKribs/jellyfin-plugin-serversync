using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Queue;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>Both queues and every peer's delivery state.</summary>
public class HintsOverview
{
    /// <summary>Gets or sets how many local changes are still gathering before they become hints.</summary>
    public int Pending { get; set; }

    /// <summary>Gets or sets how many local changes since start matched no mapping on any Push or Sync server.</summary>
    public int Unmatched { get; set; }

    /// <summary>Gets or sets the last such change, described.</summary>
    public string? LastUnmatched { get; set; }

    /// <summary>Gets or sets the changes still gathering before they become hints, newest edit first.</summary>
    public List<GatheringDto> Gathering { get; set; } = new();

    /// <summary>Gets or sets the newest outbound rows, at most a few hundred.</summary>
    public List<OutboundHintDto> Outbound { get; set; } = new();

    /// <summary>Gets or sets how many outbound rows exist per state, by state name.</summary>
    public Dictionary<string, int> OutboundCounts { get; set; } = new();

    /// <summary>Gets or sets the newest inbound rows, at most a few hundred.</summary>
    public List<InboundHintDto> Inbound { get; set; } = new();

    /// <summary>Gets or sets how many inbound rows exist.</summary>
    public int InboundCount { get; set; }

    /// <summary>Gets or sets how many inbound rows have failed to apply at least once and wait to be tried again.</summary>
    public int InboundFailingCount { get; set; }

    /// <summary>Gets or sets the delivery state of every server this one sends to.</summary>
    public List<PeerHintStateDto> Peers { get; set; } = new();
}

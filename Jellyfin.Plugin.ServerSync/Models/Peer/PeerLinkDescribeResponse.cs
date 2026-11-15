using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>The source's answer to a <see cref="PeerLinkDescribeRequest"/>.</summary>
public class PeerLinkDescribeResponse
{
    /// <summary>Gets or sets one answer per item asked about.</summary>
    public List<PeerLinkDescription> Items { get; set; } = new();
}

using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// Asks the source about files a server holds links to: whether each is still there, and, when
/// <see cref="Details"/> is set, its media info, so a link shows its runtime, codecs, and tracks
/// without being probed.
/// </summary>
public class PeerLinkDescribeRequest
{
    /// <summary>The most items one request may name.</summary>
    public const int MaxItems = 200;

    /// <summary>Gets or sets the asking server's Jellyfin id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the source's ids of the items.</summary>
    public List<string> ItemIds { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether each answer carries the file's media info.</summary>
    public bool Details { get; set; }
}

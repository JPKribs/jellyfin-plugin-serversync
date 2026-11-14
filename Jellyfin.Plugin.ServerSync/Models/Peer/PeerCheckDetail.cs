namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// One line of a link check: a single thing that was learned about the other server, with a badge.
/// </summary>
public class PeerCheckDetail
{
    /// <summary>Gets or sets what the line is about.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a sentence of detail under the title.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the badge's words.</summary>
    public string Badge { get; set; } = string.Empty;

    /// <summary>Gets or sets how the badge is coloured: ok, limited, no, or none.</summary>
    public string State { get; set; } = "ok";
}

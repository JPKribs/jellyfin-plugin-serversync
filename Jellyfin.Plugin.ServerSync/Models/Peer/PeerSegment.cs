namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// One media segment a peer holds for a file, such as its intro or credits.
/// </summary>
public class PeerSegment
{
    /// <summary>Gets or sets the segment's Jellyfin type by name, such as Intro or Outro.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets where the segment starts, in ticks from the start of the file.</summary>
    public long StartTicks { get; set; }

    /// <summary>Gets or sets where the segment ends, in ticks from the start of the file.</summary>
    public long EndTicks { get; set; }
}

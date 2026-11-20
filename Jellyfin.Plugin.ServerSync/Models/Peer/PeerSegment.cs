using System.Text.Json.Serialization;

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

    /// <summary>
    /// Gets or sets the Jellyfin id of the server the segment was first made on, or null when it was made on the server that holds it.
    /// A server never takes back a segment it made itself, so a change made on one server flows out without coming back to undo it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginServerId { get; set; }
}

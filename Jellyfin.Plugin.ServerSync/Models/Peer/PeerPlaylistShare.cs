namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>
/// One user a playlist is shared with.
/// </summary>
public class PeerPlaylistShare
{
    /// <summary>Gets or sets the id of the user the playlist is shared with.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether that user may change the playlist.</summary>
    public bool CanEdit { get; set; }
}

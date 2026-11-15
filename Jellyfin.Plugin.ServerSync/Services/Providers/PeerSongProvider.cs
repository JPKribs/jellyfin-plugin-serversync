using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of songs from the server this one pulls from.</summary>
public sealed class PeerSongProvider : PeerMetadataProvider<Audio, SongInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSongProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerSongProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

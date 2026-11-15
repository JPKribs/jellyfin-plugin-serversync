using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of music videos from the server this one pulls from.</summary>
public sealed class PeerMusicVideoProvider : PeerMetadataProvider<MusicVideo, MusicVideoInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerMusicVideoProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerMusicVideoProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

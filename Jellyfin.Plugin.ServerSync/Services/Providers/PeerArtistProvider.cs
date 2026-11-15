using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of music artists from the server this one pulls from.</summary>
public sealed class PeerArtistProvider : PeerMetadataProvider<MusicArtist, ArtistInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerArtistProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerArtistProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

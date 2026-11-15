using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of music albums from the server this one pulls from.</summary>
public sealed class PeerAlbumProvider : PeerMetadataProvider<MusicAlbum, AlbumInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAlbumProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerAlbumProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

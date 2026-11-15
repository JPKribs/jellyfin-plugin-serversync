using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of photo albums from the server this one pulls from.</summary>
public sealed class PeerPhotoAlbumProvider : PeerMetadataProvider<PhotoAlbum, ItemLookupInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPhotoAlbumProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerPhotoAlbumProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

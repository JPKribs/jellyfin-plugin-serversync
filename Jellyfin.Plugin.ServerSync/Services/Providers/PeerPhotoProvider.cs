using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of photos from the server this one pulls from.</summary>
public sealed class PeerPhotoProvider : PeerMetadataProvider<Photo, ItemLookupInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPhotoProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerPhotoProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of videos from the server this one pulls from.</summary>
public sealed class PeerVideoProvider : PeerMetadataProvider<Video, ItemLookupInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerVideoProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerVideoProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

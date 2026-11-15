using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of trailers from the server this one pulls from.</summary>
public sealed class PeerTrailerProvider : PeerMetadataProvider<Trailer, TrailerInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerTrailerProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerTrailerProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

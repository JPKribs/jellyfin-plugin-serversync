using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of series from the server this one pulls from.</summary>
public sealed class PeerSeriesProvider : PeerMetadataProvider<Series, SeriesInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSeriesProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerSeriesProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

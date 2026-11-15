using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of seasons from the server this one pulls from.</summary>
public sealed class PeerSeasonProvider : PeerMetadataProvider<Season, SeasonInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerSeasonProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerSeasonProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

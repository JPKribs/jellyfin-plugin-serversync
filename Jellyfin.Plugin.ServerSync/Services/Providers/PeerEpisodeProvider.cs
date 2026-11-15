using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of episodes from the server this one pulls from.</summary>
public sealed class PeerEpisodeProvider : PeerMetadataProvider<Episode, EpisodeInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerEpisodeProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerEpisodeProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of movies from the server this one pulls from.</summary>
public sealed class PeerMovieProvider : PeerMetadataProvider<Movie, MovieInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerMovieProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerMovieProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

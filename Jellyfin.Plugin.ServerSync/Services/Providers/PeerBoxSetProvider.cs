using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of collections from the server this one pulls from.</summary>
public sealed class PeerBoxSetProvider : PeerMetadataProvider<BoxSet, BoxSetInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerBoxSetProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerBoxSetProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

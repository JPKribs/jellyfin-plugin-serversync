using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of people from the server this one pulls from.</summary>
public sealed class PeerPersonProvider : PeerMetadataProvider<Person, PersonLookupInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerPersonProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerPersonProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

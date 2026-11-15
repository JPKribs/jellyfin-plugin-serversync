using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of books from the server this one pulls from.</summary>
public sealed class PeerBookProvider : PeerMetadataProvider<Book, BookInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerBookProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerBookProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Fills the metadata of audio books from the server this one pulls from.</summary>
public sealed class PeerAudioBookProvider : PeerMetadataProvider<AudioBook, SongInfo>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAudioBookProvider"/> class.
    /// </summary>
    /// <param name="source">Finds items and reads them from the other server.</param>
    public PeerAudioBookProvider(PeerMetadataSource source)
        : base(source)
    {
    }
}

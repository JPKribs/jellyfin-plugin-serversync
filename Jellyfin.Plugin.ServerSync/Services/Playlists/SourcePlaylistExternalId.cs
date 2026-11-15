using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

/// <summary>Shows which source playlist a copy was made from, as an external id in Jellyfin.</summary>
public sealed class SourcePlaylistExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Server Sync Playlist";

    /// <inheritdoc />
    public string Key => PlaylistMirror.SourcePlaylistProviderKey;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => null;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Playlist;
}

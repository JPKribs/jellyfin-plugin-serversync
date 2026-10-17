using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

public sealed class SourcePlaylistExternalId : IExternalId
{
    public string ProviderName => "Server Sync Playlist";

    public string Key => PlaylistMirror.SourcePlaylistProviderKey;

    public ExternalIdMediaType? Type => null;

    public bool Supports(IHasProviderIds item) => item is Playlist;
}

public sealed class SourceServerExternalId : IExternalId
{
    public string ProviderName => "Server Sync Server";

    public string Key => PlaylistMirror.SourceServerProviderKey;

    public ExternalIdMediaType? Type => null;

    public bool Supports(IHasProviderIds item) => item is Playlist;
}

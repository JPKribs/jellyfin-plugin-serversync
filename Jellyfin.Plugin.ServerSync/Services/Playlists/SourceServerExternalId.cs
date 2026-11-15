using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

/// <summary>Shows which server a playlist copy came from, as an external id in Jellyfin.</summary>
public sealed class SourceServerExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Server Sync Server";

    /// <inheritdoc />
    public string Key => PlaylistMirror.SourceServerProviderKey;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => null;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Playlist;
}

using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ServerSync.Services.Providers;

/// <summary>Shows which server and item an item was pinned to, as an external id in Jellyfin.</summary>
public sealed class PeerItemExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Server Sync Item";

    /// <inheritdoc />
    public string Key => PeerMetadataSource.PinnedProviderKey;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => null;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is not MediaBrowser.Controller.Playlists.Playlist;
}

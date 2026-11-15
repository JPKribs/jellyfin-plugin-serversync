using System;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Playlists;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Providers;

/// <summary>
/// The external id must show an item's pin under the key the metadata source writes, for every item but a playlist.
/// </summary>
public sealed class PeerItemExternalIdTests
{
    /// <summary>
    /// The id shows as "Server Sync Item" under the pin's provider key, for any media type.
    /// True: the pin a search result wrote appears in the item's external ids and can be edited or cleared.
    /// False: the pin would be stored under a key the editor never shows, and could not be removed.
    /// </summary>
    [Fact]
    public void Identity_IsThePinKeyUnderItsOwnName()
    {
        var externalId = new PeerItemExternalId();

        Assert.Equal("Server Sync Item", externalId.ProviderName);
        Assert.Equal(PeerMetadataSource.PinnedProviderKey, externalId.Key);
        Assert.Equal("ServerSyncPeerItem", externalId.Key);
        Assert.Null(externalId.Type);
    }

    /// <summary>
    /// Every kind of item but a playlist can show the pin.
    /// True: films, shows, and people show which server they are matched to.
    /// False: a playlist, which is mirrored by its own ids, would show a pin that means nothing for it.
    /// </summary>
    [Theory]
    [InlineData(typeof(Movie), true)]
    [InlineData(typeof(Series), true)]
    [InlineData(typeof(Person), true)]
    [InlineData(typeof(Folder), true)]
    [InlineData(typeof(Playlist), false)]
    public void Supports_Item_IsEverythingButAPlaylist(Type kind, bool expected)
    {
        var externalId = new PeerItemExternalId();

        Assert.Equal(expected, externalId.Supports((BaseItem)Activator.CreateInstance(kind)!));
    }
}

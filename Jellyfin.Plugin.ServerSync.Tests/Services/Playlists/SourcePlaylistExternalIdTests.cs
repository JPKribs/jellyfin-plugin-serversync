using Jellyfin.Plugin.ServerSync.Services.Playlists;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Playlists;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Playlists;

/// <summary>
/// The external id that shows which source playlist a copy was made from must read the key the mirror writes, and apply to playlists only.
/// </summary>
public sealed class SourcePlaylistExternalIdTests
{
    /// <summary>
    /// The id reads the provider key the playlist mirror tags a copy with, under its own name and no media type.
    /// True: Jellyfin shows the value the mirror wrote.
    /// False: the id would read a key nothing writes and always show empty.
    /// </summary>
    [Fact]
    public void Key_Always_IsTheMirrorsProviderKey()
    {
        var id = new SourcePlaylistExternalId();

        var key = id.Key;

        Assert.Equal(PlaylistMirror.SourcePlaylistProviderKey, key);
        Assert.Equal("Server Sync Playlist", id.ProviderName);
        Assert.Null(id.Type);
    }

    /// <summary>
    /// The id applies to a playlist and to nothing else.
    /// True: only playlists show the field.
    /// False: every film would show an empty Server Sync field.
    /// </summary>
    [Fact]
    public void Supports_OnlyPlaylists()
    {
        var id = new SourcePlaylistExternalId();

        var playlist = id.Supports(new Playlist());
        var movie = id.Supports(new Movie());

        Assert.True(playlist);
        Assert.False(movie);
    }
}

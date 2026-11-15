using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Playlists;

/// <summary>
/// A source playlist must count as the original exactly when it is not a copy.
/// </summary>
public sealed class SourcePlaylistTests
{
    /// <summary>
    /// A playlist is the original when it is not a copy, and not the original when it is.
    /// True: a copy of a copy is never taken for the playlist that owns the entries.
    /// False: two servers would each treat the other's copy as the original.
    /// </summary>
    /// <param name="isCopy">Whether the playlist is a copy.</param>
    /// <param name="expected">Whether it is the original.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IsOriginal_FollowsIsCopy(bool isCopy, bool expected)
    {
        var playlist = new SourcePlaylist("id", "root", "Name", "Video", "owner", false, new List<(string UserId, bool CanEdit)>(), new List<string?>(), true, isCopy);

        var original = playlist.IsOriginal;

        Assert.Equal(expected, original);
    }

    /// <summary>
    /// Two playlists built from the same values and lists are equal.
    /// True: a playlist read twice with nothing changed compares as unchanged.
    /// False: every read would look like a change.
    /// </summary>
    [Fact]
    public void Equals_SameValues_AreEqual()
    {
        var shares = new List<(string UserId, bool CanEdit)> { ("viewer", true) };
        var paths = new List<string?> { "/src/a.mkv", null };
        var first = new SourcePlaylist("id", "root", "Name", null, "owner", true, shares, paths, false, false);

        var second = first with { };

        Assert.Equal(first, second);
        Assert.NotEqual(first, first with { Name = "Other" });
    }
}

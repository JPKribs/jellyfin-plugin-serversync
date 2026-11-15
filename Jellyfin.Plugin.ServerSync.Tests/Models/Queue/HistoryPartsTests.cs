using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Queue;

/// <summary>
/// The parts of a history save, a play and a favorite, are sent, read back, and keyed on their own.
/// </summary>
public class HistoryPartsTests
{
    /// <summary>
    /// A history hint's item type carries the item's type and the parts that changed, and reads back
    /// the same. One with no parts named, as an older server sends, reads as not known.
    /// True: a receiver takes only the parts the sender changed, and still understands an older sender.
    /// False: a play would be taken as a change to the favorite too, undoing one the sender never heard of.
    /// </summary>
    [Theory]
    [InlineData("Movie", HistoryParts.None, "Movie")]
    [InlineData("Movie", HistoryParts.Played, "Movie+Played")]
    [InlineData("Episode", HistoryParts.Favorite, "Episode+Favorite")]
    [InlineData("Movie", HistoryParts.All, "Movie+Played+Favorite")]
    [InlineData(null, HistoryParts.Favorite, "+Favorite")]
    public void HistoryItemType_RoundTrips(string? type, HistoryParts parts, string sent)
    {
        Assert.Equal(sent, HintProtocol.HistoryItemType(type, parts));
        Assert.Equal(type, HintProtocol.SplitHistoryItemType(sent, out var read));
        Assert.Equal(parts, read);
    }

    /// <summary>
    /// A favorite and a play are saved for different reasons, and a save that could be either counts as both.
    /// True: each change is announced as the part it was.
    /// False: every change would be announced as both parts, and a play would again carry the sender's favorite.
    /// </summary>
    [Fact]
    public void PartsOf_SortsASaveByItsReason()
    {
        Assert.Equal(HistoryParts.Favorite, LocalChangeObserver.PartsOf(MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserRating));
        Assert.Equal(HistoryParts.Played, LocalChangeObserver.PartsOf(MediaBrowser.Model.Entities.UserDataSaveReason.TogglePlayed));
        Assert.Equal(HistoryParts.Played, LocalChangeObserver.PartsOf(MediaBrowser.Model.Entities.UserDataSaveReason.PlaybackFinished));
        Assert.Equal(HistoryParts.All, LocalChangeObserver.PartsOf(MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserData));
    }

    /// <summary>
    /// A part's version is kept under the history key, and still names the same user and item.
    /// True: upkeep forgets a part's version with the user or the item, as it does the whole history's.
    /// False: the versions of removed items' parts would pile up for good.
    /// </summary>
    [Fact]
    public void HistoryPartKey_StillNamesItsUserAndItem()
    {
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var key = HintProtocol.HistoryPartKey(HintProtocol.HistoryKey(user, item), HistoryParts.Favorite);

        Assert.True(HintProtocol.TryParseHistoryKey(key, out var readUser, out var readItem));
        Assert.Equal((user, item), (readUser, readItem));
    }
}

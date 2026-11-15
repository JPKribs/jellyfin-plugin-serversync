using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Queue;

/// <summary>
/// A received hint becomes a row that carries everything the peer sent and is due to apply at once.
/// </summary>
public sealed class InboundHintTests
{
    private static readonly DateTime Edited = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly DateTime Received = new(2026, 2, 3, 4, 6, 0, DateTimeKind.Utc);

    private static SyncHint Hint() => new()
    {
        HintId = "origin:42",
        OriginServerId = "origin",
        Kind = HintKind.History,
        Key = "user|item",
        ItemPath = "/media/movies/film.mkv",
        ItemId = "item",
        ItemType = "Movie",
        UserId = "user",
        UserName = "alice",
        VersionServerId = "editor",
        VersionTimestamp = Edited,
        Recorded = false
    };

    /// <summary>
    /// Every field the peer sent is copied onto the row.
    /// True: the apply step and the queue view see the hint as it was sent.
    /// False: a dropped field would make a hint apply to the wrong object or version.
    /// </summary>
    [Fact]
    public void FromHint_FullHint_CopiesEveryField()
    {
        var row = InboundHint.FromHint(Hint(), Received);

        Assert.Equal(("origin:42", "origin", HintKind.History, "user|item"), (row.HintId, row.OriginServerId, row.Kind, row.Key));
        Assert.Equal(("/media/movies/film.mkv", "item", "Movie"), (row.ItemPath, row.ItemId, row.ItemType));
        Assert.Equal(("user", "alice"), (row.UserId, row.UserName));
        Assert.Equal(("editor", Edited), (row.VersionServerId, row.VersionTimestamp));
        Assert.False(row.Recorded);
    }

    /// <summary>
    /// A new row is stamped with the time of receipt, is due at that time, and has no attempts, error, or park key.
    /// True: a fresh hint is applied on the next pass.
    /// False: a fresh hint would wait as if it had already failed, or sit parked forever.
    /// </summary>
    [Fact]
    public void FromHint_NewRow_IsDueNowWithNoHistory()
    {
        var row = InboundHint.FromHint(Hint(), Received);

        Assert.Equal(Received, row.ReceivedAt);
        Assert.Equal(Received, row.NextAttempt);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.LastError);
        Assert.Null(row.ParkedPath);
        Assert.Equal(0, row.Id);
    }

    /// <summary>
    /// A hint with no item or user leaves those fields empty.
    /// True: a people hint, which names neither, becomes a clean row.
    /// False: empty fields would be stored as text and matched against real ids.
    /// </summary>
    [Fact]
    public void FromHint_HintWithNoItemOrUser_LeavesThemNull()
    {
        var hint = new SyncHint { HintId = "origin:1", OriginServerId = "origin", Kind = HintKind.People, Key = "Jane Doe" };

        var row = InboundHint.FromHint(hint, Received);

        Assert.Null(row.ItemPath);
        Assert.Null(row.ItemId);
        Assert.Null(row.ItemType);
        Assert.Null(row.UserId);
        Assert.Null(row.UserName);
        Assert.True(row.Recorded);
    }

    /// <summary>
    /// A missing hint is refused.
    /// True: a peer route that parsed nothing fails at once with the argument named.
    /// False: an empty row would be queued and retried forever.
    /// </summary>
    [Fact]
    public void FromHint_NullHint_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => InboundHint.FromHint(null!, Received));
    }
}

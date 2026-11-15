using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Queue;

/// <summary>
/// An inbound row is shown with what an operator needs to judge it, and without the origin's user id or version stamp.
/// </summary>
public sealed class InboundHintDtoTests
{
    private static readonly DateTime Received = new(2026, 2, 3, 4, 6, 0, DateTimeKind.Utc);

    private static InboundHint Row() => new()
    {
        Id = 12,
        HintId = "origin:42",
        OriginServerId = "origin",
        Kind = HintKind.Metadata,
        Key = "item",
        ItemPath = "/media/movies/film.mkv",
        ItemId = "item",
        ItemType = "Movie",
        UserId = "user",
        UserName = "alice",
        ReceivedAt = Received,
        Attempts = 3,
        LastError = "no local item",
        Recorded = false
    };

    /// <summary>
    /// The row id, the hint's identity, its item, its user name, and its attempt history copy across.
    /// True: the queue view shows which hint is waiting, for what, and why it last failed.
    /// False: an operator could not tell a poisoned row from a healthy one.
    /// </summary>
    [Fact]
    public void From_FullRow_CopiesWhatTheQueueViewShows()
    {
        var dto = InboundHintDto.From(Row());

        Assert.Equal((12L, "origin:42", "origin", HintKind.Metadata, "item"), (dto.Id, dto.HintId, dto.OriginServerId, dto.Kind, dto.Key));
        Assert.Equal(("/media/movies/film.mkv", "item", "Movie", "alice"), (dto.ItemPath, dto.ItemId, dto.ItemType, dto.UserName));
        Assert.Equal((Received, 3, "no local item"), (dto.ReceivedAt, dto.Attempts, dto.LastError));
        Assert.False(dto.Recorded);
    }

    /// <summary>
    /// A row is shown as parked exactly when it has a park key, an empty key included.
    /// True: the queue view separates hints waiting for an item from hints waiting to apply.
    /// False: a parked hint would look stuck, or a stuck one would look parked.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", true)]
    [InlineData("/media/movies/film.mkv", true)]
    public void From_ParkKey_SetsParked(string? parkedPath, bool expected)
    {
        var row = Row();
        row.ParkedPath = parkedPath;

        var dto = InboundHintDto.From(row);

        Assert.Equal(expected, dto.Parked);
    }

    /// <summary>
    /// The shape shown has no field for the origin's user id.
    /// True: a peer or operator reading the queue sees a name and never another server's user id.
    /// False: adding the field would leak another server's user ids through the queue view.
    /// </summary>
    [Fact]
    public void From_Shape_HasNoUserIdField()
    {
        var property = typeof(InboundHintDto).GetProperty("UserId");

        Assert.Null(property);
    }

    /// <summary>
    /// A missing row is refused.
    /// True: the caller fails at once with the argument named.
    /// False: the queue view would fail with a null reference and no cause.
    /// </summary>
    [Fact]
    public void From_NullRow_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => InboundHintDto.From(null!));
    }
}

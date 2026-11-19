using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.MetadataSync;

/// <summary>
/// A file's media segments must be picked across the pool in rank order, fall back past servers with none, never come back to the server that made them, and survive a round trip through a row.
/// </summary>
public sealed class MediaSegmentsValueTests
{
    private const string ThisServer = "this-id";

    /// <summary>
    /// The highest ranked server with segments wins, passing over a higher ranked one that holds the file with none, and naming itself as the origin when it names none.
    /// True: a file gets segments whenever any server in the pool has them.
    /// False: a top server without an intro tool would hide every other server's segments.
    /// </summary>
    [Fact]
    public void Choose_TopServerHasNone_FallsBackToTheNextWithSegments()
    {
        var chosen = MediaSegmentsValue.Choose(new[] { Answer("a", "a-id", 0), Answer("b", "b-id", 1) }, ThisServer);

        Assert.Equal("b", chosen!.ServerKey);
        Assert.Equal("b-id", chosen.OriginServerId);
        Assert.Single(chosen.Segments);
    }

    /// <summary>
    /// With no server holding segments, the first that holds the file speaks with none, and with none holding it there is nothing.
    /// True: a file the pool has no segments for is told apart from one the pool does not hold.
    /// False: an empty pool would look like a removed file, or the reverse.
    /// </summary>
    [Fact]
    public void Choose_NoneHasSegments_GivesTheFirstHolderEmptyOrNothing()
    {
        var empty = MediaSegmentsValue.Choose(new[] { Answer("a", "a-id", 0), Answer("b", "b-id", 0) }, ThisServer);
        var nothing = MediaSegmentsValue.Choose(new[] { ("a", (string?)"a-id", "x", new PeerSegmentsItem { ItemId = "x", Found = false }) }, ThisServer);

        Assert.Equal("a", empty!.ServerKey);
        Assert.Empty(empty.Segments);
        Assert.Null(nothing);
    }

    /// <summary>
    /// A server whose segments were first made on this server is passed over, and an origin it names is kept.
    /// True: segments relay through servers that copied them, and never return to the server that made them.
    /// False: an intro deleted here could come back through a server that copied it.
    /// </summary>
    [Fact]
    public void Choose_OriginIsThisServer_IsPassedOver()
    {
        var back = Answer("a", "a-id", 1);
        back.Answer.OriginServerId = ThisServer;
        var relayed = Answer("b", "b-id", 1);
        relayed.Answer.OriginServerId = "first-id";

        var chosen = MediaSegmentsValue.Choose(new[] { back, relayed }, ThisServer);

        Assert.Equal("b", chosen!.ServerKey);
        Assert.Equal("first-id", chosen.OriginServerId);
    }

    /// <summary>
    /// A value written to a row and read back, and one written as JSON and read back, are the same, and unreadable JSON is nothing.
    /// True: the source and local values compare equal once applied, so the row is Synced.
    /// False: an applied row would show Changes forever.
    /// </summary>
    [Fact]
    public void RoundTrips_KeepTheValue()
    {
        var value = Answer("a", "a-id", 2).Answer;
        var original = MediaSegmentsValue.Choose(new[] { ("a", (string?)"a-id", "x", value) }, ThisServer)!;

        var fromRow = MediaSegmentsValue.FromRow(original.ToRow("local", System.DateTime.UtcNow))!;
        var fromJson = MediaSegmentsValue.FromJson(original.ToJson())!;

        Assert.Equal(original.ToJson(), fromRow.ToJson());
        Assert.Equal(original.ToJson(), fromJson.ToJson());
        Assert.Null(MediaSegmentsValue.FromJson("{not json"));
        Assert.Null(MediaSegmentsValue.FromJson(null));
    }

    /// <summary>
    /// A pool with no segments for a file that has none kept here is no change, unless removing is on, and anything else is kept as chosen.
    /// True: files no server has segments for stay Synced instead of showing Changes on every scan.
    /// False: every file in a library with no intro tool would be queued, or removing could never be applied.
    /// </summary>
    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(0, false, true, true)]
    [InlineData(0, true, false, true)]
    [InlineData(1, false, false, true)]
    public void Worth_EmptyPoolAndNothingKept_IsNoChangeUnlessRemoving(int segments, bool keptHere, bool remove, bool worth)
    {
        var chosen = MediaSegmentsValue.Choose(new[] { Answer("a", "a-id", segments) }, ThisServer);
        var local = keptHere ? chosen : null;

        Assert.Equal(worth, MediaSegmentsValue.Worth(chosen, local, remove) is not null);
        Assert.Null(MediaSegmentsValue.Worth(null, local, remove));
    }

    private static (string ServerKey, string? ServerId, string SourceItemId, PeerSegmentsItem Answer) Answer(string key, string id, int segments)
        => (key, id, key + "-item", new PeerSegmentsItem
        {
            ItemId = key + "-item",
            Found = true,
            Size = 10,
            RunTimeTicks = 20,
            Segments = Enumerable.Range(0, segments).Select(i => new PeerSegment { Type = "Intro", StartTicks = i * 10, EndTicks = (i * 10) + 5 }).ToList()
        });
}

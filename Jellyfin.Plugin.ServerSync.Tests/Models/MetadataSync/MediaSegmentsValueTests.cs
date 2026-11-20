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
    /// A server whose segments were all first made on this server is passed over, and the type they hold stays this server's below it,
    /// while an origin a lower server names for another type is kept.
    /// True: segments relay through servers that copied them, and never return to undo the server that made them.
    /// False: an intro changed here could be undone by an older copy coming back around.
    /// </summary>
    [Fact]
    public void Choose_OriginIsThisServer_IsPassedOverAndHoldsItsType()
    {
        var back = Answer("a", "a-id", 1);
        back.Answer.OriginServerId = ThisServer;
        var sameType = Answer("b", "b-id", 1);
        sameType.Answer.OriginServerId = "first-id";
        var otherType = Answer("c", "c-id", 0);
        otherType.Answer.Segments.Add(new PeerSegment { Type = "Outro", StartTicks = 50, EndTicks = 60 });
        otherType.Answer.OriginServerId = "first-id";

        Assert.Null(MediaSegmentsValue.Choose(new[] { back, sameType }, ThisServer));
        var chosen = MediaSegmentsValue.Choose(new[] { back, sameType, otherType }, ThisServer);

        Assert.Equal("c", chosen!.ServerKey);
        Assert.Equal("first-id", chosen.OriginServerId);
        Assert.Equal("Outro", Assert.Single(chosen.Segments).Type);
        Assert.Equal(new[] { "Intro" }, chosen.HeldTypes);
    }

    /// <summary>
    /// A server ranked above the one holding this server's copy still wins for that type.
    /// True: rank decides, and a copy of this server's segment sits at the rank of the server that holds it.
    /// False: one copy anywhere in the pool would lock out every better ranked server.
    /// </summary>
    [Fact]
    public void Choose_HigherServerOutranksTheCopyOfThisServers()
    {
        var top = Answer("a", "a-id", 1);
        var back = Answer("b", "b-id", 1);
        back.Answer.OriginServerId = ThisServer;

        var chosen = MediaSegmentsValue.Choose(new[] { top, back }, ThisServer);

        Assert.Equal("a", chosen!.ServerKey);
        Assert.Null(chosen.HeldTypes);
    }

    /// <summary>
    /// One segment in an answer that was made here drops only that segment and its type, and the server's own segments of other types are taken.
    /// True: a server that copied this server's intro still gives its own outro.
    /// False: one copied segment would hide everything else the server has.
    /// </summary>
    [Fact]
    public void Choose_MixedAnswer_TakesTheSegmentsNotMadeHere()
    {
        var mixed = Answer("a", "a-id", 0);
        mixed.Answer.SegmentOrigins = true;
        mixed.Answer.OriginServerId = ThisServer;
        mixed.Answer.Segments.Add(new PeerSegment { Type = "Intro", StartTicks = 0, EndTicks = 10, OriginServerId = ThisServer });
        mixed.Answer.Segments.Add(new PeerSegment { Type = "Outro", StartTicks = 50, EndTicks = 60 });
        mixed.Answer.Segments.Add(new PeerSegment { Type = "Recap", StartTicks = 20, EndTicks = 30, OriginServerId = "third-id" });

        var chosen = MediaSegmentsValue.Choose(new[] { mixed }, ThisServer)!;

        Assert.Equal(new[] { "Outro", "Recap" }, chosen.Segments.Select(s => s.Type).OrderBy(t => t));
        Assert.Equal("a-id", chosen.Segments.Single(s => s.Type == "Outro").OriginServerId);
        Assert.Equal("third-id", chosen.Segments.Single(s => s.Type == "Recap").OriginServerId);
        Assert.Null(chosen.OriginServerId);
        Assert.Equal(new[] { "Intro" }, chosen.HeldTypes);
    }

    /// <summary>
    /// An older release names one origin for every segment it copied, and that origin is given to all of them.
    /// True: a pool partly on an older release still never takes back what a server made.
    /// False: an older peer's copies would look like its own and loop back.
    /// </summary>
    [Fact]
    public void Choose_OlderPeer_GivesItsOneOriginToEverySegment()
    {
        var old = Answer("a", "a-id", 2);
        old.Answer.OriginServerId = ThisServer;

        Assert.Null(MediaSegmentsValue.Choose(new[] { old }, ThisServer));
        Assert.All(MediaSegmentsValue.WithOrigins(old.Answer, "a-id"), s => Assert.Equal(ThisServer, s.OriginServerId));
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

    /// <summary>
    /// Overlapping intros, and overlapping outros, shrink to the part they share.
    /// True: 0:00 to 0:30 and 0:05 to 0:45 become 0:05 to 0:30, which every provider agrees is the intro.
    /// False: two providers' intros would both be skipped, or one would win at random.
    /// </summary>
    [Theory]
    [InlineData("Intro")]
    [InlineData("Outro")]
    public void Consolidate_OverlappingIntrosOrOutros_ShrinkToTheSharedPart(string type)
    {
        var segments = MediaSegmentsValue.ConsolidateSegments(new[] { Seg(type, 0, 30), Seg(type, 5, 45) }, intros: true, outros: true, others: false);

        var only = Assert.Single(segments);
        Assert.Equal((type, S(5), S(30)), (only.Type, only.StartTicks, only.EndTicks));
    }

    /// <summary>
    /// Overlapping segments of the same other type join into one that covers both.
    /// True: 1:00 to 1:30 and 1:05 to 1:45 become 1:00 to 1:45.
    /// False: a recap would be split across two segments that a client skips one at a time.
    /// </summary>
    [Fact]
    public void Consolidate_OverlappingOtherSegmentsOfOneType_JoinIntoOne()
    {
        var segments = MediaSegmentsValue.ConsolidateSegments(new[] { Seg("Recap", 60, 90), Seg("Recap", 65, 105) }, intros: false, outros: false, others: true);

        var only = Assert.Single(segments);
        Assert.Equal(("Recap", S(60), S(105)), (only.Type, only.StartTicks, only.EndTicks));
    }

    /// <summary>
    /// Only overlapping segments of the same type are consolidated, and a type whose setting is off is kept as it is.
    /// True: separate intros, segments that only touch, and overlapping segments of different types all stay.
    /// False: a cold open and a later intro, or a recap and a preview, would merge into one wrong segment.
    /// </summary>
    [Fact]
    public void Consolidate_NoOverlapOrDifferentTypesOrSettingOff_KeepsSegments()
    {
        var apart = MediaSegmentsValue.ConsolidateSegments(new[] { Seg("Intro", 0, 30), Seg("Intro", 30, 60), Seg("Intro", 100, 130) }, true, true, true);
        var mixed = MediaSegmentsValue.ConsolidateSegments(new[] { Seg("Recap", 0, 30), Seg("Preview", 10, 40) }, true, true, true);
        var off = MediaSegmentsValue.ConsolidateSegments(new[] { Seg("Intro", 0, 30), Seg("Intro", 5, 45), Seg("Recap", 0, 30), Seg("Recap", 5, 45) }, intros: false, outros: true, others: false);

        Assert.Equal(3, apart.Count);
        Assert.Equal(2, mixed.Count);
        Assert.Equal(4, off.Count);
    }

    /// <summary>
    /// Three overlapping intros shrink to the part all three share, and an intro that overlaps only the last starts its own.
    /// True: the shared part narrows with each intro that overlaps it.
    /// False: a long intro could stretch the shared part past where another provider ends it.
    /// </summary>
    [Fact]
    public void Consolidate_ChainOfIntros_NarrowsThenStartsAgain()
    {
        var segments = MediaSegmentsValue.ConsolidateSegments(new[] { Seg("Intro", 0, 30), Seg("Intro", 5, 45), Seg("Intro", 10, 25), Seg("Intro", 28, 50) }, true, false, false);

        Assert.Equal(new[] { (S(10), S(25)), (S(28), S(50)) }, segments.Select(s => (s.StartTicks, s.EndTicks)));
    }

    /// <summary>
    /// A value with every setting off is the same value, and one with a setting on keeps its source and only changes its segments.
    /// True: rows are unchanged until a Consolidate setting is turned on.
    /// False: turning the feature on would also lose where the segments came from.
    /// </summary>
    [Fact]
    public void Consolidate_Value_KeepsSourceAndIsUnchangedWhenOff()
    {
        var value = new MediaSegmentsValue("a", "a-item", "a-id", 10, 20, new[] { Seg("Intro", 0, 30), Seg("Intro", 5, 45) });

        Assert.Same(value, value.Consolidate(false, false, false));
        var consolidated = value.Consolidate(true, false, false);
        Assert.Equal(("a", "a-item", "a-id"), (consolidated.ServerKey, consolidated.SourceItemId, consolidated.OriginServerId));
        Assert.Single(consolidated.Segments);
    }

    private static long S(int seconds) => seconds * 10_000_000L;

    private static PeerSegment Seg(string type, int start, int end) => new() { Type = type, StartTicks = S(start), EndTicks = S(end) };

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

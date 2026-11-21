using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The hint activity log titles each item the way the library does.
/// </summary>
public class HintActivityLogTests
{
    /// <summary>
    /// An episode is titled by its series and number, and its line adds the episode's own name.
    /// True: "Synced The Show S01E02" and "Metadata for The Show S01E02 - Pilot was updated from a".
    /// False: an entry reads "Synced Pilot", which names nothing on a server with many shows.
    /// </summary>
    [Fact]
    public void Episode_IsTitledBySeriesAndNumber()
    {
        var episode = new Episode { Name = "Pilot", SeriesName = "The Show", ParentIndexNumber = 1, IndexNumber = 2 };
        Assert.Equal("The Show S01E02", ItemTitles.TitleOf(episode));
        Assert.Equal("The Show S01E02 - Pilot", ItemTitles.LineOf(episode));
    }

    /// <summary>
    /// A season is titled by its series, since "Season 1" alone names nothing.
    /// True: "Synced The Show Season 1".
    /// False: "Synced Season 1".
    /// </summary>
    [Fact]
    public void Season_IsTitledBySeries()
    {
        var season = new Season { Name = "Season 1", SeriesName = "The Show" };
        Assert.Equal("The Show Season 1", ItemTitles.TitleOf(season));
        Assert.Equal("The Show Season 1", ItemTitles.LineOf(season));
    }

    /// <summary>
    /// Anything else goes by its name, and the subject wraps it by kind without a full stop.
    /// True: "Metadata for Only A (2021)".
    /// False: a film is renamed or the entry ends in a full stop like nothing else in the log.
    /// </summary>
    [Fact]
    public void Movie_GoesByName()
    {
        var movie = new Movie { Name = "Only A (2021)" };
        Assert.Equal("Only A (2021)", ItemTitles.TitleOf(movie));
        Assert.Equal("Metadata for Only A (2021)", HintActivityLog.SubjectOf(HintKind.Metadata, ItemTitles.LineOf(movie), null));
        Assert.Equal("Watch history for Only A (2021) (viewer)", HintActivityLog.SubjectOf(HintKind.History, ItemTitles.LineOf(movie), "viewer"));
    }

    /// <summary>
    /// Applied changes from peers are written as one entry with a generic name and the count and servers in the overview.
    /// True: "Items synced" with "Synced 3 items from source-a (2) and source-b".
    /// False: the name would carry the count, or the overview list "source-b (1)".
    /// </summary>
    /// <returns>A task that ends when the test does.</returns>
    [Fact]
    public async Task Applied_WritesOneGenericEntry()
    {
        using var harness = new HintActivityHarness();
        var hint = new InboundHint { Kind = HintKind.Metadata, Key = "k" };

        await harness.Log.AppliedAsync(hint, "source-a");
        await harness.Log.AppliedAsync(hint, "source-a");
        await harness.Log.AppliedAsync(hint, "source-b");
        harness.Log.Flush();

        Assert.Equal(("Items synced", "Synced 3 items from source-a (2) and source-b"), Assert.Single(harness.Entries));
    }

    /// <summary>
    /// Dropped changes name the server and the reasons once each.
    /// True: "Item not synced" with "Did not sync 1 item from source-a: filtered out".
    /// False: the overview would repeat a count of 1 in brackets.
    /// </summary>
    /// <returns>A task that ends when the test does.</returns>
    [Fact]
    public async Task Dropped_NamesTheServerAndReason()
    {
        using var harness = new HintActivityHarness();

        await harness.Log.DroppedAsync(new InboundHint { Kind = HintKind.Metadata, Key = "k" }, "source-a", HintCode.Filtered);
        harness.Log.Flush();

        Assert.Equal(("Item not synced", "Did not sync 1 item from source-a: filtered out"), Assert.Single(harness.Entries));
    }

    /// <summary>
    /// A server's own events keep the server out of the name and say each fact once in the overview.
    /// True: "Changes rejected" with "Marked 2 changes failed, since source-b rejected the request as malformed".
    /// False: the name would hold the server, or the overview the peer's raw response.
    /// </summary>
    /// <returns>A task that ends when the test does.</returns>
    [Fact]
    public async Task PeerEvents_HaveGenericNames()
    {
        using var harness = new HintActivityHarness();
        var rows = new List<OutboundHint> { new() { Kind = HintKind.Metadata, Key = "a" }, new() { Kind = HintKind.Metadata, Key = "b" } };

        await harness.Log.RejectedBatchAsync("source-b", rows);
        await harness.Log.ResentAsync("source-b", rows.GetRange(0, 1));
        await harness.Log.PausedAsync("source-b", "the peer refused this server's key", Array.Empty<OutboundHint>(), DateTime.UtcNow);

        var entries = harness.Entries;
        Assert.Equal(("Changes rejected", "Marked 2 changes failed, since source-b rejected the request as malformed"), entries[0]);
        Assert.Equal(("Changes resent", "Resent 1 change that source-b lost before finishing"), entries[1]);
        Assert.Equal("Changes paused", entries[2].Name);
        Assert.StartsWith("Paused changes to source-b until ", entries[2].Overview, StringComparison.Ordinal);
        Assert.EndsWith(" because the peer refused this server's key", entries[2].Overview, StringComparison.Ordinal);
    }
}

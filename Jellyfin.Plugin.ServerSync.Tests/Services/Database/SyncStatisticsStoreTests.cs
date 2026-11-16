using System;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Database;

/// <summary>
/// The store keeps only what the Statistics tab charts: the last scans of each sync type, and the link streams of the last thirty days.
/// </summary>
public sealed class SyncStatisticsStoreTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 4, 30);

    private readonly DatabaseTempFolder _folder = new("statistics");
    private readonly SyncDatabase _database;
    private readonly SyncStatisticsStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncStatisticsStoreTests"/> class.
    /// </summary>
    public SyncStatisticsStoreTests()
    {
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        _store = new SyncStatisticsStore(new FixedProvider(_database), NullLogger<SyncStatisticsStore>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// A sync type with no scan lists none, and a recorded scan reads back as it was given, in UTC.
    /// True: the chart shows the numbers the scan found.
    /// False: the chart would draw a scan that never ran, or the wrong counts.
    /// </summary>
    [Fact]
    public void RecordRun_ThenRuns_ReturnsTheScan()
    {
        Assert.Empty(_store.Runs("Content"));

        _store.RecordRun("Content", new ScanRun(Start.ToLocalTime(), 3, 2, 1));

        Assert.Equal(new ScanRun(Start, 3, 2, 1), Assert.Single(_store.Runs("Content")));
    }

    /// <summary>
    /// Only the last ten scans of a sync type are kept, oldest first, and another sync type's scans are untouched.
    /// True: the table never grows past what the charts show.
    /// False: every scan ever run would be stored, or one sync type's scans would push out another's.
    /// </summary>
    [Fact]
    public void RecordRun_PastTheLimit_KeepsTheLastTenOfThatType()
    {
        _store.RecordRun("People", new ScanRun(Start, 99, 0, 0));
        for (var i = 1; i <= SyncStatisticsStore.RunsKept + 3; i++)
        {
            _store.RecordRun("Content", new ScanRun(Start.AddHours(i), i, 0, 0));
        }

        Assert.Equal(Enumerable.Range(4, SyncStatisticsStore.RunsKept), _store.Runs("Content").Select(run => run.Added));
        Assert.Equal(99, Assert.Single(_store.Runs("People")).Added);
    }

    /// <summary>
    /// The last thirty days are listed oldest first and end today, and a server with no stream is not listed.
    /// True: the chart has a point for every day.
    /// False: a quiet day would be missing and the line would join the days around it.
    /// </summary>
    [Fact]
    public void Days_ListsThirtyDaysEndingToday()
    {
        var days = SyncStatisticsStore.Days(Today);

        Assert.Equal(SyncStatisticsStore.DaysKept, days.Count);
        Assert.Equal(Today.AddDays(-29), days[0]);
        Assert.Equal(Today, days[^1]);
        Assert.Empty(_store.LinkStreams(Today));
    }

    /// <summary>
    /// Each stream adds one to its server's day, each server keeps its own counts, and a day past the thirty is dropped by the next count.
    /// True: the chart draws a line for each server and the table holds thirty days at most.
    /// False: one server's streams would be counted as another's, or the table would keep every day forever.
    /// </summary>
    [Fact]
    public void CountLinkStream_AddsToItsServersDayAndDropsOldDays()
    {
        _store.CountLinkStream(Today.AddDays(-40), "den");
        _store.CountLinkStream(Today.AddDays(-2), "den");
        _store.CountLinkStream(Today, "den");
        _store.CountLinkStream(Today, "den");
        _store.CountLinkStream(Today, "attic");

        var streams = _store.LinkStreams(Today);

        Assert.Equal(new[] { "attic", "den" }, streams.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(SyncStatisticsStore.DaysKept, streams["den"].Length);
        Assert.Equal((2, 1, 3), (streams["den"][^1], streams["den"][^3], streams["den"].Sum()));
        Assert.Equal(1, streams["attic"].Sum());
        Assert.True(streams.ContainsKey("DEN"));

        // A window that reaches back to the old day finds it gone.
        Assert.Empty(_store.LinkStreams(Today.AddDays(-20)));
    }

    /// <summary>
    /// A missing sync type or scan is refused.
    /// True: a caller's mistake fails at once.
    /// False: a scan would be stored under no sync type and never shown.
    /// </summary>
    [Fact]
    public void Members_BadArguments_Throw()
    {
        Assert.ThrowsAny<ArgumentException>(() => _store.RecordRun(string.Empty, new ScanRun(Start, 0, 0, 0)));
        Assert.Throws<ArgumentNullException>(() => _store.RecordRun("Content", null!));
        Assert.ThrowsAny<ArgumentException>(() => _store.Runs(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => _store.CountLinkStream(Today, string.Empty));
    }
}

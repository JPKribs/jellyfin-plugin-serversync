using System;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Database;

/// <summary>
/// The store must keep exactly one watermark for each scan task and server, and give back the times it was given.
/// </summary>
public sealed class ScanWatermarkStoreTests : IDisposable
{
    private static readonly DateTime Listed = new(2026, 4, 1, 8, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Full = new(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc);

    private readonly DatabaseTempFolder _folder = new("watermarks");
    private readonly SyncDatabase _database;
    private readonly ScanWatermarkStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanWatermarkStoreTests"/> class.
    /// </summary>
    public ScanWatermarkStoreTests()
    {
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        _store = new ScanWatermarkStore(new FixedProvider(_database), NullLogger<ScanWatermarkStore>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// A task and server with no watermark reads as null.
    /// True: a first scan knows it must list everything.
    /// False: a first scan would start from a made up time and skip older items.
    /// </summary>
    [Fact]
    public void Get_NothingSaved_IsNull()
    {
        Assert.Null(_store.Get("content", "server-a"));
    }

    /// <summary>
    /// A saved watermark reads back with the same scope and the same instants in UTC, whatever zone they were given in.
    /// True: the next scan lists from exactly where the last one ended.
    /// False: a zone shift would skip or repeat hours of changes.
    /// </summary>
    [Fact]
    public void Set_ThenGet_ReturnsTheSameWatermarkInUtc()
    {
        _store.Set("content", "server-a", new ScanWatermark("scope-1", Listed.ToLocalTime(), Full));

        var read = _store.Get("content", "server-a");

        Assert.Equal(new ScanWatermark("scope-1", Listed, Full), read);
        Assert.Equal(DateTimeKind.Utc, read!.ListedFrom.Kind);
        Assert.Equal(DateTimeKind.Utc, read.FullAt.Kind);
    }

    /// <summary>
    /// Saving again for the same task and server replaces the earlier watermark.
    /// True: each scan moves its own watermark forward.
    /// False: a second save would fail on the key, or leave the old watermark in place.
    /// </summary>
    [Fact]
    public void Set_SameTaskAndServer_ReplacesTheEarlierOne()
    {
        _store.Set("content", "server-a", new ScanWatermark("scope-1", Listed, Full));

        _store.Set("content", "server-a", new ScanWatermark("scope-2", Listed.AddDays(1), Full.AddDays(1)));

        Assert.Equal(new ScanWatermark("scope-2", Listed.AddDays(1), Full.AddDays(1)), _store.Get("content", "server-a"));
        Assert.Equal(1, _store.Clear());
    }

    /// <summary>
    /// A watermark belongs to one task and one server, and neither reads the other's.
    /// True: a scan of one server never moves another server's watermark.
    /// False: one server's scan would make another skip its changes.
    /// </summary>
    [Fact]
    public void Get_OtherTaskOrServer_DoesNotSeeIt()
    {
        _store.Set("content", "server-a", new ScanWatermark("scope-1", Listed, Full));

        Assert.Null(_store.Get("content", "server-b"));
        Assert.Null(_store.Get("history", "server-a"));
        Assert.Null(_store.Get("CONTENT", "server-a"));
    }

    /// <summary>
    /// Clearing a task forgets that task on every server and nothing else, and clearing with no task forgets everything.
    /// True: resetting one sync type makes only that type list everything again.
    /// False: a reset would leave a stale watermark, or wipe every other type's.
    /// </summary>
    [Fact]
    public void Clear_OneTaskThenAll_ForgetsOnlyWhatWasAsked()
    {
        _store.Set("content", "server-a", new ScanWatermark("s", Listed, Full));
        _store.Set("content", "server-b", new ScanWatermark("s", Listed, Full));
        _store.Set("history", "server-a", new ScanWatermark("s", Listed, Full));

        var clearedTask = _store.Clear("content");
        var keptOther = _store.Get("history", "server-a");
        var clearedRest = _store.Clear();

        Assert.Equal(2, clearedTask);
        Assert.NotNull(keptOther);
        Assert.Equal(1, clearedRest);
        Assert.Null(_store.Get("history", "server-a"));
        Assert.Equal(0, _store.Clear("content"));
    }

    /// <summary>
    /// The earliest and the latest time both survive a save.
    /// True: a watermark at either end of the range reads back unchanged.
    /// False: an extreme time would overflow or lose precision on the way through text.
    /// </summary>
    [Fact]
    public void Set_ExtremeTimes_SurviveTheRoundTrip()
    {
        var earliest = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var latest = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

        _store.Set("content", "server-a", new ScanWatermark(string.Empty, earliest, latest));

        Assert.Equal(new ScanWatermark(string.Empty, earliest, latest), _store.Get("content", "server-a"));
    }

    /// <summary>
    /// A missing task, server, or watermark is refused.
    /// True: a bad call fails at once.
    /// False: a null key would be saved as a row no scan can ever read.
    /// </summary>
    [Fact]
    public void GetAndSet_MissingArgument_Throw()
    {
        var watermark = new ScanWatermark("s", Listed, Full);

        Assert.Throws<ArgumentNullException>(() => _store.Get(null!, "server-a"));
        Assert.Throws<ArgumentNullException>(() => _store.Get("content", null!));
        Assert.Throws<ArgumentNullException>(() => _store.Set(null!, "server-a", watermark));
        Assert.Throws<ArgumentNullException>(() => _store.Set("content", null!, watermark));
        Assert.Throws<ArgumentNullException>(() => _store.Set("content", "server-a", null!));
    }
}

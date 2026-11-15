using System;
using System.Collections.Generic;
using System.Linq;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Database;

/// <summary>
/// The removal record must name every item removed since a time, and must say how far back it reaches, so a server that asks from before then lists everything.
/// </summary>
public sealed class RemovedItemsStoreTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly DatabaseTempFolder _folder = new("removed");
    private readonly SyncDatabase _database;
    private readonly FixedProvider _provider;
    private readonly RemovedItemsStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemovedItemsStoreTests"/> class.
    /// </summary>
    public RemovedItemsStoreTests()
    {
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        _provider = new FixedProvider(_database);
        _store = new RemovedItemsStore(_provider, NullLogger<RemovedItemsStore>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// A removal with no item id is refused.
    /// True: the record never holds a removal that names nothing.
    /// False: a blank id would be handed to a peer as an item to remove.
    /// </summary>
    [Fact]
    public void Note_MissingId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _store.Note(null!, Noon));
        Assert.Throws<ArgumentException>(() => _store.Note(string.Empty, Noon));
        Assert.Empty(_store.Since(DateTime.UnixEpoch, Noon).ItemIds);
    }

    /// <summary>
    /// Removals at or after the asked time are returned once each, and earlier ones are not.
    /// True: a peer that asks from its last scan hears of every removal since, the one at that instant included.
    /// False: a removal on the boundary would be missed for good, or an item removed twice would be listed twice.
    /// </summary>
    [Fact]
    public void Since_RemovalsAroundTheAskedTime_ReturnsThoseAtOrAfterItOnce()
    {
        _store.Note("before", Noon.AddSeconds(-1));
        _store.Note("at", Noon);
        _store.Note("after", Noon.AddHours(1));
        _store.Note("after", Noon.AddHours(2));

        var (_, ids) = _store.Since(Noon, Noon.AddDays(1));

        Assert.Equal(new[] { "after", "at" }, ids.OrderBy(i => i, StringComparer.Ordinal));
    }

    /// <summary>
    /// A removal noted in local time is compared in UTC.
    /// True: a server in any time zone answers the same question the same way.
    /// False: removals would be missed or repeated by the zone's offset.
    /// </summary>
    [Fact]
    public void Since_RemovalNotedInLocalTime_IsComparedInUtc()
    {
        _store.Note("local", Noon.ToLocalTime());

        Assert.Single(_store.Since(Noon, Noon.AddDays(1)).ItemIds);
        Assert.Empty(_store.Since(Noon.AddSeconds(1), Noon.AddDays(1)).ItemIds);
    }

    /// <summary>
    /// The record covers from when it started, until that is further back than removals are kept, and then from the keep limit.
    /// True: a peer asking from before the covered time is told to list everything.
    /// False: a peer would trust an answer that silently misses removals older than the record.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(29, 29)]
    [InlineData(30, 30)]
    [InlineData(31, 30)]
    [InlineData(400, 30)]
    public void Since_RecordAge_BoundsHowFarBackItCovers(int recordAgeDays, int expectedCoveredDaysBack)
    {
        _folder.Run("UPDATE RemovedItemsLog SET StartedAt = @at", ("@at", UtcTime.Format(Noon.AddDays(-recordAgeDays))));

        var (coveredFrom, _) = _store.Since(DateTime.UnixEpoch, Noon);

        Assert.Equal(Noon.AddDays(-expectedCoveredDaysBack), coveredFrom);
        Assert.Equal(TimeSpan.FromDays(30), RemovedItemsStore.Kept);
    }

    /// <summary>
    /// A copy's removal, which carries a path and an origin but no item id, is listed as a copy and never as an item.
    /// True: a peer learns whose file was removed, and gets no blank id to remove.
    /// False: a removed copy would be lost, or would reach a peer as an item with an empty id.
    /// </summary>
    [Fact]
    public void CopiesSince_RemovedCopyAndRemovedItem_ListsOnlyTheCopy()
    {
        var content = new ContentSyncTableManager(_provider, NullLogger<ContentSyncTableManager>.Instance);
        content.NoteRemovedCopy("/media/movies/copy.mkv", "origin-1");
        content.NoteRemovedCopy("/media/movies/own.mkv", string.Empty);
        _store.Note("plain-item", DateTime.UtcNow);

        var copies = _store.CopiesSince(DateTime.UnixEpoch);
        var later = _store.CopiesSince(DateTime.UtcNow.AddDays(1));
        var (_, ids) = _store.Since(DateTime.UnixEpoch, DateTime.UtcNow);

        Assert.Equal(new KeyValuePair<string, string>("/media/movies/copy.mkv", "origin-1"), Assert.Single(copies));
        Assert.Empty(later);
        Assert.Equal("plain-item", Assert.Single(ids));
    }

    /// <summary>
    /// Trimming forgets removals before the cutoff, keeps the one at it, and says how many it forgot.
    /// True: the record stays small and still answers from the cutoff on.
    /// False: the table would grow without end, or a removal still inside the window would be lost.
    /// </summary>
    [Fact]
    public void Trim_Cutoff_ForgetsOnlyOlderRemovals()
    {
        _store.Note("old-1", Noon.AddDays(-2));
        _store.Note("old-2", Noon.AddSeconds(-1));
        _store.Note("at", Noon);
        _store.Note("new", Noon.AddDays(1));

        var forgotten = _store.Trim(Noon);
        var again = _store.Trim(Noon);

        Assert.Equal(2, forgotten);
        Assert.Equal(0, again);
        Assert.Equal(new[] { "at", "new" }, _store.Since(DateTime.UnixEpoch, Noon.AddDays(2)).ItemIds.OrderBy(i => i, StringComparer.Ordinal));
    }

    /// <summary>
    /// An item id is stored as data, whatever it holds.
    /// True: an id is only ever a value.
    /// False: an id built into the SQL text could drop or read the table.
    /// </summary>
    [Fact]
    public void Note_IdWithSqlInIt_IsStoredAsText()
    {
        const string Hostile = "x'); DROP TABLE RemovedItems; --";

        _store.Note(Hostile, Noon);

        Assert.Equal(Hostile, Assert.Single(_store.Since(Noon, Noon).ItemIds));
    }
}

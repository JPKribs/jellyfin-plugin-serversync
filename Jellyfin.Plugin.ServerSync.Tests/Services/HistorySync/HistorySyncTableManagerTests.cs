using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.HistorySync;

/// <summary>
/// The History sync table keeps one row for each source user and item, and every read and write touches only the rows it names.
/// </summary>
public sealed class HistorySyncTableManagerTests : IDisposable
{
    private static readonly DateTime At = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly SyncTestDatabase _db = new("historytable");
    private readonly HistorySyncTableManager _table;

    public HistorySyncTableManagerTests()
    {
        _table = new HistorySyncTableManager(_db.Provider, NullLogger<HistorySyncTableManager>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static HistorySyncItem Row(string user, string item, SyncStatus status = SyncStatus.Synced, string? name = null, string localUser = "local-user", string? serverKey = "server", string? localItem = "local-item")
        => new()
        {
            SourceUserId = user,
            LocalUserId = localUser,
            SourceLibraryId = "lib",
            LocalLibraryId = "local-lib",
            SourceItemId = item,
            LocalItemId = localItem,
            ItemName = name ?? item,
            SourcePath = "/src/" + item + ".mkv",
            Status = status,
            StatusDate = At,
            ServerKey = serverKey
        };

    private static string[] Keys(IEnumerable<HistorySyncItem> rows)
        => rows.Select(r => r.SourceUserId + "|" + r.SourceItemId).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// All three sides of the state survive a write and a read, with the largest position and both flag values.
    /// True: what a scan merged is what the apply step reads.
    /// False: a played flag would flip, or a long position would be cut, between scan and apply.
    /// </summary>
    [Fact]
    public void Upsert_EverySide_RoundTrips()
    {
        var row = Row("u", "i");
        (row.SourceIsPlayed, row.SourcePlayCount, row.SourcePlaybackPositionTicks, row.SourceLastPlayedDate, row.SourceIsFavorite) = (true, 3, long.MaxValue, At, false);
        (row.LocalIsPlayed, row.LocalPlayCount, row.LocalPlaybackPositionTicks, row.LocalLastPlayedDate, row.LocalIsFavorite) = (false, 1, 50, At.AddDays(-1), true);
        (row.MergedIsPlayed, row.MergedPlayCount, row.MergedPlaybackPositionTicks, row.MergedLastPlayedDate, row.MergedIsFavorite) = (true, 3, 100, At, true);
        row.LocalPath = "/local/i.mkv";

        _table.Upsert(row);
        var read = _table.GetByKey(("u", "i"))!;

        Assert.Equal((true, 3, long.MaxValue, At, false), (read.SourceIsPlayed, read.SourcePlayCount, read.SourcePlaybackPositionTicks, read.SourceLastPlayedDate, read.SourceIsFavorite));
        Assert.Equal((false, 1, 50L, At.AddDays(-1), true), (read.LocalIsPlayed, read.LocalPlayCount, read.LocalPlaybackPositionTicks, read.LocalLastPlayedDate, read.LocalIsFavorite));
        Assert.Equal((true, 3, 100L, At, true), (read.MergedIsPlayed, read.MergedPlayCount, read.MergedPlaybackPositionTicks, read.MergedLastPlayedDate, read.MergedIsFavorite));
        Assert.Equal(("local-user", "lib", "local-lib", "local-item", "/local/i.mkv", "server"), (read.LocalUserId, read.SourceLibraryId, read.LocalLibraryId, read.LocalItemId, read.LocalPath, read.ServerKey));
    }

    /// <summary>
    /// A state that was never read stays unknown through a write and a read.
    /// True: "the source did not say" stays different from "not played".
    /// False: an unknown flag would be stored as false and wipe a played mark on merge.
    /// </summary>
    [Fact]
    public void Upsert_UnknownState_StaysNull()
    {
        _table.Upsert(Row("u", "i"));

        var read = _table.GetByKey(("u", "i"))!;

        Assert.Null(read.SourceIsPlayed);
        Assert.Null(read.SourcePlayCount);
        Assert.Null(read.SourcePlaybackPositionTicks);
        Assert.Null(read.SourceLastPlayedDate);
        Assert.Null(read.MergedIsFavorite);
        Assert.Null(read.NegotiatedAt);
        Assert.False(read.HasNegotiatedBase);
    }

    /// <summary>
    /// A second write for the same user and item replaces the first, and the same item under another user is its own row.
    /// True: each user's history of an item is tracked apart.
    /// False: two users would share one row and overwrite each other's history.
    /// </summary>
    [Fact]
    public void Upsert_SameItemTwoUsers_KeepsOneRowEach()
    {
        _table.Upsert(Row("alice", "film", name: "Old name"));
        _table.Upsert(Row("alice", "film", name: "New name"));
        _table.Upsert(Row("bob", "film"));

        Assert.Equal(2, _table.Count());
        Assert.Equal("New name", _table.GetByKey(("alice", "film"))!.ItemName);
    }

    /// <summary>
    /// The synced state hash is kept when a write carries none, and when the stored row is ignored.
    /// True: a scan that knows nothing of the last apply does not erase its record.
    /// False: every scan would forget what was applied and queue the row again.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Synced, null, "applied")]
    [InlineData(SyncStatus.Synced, "newer", "newer")]
    [InlineData(SyncStatus.Ignored, "newer", "applied")]
    public void Upsert_SyncedStateHash_FollowsTheKeepRules(SyncStatus storedStatus, string? incoming, string expected)
    {
        var stored = Row("u", "i", storedStatus);
        stored.SourceState.SyncedHash = "applied";
        _table.Upsert(stored);
        var next = Row("u", "i");
        next.SourceState.SourceHash = "source";
        next.SourceState.SyncedHash = incoming;

        _table.Upsert(next);

        var read = _table.GetByKey(("u", "i"))!;
        Assert.Equal(expected, read.SourceState.SyncedHash);
        Assert.Equal("source", read.SourceState.SourceHash);
    }

    /// <summary>
    /// A manager built without the settings reads every row as not negotiating with its source.
    /// True: a caller that never needs the setting gets a safe answer.
    /// False: a row would claim to negotiate when nothing said so, and merge against a base it does not have.
    /// </summary>
    [Fact]
    public void GetByKey_NoSettingsGiven_ReadsAsNotNegotiating()
    {
        _table.Upsert(Row("u", "i"));

        var read = _table.GetByKey(("u", "i"))!;

        Assert.False(read.NegotiateWithSource);
    }

    /// <summary>
    /// A read, a delete, and a status change by key touch only the row with both the user and the item.
    /// True: one user's row is changed and the other user's row of the same item is not.
    /// False: ignoring one user's history of a film would ignore it for every user.
    /// </summary>
    [Fact]
    public void KeyRoutes_UserAndItem_TouchOnlyThatRow()
    {
        _table.Upsert(Row("alice", "film"));
        _table.Upsert(Row("bob", "film"));
        _table.Upsert(Row("alice", "show"));

        _table.UpdateStatusByKey(("alice", "film"), SyncStatus.Errored, "failed");
        _table.DeleteByKey(("alice", "show"));

        Assert.Equal((SyncStatus.Errored, "failed", 1), (_table.GetByKey(("alice", "film"))!.Status, _table.GetByKey(("alice", "film"))!.Reason, _table.GetByKey(("alice", "film"))!.RetryCount));
        Assert.Equal(SyncStatus.Synced, _table.GetByKey(("bob", "film"))!.Status);
        Assert.Null(_table.GetByKey(("alice", "show")));
        Assert.Null(_table.GetByKey(("film", "alice")));
        Assert.Equal(2, _table.Count());
    }

    /// <summary>
    /// The rows for a local item are those of the given peer, across users.
    /// True: an agreed base is recorded on every user's row of that item for that peer.
    /// False: another peer's rows would take a base they never agreed to.
    /// </summary>
    [Fact]
    public void GetByLocalItem_PeerAndItem_ReturnsEveryUsersRowOfThatPeer()
    {
        _table.Upsert(Row("alice", "film", localItem: "local-film"));
        _table.Upsert(Row("bob", "film", localItem: "local-film"));
        _table.Upsert(Row("carol", "film", localItem: "local-film", serverKey: "other"));
        _table.Upsert(Row("alice", "show", localItem: "local-show"));

        var rows = _table.GetByLocalItem("server", "local-film");
        var none = _table.GetByLocalItem("server", "unknown");

        Assert.Equal(new[] { "alice|film", "bob|film" }, Keys(rows));
        Assert.Empty(none);
    }

    /// <summary>
    /// The rows for a user mapping are those with both its source user and its local user.
    /// True: removing one mapping removes that mapping's history rows only.
    /// False: a source user mapped to two local users would lose both sets.
    /// </summary>
    [Fact]
    public void GetByUserMapping_SourceAndLocalUser_ReturnsThatMappingsRows()
    {
        _table.Upsert(Row("alice", "film", localUser: "local-a"));
        _table.Upsert(Row("alice", "show", localUser: "local-a"));
        _table.Upsert(Row("bob", "film", localUser: "local-a"));
        _table.Upsert(Row("alice", "song", localUser: "local-b"));

        var rows = _table.GetByUserMapping("alice", "local-a");

        Assert.Equal(new[] { "alice|film", "alice|show" }, Keys(rows));
    }

    /// <summary>
    /// A search matches the item name or the source path, and the status and user filters narrow it.
    /// True: the History tab finds a row by title or path, for one user, in one status.
    /// False: a filter would be ignored and the tab would list every row.
    /// </summary>
    [Fact]
    public void SearchHistoryItemsPaginated_EachFilter_NarrowsTheRows()
    {
        _table.Upsert(Row("alice", "film", SyncStatus.Errored, name: "The Film"));
        _table.Upsert(Row("alice", "show", SyncStatus.Synced, name: "The Show"));
        _table.Upsert(Row("bob", "film2", SyncStatus.Errored, name: "Another"));

        var (byName, byNameTotal) = _table.SearchHistoryItemsPaginated("The ");
        var (byPath, _) = _table.SearchHistoryItemsPaginated("/src/film2");
        var (byStatus, _) = _table.SearchHistoryItemsPaginated(status: SyncStatus.Errored);
        var (byUser, _) = _table.SearchHistoryItemsPaginated(sourceUserId: "bob");
        var (all, allTotal) = _table.SearchHistoryItemsPaginated("  ", sourceUserId: " ");

        Assert.Equal(new[] { "alice|film", "alice|show" }, Keys(byName));
        Assert.Equal(2, byNameTotal);
        Assert.Equal(new[] { "bob|film2" }, Keys(byPath));
        Assert.Equal(new[] { "alice|film", "bob|film2" }, Keys(byStatus));
        Assert.Equal(new[] { "bob|film2" }, Keys(byUser));
        Assert.Equal(3, all.Count);
        Assert.Equal(3, allTotal);
    }

    /// <summary>
    /// Rows come back errored first, then queued, ignored, and synced, by item name within a status, and a page is cut from that order.
    /// True: failed history is at the top of the tab and pages do not overlap.
    /// False: errors would be buried, or a row would show on two pages.
    /// </summary>
    [Fact]
    public void SearchHistoryItemsPaginated_MixedStatuses_OrdersAndPages()
    {
        _table.Upsert(Row("u", "1", SyncStatus.Synced, name: "A"));
        _table.Upsert(Row("u", "2", SyncStatus.Ignored, name: "B"));
        _table.Upsert(Row("u", "3", SyncStatus.Queued, name: "C"));
        _table.Upsert(Row("u", "4", SyncStatus.Errored, name: "Z"));
        _table.Upsert(Row("u", "5", SyncStatus.Errored, name: "D"));

        var (firstPage, total) = _table.SearchHistoryItemsPaginated(skip: 0, take: 3);
        var (secondPage, _) = _table.SearchHistoryItemsPaginated(skip: 3, take: 3);

        Assert.Equal(new[] { "D", "Z", "C" }, firstPage.Select(r => r.ItemName).ToArray());
        Assert.Equal(new[] { "B", "A" }, secondPage.Select(r => r.ItemName).ToArray());
        Assert.Equal(5, total);
    }

    /// <summary>
    /// A page below 1 reads as the first page, and a page size outside 1 to 200 is pulled into that range.
    /// True: a bad request still gets a sane page.
    /// False: a page of 0 would give a negative offset, and a huge page size would load the whole table.
    /// </summary>
    [Theory]
    [InlineData(0, 2, 2)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 5000, 3)]
    [InlineData(2, 2, 1)]
    [InlineData(9, 2, 0)]
    public void Paginate_OutOfRangeRequest_IsPulledIntoRange(int page, int pageSize, int expectedRows)
    {
        _table.Upsert(Row("u", "1"));
        _table.Upsert(Row("u", "2"));
        _table.Upsert(Row("u", "3"));

        var result = _table.Paginate(new PaginationRequest { Page = page, PageSize = pageSize });

        Assert.Equal(expectedRows, result.Items.Count);
        Assert.Equal(3, result.TotalCount);
    }

    /// <summary>
    /// A missing row or request is refused.
    /// True: the caller fails at once with the argument named.
    /// False: a null would surface as an error from inside the SQL builder.
    /// </summary>
    [Fact]
    public void UpsertAndPaginate_NullArgument_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => _table.Upsert(null!));
        Assert.Throws<ArgumentNullException>(() => _table.Paginate(null!));
    }
}

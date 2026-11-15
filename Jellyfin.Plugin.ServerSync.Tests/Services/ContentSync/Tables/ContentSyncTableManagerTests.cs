using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.ContentSync.Tables;

/// <summary>
/// The Content sync table answers each question about paths, links, and pending work from exactly the rows that match it.
/// </summary>
public sealed class ContentSyncTableManagerTests : IDisposable
{
    private static readonly DateTime At = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly SyncTestDatabase _db = new("contenttable");
    private readonly ContentSyncTableManager _table;

    public ContentSyncTableManagerTests()
    {
        _table = new ContentSyncTableManager(_db.Provider, NullLogger<ContentSyncTableManager>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private SyncItem Add(
        string id,
        string? localPath = null,
        SyncStatus status = SyncStatus.Synced,
        StorageMode storage = StorageMode.Copy,
        PendingType? pending = null,
        long size = 100,
        string? serverKey = "server",
        string library = "lib",
        string? origin = null,
        int retries = 0,
        DateTime? statusDate = null)
    {
        var row = new SyncItem
        {
            SourceItemId = id,
            SourceLibraryId = library,
            LocalLibraryId = "local-lib",
            SourcePath = "/src/" + id + ".mkv",
            SourceSize = size,
            SourceCreateDate = At,
            LocalPath = localPath,
            Status = status,
            StatusDate = statusDate ?? At,
            StorageMode = storage,
            PendingType = pending,
            ServerKey = serverKey,
            OriginServerId = origin,
            RetryCount = retries
        };
        _table.Upsert(row);
        return _table.GetBySourceItemId(id)!;
    }

    private static string[] Ids(IEnumerable<SyncItem> rows) => rows.Select(r => r.SourceItemId).OrderBy(i => i, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The columns only the Content table has survive a write and a read.
    /// True: a row comes back as a link or a copy, kept or not, with its origin and companions.
    /// False: a link would read back as a copy and be downloaded over, or a kept copy would be removed.
    /// </summary>
    [Fact]
    public void Upsert_ContentColumns_RoundTrip()
    {
        var row = new SyncItem
        {
            SourceItemId = "a",
            SourceLibraryId = "lib",
            LocalLibraryId = "local-lib",
            SourcePath = "/src/a.mkv",
            SourceSize = long.MaxValue,
            SourceCreateDate = At,
            LocalItemId = "local-item",
            LocalPath = "/local/a.strm",
            Status = SyncStatus.Pending,
            StatusDate = At.AddHours(1),
            PendingType = PendingType.Replacement,
            CompanionFiles = "[\"a.srt\"]",
            StorageMode = StorageMode.Promoting,
            Kept = KeptReason.Hand,
            OriginServerId = "origin"
        };

        _table.Upsert(row);
        var read = _table.GetByKey("a")!;

        Assert.Equal((long.MaxValue, At, At.AddHours(1)), (read.SourceSize, read.SourceCreateDate, read.StatusDate));
        Assert.Equal(("local-item", "/local/a.strm", "[\"a.srt\"]", "origin"), (read.LocalItemId, read.LocalPath, read.CompanionFiles, read.OriginServerId));
        Assert.Equal((PendingType.Replacement, StorageMode.Promoting, KeptReason.Hand), (read.PendingType, read.StorageMode, read.Kept));
    }

    /// <summary>
    /// A missing row is refused on write.
    /// True: the caller fails at once with the argument named.
    /// False: a null row would surface as an error from inside the SQL builder.
    /// </summary>
    [Fact]
    public void Upsert_NullRow_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _table.Upsert(null!));
    }

    /// <summary>
    /// A row is found by the exact local path it records, and a path no row records finds nothing.
    /// True: a file on disk can be traced to the row that owns it.
    /// False: a file would be taken as untracked and removed, or matched to a neighbor's row.
    /// </summary>
    [Theory]
    [InlineData("/local/movies/film.mkv", "a")]
    [InlineData("/local/movies/film.mkv2", null)]
    [InlineData("/local/movies", null)]
    [InlineData("", null)]
    public void GetByLocalPath_ExactPathOnly_FindsTheRow(string path, string? expectedId)
    {
        Add("a", "/local/movies/film.mkv");
        Add("b", "/local/movies2/film.mkv");

        var found = _table.GetByLocalPath(path);

        Assert.Equal(expectedId, found?.SourceItemId);
    }

    /// <summary>
    /// The asker's own row is passed over, and another row with the same path is returned.
    /// True: a row learns that a different source item already claims its file.
    /// False: a row would see itself as the rival, or miss a real one and overwrite its file.
    /// </summary>
    [Fact]
    public void GetOtherByLocalPath_OwnRowAndRival_ReturnsOnlyTheRival()
    {
        Add("a", "/local/film.mkv");
        var aloneBefore = _table.GetOtherByLocalPath("/local/film.mkv", "a");
        Add("b", "/local/film.mkv");

        var rival = _table.GetOtherByLocalPath("/local/film.mkv", "a");

        Assert.Null(aloneBefore);
        Assert.Equal("b", rival!.SourceItemId);
    }

    /// <summary>
    /// With no asker, any row at the path is a collision, and a sibling path is not.
    /// True: a new file is held back only when its exact path is taken.
    /// False: two items would write one file, or a file would be blocked by a neighbor.
    /// </summary>
    [Theory]
    [InlineData("/local/film.mkv", true)]
    [InlineData("/local/film.mkv2", false)]
    [InlineData("/local/other.mkv", false)]
    public void CheckPathCollision_NoAsker_IsTrueOnlyForATakenPath(string path, bool expected)
    {
        Add("a", "/local/film.mkv", SyncStatus.Queued);

        var collides = _table.CheckPathCollision(path);

        Assert.Equal(expected, collides);
    }

    /// <summary>
    /// A row does not collide with itself.
    /// True: a row that is the only one at its path goes ahead.
    /// False: every row would wait on itself forever.
    /// </summary>
    [Fact]
    public void CheckPathCollision_OnlyTheAskersOwnRow_IsFalse()
    {
        Add("a", "/local/film.mkv");

        var collides = _table.CheckPathCollision("/local/film.mkv", "a");

        Assert.False(collides);
    }

    /// <summary>
    /// Of two unsynced rows that want one path, the one tracked first goes ahead and the later one waits.
    /// True: exactly one of two rivals proceeds.
    /// False: both would wait forever, or both would write the same file.
    /// </summary>
    [Fact]
    public void CheckPathCollision_TwoUnsyncedRivals_OnlyTheLaterOneWaits()
    {
        Add("first", "/local/film.mkv", SyncStatus.Queued);
        Add("second", "/local/film.mkv", SyncStatus.Queued);

        var firstWaits = _table.CheckPathCollision("/local/film.mkv", "first");
        var secondWaits = _table.CheckPathCollision("/local/film.mkv", "second");

        Assert.False(firstWaits);
        Assert.True(secondWaits);
    }

    /// <summary>
    /// A rival that is already synced holds the path even against a row tracked before it.
    /// True: a file on disk is never overwritten by another item's download.
    /// False: the earlier row would replace a file that belongs to a synced item.
    /// </summary>
    [Fact]
    public void CheckPathCollision_SyncedRival_HoldsThePathAgainstAnEarlierRow()
    {
        Add("first", "/local/film.mkv", SyncStatus.Queued);
        Add("second", "/local/film.mkv", SyncStatus.Synced);

        var firstWaits = _table.CheckPathCollision("/local/film.mkv", "first");

        Assert.True(firstWaits);
    }

    /// <summary>
    /// An asker with no row of its own waits for any row at the path.
    /// True: an item not yet tracked never takes a path a tracked item wants.
    /// False: an untracked item would race a tracked one for the file.
    /// </summary>
    [Fact]
    public void CheckPathCollision_AskerWithNoRow_WaitsForAnyRival()
    {
        Add("a", "/local/film.mkv", SyncStatus.Queued);

        var collides = _table.CheckPathCollision("/local/film.mkv", "not-tracked");

        Assert.True(collides);
    }

    /// <summary>
    /// Each link read returns the links and the rows turning into links that have a local path, and nothing else.
    /// True: the link cache holds every link and no copy.
    /// False: a copy would be streamed as a link, or a link would be missing and play nothing.
    /// </summary>
    [Fact]
    public void LinkReads_MixedRows_ReturnLinksWithAPathOnly()
    {
        Add("copy", "/local/copy.mkv", storage: StorageMode.Copy);
        Add("link", "/local/link.strm", storage: StorageMode.Link);
        Add("promoting", "/local/promoting.strm", storage: StorageMode.Promoting);
        Add("unkeeping", "/local/unkeeping.mkv", storage: StorageMode.Unkeeping);
        Add("pathless", null, storage: StorageMode.Link);

        var links = _table.GetLinks();
        var tried = _table.TryGetLinks();
        var strict = _table.GetLinksStrict();

        Assert.Equal(new[] { "link", "promoting" }, Ids(links));
        Assert.Equal(new[] { "link", "promoting" }, Ids(tried!));
        Assert.Equal(new[] { "link", "promoting" }, Ids(strict));
    }

    /// <summary>
    /// An empty table gives an empty list from each link read, never null.
    /// True: no links reads as "no links", which is different from a failed read.
    /// False: an empty table would be taken for a failure and the link cache would never settle.
    /// </summary>
    [Fact]
    public void LinkReads_EmptyTable_ReturnEmptyLists()
    {
        Assert.Empty(_table.GetLinks());
        Assert.Empty(_table.TryGetLinks()!);
        Assert.Empty(_table.GetLinksStrict());
    }

    /// <summary>
    /// The reader floor rises to the build that knows the storage in use, and stays where it is for copies.
    /// True: an older build sets a database with links aside instead of downloading over them.
    /// False: a downgrade would treat links as copies and replace them with full downloads.
    /// </summary>
    [Theory]
    [InlineData(StorageMode.Copy, SyncSchema.MinReaderVersion)]
    [InlineData(StorageMode.Link, SyncSchema.LinkReaderVersion)]
    [InlineData(StorageMode.Promoting, SyncSchema.LinkReaderVersion)]
    [InlineData(StorageMode.Unkeeping, SyncSchema.UnkeepReaderVersion)]
    public void RequireLinkAwareReaders_StorageInUse_SetsTheReaderFloor(StorageMode storage, int expectedFloor)
    {
        Add("a", "/local/a", storage: storage);

        _table.RequireLinkAwareReaders();

        Assert.Equal(expectedFloor, ReaderFloor());
    }

    /// <summary>
    /// The reader floor does not drop once the rows that raised it are gone.
    /// True: the floor is only ever raised by this call.
    /// False: removing the last link would reopen the database to builds that misread it.
    /// </summary>
    [Fact]
    public void RequireLinkAwareReaders_LinksRemoved_KeepsTheRaisedFloor()
    {
        Add("a", "/local/a", storage: StorageMode.Unkeeping);
        _table.RequireLinkAwareReaders();
        _table.Delete("a");

        _table.RequireLinkAwareReaders();

        Assert.Equal(SyncSchema.UnkeepReaderVersion, ReaderFloor());
    }

    /// <summary>
    /// A removed copy is recorded with its path and origin.
    /// True: the origin can later be told its copy is gone.
    /// False: a removed copy would leave no trace and the origin would never hear of it.
    /// </summary>
    [Fact]
    public void NoteRemovedCopy_PathAndOrigin_RecordsOneRow()
    {
        _table.NoteRemovedCopy("/local/film.mkv", "origin-id");

        Assert.Equal(1L, _db.Scalar("SELECT COUNT(*) FROM RemovedItems WHERE Path = '/local/film.mkv' AND OriginServerId = 'origin-id' AND ItemId = ''"));
    }

    /// <summary>
    /// A removal with no path or no origin records nothing.
    /// True: only removals that can be reported are kept.
    /// False: the log would fill with rows nobody can be told about.
    /// </summary>
    [Theory]
    [InlineData(null, "origin-id")]
    [InlineData("", "origin-id")]
    [InlineData("/local/film.mkv", null)]
    [InlineData("/local/film.mkv", "")]
    public void NoteRemovedCopy_MissingPathOrOrigin_RecordsNothing(string? path, string? origin)
    {
        _table.NoteRemovedCopy(path, origin);

        Assert.Equal(0L, _db.Scalar("SELECT COUNT(*) FROM RemovedItems"));
    }

    /// <summary>
    /// Only copies with a local path and a named origin are returned.
    /// True: the origins told about a copy are the ones that have one.
    /// False: a link, or a copy with no origin, would be reported to a server that never sent it.
    /// </summary>
    [Fact]
    public void TryGetCopiesWithOrigin_MixedRows_ReturnsCopiesThatNameAnOrigin()
    {
        Add("copy", "/local/copy.mkv", origin: "origin");
        Add("no-origin", "/local/no-origin.mkv");
        Add("empty-origin", "/local/empty.mkv", origin: string.Empty);
        Add("link", "/local/link.strm", storage: StorageMode.Link, origin: "origin");
        Add("pathless", null, origin: "origin");

        var copies = _table.TryGetCopiesWithOrigin();

        Assert.Equal(new[] { "copy" }, Ids(copies!));
    }

    /// <summary>
    /// The links filter counts a row turning into a link with the links and a row turning back with the copies.
    /// True: the Links and Copies views each show a row under the storage it is leaving.
    /// False: a row changing storage would vanish from both views or show in both.
    /// </summary>
    [Fact]
    public void SearchPaginated_ServerKey_ListsOnlyThatServersRows()
    {
        Add("a1", "/local/a1.mkv", serverKey: "a");
        Add("a2", "/local/a2.strm", storage: StorageMode.Link, serverKey: "a");
        Add("b1", "/local/b1.mkv", serverKey: "b");

        var (items, total) = _table.SearchPaginated(null, serverKey: "a");
        var (links, linkTotal) = _table.SearchPaginated(null, links: true, serverKey: "a");

        Assert.Equal(new[] { "a1", "a2" }, Ids(items));
        Assert.Equal(2, total);
        Assert.Equal(new[] { "a2" }, Ids(links));
        Assert.Equal(1, linkTotal);
        Assert.Equal(0, _table.SearchPaginated(null, serverKey: "none").TotalCount);
    }

    /// <summary>
    /// The Storage filter splits rows into links and copies, with a row changing storage under the one it leaves.
    /// </summary>
    [Theory]
    [InlineData(true, new[] { "link", "promoting" })]
    [InlineData(false, new[] { "copy", "unkeeping" })]
    [InlineData(null, new[] { "copy", "link", "promoting", "unkeeping" })]
    public void SearchPaginated_LinksFilter_SplitsRowsByStorage(bool? links, string[] expected)
    {
        Add("copy", "/local/copy.mkv", storage: StorageMode.Copy);
        Add("link", "/local/link.strm", storage: StorageMode.Link);
        Add("promoting", "/local/promoting.strm", storage: StorageMode.Promoting);
        Add("unkeeping", "/local/unkeeping.mkv", storage: StorageMode.Unkeeping);

        var (items, total) = _table.SearchPaginated(null, links: links);

        Assert.Equal(expected, Ids(items));
        Assert.Equal(expected.Length, total);
    }

    /// <summary>
    /// A search matches text in the source path or the local path, and a pending type narrows it further.
    /// True: an operator finds a row by either path and can list one kind of pending work.
    /// False: a row known only by its local name could not be found.
    /// </summary>
    [Fact]
    public void SearchPaginated_TextAndPendingType_NarrowTheRows()
    {
        Add("alpha", "/local/renamed-here.mkv", SyncStatus.Pending, pending: PendingType.Download);
        Add("beta", "/local/beta.mkv", SyncStatus.Pending, pending: PendingType.Deletion);
        Add("gamma", "/local/gamma.mkv");

        var (byLocal, _) = _table.SearchPaginated("renamed-here");
        var (bySource, _) = _table.SearchPaginated("/src/beta");
        var (deletions, deletionTotal) = _table.SearchPaginated(null, SyncStatus.Pending, PendingType.Deletion);

        Assert.Equal(new[] { "alpha" }, Ids(byLocal));
        Assert.Equal(new[] { "beta" }, Ids(bySource));
        Assert.Equal(new[] { "beta" }, Ids(deletions));
        Assert.Equal(1, deletionTotal);
    }

    /// <summary>
    /// Rows come back with pending first, then errored, queued, deleting, ignored, and synced, and by local path within a status.
    /// True: the work that needs an operator is at the top of the Sync tab.
    /// False: pending approvals would be buried under thousands of synced rows.
    /// </summary>
    [Fact]
    public void SearchPaginated_MixedStatuses_OrdersByStatusPriorityThenPath()
    {
        Add("synced", "/local/a.mkv", SyncStatus.Synced);
        Add("ignored", "/local/b.mkv", SyncStatus.Ignored);
        Add("deleting", "/local/c.mkv", SyncStatus.Deleting);
        Add("queued", "/local/d.mkv", SyncStatus.Queued);
        Add("errored", "/local/e.mkv", SyncStatus.Errored);
        Add("pending-z", "/local/z.mkv", SyncStatus.Pending, pending: PendingType.Download);
        Add("pending-f", "/local/f.mkv", SyncStatus.Pending, pending: PendingType.Download);

        var (items, _) = _table.SearchPaginated(null);

        Assert.Equal(new[] { "pending-f", "pending-z", "errored", "queued", "deleting", "ignored", "synced" }, items.Select(i => i.SourceItemId).ToArray());
    }

    /// <summary>
    /// A page holds the rows after the skip, up to the take, while the total counts every match.
    /// True: the pager shows the right rows and the right page count.
    /// False: pages would repeat or drop rows, or the pager would show the page size as the total.
    /// </summary>
    [Fact]
    public void SearchPaginated_SkipAndTake_ReturnThatPageAndTheFullTotal()
    {
        for (var i = 0; i < 5; i++)
        {
            Add("item" + i, "/local/" + i + ".mkv");
        }

        var (items, total) = _table.SearchPaginated(null, skip: 3, take: 10);
        var (past, pastTotal) = _table.SearchPaginated(null, skip: 50, take: 10);

        Assert.Equal(new[] { "item3", "item4" }, items.Select(i => i.SourceItemId).ToArray());
        Assert.Equal(5, total);
        Assert.Empty(past);
        Assert.Equal(5, pastTotal);
    }

    /// <summary>
    /// Kept copies are the copies of one server's given libraries that are synced, queued, or errored.
    /// True: the Permanent list shows what this server really holds for those Link libraries.
    /// False: another server's rows, links, or rows awaiting deletion would be listed as kept.
    /// </summary>
    [Fact]
    public void SearchKeptCopies_MixedRows_ReturnsThatServersHeldCopies()
    {
        Add("synced", "/local/b.mkv", SyncStatus.Synced);
        Add("queued", "/local/a.mkv", SyncStatus.Queued);
        Add("errored", "/local/c.mkv", SyncStatus.Errored);
        Add("unkeeping", "/local/d.mkv", SyncStatus.Synced, StorageMode.Unkeeping);
        Add("pending", "/local/e.mkv", SyncStatus.Pending, pending: PendingType.Download);
        Add("ignored", "/local/f.mkv", SyncStatus.Ignored);
        Add("deleting", "/local/g.mkv", SyncStatus.Deleting);
        Add("link", "/local/h.strm", storage: StorageMode.Link);
        Add("other-server", "/local/i.mkv", serverKey: "other");
        Add("other-library", "/local/j.mkv", library: "lib2");

        var (items, total) = _table.SearchKeptCopies("server", new[] { "lib", "lib-unused" }, null, 0, 50);

        Assert.Equal(new[] { "queued", "synced", "errored", "unkeeping" }, items.Select(i => i.SourceItemId).ToArray());
        Assert.Equal(4, total);
    }

    /// <summary>
    /// A search and a page narrow the kept copies while the total counts every match of the search.
    /// True: the Permanent list can be searched and paged.
    /// False: a search would return every copy, or the pager would miscount.
    /// </summary>
    [Fact]
    public void SearchKeptCopies_SearchAndPage_NarrowTheList()
    {
        Add("one", "/local/show-1.mkv");
        Add("two", "/local/show-2.mkv");
        Add("three", "/local/show-3.mkv");
        Add("film", "/local/film.mkv");

        var (items, total) = _table.SearchKeptCopies("server", new[] { "lib" }, "show", 1, 1);

        Assert.Equal(new[] { "two" }, items.Select(i => i.SourceItemId).ToArray());
        Assert.Equal(3, total);
    }

    /// <summary>
    /// No libraries gives no rows, and a missing list is refused.
    /// True: a server with no Link library has no Permanent list, at no cost.
    /// False: an empty list would build broken SQL and fail the page.
    /// </summary>
    [Fact]
    public void SearchKeptCopies_NoLibraries_ReturnsNothing()
    {
        Add("a", "/local/a.mkv");

        var (items, total) = _table.SearchKeptCopies("server", Array.Empty<string>(), null, 0, 50);

        Assert.Empty(items);
        Assert.Equal(0, total);
        Assert.Throws<ArgumentNullException>(() => _table.SearchKeptCopies("server", null!, null, 0, 50));
    }

    /// <summary>
    /// A page below 1 reads as the first page, and a page size outside 1 to 200 is pulled into that range.
    /// True: a bad request still gets a sane page.
    /// False: a page of 0 would give a negative offset, and a huge page size would load the whole table.
    /// </summary>
    [Theory]
    [InlineData(0, 2, 2)]
    [InlineData(-5, 2, 2)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 5000, 3)]
    [InlineData(2, 2, 1)]
    public void Paginate_OutOfRangeRequest_IsPulledIntoRange(int page, int pageSize, int expectedRows)
    {
        Add("a", "/local/a.mkv");
        Add("b", "/local/b.mkv");
        Add("c", "/local/c.mkv");

        var result = _table.Paginate(new PaginationRequest { Page = page, PageSize = pageSize });

        Assert.Equal(expectedRows, result.Items.Count);
        Assert.Equal(3, result.TotalCount);
    }

    /// <summary>
    /// The request's status and search reach the query, and a missing request is refused.
    /// True: the generic list path filters like the Sync tab does.
    /// False: a filtered request would return every row.
    /// </summary>
    [Fact]
    public void Paginate_StatusAndSearch_FilterTheRows()
    {
        Add("a", "/local/show-a.mkv", SyncStatus.Errored);
        Add("b", "/local/show-b.mkv", SyncStatus.Synced);
        Add("c", "/local/film-c.mkv", SyncStatus.Errored);

        var result = _table.Paginate(new PaginationRequest { StatusFilter = SyncStatus.Errored, SearchTerm = "show" });

        Assert.Equal(new[] { "a" }, Ids(result.Items));
        Assert.Equal(1, result.TotalCount);
        Assert.Throws<ArgumentNullException>(() => _table.Paginate(null!));
    }

    /// <summary>
    /// Pending rows are counted by type, and rows in another status are left out.
    /// True: the dashboard's approval counts match the rows waiting.
    /// False: a queued or synced row would inflate the count of approvals.
    /// </summary>
    [Fact]
    public void GetPendingCounts_MixedRows_CountsPendingByType()
    {
        Add("d1", status: SyncStatus.Pending, pending: PendingType.Download);
        Add("d2", status: SyncStatus.Pending, pending: PendingType.Download);
        Add("r1", status: SyncStatus.Pending, pending: PendingType.Replacement);
        Add("untyped", status: SyncStatus.Pending);
        Add("queued", status: SyncStatus.Queued, pending: PendingType.Deletion);

        var counts = _table.GetPendingCounts();

        Assert.Equal(2, counts[PendingType.Download]);
        Assert.Equal(1, counts[PendingType.Replacement]);
        Assert.False(counts.ContainsKey(PendingType.Deletion));
    }

    /// <summary>
    /// The sizes sum by kind, and the total is downloads plus replacements plus queued, less deletions.
    /// True: the dashboard shows how much space the waiting work will take.
    /// False: the space estimate would be wrong and a full disk would come as a surprise.
    /// </summary>
    [Fact]
    public void GetPendingSizes_MixedRows_SumsEachKindAndTheNetTotal()
    {
        Add("d1", status: SyncStatus.Pending, pending: PendingType.Download, size: 100);
        Add("d2", status: SyncStatus.Pending, pending: PendingType.Download, size: 50);
        Add("r1", status: SyncStatus.Pending, pending: PendingType.Replacement, size: 30);
        Add("x1", status: SyncStatus.Pending, pending: PendingType.Deletion, size: 20);
        Add("q1", status: SyncStatus.Queued, size: 7);
        Add("s1", status: SyncStatus.Synced, size: 1000);

        var sizes = _table.GetPendingSizes();

        Assert.Equal((150L, 30L, 20L, 7L), (sizes["PendingDownload"], sizes["PendingReplacement"], sizes["PendingDeletion"], sizes["Queued"]));
        Assert.Equal(167L, sizes["Total"]);
    }

    /// <summary>
    /// A net total below zero reads as zero, and an empty table reads as zero for every kind.
    /// True: the dashboard never shows negative space.
    /// False: deletions larger than downloads would show a negative size.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetPendingSizes_DeletionsOutweighOrNothingWaits_TotalIsZero(bool withDeletion)
    {
        if (withDeletion)
        {
            Add("x1", status: SyncStatus.Pending, pending: PendingType.Deletion, size: 500);
            Add("d1", status: SyncStatus.Pending, pending: PendingType.Download, size: 100);
        }

        var sizes = _table.GetPendingSizes();

        Assert.Equal(0L, sizes["Total"]);
        Assert.Equal(5, sizes.Count);
    }

    /// <summary>
    /// The rows returned are errored, out of tries, errored before the given time, and not flagged by the link check.
    /// True: maintenance reports exactly the rows that have been stuck long enough.
    /// False: a row still retrying, a fresh failure, or a flagged link would be reported as stuck.
    /// </summary>
    [Fact]
    public void GetErroredCopiesSince_MixedRows_ReturnsOnlyTheLongStuckOnes()
    {
        var cutoff = At;
        Add("stuck", status: SyncStatus.Errored, retries: 3, statusDate: At.AddDays(-2));
        Add("past-max", status: SyncStatus.Errored, retries: 9, statusDate: At.AddDays(-2));
        Add("retrying", status: SyncStatus.Errored, retries: 2, statusDate: At.AddDays(-2));
        Add("fresh", status: SyncStatus.Errored, retries: 3, statusDate: At.AddMinutes(1));
        Add("flagged-link", status: SyncStatus.Errored, retries: 1000000, statusDate: At.AddDays(-2));
        Add("synced", status: SyncStatus.Synced, retries: 3, statusDate: At.AddDays(-2));

        var rows = _table.GetErroredCopiesSince(3, cutoff);

        Assert.Equal(new[] { "past-max", "stuck" }, Ids(rows));
    }

    /// <summary>
    /// The pending type is stored only with a Pending status and cleared by any other.
    /// True: a row that left Pending no longer shows as awaiting an approval.
    /// False: an approved download would still count as pending approval.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Pending, PendingType.Deletion)]
    [InlineData(SyncStatus.Queued, null)]
    [InlineData(SyncStatus.Synced, null)]
    [InlineData(SyncStatus.Ignored, null)]
    public void UpdateStatus_PendingType_IsKeptOnlyWhilePending(SyncStatus status, PendingType? expected)
    {
        Add("a", "/local/a.mkv", SyncStatus.Pending, pending: PendingType.Download);

        _table.UpdateStatus("a", status, PendingType.Deletion);

        Assert.Equal(expected, _table.GetByKey("a")!.PendingType);
    }

    /// <summary>
    /// The local details given with a status are recorded, and the ones left out keep their values.
    /// True: a finished download records where it landed without losing what the row already knew.
    /// False: a plain status change would blank the row's local path and size.
    /// </summary>
    [Fact]
    public void UpdateStatus_OptionalDetails_AreWrittenOnlyWhenGiven()
    {
        Add("a", "/local/a.mkv", SyncStatus.Queued, size: 100);
        _table.UpdateStatus("a", SyncStatus.Synced, localItemId: "item", localPath: "/local/new.mkv", sourceSize: 250, companionFiles: "[\"a.srt\"]");
        var written = _table.GetByKey("a")!;

        _table.UpdateStatus("a", SyncStatus.Errored, errorMessage: "failed");
        var kept = _table.GetByKey("a")!;

        Assert.Equal(("item", "/local/new.mkv", 250L, "[\"a.srt\"]"), (written.LocalItemId, written.LocalPath, written.SourceSize, written.CompanionFiles));
        Assert.Equal(("item", "/local/new.mkv", 250L, "[\"a.srt\"]"), (kept.LocalItemId, kept.LocalPath, kept.SourceSize, kept.CompanionFiles));
        Assert.Equal((SyncStatus.Errored, "failed"), (kept.Status, kept.Reason));
    }

    /// <summary>
    /// A status change for an id no row has changes nothing.
    /// True: a late update for a pruned row is harmless.
    /// False: an unknown id would throw, or touch another row.
    /// </summary>
    [Fact]
    public void UpdateStatus_UnknownId_ChangesNothing()
    {
        Add("a", "/local/a.mkv", SyncStatus.Queued);

        _table.UpdateStatus("missing", SyncStatus.Errored, errorMessage: "failed");

        Assert.Equal(SyncStatus.Queued, _table.GetByKey("a")!.Status);
        Assert.Equal(1, _table.Count());
    }

    /// <summary>
    /// Deleting by source item removes that row and leaves the rest.
    /// True: a pruned item's row goes and its neighbors stay.
    /// False: pruning one item would drop others, or leave the row behind.
    /// </summary>
    [Fact]
    public void Delete_OneSourceItem_RemovesOnlyThatRow()
    {
        Add("a", "/local/a.mkv");
        Add("ab", "/local/ab.mkv");

        _table.Delete("a");

        Assert.Null(_table.GetBySourceItemId("a"));
        Assert.NotNull(_table.GetBySourceItemId("ab"));
    }

    private int? ReaderFloor()
    {
        using var connection = new SqliteConnection($"Data Source={_db.FilePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return DatabaseSchema.GetMinReaderVersion(connection);
    }
}

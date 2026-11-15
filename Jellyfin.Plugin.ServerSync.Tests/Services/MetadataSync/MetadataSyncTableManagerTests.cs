using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.MetadataSync;

/// <summary>
/// The Metadata sync table keeps one row for each source library and item, and queueing a row makes every category apply again.
/// </summary>
public sealed class MetadataSyncTableManagerTests : IDisposable
{
    private static readonly DateTime At = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly SyncTestDatabase _db = new("metadatatable");
    private readonly MetadataSyncTableManager _table;

    public MetadataSyncTableManagerTests()
    {
        _table = new MetadataSyncTableManager(_db.Provider, NullLogger<MetadataSyncTableManager>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static MetadataSyncItem Row(string library, string item, SyncStatus status = SyncStatus.Synced, string? name = null, bool synced = false)
    {
        var row = new MetadataSyncItem
        {
            SourceLibraryId = library,
            LocalLibraryId = "local-" + library,
            SourceItemId = item,
            LocalItemId = "local-" + item,
            ItemName = name ?? item,
            SourcePath = "/src/" + library + "/" + item + ".mkv",
            Status = status,
            StatusDate = At
        };
        if (synced)
        {
            row.Metadata.SyncedHash = "m";
            row.Images.SyncedHash = "i";
            row.People.SyncedHash = "p";
            row.Studios.SyncedHash = "s";
        }

        return row;
    }

    private MetadataSyncItem Add(string library, string item, SyncStatus status = SyncStatus.Synced, string? name = null, bool synced = false)
    {
        _table.Upsert(Row(library, item, status, name, synced));
        return _table.GetByKey((library, item))!;
    }

    private static string?[] SyncedHashes(MetadataSyncItem row)
        => new[] { row.Metadata.SyncedHash, row.Images.SyncedHash, row.People.SyncedHash, row.Studios.SyncedHash };

    private static string[] Keys(IEnumerable<MetadataSyncItem> rows)
        => rows.Select(r => r.SourceLibraryId + "|" + r.SourceItemId).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Every category's source value, local value, and source hash survive a write and a read, with the item's type and folder flag.
    /// True: what a scan stored is what the apply step and the dashboard read.
    /// False: a category would read back empty, or under another category's column.
    /// </summary>
    [Fact]
    public void Upsert_EveryCategory_RoundTrips()
    {
        var row = Row("lib", "item");
        row.ItemType = "Series";
        row.IsFolder = true;
        row.LocalPath = "/local/item";
        row.Metadata.UpdateSource("{\"Name\":\"A\"}");
        row.Metadata.Local = "{\"Name\":\"B\"}";
        row.Images.UpdateSource("{\"Primary\":[{\"ImageType\":\"Primary\",\"Size\":10}]}");
        row.Images.Local = "{}";
        row.People.UpdateSource("[{\"Name\":\"Jane\",\"Type\":\"Actor\"}]");
        row.People.Local = "[]";
        row.Studios.UpdateSource("[\"S1\"]");
        row.Studios.Local = "[\"S2\"]";

        _table.Upsert(row);
        var read = _table.GetByKey(("lib", "item"))!;

        Assert.Equal(("Series", true, "/local/item", "local-lib", "local-item"), (read.ItemType, read.IsFolder, read.LocalPath, read.LocalLibraryId, read.LocalItemId));
        Assert.Equal((row.Metadata.Source, row.Metadata.Local, row.Metadata.SourceHash), (read.Metadata.Source, read.Metadata.Local, read.Metadata.SourceHash));
        Assert.Equal((row.Images.Source, row.Images.Local, row.Images.SourceHash), (read.Images.Source, read.Images.Local, read.Images.SourceHash));
        Assert.Equal((row.People.Source, row.People.Local, row.People.SourceHash), (read.People.Source, read.People.Local, read.People.SourceHash));
        Assert.Equal((row.Studios.Source, row.Studios.Local, row.Studios.SourceHash), (read.Studios.Source, read.Studios.Local, read.Studios.SourceHash));
        Assert.NotNull(read.Metadata.SourceHash);
    }

    /// <summary>
    /// A write that carries no synced hash keeps each stored one, and a write that carries one replaces it.
    /// True: a scan does not erase the record of what was applied, and an apply updates it.
    /// False: every scan would forget what was applied and queue the whole library again.
    /// </summary>
    [Fact]
    public void Upsert_SyncedHashes_AreKeptUnlessGiven()
    {
        Add("lib", "item", synced: true);
        _table.Upsert(Row("lib", "item"));
        var afterScan = SyncedHashes(_table.GetByKey(("lib", "item"))!);
        var applied = Row("lib", "item");
        applied.Images.SyncedHash = "i2";

        _table.Upsert(applied);

        Assert.Equal(new[] { "m", "i", "p", "s" }, afterScan);
        Assert.Equal(new[] { "m", "i2", "p", "s" }, SyncedHashes(_table.GetByKey(("lib", "item"))!));
    }

    /// <summary>
    /// A read, a delete, and a status change by key touch only the row with both the library and the item.
    /// True: an item mapped through two libraries has two independent rows.
    /// False: ignoring an item in one library would ignore it in the other.
    /// </summary>
    [Fact]
    public void KeyRoutes_LibraryAndItem_TouchOnlyThatRow()
    {
        Add("movies", "item");
        Add("kids", "item");
        Add("movies", "other");

        _table.UpdateStatusByKey(("movies", "item"), SyncStatus.Ignored, "by hand");
        _table.DeleteByKey(("movies", "other"));

        Assert.Equal((SyncStatus.Ignored, "by hand"), (_table.GetByKey(("movies", "item"))!.Status, _table.GetByKey(("movies", "item"))!.Reason));
        Assert.Equal(SyncStatus.Synced, _table.GetByKey(("kids", "item"))!.Status);
        Assert.Null(_table.GetByKey(("movies", "other")));
        Assert.Null(_table.GetByKey(("item", "movies")));
        Assert.Equal(2, _table.Count());
    }

    /// <summary>
    /// Queueing every row of a status clears their synced hashes and leaves rows in other statuses alone.
    /// True: Retry All makes each errored row apply every category again.
    /// False: a queued row would be skipped as already applied and the retry would do nothing.
    /// </summary>
    [Fact]
    public void QueueAllWithStatus_ErroredRows_ClearTheirSyncedHashes()
    {
        Add("lib", "errored", SyncStatus.Errored, synced: true);
        Add("lib", "synced", SyncStatus.Synced, synced: true);

        var queued = _table.QueueAllWithStatus(SyncStatus.Errored);

        Assert.Equal(1, queued);
        Assert.Equal(SyncStatus.Queued, _table.GetByKey(("lib", "errored"))!.Status);
        Assert.Equal(new string?[] { null, null, null, null }, SyncedHashes(_table.GetByKey(("lib", "errored"))!));
        Assert.Equal(new[] { "m", "i", "p", "s" }, SyncedHashes(_table.GetByKey(("lib", "synced"))!));
    }

    /// <summary>
    /// Queueing rows by id clears their synced hashes, and any other status keeps them.
    /// True: Queue makes the chosen rows apply again, and Ignore keeps their record for later.
    /// False: a queued row would be skipped, or an ignored row would lose what it had applied.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Queued, true)]
    [InlineData(SyncStatus.Ignored, false)]
    [InlineData(SyncStatus.Errored, false)]
    public void BatchUpdateStatusByIdsWithDetails_Status_ClearsHashesOnlyWhenQueueing(SyncStatus status, bool cleared)
    {
        var row = Add("lib", "item", synced: true);

        var (updated, notFound) = _table.BatchUpdateStatusByIdsWithDetails(new[] { row.Id }, status, "why");

        var read = _table.GetByKey(("lib", "item"))!;
        Assert.Equal(1, updated);
        Assert.Empty(notFound);
        Assert.Equal(status, read.Status);
        Assert.Equal(cleared ? new string?[] { null, null, null, null } : new[] { "m", "i", "p", "s" }, SyncedHashes(read));
    }

    /// <summary>
    /// Ids that match no row are reported and the rest are updated, and no ids changes nothing.
    /// True: the dashboard can say which chosen rows were gone.
    /// False: a stale selection would be reported as fully applied.
    /// </summary>
    [Fact]
    public void BatchUpdateStatusByIdsWithDetails_UnknownIds_AreReported()
    {
        var row = Add("lib", "item");
        var other = Add("lib", "other");

        var (updated, notFound) = _table.BatchUpdateStatusByIdsWithDetails(new[] { row.Id, 99999L, long.MaxValue }, SyncStatus.Ignored);
        var (noneUpdated, noneMissing) = _table.BatchUpdateStatusByIdsWithDetails(Array.Empty<long>(), SyncStatus.Ignored);

        Assert.Equal(1, updated);
        Assert.Equal(new[] { 99999L, long.MaxValue }, notFound.ToArray());
        Assert.Equal((0, 0), (noneUpdated, noneMissing.Count));
        Assert.Equal(SyncStatus.Synced, _table.GetById(other.Id)!.Status);
        Assert.Throws<ArgumentNullException>(() => _table.BatchUpdateStatusByIdsWithDetails(null!, SyncStatus.Ignored));
    }

    /// <summary>
    /// A search matches the item name or the source path, and the status and library filters narrow it.
    /// True: the Metadata tab finds a row by title or path, in one library, in one status.
    /// False: a filter would be ignored and the tab would list every row.
    /// </summary>
    [Fact]
    public void SearchMetadataSyncItemsPaginated_EachFilter_NarrowsTheRows()
    {
        Add("movies", "film", SyncStatus.Errored, name: "The Film");
        Add("movies", "other", SyncStatus.Synced, name: "Other");
        Add("shows", "show", SyncStatus.Errored, name: "The Show");

        var (byName, byNameTotal) = _table.SearchMetadataSyncItemsPaginated("The ");
        var (byPath, _) = _table.SearchMetadataSyncItemsPaginated("/src/shows/");
        var (byStatus, _) = _table.SearchMetadataSyncItemsPaginated(status: SyncStatus.Synced);
        var (byLibrary, libraryTotal) = _table.SearchMetadataSyncItemsPaginated(sourceLibraryId: "movies");

        Assert.Equal(new[] { "movies|film", "shows|show" }, Keys(byName));
        Assert.Equal(2, byNameTotal);
        Assert.Equal(new[] { "shows|show" }, Keys(byPath));
        Assert.Equal(new[] { "movies|other" }, Keys(byStatus));
        Assert.Equal(new[] { "movies|film", "movies|other" }, Keys(byLibrary));
        Assert.Equal(2, libraryTotal);
    }

    /// <summary>
    /// Rows come back errored first, then queued, ignored, and synced, by item name within a status, and a page is cut from that order.
    /// True: failed metadata is at the top of the tab and pages do not overlap.
    /// False: errors would be buried, or a row would show on two pages.
    /// </summary>
    [Fact]
    public void SearchMetadataSyncItemsPaginated_MixedStatuses_OrdersAndPages()
    {
        Add("lib", "1", SyncStatus.Synced, name: "A");
        Add("lib", "2", SyncStatus.Ignored, name: "B");
        Add("lib", "3", SyncStatus.Queued, name: "C");
        Add("lib", "4", SyncStatus.Errored, name: "Z");
        Add("lib", "5", SyncStatus.Errored, name: "D");

        var (firstPage, total) = _table.SearchMetadataSyncItemsPaginated(skip: 0, take: 3);
        var (secondPage, _) = _table.SearchMetadataSyncItemsPaginated(skip: 3, take: 3);

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
        Add("lib", "1");
        Add("lib", "2");
        Add("lib", "3");

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

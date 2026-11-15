using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The Content routes list and count rows as stored, change only the rows named, and delete a file only inside a mapped library folder.
/// </summary>
public sealed class ConfigurationControllerContentSyncTests : IDisposable
{
    private const string CachedLink = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DownloadTask = "ServerSyncDownloadContent";

    private readonly ControllerTestHarness _harness = new();
    private readonly string _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationControllerContentSyncTests"/> class, with one peer whose library maps to a temp folder.
    /// </summary>
    public ConfigurationControllerContentSyncTests()
    {
        _library = Path.Combine(_harness.TempDir, "library");
        Directory.CreateDirectory(_library);
        var peer = _harness.AddPeer();
        peer.ExternalUrl = "https://peer.example";
        peer.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "lib", SourceLibraryName = "Source Films", LocalLibraryId = "local", LocalLibraryName = "Films", SourceRootPath = "/src", LocalRootPath = _library });
    }

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// A listed row carries its library names and its server's name, ID, and browser URL from the settings.
    /// True: the Sync tab shows where each row came from and links to the right server.
    /// False: rows would show bare IDs, or link to the address only this server can reach.
    /// </summary>
    [Fact]
    public void GetSyncItems_Row_CarriesItsLibraryAndServerFromTheSettings()
    {
        _harness.AddContent("a", SyncStatus.Errored, change: r => r.Reason = "disk full");

        var page = Page(_harness.Configuration().GetSyncItems(_harness.Content));

        var row = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(("Source Films", "Films"), (row.SourceLibraryName, row.LocalLibraryName));
        Assert.Equal(("Peer", ControllerTestHarness.PeerId, "https://peer.example"), (row.ServerName, row.SourceServerId, row.SourceServerUrl));
        Assert.Equal(("Errored", "disk full", "Copy", null), (row.Status, row.ErrorMessage, row.StorageMode, row.Kept));
        Assert.Equal(FormatUtilities.FormatBytes(100), row.SourceSizeFormatted);
        Assert.False(row.Cached);
    }

    /// <summary>
    /// The page size is held between 1 and 200 and a negative skip starts at the first row.
    /// True: a caller cannot ask for an unbounded page, and a bad skip still lists rows.
    /// False: one request could load the whole table, or a negative skip would fail the query.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(-10, 2, 2)]
    [InlineData(int.MinValue, int.MaxValue, 3)]
    [InlineData(2, 50, 1)]
    [InlineData(3, 50, 0)]
    public void GetSyncItems_Paging_IsClamped(int skip, int take, int expectedRows)
    {
        _harness.AddContent("a");
        _harness.AddContent("b");
        _harness.AddContent("c");

        var page = Page(_harness.Configuration().GetSyncItems(_harness.Content, skip: skip, take: take));

        Assert.Equal(expectedRows, page.Items.Count);
        Assert.Equal(3, page.TotalCount);
    }

    /// <summary>
    /// A status or pending type filter applies only when it names a real value exactly, and anything else lists every row.
    /// True: a known filter narrows the list, and a value the page should never send does not hide rows.
    /// False: an unknown filter would show an empty table, or be read as some other status.
    /// </summary>
    [Theory]
    [InlineData("Errored", null, 1)]
    [InlineData("Pending", "Deletion", 1)]
    [InlineData("Pending", "Download", 0)]
    [InlineData(null, "Deletion", 1)]
    [InlineData("errored", null, 3)]
    [InlineData("Bogus", "Bogus", 3)]
    [InlineData("99", "99", 3)]
    [InlineData("", "", 3)]
    public void GetSyncItems_StatusAndPendingType_FilterOnlyOnExactNames(string? status, string? pendingType, int expectedRows)
    {
        _harness.AddContent("a", SyncStatus.Errored);
        _harness.AddContent("b", SyncStatus.Pending, change: r => r.PendingType = PendingType.Deletion);
        _harness.AddContent("c", SyncStatus.Synced);

        var page = Page(_harness.Configuration().GetSyncItems(_harness.Content, status: status, pendingType: pendingType));

        Assert.Equal(expectedRows, page.TotalCount);
    }

    /// <summary>
    /// The Storage filter lists links or copies, whatever its case, and anything else lists both.
    /// True: the Link and Copy views each show their own rows.
    /// False: a copy would be listed among links, or the other way round.
    /// </summary>
    [Theory]
    [InlineData("Link", "link")]
    [InlineData("LINK", "link")]
    [InlineData("copy", "copy")]
    [InlineData("Both", "copy,link")]
    [InlineData(null, "copy,link")]
    public void GetSyncItems_Storage_ListsLinksOrCopies(string? storage, string expectedIds)
    {
        _harness.AddContent("copy");
        _harness.AddContent("link", change: r => r.StorageMode = StorageMode.Link);

        var page = Page(_harness.Configuration().GetSyncItems(_harness.Content, storage: storage));

        Assert.Equal(expectedIds.Split(','), page.Items.Select(i => i.SourceItemId).OrderBy(i => i, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The cached filter lists only links whose file the cache holds, marks them as cached, and lists nothing when there is no cache.
    /// True: the Cached view shows exactly what plays from this server.
    /// False: the view would list every link, or fail when the cache service is missing.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void GetSyncItems_CachedOnly_ListsWhatTheCacheHolds(bool withCache, int expectedRows)
    {
        var cache = Cache(holding: CachedLink);
        _harness.AddContent(CachedLink, change: r => r.StorageMode = StorageMode.Link);
        _harness.AddContent("other", change: r => r.StorageMode = StorageMode.Link);
        using var services = _harness.Services(s =>
        {
            if (withCache)
            {
                s.AddSingleton(cache);
            }
        });

        var page = Page(_harness.Configuration(services).GetSyncItems(_harness.Content, cached: true));

        Assert.Equal(expectedRows, page.Items.Count);
        Assert.All(page.Items, i => Assert.True(i.Cached));
    }

    /// <summary>
    /// The status counts split pending rows by type and carry the newest Content failure.
    /// True: the health card shows each count and the reason the last run failed.
    /// False: the card would hide a failed run, or show another sync type's failure.
    /// </summary>
    [Fact]
    public void GetSyncStatus_Rows_AreCountedByStatusWithTheNewestContentFailure()
    {
        _harness.AddContent("a", SyncStatus.Pending, change: r => r.PendingType = PendingType.Download);
        _harness.AddContent("b", SyncStatus.Pending, change: r => r.PendingType = PendingType.Replacement);
        _harness.AddContent("c", SyncStatus.Pending, change: r => r.PendingType = PendingType.Deletion);
        _harness.AddContent("d", SyncStatus.Queued);
        _harness.AddContent("e", SyncStatus.Synced);
        _harness.AddContent("f", SyncStatus.Errored);
        _harness.AddContent("g", SyncStatus.Ignored);
        _harness.AddContent("h", SyncStatus.Deleting);
        var newest = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        _harness.Config.Configuration.LastRunFailures = new List<SyncRunFailure>
        {
            new() { ModuleKey = "content", Phase = "Old", Reason = "old", Timestamp = newest.AddDays(-1) },
            new() { ModuleKey = "Content", Phase = "Download", Reason = "the source refused", Timestamp = newest },
            new() { ModuleKey = "History", Phase = "Apply", Reason = "other", Timestamp = newest.AddDays(1) }
        };

        var result = _harness.Configuration().GetSyncStatus(_harness.Content);

        var body = Assert.IsType<SyncStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((3, 1, 1, 1), (body.Pending, body.PendingDownload, body.PendingReplacement, body.PendingDeletion));
        Assert.Equal((1, 1, 1, 1, 1), (body.Queued, body.Synced, body.Errored, body.Ignored, body.Deleting));
        Assert.Equal(("Download", "the source refused", newest), (body.LastFailurePhase, body.LastFailureReason, body.LastFailureTime));
    }

    /// <summary>
    /// An empty table with no recorded failure counts zero everywhere and carries no failure.
    /// True: a fresh install shows an empty, healthy card.
    /// False: the card would fail on an empty table or a missing failure list.
    /// </summary>
    [Fact]
    public void GetSyncStatus_EmptyTableAndNoFailures_AnswersZeroes()
    {
        _harness.Config.Configuration.LastRunFailures = null!;

        var result = _harness.Configuration().GetSyncStatus(_harness.Content);

        var body = Assert.IsType<SyncStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(0, body.Pending + body.Queued + body.Synced + body.Errored + body.Ignored + body.Deleting);
        Assert.Null(body.LastFailureReason);
    }

    /// <summary>
    /// The pending size sums downloads, replacements, and queued rows, less deletions.
    /// True: the Sync tab shows how much disk the next run needs.
    /// False: the figure would count synced files, or ignore space a deletion frees.
    /// </summary>
    [Fact]
    public void GetPendingSize_Rows_AreSummedByPart()
    {
        _harness.AddContent("a", SyncStatus.Pending, change: r => (r.PendingType, r.SourceSize) = (PendingType.Download, 1000));
        _harness.AddContent("b", SyncStatus.Pending, change: r => (r.PendingType, r.SourceSize) = (PendingType.Replacement, 200));
        _harness.AddContent("c", SyncStatus.Pending, change: r => (r.PendingType, r.SourceSize) = (PendingType.Deletion, 50));
        _harness.AddContent("d", SyncStatus.Queued, change: r => r.SourceSize = 30);
        _harness.AddContent("e", SyncStatus.Synced, change: r => r.SourceSize = 7000);

        var result = _harness.Configuration().GetPendingSize(_harness.Content);

        var body = Assert.IsType<PendingSizeResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((1000L, 200L, 50L, 30L, 1180L), (body.PendingDownloadBytes, body.PendingReplacementBytes, body.PendingDeletionBytes, body.QueuedBytes, body.TotalPendingBytes));
    }

    /// <summary>
    /// Disk space is an empty list when no library is mapped.
    /// True: a server with no mappings shows no disks.
    /// False: the Sync tab would fail before any library is mapped.
    /// </summary>
    [Fact]
    public void GetDiskSpace_NoMappings_AnswersAnEmptyList()
    {
        _harness.Config.Configuration.Servers.Clear();

        var result = _harness.Configuration().GetDiskSpace();

        Assert.Empty(Assert.IsType<List<DiskSpaceInfo>>(Assert.IsType<OkObjectResult>(result.Result).Value));
    }

    /// <summary>
    /// Retry with no rows named queues every errored row and no other, and with rows named queues exactly those.
    /// True: Retry All reaches every failure, and a selection reaches only itself.
    /// False: a retry would requeue synced rows, or miss errored ones.
    /// </summary>
    [Theory]
    [InlineData(false, "Queued", "Queued", "Synced")]
    [InlineData(true, "Queued", "Errored", "Synced")]
    public void RetryErroredItems_AllOrNamed_QueuesTheRightRows(bool named, string first, string second, string third)
    {
        _harness.AddContent("a", SyncStatus.Errored, change: r => r.RetryCount = 5);
        _harness.AddContent("b", SyncStatus.Errored);
        _harness.AddContent("c", SyncStatus.Synced);
        var request = named ? new BulkItemsRequest { SourceItemIds = { "a" } } : null;

        var result = _harness.Configuration().RetryErroredItems(_harness.Content, request);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[] { first, second, third }, new[] { "a", "b", "c" }.Select(id => _harness.Content.GetBySourceItemId(id)!.Status.ToString()).ToArray());
        Assert.Equal(0, _harness.Content.GetBySourceItemId("a")!.RetryCount);
    }

    /// <summary>
    /// Every bulk route answers 400 when no row is selected, whether the body or its list is missing or empty.
    /// True: an empty selection changes nothing and says so.
    /// False: an empty selection would act on every row, or throw.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BulkRoutes_NothingSelected_Answer400(bool withBody)
    {
        var request = withBody ? new BulkItemsRequest() : null;
        var noList = new BulkItemsRequest { SourceItemIds = null! };
        var controller = _harness.Configuration();
        var cache = Cache();

        Assert.IsType<BadRequestObjectResult>(controller.IgnoreItems(_harness.Content, request!));
        Assert.IsType<BadRequestObjectResult>(controller.QueueItems(_harness.Content, request!));
        Assert.IsType<BadRequestObjectResult>(controller.PromoteItems(_harness.Content, cache, request!));
        Assert.IsType<BadRequestObjectResult>(controller.MarkSynced(_harness.Content, request!));
        Assert.IsType<BadRequestObjectResult>(controller.DeleteLocalItems(_harness.Content, request!));
        Assert.IsType<BadRequestObjectResult>(controller.DeleteLocalItems(_harness.Content, noList));
        Assert.IsType<BadRequestObjectResult>(controller.UnkeepCopies(_harness.Content, withBody ? new UnkeepRequest() : null!));
    }

    /// <summary>
    /// Ignore and Queue set the named rows that exist, clear their pending type, and report how many changed.
    /// True: the count the page shows is the number of rows that really changed.
    /// False: an ID that names no row would be counted, or other rows would change.
    /// </summary>
    [Theory]
    [InlineData(true, SyncStatus.Ignored)]
    [InlineData(false, SyncStatus.Queued)]
    public void IgnoreAndQueueItems_NamedRows_ChangeAndAreCounted(bool ignore, SyncStatus expected)
    {
        _harness.AddContent("a", SyncStatus.Pending, change: r => r.PendingType = PendingType.Download);
        _harness.AddContent("b", SyncStatus.Errored);
        _harness.AddContent("untouched", SyncStatus.Synced);
        var request = new BulkItemsRequest { SourceItemIds = { "a", "b", "no-such-row" } };

        var result = ignore ? _harness.Configuration().IgnoreItems(_harness.Content, request) : _harness.Configuration().QueueItems(_harness.Content, request);

        Assert.Equal(2, Read<int>(result, "Updated"));
        Assert.Equal((expected, (PendingType?)null), (_harness.Content.GetBySourceItemId("a")!.Status, _harness.Content.GetBySourceItemId("a")!.PendingType));
        Assert.Equal(expected, _harness.Content.GetBySourceItemId("b")!.Status);
        Assert.Equal(SyncStatus.Synced, _harness.Content.GetBySourceItemId("untouched")!.Status);
    }

    /// <summary>
    /// The link cache report is all zeroes while the cache is off, and counts its files and bytes while it is on.
    /// True: the Storage tab shows what the cache holds and how large it may grow.
    /// False: the tab would show a cache that is off as holding files, or an on cache as empty.
    /// </summary>
    [Theory]
    [InlineData(0, false, 0, 0L)]
    [InlineData(1, true, 1, 10L)]
    public void GetLinkCache_OffOrOn_ReportsWhatItHolds(int sizeGb, bool enabled, int files, long usedBytes)
    {
        var cache = Cache(holding: CachedLink);
        _harness.Config.Configuration.LinkCacheSizeGb = sizeGb;

        var result = _harness.Configuration().GetLinkCache(cache, Filler(cache));

        Assert.Equal(enabled, Read<bool>(result, "Enabled"));
        Assert.Equal(files, Read<int>(result, "Files"));
        Assert.Equal(usedBytes, Read<long>(result, "UsedBytes"));
        Assert.Equal(sizeGb * 1024L * 1024 * 1024, Read<long>(result, "LimitBytes"));
        Assert.Equal(enabled ? "Fixed" : "Off", Read<string>(result, "Sizing"));
        Assert.Equal(0, Read<int>(result, "Waiting"));
        Assert.Equal(cache.Root, Read<string>(result, "Location"));
    }

    /// <summary>
    /// Clearing the link cache removes every cached file and answers 204.
    /// True: the cache's disk space is freed at once.
    /// False: the button would report done while the files stay.
    /// </summary>
    [Fact]
    public void ClearLinkCache_HoldingAFile_EmptiesTheCache()
    {
        var cache = Cache(holding: CachedLink);

        var result = _harness.Configuration().ClearLinkCache(cache);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(cache.List());
        Assert.False(cache.Holds(CachedLink));
    }

    /// <summary>
    /// Promoting queues each link as a copy kept by hand, and skips copies, unknown rows, and rows that are ignored or being deleted.
    /// True: only a live link becomes a copy, and it is marked so an Always Keep change never turns it back.
    /// False: a copy would be downloaded again, or an ignored or deleting row would be revived.
    /// </summary>
    [Fact]
    public void PromoteItems_MixedRows_PromotesOnlyLiveLinks()
    {
        _harness.AddContent("link", change: r => r.StorageMode = StorageMode.Link);
        _harness.AddContent("copy");
        _harness.AddContent("ignored", SyncStatus.Ignored, change: r => r.StorageMode = StorageMode.Link);
        _harness.AddContent("deleting", SyncStatus.Deleting, change: r => r.StorageMode = StorageMode.Link);
        _harness.AddContent("leaving", SyncStatus.Pending, change: r => (r.StorageMode, r.PendingType) = (StorageMode.Link, PendingType.Deletion));
        var request = new BulkItemsRequest { SourceItemIds = { "link", "copy", "ignored", "deleting", "leaving", "no-such-row" } };

        var result = _harness.Configuration().PromoteItems(_harness.Content, Cache(), request);

        var promoted = _harness.Content.GetBySourceItemId("link")!;
        Assert.Equal((1, 0, 5), (Read<int>(result, "Promoted"), Read<int>(result, "FromCache"), Read<int>(result, "Skipped")));
        Assert.Equal((StorageMode.Promoting, SyncStatus.Queued, KeptReason.Hand), (promoted.StorageMode, promoted.Status, promoted.Kept));
        Assert.Equal(SyncStatus.Ignored, _harness.Content.GetBySourceItemId("ignored")!.Status);
        Assert.Equal(StorageMode.Link, _harness.Content.GetBySourceItemId("leaving")!.StorageMode);
    }

    /// <summary>
    /// A download run is queued at once only when a promoted link's whole file is already cached.
    /// True: a cached film becomes a copy in seconds, and an uncached one waits for the next run.
    /// False: every promotion would start a run, or a cached file would wait for the schedule.
    /// </summary>
    [Theory]
    [InlineData(CachedLink, 10, 1, 1)]
    [InlineData(CachedLink, 11, 0, 0)]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 10, 0, 0)]
    public void PromoteItems_WholeFileCached_QueuesARunAtOnce(string linkId, long sourceSize, int fromCache, int runsQueued)
    {
        _harness.AddScheduledTask(DownloadTask);
        _harness.AddContent(linkId, change: r => (r.StorageMode, r.SourceSize) = (StorageMode.Link, sourceSize));
        var request = new BulkItemsRequest { SourceItemIds = { linkId } };

        var result = _harness.Configuration().PromoteItems(_harness.Content, Cache(holding: CachedLink), request);

        Assert.Equal((1, fromCache), (Read<int>(result, "Promoted"), Read<int>(result, "FromCache")));
        Assert.Equal(runsQueued, _harness.TaskManagerStub.Calls.Count(c => c.Name == nameof(ITaskManager.QueueScheduledTask)));
    }

    /// <summary>
    /// Kept copies are listed for one entry's Link libraries only, and a copy with no recorded reason reads as Unknown.
    /// True: the Storage tab lists the copies that could go back to links, each with why it is a copy.
    /// False: copies in a Copy library, links, or another server's rows would be offered to turn back.
    /// </summary>
    [Fact]
    public void GetKeptCopies_LinkLibrary_ListsItsCopiesWithTheirReason()
    {
        Mapping().StorageMode = StorageMode.Link;
        _harness.Config.Configuration.Servers[0].LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "copies", StorageMode = StorageMode.Copy });
        _harness.AddContent("picked", change: r => r.Kept = KeptReason.Pick);
        _harness.AddContent("plain");
        _harness.AddContent("link", change: r => r.StorageMode = StorageMode.Link);
        _harness.AddContent("pending", SyncStatus.Pending);
        _harness.AddContent("copy-library", change: r => r.SourceLibraryId = "copies");
        _harness.AddContent("other-server", serverKey: "other");

        var page = Page(_harness.Configuration().GetKeptCopies(_harness.Content, "PEER"));

        var kept = page.Items.ToDictionary(i => i.SourceItemId, i => i.Kept, StringComparer.Ordinal);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(("Pick", "Unknown"), (kept["picked"], kept["plain"]));
        Assert.All(page.Items, i => Assert.Equal(("Source Films", "Films", "Peer"), (i.SourceLibraryName, i.LocalLibraryName, i.ServerName)));
    }

    /// <summary>
    /// Kept copies are an empty page when no entry is named, the entry is unknown, or it has no Link library.
    /// True: the Storage tab shows nothing to turn back.
    /// False: the tab would list every copy on the server, or fail without an entry.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-entry")]
    [InlineData("peer")]
    public void GetKeptCopies_NoEntryOrNoLinkLibrary_AnswersAnEmptyPage(string? serverKey)
    {
        _harness.AddContent("a");

        var page = Page(_harness.Configuration().GetKeptCopies(_harness.Content, serverKey));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    /// <summary>
    /// A synced copy in a Link library whose file is here is queued to turn back into a link, and a run is queued.
    /// True: the row is marked as going back, with its retries and error cleared.
    /// False: Turn Back Into Links would do nothing until some later change.
    /// </summary>
    [Fact]
    public void UnkeepCopies_SyncedCopyInALinkLibrary_IsQueuedToTurnBack()
    {
        Mapping().StorageMode = StorageMode.Link;
        _harness.AddScheduledTask(DownloadTask);
        var row = _harness.AddContent("a", change: r => (r.LocalPath, r.SourcePath, r.RetryCount, r.Reason) = (WriteFile("Film.mkv"), "/src/Film.mkv", 3, "old error"));

        var result = _harness.Configuration().UnkeepCopies(_harness.Content, new UnkeepRequest { Ids = new List<long> { row.Id, row.Id } });

        var stored = _harness.Content.GetById(row.Id)!;
        Assert.Equal(1, Read<int>(result, "Queued"));
        Assert.Empty(Read<List<object>>(result, "Skipped"));
        Assert.Equal((StorageMode.Unkeeping, SyncStatus.Queued, 0, null), (stored.StorageMode, stored.Status, stored.RetryCount, stored.Reason));
        Assert.Single(_harness.TaskManagerStub.Calls, c => c.Name == nameof(ITaskManager.QueueScheduledTask));
    }

    /// <summary>
    /// A row that cannot go back to a link is skipped with the reason, and nothing is queued.
    /// True: the page tells the operator why each selected copy stays a copy.
    /// False: a link, an unsynced copy, a copy in a Copy library, a picked copy, or a missing file would be queued and fail later.
    /// </summary>
    [Theory]
    [InlineData("missing", "the row no longer exists")]
    [InlineData("link", "it is not a copy")]
    [InlineData("errored", "it is not a synced copy yet")]
    [InlineData("copy-library", "its library is not mapped as Link")]
    [InlineData("picked", "an Always Keep pick covers it. Remove the pick on the Storage tab instead")]
    [InlineData("is-strm", "it cannot be a link")]
    [InlineData("no-file", "its file is not here")]
    public void UnkeepCopies_RowThatCannotTurnBack_IsSkippedWithTheReason(string scenario, string reason)
    {
        var mapping = Mapping();
        mapping.StorageMode = scenario == "copy-library" ? StorageMode.Copy : StorageMode.Link;
        if (scenario == "picked")
        {
            mapping.PermanentItems = new List<FilteredItem> { new() { Path = "/src/Film.mkv" } };
        }

        _harness.AddScheduledTask(DownloadTask);
        var row = _harness.AddContent("a", scenario == "errored" ? SyncStatus.Errored : SyncStatus.Synced, change: r =>
        {
            r.SourcePath = "/src/Film.mkv";
            r.LocalPath = scenario switch { "no-file" => Path.Combine(_library, "Gone.mkv"), "is-strm" => WriteFile("Film.strm"), _ => WriteFile("Film.mkv") };
            r.StorageMode = scenario == "link" ? StorageMode.Link : StorageMode.Copy;
        });
        var id = scenario == "missing" ? row.Id + 1000 : row.Id;

        var result = _harness.Configuration().UnkeepCopies(_harness.Content, new UnkeepRequest { Ids = new List<long> { id } });

        var skipped = Assert.Single(Read<List<object>>(result, "Skipped"));
        Assert.Equal(0, Read<int>(result, "Queued"));
        Assert.Equal((id, reason), ((long)skipped.GetType().GetProperty("Id")!.GetValue(skipped)!, (string)skipped.GetType().GetProperty("Reason")!.GetValue(skipped)!));
        Assert.DoesNotContain(_harness.TaskManagerStub.Calls, c => c.Name == nameof(ITaskManager.QueueScheduledTask));
        Assert.Equal(scenario == "link" ? StorageMode.Link : StorageMode.Copy, _harness.Content.GetById(row.Id)!.StorageMode);
    }

    /// <summary>
    /// Mark Synced settles only a row whose local file exists, and counts the rest as not found.
    /// True: a stuck row with its file in place is settled, and a row without one is left for the next run.
    /// False: a row could be marked synced with no file on disk, and never be downloaded.
    /// </summary>
    [Fact]
    public void MarkSynced_MixedRows_SettlesOnlyRowsWhoseFileExists()
    {
        _harness.AddContent("here", SyncStatus.Errored, change: r => r.LocalPath = WriteFile("Here.mkv"));
        _harness.AddContent("gone", SyncStatus.Errored, change: r => r.LocalPath = Path.Combine(_library, "Gone.mkv"));
        _harness.AddContent("no-path", SyncStatus.Queued, change: r => r.LocalPath = null);
        var request = new BulkItemsRequest { SourceItemIds = { "here", "gone", "no-path", "no-such-row" } };

        var result = _harness.Configuration().MarkSynced(_harness.Content, request);

        Assert.Equal((1, 3), (Read<int>(result, "Synced"), Read<int>(result, "NotFound")));
        Assert.Equal(SyncStatus.Synced, _harness.Content.GetBySourceItemId("here")!.Status);
        Assert.Equal(SyncStatus.Errored, _harness.Content.GetBySourceItemId("gone")!.Status);
        Assert.Equal(SyncStatus.Queued, _harness.Content.GetBySourceItemId("no-path")!.Status);
    }

    /// <summary>
    /// Deleting a tracked file inside a mapped library folder removes the file and keeps its row as Ignored.
    /// True: the file is gone and the next scan does not download it again.
    /// False: the file would come straight back, or the row would vanish and lose that the operator removed it.
    /// </summary>
    [Fact]
    public void DeleteLocalItems_FileInsideTheLibrary_DeletesItAndKeepsTheRowIgnored()
    {
        var path = WriteFile("Film.mkv");
        _harness.AddContent("a", change: r => (r.LocalPath, r.LocalItemId) = (path, "not-a-guid"));

        var result = _harness.Configuration().DeleteLocalItems(_harness.Content, new BulkItemsRequest { SourceItemIds = { "a" } });

        var row = _harness.Content.GetBySourceItemId("a")!;
        Assert.Equal((1, 0, 0), (Read<int>(result, "Deleted"), Read<int>(result, "Failed"), Read<int>(result, "Skipped")));
        Assert.False(File.Exists(path));
        Assert.Equal((SyncStatus.Ignored, null), (row.Status, row.LocalItemId));
    }

    /// <summary>
    /// A row whose path lies outside every mapped library folder is skipped, and its file and row are untouched.
    /// True: the route can never delete a file outside the folders Server Sync manages.
    /// False: a row with a bad path could delete any file Jellyfin can reach.
    /// </summary>
    [Fact]
    public void DeleteLocalItems_PathOutsideEveryLibrary_IsSkippedAndTheFileStays()
    {
        var outside = Path.Combine(_harness.TempDir, "elsewhere.mkv");
        File.WriteAllText(outside, "keep me");
        _harness.AddContent("a", change: r => r.LocalPath = outside);
        _harness.AddContent("b", change: r => r.LocalPath = Path.Combine(_library, "..", "elsewhere.mkv"));

        var result = _harness.Configuration().DeleteLocalItems(_harness.Content, new BulkItemsRequest { SourceItemIds = { "a", "b" } });

        Assert.Equal((0, 0, 2), (Read<int>(result, "Deleted"), Read<int>(result, "Failed"), Read<int>(result, "Skipped")));
        Assert.True(File.Exists(outside));
        Assert.Equal(SyncStatus.Synced, _harness.Content.GetBySourceItemId("a")!.Status);
    }

    /// <summary>
    /// An unknown row is skipped, and a row whose file is already gone is skipped and kept as Ignored.
    /// True: nothing is deleted, and the missing file is not downloaded again.
    /// False: a missing row would fail the batch, or a file removed by hand would return on the next scan.
    /// </summary>
    [Fact]
    public void DeleteLocalItems_UnknownRowOrFileAlreadyGone_IsSkipped()
    {
        _harness.AddContent("gone", change: r => r.LocalPath = Path.Combine(_library, "Gone.mkv"));

        var result = _harness.Configuration().DeleteLocalItems(_harness.Content, new BulkItemsRequest { SourceItemIds = { "no-such-row", "gone" } });

        Assert.Equal((0, 0, 2), (Read<int>(result, "Deleted"), Read<int>(result, "Failed"), Read<int>(result, "Skipped")));
        Assert.Equal(SyncStatus.Ignored, _harness.Content.GetBySourceItemId("gone")!.Status);
    }

    /// <summary>
    /// A stale local item ID that resolves to another file is not trusted, and only the tracked file is deleted.
    /// True: the item Jellyfin resolved is never removed from the library, and its file stays.
    /// False: a reused item ID would delete some other film.
    /// </summary>
    [Fact]
    public void DeleteLocalItems_StaleLocalItemId_DeletesOnlyTheTrackedFile()
    {
        var tracked = WriteFile("Tracked.mkv");
        var other = WriteFile("Other.mkv");
        var staleId = Guid.NewGuid();
        _harness.LibraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => new Movie { Id = staleId, Path = other });
        _harness.AddContent("a", change: r => (r.LocalPath, r.LocalItemId) = (tracked, staleId.ToString("N")));

        var result = _harness.Configuration().DeleteLocalItems(_harness.Content, new BulkItemsRequest { SourceItemIds = { "a" } });

        Assert.Equal(1, Read<int>(result, "Deleted"));
        Assert.False(File.Exists(tracked));
        Assert.True(File.Exists(other));
        Assert.DoesNotContain(_harness.LibraryStub.Calls, c => c.Name == nameof(ILibraryManager.DeleteItem));
    }

    /// <summary>
    /// Each Content route requires elevation and answers one verb at its own path.
    /// True: only an administrator can list rows, change them, or delete files.
    /// False: any signed in user could delete local media through the plugin.
    /// </summary>
    [Theory]
    [InlineData(nameof(ConfigurationController.GetSyncItems), "GET", "Items")]
    [InlineData(nameof(ConfigurationController.GetSyncStatus), "GET", "Status")]
    [InlineData(nameof(ConfigurationController.GetPendingSize), "GET", "PendingSize")]
    [InlineData(nameof(ConfigurationController.GetDiskSpace), "GET", "DiskSpace")]
    [InlineData(nameof(ConfigurationController.RetryErroredItems), "POST", "RetryErroredItems")]
    [InlineData(nameof(ConfigurationController.IgnoreItems), "POST", "IgnoreItems")]
    [InlineData(nameof(ConfigurationController.QueueItems), "POST", "QueueItems")]
    [InlineData(nameof(ConfigurationController.GetLinkCache), "GET", "LinkCache")]
    [InlineData(nameof(ConfigurationController.ClearLinkCache), "POST", "LinkCache/Clear")]
    [InlineData(nameof(ConfigurationController.PromoteItems), "POST", "PromoteItems")]
    [InlineData(nameof(ConfigurationController.GetKeptCopies), "GET", "KeptCopies")]
    [InlineData(nameof(ConfigurationController.UnkeepCopies), "POST", "KeptCopies/Unkeep")]
    [InlineData(nameof(ConfigurationController.MarkSynced), "POST", "MarkSynced")]
    [InlineData(nameof(ConfigurationController.DeleteLocalItems), "POST", "DeleteLocalItems")]
    public void Route_RequiresElevation(string action, string verb, string template)
    {
        ControllerRouteAssert.Elevated(typeof(ConfigurationController), action, verb, template);
    }

    private static PagedResult<SyncItemDto> Page(ActionResult<PagedResult<SyncItemDto>> result)
        => Assert.IsType<PagedResult<SyncItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static T Read<T>(ActionResult result, string property)
    {
        var body = Assert.IsType<OkObjectResult>(result).Value!;
        return (T)body.GetType().GetProperty(property)!.GetValue(body)!;
    }

    private LibraryMapping Mapping() => _harness.Config.Configuration.Servers[0].LibraryMappings[0];

    private string WriteFile(string name)
    {
        var path = Path.Combine(_library, name);
        File.WriteAllText(path, "media");
        return path;
    }

    private LinkCache Cache(string? holding = null)
    {
        _harness.Config.Configuration.LinkCacheSizeGb = 1;
        var cache = new LinkCache(_harness.Config, Path.Combine(_harness.TempDir, "cache"), NullLogger<LinkCache>.Instance, () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        if (holding is not null)
        {
            File.WriteAllBytes(cache.PartPathFor(holding, ".mkv"), new byte[10]);
            Assert.True(cache.Complete(holding, ".mkv", 10, CacheReason.Played));
        }

        return cache;
    }

    private LinkCacheFiller Filler(LinkCache cache)
    {
        var (links, _) = InterfaceStub.Create<ILinkRegistry>();
        var (sessions, _) = InterfaceStub.Create<ISessionManager>();
        return new LinkCacheFiller(cache, links, new ServiceCollection().BuildServiceProvider(), _harness.Config, _harness.Clients, _harness.Host, sessions, _harness.Library, NullLogger<LinkCacheFiller>.Instance);
    }
}

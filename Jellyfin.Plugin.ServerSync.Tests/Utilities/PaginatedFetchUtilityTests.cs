using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// <see cref="PaginatedFetchUtility.FetchAllPagesAsync"/> is the choke point
/// between "what the source answered" and "what the prune treats as removed":
/// CompletedFully=true licenses deletion of everything unseen. Every test here
/// asserts the flag, because a wrong true is a data-loss license.
/// </summary>
public class PaginatedFetchUtilityTests
{
    private static BaseItemDto Item(Guid id, string? path = "/media/a.mkv") => new()
    {
        Id = id,
        Path = path
    };

    private static BaseItemDtoQueryResult Page(int total, params BaseItemDto[] items) => new()
    {
        Items = items.ToList(),
        TotalRecordCount = total
    };

    /// <summary>
    /// Serves pre-baked pages by startIndex; anything past the script is an
    /// empty page (the utility's normal termination signal).
    /// </summary>
    private static PaginatedFetchUtility.FetchPageAsync Pager(Dictionary<int, BaseItemDtoQueryResult?> pages)
        => (startIndex, _, _) => Task.FromResult(pages.TryGetValue(startIndex, out var page)
            ? page
            : new BaseItemDtoQueryResult { Items = new List<BaseItemDto>(), TotalRecordCount = 0 });

    private static Task<PaginatedFetchOutcome> RunAsync(
        PaginatedFetchUtility.FetchPageAsync fetchPage,
        PaginatedFetchUtility.ProcessItemAsync? processItem = null,
        CancellationToken cancellationToken = default)
        => PaginatedFetchUtility.FetchAllPagesAsync(
            fetchPage,
            processItem ?? ((_, _) => Task.FromResult(true)),
            libraryName: "TestLib",
            sourceRootPath: "/media",
            filterMode: LibraryFilterMode.AllowAll,
            filteredItems: null,
            logger: NullLogger.Instance,
            cancellationToken: cancellationToken);

    private static BaseItemDto[] Items(int count)
        => Enumerable.Range(0, count).Select(i => Item(Guid.NewGuid(), $"/media/{i}.mkv")).ToArray();

    /// <summary>
    /// The happy path must complete fully — if a clean single page reported
    /// incomplete, pruning would be permanently blocked for healthy libraries.
    /// </summary>
    [Fact]
    public async Task CleanSinglePage_CompletesFully()
    {
        var items = Items(3);
        var outcome = await RunAsync(Pager(new() { [0] = Page(3, items) }));

        Assert.True(outcome.CompletedFully);
        Assert.Equal(3, outcome.ProcessedItems);
    }

    /// <summary>
    /// A genuinely empty library is a complete answer (legitimate mass
    /// removal reconciles); the per-mapping zero-seen guard upstream decides
    /// whether to trust it — not this utility.
    /// </summary>
    [Fact]
    public async Task EmptyFirstPage_CompletesFully()
    {
        var outcome = await RunAsync(Pager(new()
        {
            [0] = new BaseItemDtoQueryResult { Items = new List<BaseItemDto>(), TotalRecordCount = 0 }
        }));

        Assert.True(outcome.CompletedFully);
        Assert.Equal(0, outcome.ProcessedItems);
    }

    /// <summary>
    /// The same item ID on two pages means page boundaries shifted mid-scan —
    /// a duplicate on one side implies a silent skip on the other, and the
    /// skipped item would be pruned as "removed from source". The count check
    /// alone can't see this (the duplicate keeps totals matching), which is
    /// exactly the reviewed-and-fixed hole this test pins down.
    /// </summary>
    [Fact]
    public async Task DuplicateIdAcrossPages_PoisonsCompletedFully()
    {
        var batch = PaginatedFetchUtility.DefaultBatchSize;
        var page1 = Items(batch);
        var page2 = Items(batch - 1).Concat(new[] { Item(page1[0].Id!.Value, page1[0].Path) }).ToArray();

        var outcome = await RunAsync(Pager(new()
        {
            [0] = Page(batch * 2, page1),
            [batch] = Page(batch * 2, page2)
        }));

        Assert.False(outcome.CompletedFully);
    }

    /// <summary>
    /// Fetching fewer items than the first page's TotalRecordCount promised
    /// means the catalog changed underneath the scan (an item deleted on the
    /// source shifts everything down one slot and silently skips an unrelated
    /// item at the page boundary).
    /// </summary>
    [Fact]
    public async Task FetchedCountBelowExpectedTotal_PoisonsCompletedFully()
    {
        var batch = PaginatedFetchUtility.DefaultBatchSize;
        var outcome = await RunAsync(Pager(new()
        {
            [0] = Page(batch + 10, Items(batch)),
            [batch] = Page(batch + 10, Items(4))
        }));

        Assert.False(outcome.CompletedFully);
    }

    /// <summary>
    /// A processItem exception leaves that item out of the caller's seen set
    /// exactly like an unfetched item, so it must poison the run.
    /// </summary>
    [Fact]
    public async Task ProcessFailure_PoisonsCompletedFully()
    {
        var items = Items(3);
        var callCount = 0;
        var outcome = await RunAsync(
            Pager(new() { [0] = Page(3, items) }),
            (_, _) => ++callCount == 2
                ? throw new InvalidOperationException("test: item processing failed")
                : Task.FromResult(true));

        Assert.False(outcome.CompletedFully);
        Assert.Equal(2, outcome.ProcessedItems);
    }

    /// <summary>
    /// Path-less items are skipped (they can't be synced) but must not fail
    /// the run by themselves: one virtual episode in a TV library would
    /// otherwise block pruning forever. The all-pathless catastrophe (API key
    /// lost admin) is handled by the per-mapping zero-seen guard upstream.
    /// </summary>
    [Fact]
    public async Task PathlessItems_SkippedWithoutPoisoning()
    {
        var good = Item(Guid.NewGuid());
        var pathless = Item(Guid.NewGuid(), path: null);

        var outcome = await RunAsync(Pager(new() { [0] = Page(2, good, pathless) }));

        Assert.True(outcome.CompletedFully);
        Assert.Equal(1, outcome.ProcessedItems);
    }

    /// <summary>
    /// Persistent nulls (transport layer returning nothing) must abort as
    /// incomplete after the retry budget, never report a clean empty catalog.
    /// </summary>
    [Fact]
    public async Task PersistentNullPages_AbortIncomplete()
    {
        var outcome = await RunAsync((_, _, _) => Task.FromResult<BaseItemDtoQueryResult?>(null));

        Assert.False(outcome.CompletedFully);
        Assert.Equal(0, outcome.ProcessedItems);
    }

    /// <summary>
    /// Persistent fetch exceptions likewise abort as incomplete instead of
    /// propagating (per-library isolation) or reporting complete.
    /// </summary>
    [Fact]
    public async Task PersistentFetchErrors_AbortIncomplete()
    {
        var outcome = await RunAsync((_, _, _) => throw new HttpRequestExceptionStub());

        Assert.False(outcome.CompletedFully);
    }

    private sealed class HttpRequestExceptionStub : System.Net.Http.HttpRequestException
    {
    }

    /// <summary>
    /// Cancellation mid-run must not report complete discovery — the
    /// remaining items were never enumerated and are not "removed".
    /// </summary>
    [Fact]
    public async Task CancellationMidRun_ReportsIncomplete()
    {
        using var cts = new CancellationTokenSource();
        var items = Items(3);
        var outcome = await RunAsync(
            Pager(new() { [0] = Page(3, items) }),
            (_, _) =>
            {
                cts.Cancel();
                return Task.FromResult(true);
            },
            cts.Token);

        Assert.False(outcome.CompletedFully);
    }

    /// <summary>
    /// Blacklisted items are an intentional exclusion: they don't count as
    /// processed and don't poison the run.
    /// </summary>
    [Fact]
    public async Task BlacklistedItems_ExcludedCleanly()
    {
        var kept = Item(Guid.NewGuid(), "/media/keep/movie.mkv");
        var blocked = Item(Guid.NewGuid(), "/media/blocked/movie.mkv");

        var outcome = await PaginatedFetchUtility.FetchAllPagesAsync(
            Pager(new() { [0] = Page(2, kept, blocked) }),
            (_, _) => Task.FromResult(true),
            libraryName: "TestLib",
            sourceRootPath: "/media",
            filterMode: LibraryFilterMode.Blacklist,
            filteredItems: new List<FilteredItem> { new() { Path = "/media/blocked" } },
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None);

        Assert.True(outcome.CompletedFully);
        Assert.Equal(1, outcome.ProcessedItems);
    }
}

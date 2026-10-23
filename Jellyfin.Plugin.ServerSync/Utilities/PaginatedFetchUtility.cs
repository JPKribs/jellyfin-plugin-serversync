using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Outcome of a paginated fetch loop.
/// <see cref="CompletedFully"/> is false when the loop broke early on
/// errors/null results/cancellation, callers that drive pruning from
/// "items I didn't see this run" must treat partial discovery as
/// unsafe-to-prune, since the unseen items may just be ones we never
/// got to enumerate.
/// </summary>
public readonly record struct PaginatedFetchOutcome(int ProcessedItems, bool CompletedFully);

/// <summary>
/// Reusable paginated fetch loop with retry and library-filter handling.
/// </summary>
public static class PaginatedFetchUtility
{
    /// <summary>
    /// Items fetched per page. Callers request only light fields (Path /
    /// DateCreated / MediaSources), so a larger page cuts round-trip count
    /// against the source server without meaningfully growing the response.
    /// </summary>
    public const int DefaultBatchSize = 500;

    /// <summary>
    /// Maximum consecutive fetch errors before aborting the loop.
    /// </summary>
    public const int MaxConsecutiveErrors = 3;

    /// <summary>
    /// Fetches a single page of items from the source server.
    /// </summary>
    public delegate Task<BaseItemDtoQueryResult?> FetchPageAsync(int startIndex, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Processes a single item. Returns true to count it toward the processed total.
    /// </summary>
    public delegate Task<bool> ProcessItemAsync(BaseItemDto item, CancellationToken cancellationToken);

    /// <summary>
    /// How many times one library is enumerated when the catalog shifts underneath the scan. A second
    /// pass skips everything the first one handled, so it costs little, and a clean second pass proves
    /// nothing was skipped. A library that keeps changing still reports incomplete discovery.
    /// </summary>
    public const int MaxEnumerationPasses = 2;

    /// <summary>
    /// Drives a paginated fetch loop, applying library filters per item and stopping
    /// after <see cref="MaxConsecutiveErrors"/> consecutive failures.
    /// </summary>
    public static async Task<PaginatedFetchOutcome> FetchAllPagesAsync(
        FetchPageAsync fetchPage,
        ProcessItemAsync processItem,
        string libraryName,
        string? sourceRootPath,
        LibraryFilterMode filterMode,
        List<FilteredItem>? filteredItems,
        ILogger logger,
        CancellationToken cancellationToken,
        Action? onItemProcessed = null)
    {
        var state = new FetchState();
        var completedFully = false;
        for (var pass = 1; pass <= MaxEnumerationPasses; pass++)
        {
            var result = await RunPassAsync(fetchPage, processItem, libraryName, sourceRootPath, filterMode, filteredItems, logger, state, onItemProcessed, cancellationToken).ConfigureAwait(false);
            completedFully = result == PassResult.Complete;
            if (result != PassResult.Shifted)
            {
                break;
            }

            if (pass < MaxEnumerationPasses)
            {
                logger.LogInformation(
                    "Library {LibraryName} changed during enumeration, listing it again so items that moved between pages are not missed",
                    libraryName);
            }
        }

        if (state.PathlessSkipped.Count > 0)
        {
            logger.LogWarning(
                "{Count} item(s) in library {LibraryName} came back without a file path and were skipped, if this is unexpected, check that the API key has admin rights on the source",
                state.PathlessSkipped.Count, libraryName);
        }

        if (state.Failed.Count > 0)
        {
            logger.LogWarning(
                "{Count} item(s) in library {LibraryName} failed to process. Reporting incomplete discovery so they are not treated as removed",
                state.Failed.Count, libraryName);
            completedFully = false;
        }

        return new PaginatedFetchOutcome(state.ProcessedItems, completedFully);
    }

    private enum PassResult
    {
        Complete,
        Shifted,
        Stopped
    }

    // Survives from one pass to the next, so a second pass only handles what the first did not.
    private sealed class FetchState
    {
        public HashSet<Guid> Handled { get; } = new();

        public HashSet<Guid> Failed { get; } = new();

        public HashSet<Guid> PathlessSkipped { get; } = new();

        public int ProcessedItems { get; set; }
    }

    private static async Task<PassResult> RunPassAsync(
        FetchPageAsync fetchPage,
        ProcessItemAsync processItem,
        string libraryName,
        string? sourceRootPath,
        LibraryFilterMode filterMode,
        List<FilteredItem>? filteredItems,
        ILogger logger,
        FetchState state,
        Action? onItemProcessed,
        CancellationToken cancellationToken)
    {
        var startIndex = 0;
        var consecutiveErrors = 0;
        var rawFetched = 0;
        var duplicateIds = 0;
        var fetchedIds = new HashSet<Guid>();
        int? expectedTotal = null;
        var completedFully = false;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            BaseItemDtoQueryResult? result;
            try
            {
                result = await fetchPage(startIndex, DefaultBatchSize, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // An HTTP timeout arrives as a TaskCanceledException without the caller's token
                // being cancelled, so it lands here and is retried like any other failed page.
                consecutiveErrors++;
                logger.LogWarning(ex,
                    "Failed to fetch items from library {LibraryName} at index {Index} (attempt {Attempt}/{Max})",
                    libraryName, startIndex, consecutiveErrors, MaxConsecutiveErrors);

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    logger.LogError(
                        "Too many consecutive errors fetching from {LibraryName}, stopping sync for this library",
                        libraryName);
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(consecutiveErrors * 2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // null result is treated as a transient error (distinct from empty Items).
            if (result == null)
            {
                consecutiveErrors++;
                logger.LogWarning(
                    "Fetch returned null for library {LibraryName} at index {Index} (attempt {Attempt}/{Max})",
                    libraryName, startIndex, consecutiveErrors, MaxConsecutiveErrors);

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    logger.LogError(
                        "Too many consecutive null results fetching from {LibraryName}, stopping sync for this library",
                        libraryName);
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(consecutiveErrors * 2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (result.Items == null || result.Items.Count == 0)
            {
                completedFully = true;
                break;
            }

            consecutiveErrors = 0;
            expectedTotal ??= result.TotalRecordCount;
            rawFetched += result.Items.Count;

            foreach (var item in result.Items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (item.Id == null)
                {
                    continue;
                }

                var id = item.Id.Value;
                if (!fetchedIds.Add(id))
                {
                    // A repeated ID means page boundaries shifted mid-scan
                    // (tie reorder or source mutation). A duplicate on one
                    // side implies a silent skip on the other, and the
                    // count-based check below can't see a swap, because the
                    // duplicate keeps the totals matching.
                    duplicateIds++;
                    continue;
                }

                // Handled by an earlier pass: seen once is enough, and processing it again would
                // count it twice.
                if (state.Handled.Contains(id))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(item.Path))
                {
                    // Distinct from a plain skip: a Path-less item is invisible
                    // to the caller's seen set, and Jellyfin omits Path on
                    // every item when the API key lacks admin rights, the
                    // per-mapping zero-seen guard catches the all-pathless
                    // case, this counter surfaces partial ones.
                    state.PathlessSkipped.Add(id);
                    state.Handled.Add(id);
                    continue;
                }

                if (PathUtilities.IsItemFiltered(item.Path, sourceRootPath ?? string.Empty, filterMode, filteredItems))
                {
                    state.Handled.Add(id);
                    continue;
                }

                try
                {
                    var wasProcessed = await processItem(item, cancellationToken).ConfigureAwait(false);
                    if (wasProcessed)
                    {
                        state.ProcessedItems++;
                    }

                    state.Handled.Add(id);
                    state.Failed.Remove(id);
                    onItemProcessed?.Invoke();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Counted: an item that failed to process is absent from
                    // the caller's seen set exactly like an item that was
                    // never fetched, so it must poison CompletedFully or the
                    // caller's prune would treat it as removed from source.
                    // Left unhandled, so a second pass tries it again.
                    state.Failed.Add(id);
                    logger.LogWarning(ex, "Failed to process item {ItemId} ({Path})", item.Id, item.Path);
                }
            }

            // Cancellation can break the item loop mid-page. The remaining
            // items on this page were never processed, so this run must not
            // report complete discovery.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            startIndex += DefaultBatchSize;

            if (result.Items.Count < DefaultBatchSize)
            {
                completedFully = true;
                break;
            }
        }

        if (!completedFully)
        {
            return PassResult.Stopped;
        }

        // Offset pagination races with catalog mutations: an item deleted on
        // the source mid-enumeration shifts everything after it down one slot,
        // silently skipping an unrelated item at the next page boundary, and
        // a skipped item is indistinguishable from a removed one to the
        // caller's prune. The first page's TotalRecordCount tells us how many
        // items existed at pass start. Fetching a different number means the
        // catalog changed underneath us, so the pass is not a consistent snapshot.
        if (expectedTotal.HasValue && rawFetched != expectedTotal.Value)
        {
            logger.LogWarning(
                "Library {LibraryName} changed during enumeration (expected {Expected} items, fetched {Fetched}), reporting incomplete discovery so shifted items are not treated as removed",
                libraryName, expectedTotal.Value, rawFetched);
            return PassResult.Shifted;
        }

        if (duplicateIds > 0)
        {
            logger.LogWarning(
                "{Count} duplicate item ID(s) across pages in library {LibraryName}, page boundaries shifted during enumeration. Reporting incomplete discovery so skipped items are not treated as removed",
                duplicateIds, libraryName);
            return PassResult.Shifted;
        }

        return PassResult.Complete;
    }
}

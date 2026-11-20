using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Reusable paginated fetch loop with retry and library-filter handling.
/// </summary>
public static class PaginatedFetchUtility
{
    /// <summary>
    /// Items fetched per page. Callers ask for light fields only, so a large page saves round trips to the source.
    /// </summary>
    public const int DefaultBatchSize = 500;

    /// <summary>
    /// Maximum consecutive fetch errors before aborting the loop.
    /// </summary>
    public const int MaxConsecutiveErrors = 3;

    /// <summary>
    /// Fetches a single page of items from the source server.
    /// </summary>
    /// <param name="startIndex">The index of the first item to fetch.</param>
    /// <param name="batchSize">How many items each page holds.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The page, or null when the source gave no answer.</returns>
    public delegate Task<BaseItemDtoQueryResult?> FetchPageAsync(int startIndex, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Processes a single item.
    /// </summary>
    /// <param name="item">The item the source listed.</param>
    /// <param name="cancellationToken">Cancels the processing.</param>
    /// <returns><c>true</c> to count the item toward the processed total.</returns>
    public delegate Task<bool> ProcessItemAsync(BaseItemDto item, CancellationToken cancellationToken);

    /// <summary>
    /// How many times one library is listed when it changes under the scan. A clean second pass proves nothing was skipped.
    /// </summary>
    public const int MaxEnumerationPasses = 2;

    /// <summary>
    /// Drives a paginated fetch loop, applying library filters per item and stopping
    /// after <see cref="MaxConsecutiveErrors"/> consecutive failures.
    /// </summary>
    /// <param name="fetchPage">Fetches one page of items.</param>
    /// <param name="processItem">Handles one item.</param>
    /// <param name="libraryName">The library's name, for log entries.</param>
    /// <param name="sourceRootPath">The library's root folder on the source.</param>
    /// <param name="filterMode">The Library filter mode.</param>
    /// <param name="filteredItems">The paths the Library filter lists, or null.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="cancellationToken">Cancels the loop.</param>
    /// <param name="onItemProcessed">Called after each item, or null.</param>
    /// <returns>How many items were handled and whether the loop reached the end.</returns>
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
        ArgumentNullException.ThrowIfNull(fetchPage);
        ArgumentNullException.ThrowIfNull(processItem);
        ArgumentNullException.ThrowIfNull(logger);

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
                    FormatUtilities.SanitizeForLog(libraryName));
            }
        }

        if (state.PathlessSkipped.Count > 0)
        {
            logger.LogWarning(
                "{Count} item(s) in library {LibraryName} came back without a file path and were skipped, if this is unexpected, check that the API key has admin rights on the source",
                state.PathlessSkipped.Count, FormatUtilities.SanitizeForLog(libraryName));
        }

        if (state.Failed.Count > 0)
        {
            logger.LogWarning(
                "{Count} item(s) in library {LibraryName} failed to process. Reporting incomplete discovery so they are not treated as removed",
                state.Failed.Count, FormatUtilities.SanitizeForLog(libraryName));
            completedFully = false;
        }

        return new PaginatedFetchOutcome(state.ProcessedItems, completedFully);
    }

    /// <summary>How one pass over a library ended.</summary>
    private enum PassResult
    {
        Complete,
        Shifted,
        Stopped
    }

    /// <summary>
    /// Survives from one pass to the next, so a second pass only handles what the first did not.
    /// </summary>
    private sealed class FetchState
    {
        /// <summary>Gets the items already processed.</summary>
        public HashSet<Guid> Handled { get; } = new();

        /// <summary>Gets the items that failed.</summary>
        public HashSet<Guid> Failed { get; } = new();

        /// <summary>Gets the items skipped for having no path.</summary>
        public HashSet<Guid> PathlessSkipped { get; } = new();

        /// <summary>Gets or sets how many items have been processed.</summary>
        public int ProcessedItems { get; set; }
    }

    /// <summary>
    /// Lists a library once, and reports a shift when the count or a repeated ID shows the pages moved mid pass.
    /// </summary>
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
            // A timeout arrives as a cancellation the caller never asked for, so it retries.
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                consecutiveErrors++;
                logger.LogWarning(ex,
                    "Failed to fetch items from library {LibraryName} at index {Index} (attempt {Attempt}/{Max})",
                    FormatUtilities.SanitizeForLog(libraryName), startIndex, consecutiveErrors, MaxConsecutiveErrors);

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    logger.LogError(
                        "Too many consecutive errors fetching from {LibraryName}, stopping sync for this library",
                        FormatUtilities.SanitizeForLog(libraryName));
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

            // A null page is a failed fetch, while an empty one ends the list.
            if (result == null)
            {
                consecutiveErrors++;
                logger.LogWarning(
                    "Fetch returned null for library {LibraryName} at index {Index} (attempt {Attempt}/{Max})",
                    FormatUtilities.SanitizeForLog(libraryName), startIndex, consecutiveErrors, MaxConsecutiveErrors);

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    logger.LogError(
                        "Too many consecutive null results fetching from {LibraryName}, stopping sync for this library",
                        FormatUtilities.SanitizeForLog(libraryName));
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
                // A repeated ID means pages shifted and another item was skipped unseen.
                if (!fetchedIds.Add(id))
                {
                    duplicateIds++;
                    continue;
                }

                // An earlier pass handled it, and a second time would count it twice.
                if (state.Handled.Contains(id))
                {
                    continue;
                }

                // Jellyfin omits Path when the API Key lacks admin rights, so these are counted.
                if (string.IsNullOrEmpty(item.Path))
                {
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
                // A failed item is counted so the caller's prune never reads it as removed.
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    state.Failed.Add(id);
                    logger.LogWarning(ex, "Failed to process item {ItemId} ({Path})", item.Id, FormatUtilities.SanitizeForLog(item.Path));
                }
            }

            // A cancel mid page leaves items unprocessed, so the pass must not report complete.
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

        // A count unlike the first page's total means the library changed mid pass.
        if (expectedTotal.HasValue && rawFetched != expectedTotal.Value)
        {
            logger.LogWarning(
                "Library {LibraryName} changed during enumeration (expected {Expected} items, fetched {Fetched}), reporting incomplete discovery so shifted items are not treated as removed",
                FormatUtilities.SanitizeForLog(libraryName), expectedTotal.Value, rawFetched);
            return PassResult.Shifted;
        }

        if (duplicateIds > 0)
        {
            logger.LogWarning(
                "{Count} duplicate item ID(s) across pages in library {LibraryName}, page boundaries shifted during enumeration. Reporting incomplete discovery so skipped items are not treated as removed",
                duplicateIds, FormatUtilities.SanitizeForLog(libraryName));
            return PassResult.Shifted;
        }

        return PassResult.Complete;
    }
}

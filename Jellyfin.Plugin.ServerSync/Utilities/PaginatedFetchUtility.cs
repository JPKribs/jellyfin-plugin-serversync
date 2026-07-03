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
/// errors/null results/cancellation — callers that drive pruning from
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
        var startIndex = 0;
        var processedItems = 0;
        var consecutiveErrors = 0;
        var processFailures = 0;
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
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

            foreach (var item in result.Items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (item.Id == null || string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                if (PathUtilities.IsItemFiltered(item.Path, sourceRootPath ?? string.Empty, filterMode, filteredItems))
                {
                    continue;
                }

                try
                {
                    var wasProcessed = await processItem(item, cancellationToken).ConfigureAwait(false);
                    if (wasProcessed)
                    {
                        processedItems++;
                    }

                    onItemProcessed?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Counted: an item that failed to process is absent from
                    // the caller's seen set exactly like an item that was
                    // never fetched, so it must poison CompletedFully or the
                    // caller's prune would treat it as removed from source.
                    processFailures++;
                    logger.LogWarning(ex, "Failed to process item {ItemId} ({Path})", item.Id, item.Path);
                }
            }

            // Cancellation can break the item loop mid-page; the remaining
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

        if (processFailures > 0)
        {
            logger.LogWarning(
                "{Count} item(s) in library {LibraryName} failed to process; reporting incomplete discovery so they are not treated as removed",
                processFailures, libraryName);
            completedFully = false;
        }

        return new PaginatedFetchOutcome(processedItems, completedFully);
    }
}

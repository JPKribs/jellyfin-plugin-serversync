using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Reusable paginated fetch loop with retry and library-filter handling.
/// </summary>
public static class PaginatedFetchUtility
{
    /// <summary>
    /// Items fetched per page.
    /// </summary>
    public const int DefaultBatchSize = 100;

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
    public static async Task<int> FetchAllPagesAsync(
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
                    logger.LogWarning(ex, "Failed to process item {ItemId} ({Path})", item.Id, item.Path);
                }
            }

            startIndex += DefaultBatchSize;

            if (result.Items.Count < DefaultBatchSize)
            {
                break;
            }
        }

        return processedItems;
    }
}

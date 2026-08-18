using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Local image maintenance shared by the metadata and people image applies.
/// </summary>
public static class LocalImageUtilities
{
    /// <summary>
    /// Deletes every local image of the given type, both the item entry and
    /// the file on disk. Each removal is persisted to the repository as it
    /// happens.
    /// </summary>
    /// <param name="item">Item to clear images on.</param>
    /// <param name="imageType">Image type to clear.</param>
    /// <param name="logger">Logger for per image failures.</param>
    /// <param name="context">Item name or id, used in log messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of images actually removed.</returns>
    // BaseItem.RemoveImage only drops the in memory entry and leaves the file
    // where it is. Multi image types make that a problem. Jellyfin names
    // backdrops deterministically as backdrop.jpg, backdrop1.jpg and so on,
    // so a local set of five replaced by a source set of two leaves
    // backdrop2 through backdrop4 on disk. The next library scan re-adopts
    // them, the manifest reads five again, and the row sits in a permanent
    // "source has 2, local has 5" variance that re-pulls every image on every
    // run. Deleting the files makes the local set genuinely match the source
    // set, so the next refresh sees no variance.
    //
    // Deletion runs highest index first. Images are addressed by their
    // ordinal within the type, so clearing index 0 first shifts everything
    // down and an ascending loop would skip every other image.
    public static async Task<int> ClearImagesAsync(
        BaseItem item,
        ImageType imageType,
        ILogger logger,
        string context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(logger);

        if (imageType == ImageType.Chapter)
        {
            // GetImages throws for chapter images, and we never sync them.
            return 0;
        }

        var existing = SafeCount(item, imageType, logger, context);
        if (existing == 0)
        {
            return 0;
        }

        var removed = 0;
        for (var index = existing - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await item.DeleteImageAsync(imageType, index).ConfigureAwait(false);
                removed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not delete existing {ImageType}[{Index}] for {Context}. The file may survive and be re-adopted on the next library scan",
                    imageType,
                    index,
                    context);
            }
        }

        // Anything DeleteImageAsync could not take, such as an unreadable
        // path or a denied delete, still has to leave the item. Otherwise the
        // apply that follows appends to a stale set and verification reports
        // a count mismatch it can never resolve. Drop those entries in one
        // pass and persist.
        var stragglers = SafeList(item, imageType, logger, context);
        if (stragglers.Count > 0)
        {
            logger.LogWarning(
                "{Count} {ImageType} image(s) survived deletion for {Context}. Dropping the entries so the local set still matches source",
                stragglers.Count,
                imageType,
                context);

            try
            {
                item.RemoveImages(stragglers);
                removed += stragglers.Count;
                await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Persisting the {ImageType} entry drop for {Context} threw", imageType, context);
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Cleared {Count} existing {ImageType} image(s) for {Context}", removed, imageType, context);
        }

        return removed;
    }

    /// <summary>
    /// Groups download work by image type and orders each group by the
    /// source index. Every image is then given a sequential target slot
    /// starting at zero within its own type.
    /// </summary>
    /// <param name="work">Image type plus the source index, which may be null.</param>
    /// <returns>The same images with a target slot assigned, in write order.</returns>
    // Multi image types need this. Jellyfin's SaveImage appends when the
    // target slot does not exist yet, so writing Backdrop slot 2 into an
    // empty set lands it at position 0 and the following write to slot 0
    // overwrites it. Three source backdrops collapse into one. Sequential
    // slots also line up with the local manifest, which numbers images by
    // position, so a source whose indexes are sparse or null still verifies.
    public static List<(string ImageType, int? SourceIndex, int TargetIndex)> AssignSequentialSlots(
        IEnumerable<(string ImageType, int? ImageIndex)> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return work
            .GroupBy(w => w.ImageType, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g
                .OrderBy(w => w.ImageIndex ?? 0)
                .Select((w, slot) => (w.ImageType, SourceIndex: w.ImageIndex, TargetIndex: slot)))
            .ToList();
    }

    private static int SafeCount(BaseItem item, ImageType imageType, ILogger logger, string context)
        => SafeList(item, imageType, logger, context).Count;

    private static List<ItemImageInfo> SafeList(BaseItem item, ImageType imageType, ILogger logger, string context)
    {
        try
        {
            return item.GetImages(imageType).ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not enumerate {ImageType} images for {Context}", imageType, context);
            return new List<ItemImageInfo>();
        }
    }
}

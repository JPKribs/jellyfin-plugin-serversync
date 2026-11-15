using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Clears and orders local images for the metadata and people image applies.
/// </summary>
public static class LocalImageUtilities
{
    /// <summary>
    /// Deletes every local image of one type, both the item entry and the file on disk.
    /// A file left behind would be adopted again by the next library scan and never match the source.
    /// </summary>
    /// <param name="item">The item whose images are cleared.</param>
    /// <param name="imageType">The image type to clear.</param>
    /// <param name="logger">Writes a log entry for each image that could not be deleted.</param>
    /// <param name="context">Names the item in log entries.</param>
    /// <param name="cancellationToken">Cancels the deletion between images.</param>
    /// <returns>How many images were removed.</returns>
    public static async Task<int> ClearImagesAsync(
        BaseItem item,
        ImageType imageType,
        ILogger logger,
        string context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(logger);

        // GetImages throws for chapter images, and they are never synced.
        if (imageType == ImageType.Chapter)
        {
            return 0;
        }

        var existing = SafeList(item, imageType, logger, context).Count;
        if (existing == 0)
        {
            return 0;
        }

        var removed = 0;

        // Deleting index 0 first would shift the rest down and skip every other image.
        for (var index = existing - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await item.DeleteImageAsync(imageType, index).ConfigureAwait(false);
                removed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Could not delete existing {ImageType}[{Index}] for {Context}. The file may survive and be re-adopted on the next library scan",
                    imageType,
                    index,
                    FormatUtilities.SanitizeForLog(context));
            }
        }

        var stragglers = SafeList(item, imageType, logger, context);

        // An entry whose file survived must still go, or the apply appends to it.
        if (stragglers.Count > 0)
        {
            logger.LogWarning(
                "{Count} {ImageType} image(s) survived deletion for {Context}. Dropping the entries so the local set still matches source",
                stragglers.Count,
                imageType,
                FormatUtilities.SanitizeForLog(context));

            try
            {
                item.RemoveImages(stragglers);
                removed += stragglers.Count;
                await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Persisting the {ImageType} entry drop for {Context} threw", imageType, FormatUtilities.SanitizeForLog(context));
            }
        }

        if (removed > 0)
        {
            logger.LogInformation("Cleared {Count} existing {ImageType} image(s) for {Context}", removed, imageType, FormatUtilities.SanitizeForLog(context));
        }

        return removed;
    }

    /// <summary>
    /// Orders the images of each type by source index and gives each a target slot counted from zero.
    /// Jellyfin appends when a slot does not exist yet, so a sparse source index would overwrite an earlier image.
    /// </summary>
    /// <param name="work">Each image's type and its index on the source, which may be null.</param>
    /// <returns>The same images with a target slot assigned, in write order.</returns>
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

    /// <summary>Lists the item's images of one type, or none when Jellyfin cannot list them.</summary>
    private static List<ItemImageInfo> SafeList(BaseItem item, ImageType imageType, ILogger logger, string context)
    {
        try
        {
            return item.GetImages(imageType).ToList();
        }
        // Any failure inside Jellyfin's image listing reads as no images.
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not enumerate {ImageType} images for {Context}", imageType, FormatUtilities.SanitizeForLog(context));
            return new List<ItemImageInfo>();
        }
    }
}

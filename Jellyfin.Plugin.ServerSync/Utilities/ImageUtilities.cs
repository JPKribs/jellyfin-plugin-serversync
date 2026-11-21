using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Everything the modules do with images, in one place.
/// This file holds the actions: the capped download, the replace of an item's images, the clear of one image type, and the slot order.
/// <c>ImageUtilities.Manifest.cs</c> builds and sizes the image manifests, and <c>ImageUtilities.Compare.cs</c> compares them.
/// </summary>
public static partial class ImageUtilities
{
    /// <summary>
    /// Writes a downloaded image to a file, cut off at the largest image a sync takes and at a stalled download.
    /// </summary>
    /// <param name="source">The image as the source sends it.</param>
    /// <param name="path">The file to write, which is replaced when it exists.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>How many bytes were written.</returns>
    public static async Task<long> WriteCappedAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(path);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920))
        {
            await StreamUtilities.CopyWithIdleTimeoutAsync(new CappedReadStream(source, StreamCaps.MaxSyncedImageBytes), file, StreamUtilities.DefaultIdleTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return new FileInfo(path).Length;
    }

    /// <summary>
    /// Replaces an item's images with the source's, one image type at a time.
    /// A type's images are all downloaded to temporary files first, since clearing the local ones is a hard delete. A type with a failed download keeps its local images.
    /// The item is not saved here, so the caller saves it when the result says it changed.
    /// </summary>
    /// <param name="item">The local item whose images are replaced.</param>
    /// <param name="work">The images to bring over, each with the slot it is saved to.</param>
    /// <param name="download">Opens one image on the source, or answers a null stream when it cannot.</param>
    /// <param name="providerManager">Saves an image to the item.</param>
    /// <param name="tempDirectory">Where the downloads are held until they are saved.</param>
    /// <param name="logger">Writes a log entry for each image that did not land.</param>
    /// <param name="context">Names the item in log entries.</param>
    /// <param name="cancellationToken">Cancels the apply between images.</param>
    /// <returns>What changed and what did not land.</returns>
    public static async Task<ImageApplyResult> ReplaceAsync(
        BaseItem item,
        IReadOnlyList<ImageToApply> work,
        Func<ImageToApply, CancellationToken, Task<(Stream? Stream, string? ContentType)>> download,
        IProviderManager providerManager,
        string tempDirectory,
        ILogger logger,
        string context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(providerManager);
        ArgumentException.ThrowIfNullOrEmpty(tempDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var name = FormatUtilities.SanitizeForLog(context);
        var errors = new List<string>();
        var changed = false;
        var saved = 0;
        var downloadFailures = 0;
        var saveFailures = 0;

        foreach (var group in work.GroupBy(w => w.ImageType, StringComparer.OrdinalIgnoreCase))
        {
            var typeName = group.Key;
            if (!Enum.TryParse<ImageType>(typeName, true, out var imageType))
            {
                logger.LogWarning("Image apply for {Item}: unknown image type {ImageType}", name, FormatUtilities.SanitizeForLog(typeName));
                errors.Add($"{typeName}: unknown image type");
                continue;
            }

            // One failed image fails its type, and the other types still go on.
            var typeErrors = new List<string>();
            var files = new List<(ImageToApply Image, string Path, string ContentType, long Bytes)>();
            try
            {
                foreach (var image in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var file = await DownloadAsync(image, download, tempDirectory, cancellationToken).ConfigureAwait(false);
                        files.Add((image, file.Path, file.ContentType, file.Bytes));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        downloadFailures++;
                        logger.LogWarning(ex, "Image apply for {Item}: could not download {ImageType}/{Index}", name, FormatUtilities.SanitizeForLog(typeName), image.TargetIndex);
                        typeErrors.Add($"download index {image.TargetIndex}: {ex.Message}");
                    }
                }

                // A failed download keeps the existing images, so no type is half replaced.
                if (typeErrors.Count > 0)
                {
                    logger.LogWarning(
                        "Image apply for {Item}: not all {ImageType} downloads succeeded ({Downloaded}/{Total}), keeping existing local images",
                        name,
                        FormatUtilities.SanitizeForLog(typeName),
                        files.Count,
                        group.Count());
                    errors.Add($"{typeName}: {string.Join("; ", typeErrors)} (kept existing local images)");
                    continue;
                }

                // The type is wiped first, or a leftover image would be adopted again.
                if (await ClearImagesAsync(item, imageType, logger, context, cancellationToken).ConfigureAwait(false) > 0)
                {
                    changed = true;
                }

                foreach (var file in files.OrderBy(f => f.Image.TargetIndex))
                {
                    try
                    {
                        var before = CountImages(item, imageType);
                        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await providerManager.SaveImage(item, stream, file.ContentType, imageType, file.Image.TargetIndex, cancellationToken).ConfigureAwait(false);
                        changed = true;
                        saved++;

                        // A save that adds no image did nothing, which the log should show.
                        var after = CountImages(item, imageType);
                        if (after <= before)
                        {
                            logger.LogWarning(
                                "Image apply for {Item}: saving {ImageType}/{Index} ({Bytes} bytes) added no image, and the local count stayed at {Count}",
                                name,
                                FormatUtilities.SanitizeForLog(typeName),
                                file.Image.TargetIndex,
                                file.Bytes,
                                after);
                        }
                        else
                        {
                            logger.LogDebug("Image apply for {Item}: saved {ImageType}/{Index} ({Bytes} bytes)", name, FormatUtilities.SanitizeForLog(typeName), file.Image.TargetIndex, file.Bytes);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        saveFailures++;
                        logger.LogWarning(ex, "Image apply for {Item}: could not save {ImageType}/{Index}", name, FormatUtilities.SanitizeForLog(typeName), file.Image.TargetIndex);
                        typeErrors.Add($"save index {file.Image.TargetIndex}: {ex.Message}");
                    }
                }

                if (typeErrors.Count > 0)
                {
                    errors.Add($"{typeName}: {string.Join("; ", typeErrors)}");
                }
            }
            finally
            {
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file.Path);
                    }
                    catch (IOException)
                    {
                        // Cleanup is best effort, and the temp cleanup task takes what is left.
                    }
                }
            }
        }

        return new ImageApplyResult(changed, work.Count, saved, downloadFailures, saveFailures, errors);
    }

    /// <summary>
    /// Downloads one image to a temporary file, and throws when the source sends nothing, an empty body, or a body that is not an image.
    /// </summary>
    private static async Task<(string Path, string ContentType, long Bytes)> DownloadAsync(
        ImageToApply image,
        Func<ImageToApply, CancellationToken, Task<(Stream? Stream, string? ContentType)>> download,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        var (stream, contentType) = await download(image, cancellationToken).ConfigureAwait(false);
        if (stream == null)
        {
            throw new InvalidOperationException("returned null");
        }

        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            long bytes;
            await using (stream.ConfigureAwait(false))
            {
                // What is saved is named by its type, so only an image is taken. Anything else is an error page.
                if (!string.IsNullOrEmpty(contentType) && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"the source answered {contentType}, which is not an image");
                }

                bytes = await WriteCappedAsync(stream, path, cancellationToken).ConfigureAwait(false);
            }

            // An empty body would save nothing silently, so it counts as failed.
            if (bytes == 0)
            {
                throw new InvalidOperationException("the source answered 0 bytes");
            }

            return (path, string.IsNullOrEmpty(contentType) ? "image/jpeg" : contentType, bytes);
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Cleanup is best effort.
            }

            throw;
        }
    }

    /// <summary>Counts the item's images of one type, or zero when Jellyfin cannot list them.</summary>
    private static int CountImages(BaseItem item, ImageType imageType)
    {
        try
        {
            return item.GetImages(imageType).Count();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            return 0;
        }
    }

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
            logger.LogDebug("Cleared {Count} existing {ImageType} image(s) for {Context}", removed, imageType, FormatUtilities.SanitizeForLog(context));
        }

        return removed;
    }

    /// <summary>
    /// Orders the images of each type by source index and gives each a target slot counted from zero.
    /// Jellyfin appends when a slot does not exist yet, so a sparse source index would overwrite an earlier image.
    /// </summary>
    /// <param name="work">Each image's type and its index on the source, which may be null.</param>
    /// <returns>The same images with a target slot assigned, in write order.</returns>
    public static List<ImageToApply> AssignSequentialSlots(
        IEnumerable<(string ImageType, int? ImageIndex)> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work
            .GroupBy(w => w.ImageType, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g
                .OrderBy(w => w.ImageIndex ?? 0)
                .Select((w, slot) => new ImageToApply(w.ImageType, w.ImageIndex, slot)))
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

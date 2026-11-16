using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Common.Comparators;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Compares images: an item's manifests after an apply, and one picture by its hash and size.
/// </summary>
public static partial class ImageUtilities
{
    /// <summary>
    /// Compares the local image manifest with the source's after an apply, with real sizes on the source side, and on a match records each pair of tags.
    /// The caller reads the local manifest into <paramref name="images"/> first.
    /// </summary>
    /// <param name="images">The row's image manifests.</param>
    /// <param name="sourceItemId">The source item the images belong to, or null to compare without sizes.</param>
    /// <param name="client">The client for the source, or null to compare without sizes.</param>
    /// <param name="logger">Writes a log entry when the sizes cannot be read.</param>
    /// <param name="context">Names the item in log entries.</param>
    /// <param name="prepareSource">Changes the source manifest before the comparison, or null to compare it as it is.</param>
    /// <param name="cancellationToken">Cancels the request for sizes.</param>
    /// <returns>Whether the manifests match, and what differs when they do not.</returns>
    public static async Task<(bool Succeeded, string? FailureReason)> VerifyAsync(
        SyncableValue<string> images,
        Guid? sourceItemId,
        SourceServerClient? client,
        ILogger logger,
        string context,
        Func<string?, string?>? prepareSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(logger);

        // Scan manifests carry no sizes, so the source side gets real sizes here.
        var source = images.Source;
        if (client != null && sourceItemId is { } id)
        {
            try
            {
                source = await EnrichAsync(images.Source, id, client, logger, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Source image enrichment for verify failed for {Item}. Falling back to tag-only comparison", FormatUtilities.SanitizeForLog(context));
            }
        }

        if (prepareSource != null)
        {
            source = prepareSource(source);
        }

        if (images.Comparator is ImageManifestComparator comparator)
        {
            var difference = comparator.DescribeDifference(source, images.Local);
            if (difference != null)
            {
                return (false, $"image manifest after apply does not match source manifest: {difference}");
            }

            // The sizes just matched, so each local image takes its source tag and later scans compare by tag.
            images.Local = RecordTagPairs(source, images.Local);
            return (true, null);
        }

        return images.Comparator.Equals(source, images.Local)
            ? (true, null)
            : (false, "image manifest after apply does not match source manifest");
    }

    /// <summary>
    /// Describes the first difference between a source manifest and a local one, naming the image type so a failed check points at the cause.
    /// A local image matched before is compared by tag alone. Any other is compared by file size, where a source size of zero was never measured and is not a difference.
    /// </summary>
    /// <param name="source">The source's manifest, as JSON.</param>
    /// <param name="local">This server's manifest, as JSON.</param>
    /// <returns>The first difference in words, or null when they match.</returns>
    public static string? DescribeManifestDifference(string? source, string? local)
    {
        // An empty source has nothing to apply, so local images are left alone.
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        if (string.IsNullOrEmpty(local))
        {
            return "local manifest empty, source non-empty";
        }

        Dictionary<string, List<ImageInfoDto>>? sourceMap;
        Dictionary<string, List<ImageInfoDto>>? localMap;
        try
        {
            sourceMap = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(source);
            localMap = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(local);
        }
        catch (JsonException ex)
        {
            return $"manifest parse error: {ex.Message}";
        }

        if (sourceMap == null && localMap == null)
        {
            return null;
        }

        if (sourceMap == null)
        {
            return "source manifest deserialized to null";
        }

        if (localMap == null)
        {
            return "local manifest deserialized to null";
        }

        foreach (var (type, sourceImages) in sourceMap)
        {
            if (!localMap.TryGetValue(type, out var localImages))
            {
                return $"type {type} present on source ({ActivityText.Count(sourceImages.Count, "image")}) but missing on local";
            }

            var difference = MatchType(type, sourceImages, localImages).Difference;
            if (difference != null)
            {
                return difference;
            }
        }

        // An image type only this server holds is left alone.
        return null;
    }

    /// <summary>
    /// Whether a comparison of the two manifests would turn on a source size no one has measured.
    /// A scan asks the source for sizes only then, so a row whose tags settle it costs no call.
    /// </summary>
    /// <param name="source">The source's manifest, as JSON.</param>
    /// <param name="local">This server's manifest, as JSON.</param>
    /// <returns>True when sizes from the source would change the answer.</returns>
    public static bool NeedsSourceSizes(string? source, string? local)
    {
        if (!TryReadBoth(source, local, out var sourceMap, out var localMap))
        {
            return false;
        }

        foreach (var (type, sourceImages) in sourceMap)
        {
            if (localMap.TryGetValue(type, out var localImages))
            {
                var match = MatchType(type, sourceImages, localImages);
                if (match.Difference == null && match.Unmeasured)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the source tag onto each local image that matched a source image by size, so the next scan compares the pair by tag alone.
    /// An image type that differs is left as it is, since an apply replaces it.
    /// </summary>
    /// <param name="source">The source's manifest, as JSON.</param>
    /// <param name="local">This server's manifest, as JSON.</param>
    /// <returns>The local manifest with the new pairs, or the input when there are none.</returns>
    public static string? RecordTagPairs(string? source, string? local)
    {
        if (!TryReadBoth(source, local, out var sourceMap, out var localMap))
        {
            return local;
        }

        var recorded = false;
        foreach (var (type, sourceImages) in sourceMap)
        {
            if (!localMap.TryGetValue(type, out var localImages))
            {
                continue;
            }

            var match = MatchType(type, sourceImages, localImages);
            if (match.Difference != null)
            {
                continue;
            }

            foreach (var (sourceImage, localImage) in match.MatchedBySize)
            {
                if (!string.IsNullOrEmpty(sourceImage.Tag))
                {
                    localImage.SyncedTag = sourceImage.Tag;
                    recorded = true;
                }
            }
        }

        return recorded ? JsonSerializer.Serialize(localMap) : local;
    }

    /// <summary>
    /// Matches one image type's source images to its local ones. Jellyfin keeps images in no fixed order, so they match as a set.
    /// </summary>
    private static (string? Difference, bool Unmeasured, List<(ImageInfoDto Source, ImageInfoDto Local)> MatchedBySize) MatchType(
        string type,
        List<ImageInfoDto> sourceImages,
        List<ImageInfoDto> localImages)
    {
        var bySize = new List<(ImageInfoDto Source, ImageInfoDto Local)>();
        if (sourceImages.Count != localImages.Count)
        {
            return ($"type {type}: source has {ActivityText.Count(sourceImages.Count, "image")}, local has {localImages.Count}", false, bySize);
        }

        // A local image carries the tag of the source image it was matched to, so an unchanged tag settles the pair.
        var unmatched = localImages.ToList();
        var pending = new List<ImageInfoDto>();
        foreach (var sourceImage in sourceImages)
        {
            var paired = string.IsNullOrEmpty(sourceImage.Tag)
                ? null
                : unmatched.FirstOrDefault(l => string.Equals(l.SyncedTag, sourceImage.Tag, StringComparison.Ordinal));
            if (paired != null)
            {
                unmatched.Remove(paired);
            }
            else
            {
                pending.Add(sourceImage);
            }
        }

        var unmeasured = false;
        foreach (var sourceImage in pending.OrderByDescending(s => s.Size))
        {
            var unpaired = unmatched.Where(l => string.IsNullOrEmpty(l.SyncedTag)).ToList();
            if (unpaired.Count == 0)
            {
                // Every local image left was matched to another tag, so the source's image is a new one.
                return ($"type {type}: the source image changed since it was last matched", false, bySize);
            }

            // A size of 0 was never measured, which is unknown and not a difference.
            if (sourceImage.Size <= 0)
            {
                unmeasured = true;
                continue;
            }

            var sameSize = unpaired.FirstOrDefault(l => l.Size == sourceImage.Size);
            if (sameSize == null)
            {
                return ($"type {type}: source image of size {sourceImage.Size} has no local match (unmatched local sizes: {string.Join(",", unpaired.Select(l => l.Size))})", false, bySize);
            }

            unmatched.Remove(sameSize);
            bySize.Add((sourceImage, sameSize));
        }

        return (null, unmeasured, bySize);
    }

    /// <summary>Reads both manifests, and answers false when either is missing, empty, or unreadable.</summary>
    private static bool TryReadBoth(
        string? source,
        string? local,
        out Dictionary<string, List<ImageInfoDto>> sourceMap,
        out Dictionary<string, List<ImageInfoDto>> localMap)
    {
        sourceMap = new Dictionary<string, List<ImageInfoDto>>();
        localMap = new Dictionary<string, List<ImageInfoDto>>();
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(local))
        {
            return false;
        }

        try
        {
            var readSource = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(source);
            var readLocal = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(local);
            if (readSource == null || readLocal == null)
            {
                return false;
            }

            sourceMap = readSource;
            localMap = readLocal;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a hash or a size says a picture exists.
    /// </summary>
    /// <param name="hash">The picture's hash, or null.</param>
    /// <param name="size">The picture's size in bytes, or null.</param>
    /// <returns>True when either names a picture.</returns>
    public static bool HasPicture(string? hash, long? size) => !string.IsNullOrEmpty(hash) || (size ?? 0) > 0;

    /// <summary>
    /// Whether a source picture is the one already read: its tag is the tag its stored hash was read under.
    /// A scan then keeps the stored hash and does not download the picture again.
    /// </summary>
    /// <param name="tag">The source picture's tag now, or null when the source has none.</param>
    /// <param name="priorTag">The tag the stored hash was read under, or null.</param>
    /// <param name="priorHash">The stored hash, or null.</param>
    /// <returns>True when the stored hash still stands.</returns>
    public static bool PictureTagUnchanged(string? tag, string? priorTag, string? priorHash)
        => !string.IsNullOrEmpty(tag) && !string.IsNullOrEmpty(priorHash) && string.Equals(tag, priorTag, StringComparison.Ordinal);

    /// <summary>
    /// Whether a local picture differs from the source's. The hash decides when the source has one, and the size when it does not.
    /// A picture the source no longer has differs for as long as this server still holds one.
    /// </summary>
    /// <param name="sourceHash">The source picture's hash, or null.</param>
    /// <param name="sourceSize">The source picture's size in bytes, or null.</param>
    /// <param name="localHash">The local picture's hash, or null.</param>
    /// <param name="localSize">The local picture's size in bytes, or null.</param>
    /// <returns>True when the two differ.</returns>
    public static bool PictureDiffers(string? sourceHash, long? sourceSize, string? localHash, long? localSize)
        => DescribePictureMismatch(sourceHash, sourceSize, localHash, localSize) != null;

    /// <summary>
    /// Says how a local picture differs from the source's after an apply, by the rule of <see cref="PictureDiffers"/>.
    /// </summary>
    /// <param name="sourceHash">The source picture's hash, or null.</param>
    /// <param name="sourceSize">The source picture's size in bytes, or null.</param>
    /// <param name="localHash">The local picture's hash, or null.</param>
    /// <param name="localSize">The local picture's size in bytes, or null.</param>
    /// <returns>What differs, or null when the two match.</returns>
    public static string? DescribePictureMismatch(string? sourceHash, long? sourceSize, string? localHash, long? localSize)
    {
        if (!HasPicture(sourceHash, sourceSize))
        {
            return HasPicture(localHash, localSize) ? "local still has an image after apply intended to clear it" : null;
        }

        if (!string.IsNullOrEmpty(sourceHash))
        {
            return string.Equals(sourceHash, localHash, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"hash mismatch after apply (source {sourceHash}, local {localHash})";
        }

        return sourceSize == localSize ? null : $"size mismatch after apply (source {sourceSize}, local {localSize})";
    }

    /// <summary>
    /// Reads a picture file's SHA-256 hash and its size from one pass over the file.
    /// </summary>
    /// <param name="path">The picture file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The hash and the size in bytes.</returns>
    public static async Task<(string Hash, long Size)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        var (hash, bytes) = await HashUtilities.ComputeSha256HashAsync(stream, cancellationToken).ConfigureAwait(false);
        return (hash, bytes);
    }
}

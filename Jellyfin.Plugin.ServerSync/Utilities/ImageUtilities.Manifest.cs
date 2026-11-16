using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SourceItemDto = Jellyfin.Sdk.Generated.Models.BaseItemDto;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Builds the image manifests a scan compares, and fills the source side with sizes.
/// </summary>
public static partial class ImageUtilities
{
    /// <summary>
    /// Builds a source item's image manifest from the image tags its listing already carries, which saves a call for each item.
    /// </summary>
    /// <param name="source">The item as the source returned it.</param>
    /// <param name="withoutTypes">Image types to leave out, or null to keep every type.</param>
    /// <returns>The manifest as JSON, or null when the item has no images.</returns>
    public static string? BuildSourceManifest(SourceItemDto source, IReadOnlyCollection<string>? withoutTypes = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var byType = new Dictionary<string, List<ImageInfoDto>>();
        if (source.ImageTags?.AdditionalData != null)
        {
            foreach (var (type, value) in source.ImageTags.AdditionalData)
            {
                if (withoutTypes != null && withoutTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var tag = MediaItemUtilities.UnwrapKiotaPrimitive(value);
                if (!string.IsNullOrEmpty(tag))
                {
                    byType[type] = new List<ImageInfoDto> { new() { ImageType = type, ImageIndex = 0, Tag = tag } };
                }
            }
        }

        // An item with only backdrops has no image tags, so this stands apart.
        if (source.BackdropImageTags?.Count > 0)
        {
            byType["Backdrop"] = source.BackdropImageTags
                .Select((tag, index) => new ImageInfoDto { ImageType = "Backdrop", ImageIndex = index, Tag = tag })
                .ToList();
        }

        return byType.Count > 0 ? JsonSerializer.Serialize(byType) : null;
    }

    /// <summary>
    /// Builds a local item's image manifest. Each image carries a mark of its file and when it was written.
    /// An image whose mark is unchanged since the prior manifest keeps that entry's size and the source tag it was matched to, so its file is not read again.
    /// </summary>
    /// <param name="item">The local item whose images are read.</param>
    /// <param name="imageTypes">The image types to read.</param>
    /// <param name="priorManifestJson">The local manifest the row held before, or null to measure every image and match none.</param>
    /// <returns>The manifest as JSON, or null when the item has none of those images.</returns>
    public static string? BuildLocalManifest(BaseItem item, IReadOnlyList<ImageType> imageTypes, string? priorManifestJson = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(imageTypes);

        var prior = ReadManifest(priorManifestJson);
        var byType = new Dictionary<string, List<ImageInfoDto>>();
        foreach (var imageType in imageTypes)
        {
            var images = item.GetImages(imageType).ToList();
            if (images.Count == 0)
            {
                continue;
            }

            var known = prior != null && prior.TryGetValue(imageType.ToString(), out var priorEntries)
                ? priorEntries.Where(e => !string.IsNullOrEmpty(e.Tag)).ToList()
                : new List<ImageInfoDto>();
            var entries = new List<ImageInfoDto>();
            for (var index = 0; index < images.Count; index++)
            {
                var image = images[index];
                var mark = LocalMark(image);
                var same = known.FirstOrDefault(e => string.Equals(e.Tag, mark, StringComparison.Ordinal));
                if (same != null)
                {
                    known.Remove(same);
                }

                entries.Add(new ImageInfoDto
                {
                    ImageType = imageType.ToString(),
                    ImageIndex = index,
                    Size = same is { Size: > 0 } ? same.Size : FileSize(image.Path),
                    Width = image.Width,
                    Height = image.Height,
                    Tag = mark,
                    SyncedTag = same?.SyncedTag
                });
            }

            byType[imageType.ToString()] = entries;
        }

        return byType.Count > 0 ? JsonSerializer.Serialize(byType) : null;
    }

    /// <summary>
    /// Reads both image manifests for a scan and compares as little as it can.
    /// The regular check settles an image by its tag when a scan or apply matched it before, and asks the source for sizes only for an image never matched.
    /// The deep check measures every image again and compares sizes, whatever the tags say.
    /// When the source cannot be asked for sizes, the sizes the row already held are kept.
    /// </summary>
    /// <param name="source">The item as the source returned it.</param>
    /// <param name="local">The matching local item, or null when there is none.</param>
    /// <param name="localTypes">The local image types to read.</param>
    /// <param name="priorSourceJson">The source manifest the row held before this scan, or null.</param>
    /// <param name="priorLocalJson">The local manifest the row held before this scan, or null.</param>
    /// <param name="sourceItemId">The item's id on the source, or null to skip sizing.</param>
    /// <param name="client">The client for the source, or null to skip sizing.</param>
    /// <param name="deepVerification">Whether to measure every image again and compare by size alone.</param>
    /// <param name="withoutTypes">Source image types to leave out, or null to keep every type.</param>
    /// <param name="logger">Writes a debug entry when sizing fails.</param>
    /// <param name="context">Names the item in log entries.</param>
    /// <param name="cancellationToken">Cancels the calls that size the images.</param>
    /// <returns>The source and local manifests as JSON. Each is null when its side has no images, and the local one is null with no local item.</returns>
    public static async Task<(string? Source, string? Local)> ReadManifestsAsync(
        SourceItemDto source,
        BaseItem? local,
        IReadOnlyList<ImageType> localTypes,
        string? priorSourceJson,
        string? priorLocalJson,
        Guid? sourceItemId,
        SourceServerClient? client,
        bool deepVerification,
        IReadOnlyCollection<string>? withoutTypes,
        ILogger logger,
        string context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var sourceManifest = CarryForwardSizes(BuildSourceManifest(source, withoutTypes), priorSourceJson);

        // The deep check drops what was matched before, so every image is measured and compared by size.
        var localManifest = local == null ? null : BuildLocalManifest(local, localTypes, deepVerification ? null : priorLocalJson);

        if (sourceManifest != null
            && client != null
            && sourceItemId is { } id
            && (deepVerification || NeedsSourceSizes(sourceManifest, localManifest)))
        {
            try
            {
                sourceManifest = await EnrichWithCarryForwardAsync(sourceManifest, priorSourceJson, id, client, logger, context, deepVerification, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Sizing is optional, and the carried sizes still stand.
                logger.LogDebug(ex, "Source image sizing failed for {Item}. The comparison falls back to tags", FormatUtilities.SanitizeForLog(context));
            }
        }

        return (sourceManifest, RecordTagPairs(sourceManifest, localManifest));
    }

    /// <summary>
    /// Removes image types from a manifest, and returns it unchanged when it holds none of them or cannot be read.
    /// </summary>
    /// <param name="manifestJson">The manifest as JSON.</param>
    /// <param name="types">The image types to remove.</param>
    /// <returns>The manifest without those types.</returns>
    public static string? WithoutTypes(string? manifestJson, IReadOnlyCollection<string> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (string.IsNullOrEmpty(manifestJson))
        {
            return manifestJson;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(manifestJson);
            var removed = manifest?.Keys.Where(key => types.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList();
            if (manifest == null || removed == null || removed.Count == 0)
            {
                return manifestJson;
            }

            foreach (var key in removed)
            {
                manifest.Remove(key);
            }

            return JsonSerializer.Serialize(manifest);
        }
        catch (JsonException)
        {
            return manifestJson;
        }
    }

    /// <summary>
    /// Fills a tag-only manifest with sizes, reusing the prior sizes of unchanged tags and calling the source only for the rest.
    /// Only <paramref name="deepVerification"/> catches an image replaced on the source's disk under the same tag.
    /// </summary>
    /// <param name="freshManifestJson">The image manifest this scan read from the source, as JSON.</param>
    /// <param name="priorSourceManifestJson">The manifest the row held before this scan, as JSON.</param>
    /// <param name="sourceItemId">The item's id on the source.</param>
    /// <param name="client">The client for the source server.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="logContext">Names the item in log entries.</param>
    /// <param name="deepVerification">Whether to measure every image again even when its tag is unchanged.</param>
    /// <param name="cancellationToken">Cancels the calls to the source.</param>
    /// <returns>The manifest with sizes filled in.</returns>
    public static async Task<string?> EnrichWithCarryForwardAsync(
        string? freshManifestJson,
        string? priorSourceManifestJson,
        Guid sourceItemId,
        SourceServerClient client,
        ILogger logger,
        string logContext,
        bool deepVerification,
        CancellationToken cancellationToken)
    {
        var carried = CarryForwardSizes(freshManifestJson, priorSourceManifestJson);
        if (!deepVerification && HasAllSizes(carried))
        {
            return carried;
        }

        var enriched = await EnrichAsync(carried, sourceItemId, client, logger, logContext, cancellationToken).ConfigureAwait(false);
        return CarryForwardSizes(enriched, priorSourceManifestJson);
    }

    /// <summary>
    /// Whether every image in the manifest has a measured size. A manifest that is empty or cannot be read counts as unsized.
    /// </summary>
    private static bool HasAllSizes(string? manifestJson)
    {
        if (string.IsNullOrEmpty(manifestJson))
        {
            return false;
        }

        Dictionary<string, List<ImageInfoDto>>? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(manifestJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (manifest == null || manifest.Count == 0)
        {
            return false;
        }

        foreach (var entries in manifest.Values)
        {
            foreach (var entry in entries)
            {
                if (entry.Size <= 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Fills a manifest with the sizes the source reports, and returns the input unchanged on any failure.
    /// </summary>
    /// <param name="sourceManifestJson">The image manifest the source reports, as JSON.</param>
    /// <param name="sourceItemId">The item's id on the source.</param>
    /// <param name="client">The client for the source server.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="logContext">Names the item in log entries.</param>
    /// <param name="cancellationToken">Cancels the calls to the source.</param>
    /// <returns>The enriched manifest, or the input unchanged when enrichment failed.</returns>
    public static async Task<string?> EnrichAsync(
        string? sourceManifestJson,
        Guid sourceItemId,
        SourceServerClient client,
        ILogger logger,
        string logContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrEmpty(sourceManifestJson)) return sourceManifestJson;

        Dictionary<string, List<ImageInfoDto>>? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(sourceManifestJson);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Could not deserialize source image manifest for {Context}", FormatUtilities.SanitizeForLog(logContext));
            return sourceManifestJson;
        }

        if (manifest == null || manifest.Count == 0) return sourceManifestJson;

        List<Jellyfin.Sdk.Generated.Models.ImageInfo>? sourceInfo;
        try
        {
            sourceInfo = await client.GetItemImageInfoAsync(sourceItemId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Source image-info enrichment failed for {Context}", FormatUtilities.SanitizeForLog(logContext));
            return sourceManifestJson;
        }

        if (sourceInfo == null || sourceInfo.Count == 0) return sourceManifestJson;

        var byKey = new Dictionary<(string Type, int Index), Jellyfin.Sdk.Generated.Models.ImageInfo>();
        foreach (var info in sourceInfo)
        {
            var typeName = info.ImageType?.ToString();
            if (string.IsNullOrEmpty(typeName)) continue;
            byKey[(typeName, info.ImageIndex ?? 0)] = info;
        }

        var anyChanged = false;
        foreach (var kvp in manifest)
        {
            foreach (var entry in kvp.Value)
            {
                if (byKey.TryGetValue((kvp.Key, entry.ImageIndex), out var info))
                {
                    if (info.Size.HasValue) entry.Size = info.Size.Value;
                    if (info.Width.HasValue) entry.Width = info.Width.Value;
                    if (info.Height.HasValue) entry.Height = info.Height.Value;
                    anyChanged = true;
                }

                int? imageIndex = kvp.Key == "Backdrop" || kvp.Value.Count > 1 ? entry.ImageIndex : null;
                var headSize = await client.GetItemImageContentLengthAsync(sourceItemId, kvp.Key, imageIndex, cancellationToken).ConfigureAwait(false);

                // The reported size goes stale when a file is replaced, so the length wins.
                if (headSize.HasValue && headSize.Value != entry.Size)
                {
                    entry.Size = headSize.Value;
                    anyChanged = true;
                }
            }
        }

        return anyChanged ? JsonSerializer.Serialize(manifest) : sourceManifestJson;
    }

    /// <summary>
    /// Fills missing sizes on a tag-only manifest from the stored one, for images whose tag is unchanged.
    /// A failed or skipped call to the source then never reads as a change, and a changed tag stays unsized so it is still detected.
    /// </summary>
    /// <param name="newManifestJson">The manifest to fill in sizes for, as JSON.</param>
    /// <param name="oldManifestJson">The manifest whose measured sizes are reused, as JSON.</param>
    /// <returns>The new manifest with the old sizes filled in.</returns>
    public static string? CarryForwardSizes(string? newManifestJson, string? oldManifestJson)
    {
        if (string.IsNullOrEmpty(newManifestJson) || string.IsNullOrEmpty(oldManifestJson))
        {
            return newManifestJson;
        }

        Dictionary<string, List<ImageInfoDto>>? newManifest;
        Dictionary<string, List<ImageInfoDto>>? oldManifest;
        try
        {
            newManifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(newManifestJson);
            oldManifest = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(oldManifestJson);
        }
        catch (JsonException)
        {
            return newManifestJson;
        }

        if (newManifest == null || newManifest.Count == 0 || oldManifest == null || oldManifest.Count == 0)
        {
            return newManifestJson;
        }

        var carried = false;
        foreach (var kvp in newManifest)
        {
            if (!oldManifest.TryGetValue(kvp.Key, out var oldEntries))
            {
                continue;
            }

            foreach (var entry in kvp.Value)
            {
                if (entry.Size > 0 || string.IsNullOrEmpty(entry.Tag))
                {
                    continue;
                }

                var match = oldEntries.FirstOrDefault(o => o.Tag == entry.Tag && o.Size > 0);
                if (match == null)
                {
                    continue;
                }

                entry.Size = match.Size;
                entry.Width = match.Width;
                entry.Height = match.Height;
                carried = true;
            }
        }

        return carried ? JsonSerializer.Serialize(newManifest) : newManifestJson;
    }

    /// <summary>
    /// Marks a local image by its file and when it was written, which is what Jellyfin builds its own image tag from.
    /// </summary>
    private static string LocalMark(ItemImageInfo image)
        => HashUtilities.Sha256Hex(image.Path + "|" + image.DateModified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Reads a local image file's size, or 0 when it is not a file that can be read.</summary>
    private static long FileSize(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return 0;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // An unreadable image keeps a size of 0.
            return 0;
        }
    }

    /// <summary>Reads a manifest, or null when it is missing, empty, or unreadable.</summary>
    private static Dictionary<string, List<ImageInfoDto>>? ReadManifest(string? manifestJson)
    {
        if (string.IsNullOrEmpty(manifestJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(manifestJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

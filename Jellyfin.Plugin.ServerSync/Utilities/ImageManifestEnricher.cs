using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Adds size, width, and height to a source image manifest that holds tags only.
/// </summary>
public static class ImageManifestEnricher
{
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
}

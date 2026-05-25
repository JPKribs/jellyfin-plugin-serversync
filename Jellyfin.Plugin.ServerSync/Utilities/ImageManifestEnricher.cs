using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Adds Size / Width / Height to a tag-only source image manifest via one HTTP call.
/// </summary>
public static class ImageManifestEnricher
{
    /// <summary>
    /// Returns the enriched manifest JSON, or the input unchanged on any failure.
    /// </summary>
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
            logger.LogDebug(ex, "Could not deserialize source image manifest for {Context}", logContext);
            return sourceManifestJson;
        }

        if (manifest == null || manifest.Count == 0) return sourceManifestJson;

        List<Jellyfin.Sdk.Generated.Models.ImageInfo>? sourceInfo;
        try
        {
            sourceInfo = await client.GetItemImageInfoAsync(sourceItemId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Source image-info enrichment failed for {Context}", logContext);
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
            for (var i = 0; i < kvp.Value.Count; i++)
            {
                var entry = kvp.Value[i];
                if (byKey.TryGetValue((kvp.Key, entry.ImageIndex), out var info))
                {
                    if (info.Size.HasValue) entry.Size = info.Size.Value;
                    if (info.Width.HasValue) entry.Width = info.Width.Value;
                    if (info.Height.HasValue) entry.Height = info.Height.Value;
                    anyChanged = true;
                }

                // /Items/{id}/Images reads Size from Jellyfin's in-memory
                // item state, which goes stale when the image file is
                // replaced on disk without a metadata rescan. HEAD the
                // actual image URL — Jellyfin's image controller serves
                // the file directly, so Content-Length is the current
                // file length and the comparator sees the replacement
                // even when Jellyfin hasn't re-indexed.
                int? imageIndex = kvp.Key == "Backdrop" || kvp.Value.Count > 1 ? entry.ImageIndex : null;
                var headSize = await client.GetItemImageContentLengthAsync(sourceItemId, kvp.Key, imageIndex, cancellationToken).ConfigureAwait(false);
                if (headSize.HasValue && headSize.Value != entry.Size)
                {
                    entry.Size = headSize.Value;
                    anyChanged = true;
                }
            }
        }

        return anyChanged ? JsonSerializer.Serialize(manifest) : sourceManifestJson;
    }
}

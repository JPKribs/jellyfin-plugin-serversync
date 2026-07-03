using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Enriches a freshly built tag-only manifest, preferring carried-forward
    /// sizes over live HTTP. Flow:
    /// <list type="number">
    /// <item>Carry sizes forward from <paramref name="priorSourceManifestJson"/>
    /// for entries whose Tag is unchanged (same image → same size).</item>
    /// <item>If every entry now has a size and <paramref name="deepVerification"/>
    /// is off, return without any HTTP — the steady-state path. This is what
    /// keeps refresh from issuing one GET + one HEAD per image per item per
    /// run on libraries where nothing changed.</item>
    /// <item>Otherwise fall through to <see cref="EnrichAsync"/> (per-item GET
    /// + per-image HEAD), then carry forward once more so a partial enrichment
    /// failure doesn't read as a phantom change.</item>
    /// </list>
    /// A changed Tag always misses the carry-forward and triggers live
    /// enrichment, so real image changes are still detected. The case only
    /// <paramref name="deepVerification"/> catches: an image file replaced on
    /// the source's disk without a metadata rescan (same Tag, different bytes).
    /// </summary>
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
    /// True when every image entry in the manifest has a measured size.
    /// Unparseable or empty manifests return false so callers fall through to
    /// live enrichment rather than skipping on bad data.
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

    /// <summary>
    /// Fills missing Size / Width / Height on a freshly built (tag-only) source
    /// manifest from the previously stored manifest, for images whose Tag is
    /// unchanged. Refresh rebuilds the source side tag-only every run and only
    /// enriches it with a live HTTP call; when that call fails or is skipped,
    /// the row would otherwise drop back to Size=0 and read as a change against
    /// the sized local side until the next successful enrichment. An unchanged
    /// Tag means the same image, so the previously measured size still holds —
    /// carrying it forward keeps the comparison honest with no extra HTTP. A
    /// changed Tag is a real new image and is left tag-only so the change is
    /// still detected.
    /// </summary>
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

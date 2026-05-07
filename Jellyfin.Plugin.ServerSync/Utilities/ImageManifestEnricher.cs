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
/// Enriches a source-side image manifest (built from <c>BaseItemDto.ImageTags</c>
/// during refresh, which is tag-only with Size=0) with real Size / Width /
/// Height fetched from the source server. The refresh skips this enrichment
/// for performance; the per-modal-open path uses this helper so the modal
/// renders honest values instead of "1 (0 B)".
/// </summary>
public static class ImageManifestEnricher
{
    /// <summary>
    /// Returns the enriched manifest JSON, or the original on failure.
    /// One HTTP call to <c>/Items/{sourceItemId}/Images</c>; failure logs
    /// at Debug and returns the input unchanged so the caller can fall back
    /// to the tag-only manifest.
    /// </summary>
    /// <param name="sourceManifestJson">Existing tag-only manifest JSON.</param>
    /// <param name="sourceItemId">Source server item ID.</param>
    /// <param name="client">Source server client.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="logContext">Item identifier (name or person) for diagnostic log lines.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The enriched JSON, or the input unchanged.</returns>
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
            }
        }

        return anyChanged ? JsonSerializer.Serialize(manifest) : sourceManifestJson;
    }
}

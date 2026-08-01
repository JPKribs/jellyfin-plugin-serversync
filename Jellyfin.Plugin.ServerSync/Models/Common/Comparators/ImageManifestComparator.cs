// CA5351 — SHA256 here is a content fingerprint, not a security primitive.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Comparator for serialized image manifests of the form
/// <c>Dictionary&lt;ImageType, List&lt;ImageInfoDto&gt;&gt;</c>. Equality compares
/// per-type counts and per-image file sizes (sizes of zero are treated as
/// "unknown" and ignored). Hashing produces a stable fingerprint over the
/// type/size/dimensions tuple — order-independent across types since they're
/// sorted by name first.
/// </summary>
public sealed class ImageManifestComparator : ISyncComparator<string>
{
    /// <inheritdoc />
    public bool Equals(string? source, string? local)
        => DescribeDifference(source, local) == null;

    /// <summary>
    /// Returns the first detected mismatch between two manifests, or null
    /// when they match. Names the diverging type/index so verify failures
    /// pinpoint the cause instead of a generic message.
    /// </summary>
    public string? DescribeDifference(string? source, string? local)
    {
        if (string.IsNullOrEmpty(source))
        {
            // Empty source = nothing to sync; leave local alone rather than
            // queue an apply that can't deliver and would fail verify.
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

        if (sourceMap == null && localMap == null) return null;
        if (sourceMap == null) return "source manifest deserialized to null";
        if (localMap == null) return "local manifest deserialized to null";

        foreach (var (type, sourceImages) in sourceMap)
        {
            if (!localMap.TryGetValue(type, out var localImages))
            {
                return $"type {type} present on source ({sourceImages.Count} image(s)) but missing on local";
            }

            if (sourceImages.Count != localImages.Count)
            {
                return $"type {type}: source has {sourceImages.Count} image(s), local has {localImages.Count}";
            }

            for (int i = 0; i < sourceImages.Count; i++)
            {
                var s = sourceImages[i];
                var l = localImages[i];

                // Both sides have real sizes — canonical case, strict compare.
                if (s.Size > 0 && l.Size > 0)
                {
                    if (s.Size != l.Size)
                    {
                        return $"type {type}[{i}]: source size {s.Size}, local size {l.Size}";
                    }

                    continue;
                }

                // Tag-only source vs sized local: indeterminate, NOT a
                // difference.
                //
                // This used to report a difference, relying on "MarkSynced
                // after a successful apply makes the next refresh
                // short-circuit on SourceHash" to stop it repeating. That
                // short-circuit is gone (see SyncableValue.HasChanges), and
                // without it the pair loops: refresh queues the row, sync
                // re-downloads every image, verify enriches and hits the same
                // unmeasurable source, the row lands Errored, and the next
                // refresh queues it again — forever, re-pulling every image
                // each cycle.
                //
                // Reporting equal is also the more defensible claim. A source
                // size of 0 means enrichment could not measure the image (the
                // /Items/{id}/Images call failed — a non-admin token gets 403
                // here). We cannot assert the images differ, and an apply
                // built on that guess fails verification for the same reason,
                // so queueing it only burns bandwidth. Real divergence in
                // image COUNT or type is still caught above, and once
                // enrichment works again sizes populate and the strict
                // compare resumes.
                if (s.Size == 0 && l.Size > 0)
                {
                    continue;
                }

                // Local file missing or unreadable (Size=0). Queue for re-pull.
                if (s.Size > 0 && l.Size == 0)
                {
                    return $"type {type}[{i}]: source size {s.Size}, local size 0 (file missing or unreadable)";
                }

                // Both sides Size=0: indeterminate, fall through — nothing
                // actionable. Tag change invalidates the SourceHash so we'll
                // re-enrich on the next refresh anyway.
            }
        }

        // Local-only types are tolerated (source→local direction only).
        foreach (var (type, localImages) in localMap)
        {
            if (!sourceMap.ContainsKey(type))
            {
                // Not a failure cause — Equals() above ignores local-only
                // types — so don't report it as the diff. Continue.
                _ = localImages;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public string? ComputeHash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(value);
            if (map == null || map.Count == 0)
            {
                return null;
            }

            // Tag is included so source-only manifests (built from
            // BaseItemDto.ImageTags without a per-item HTTP call) still
            // discriminate content changes — Jellyfin updates an image's
            // Tag whenever the underlying file changes. Size/W/H are 0 in
            // that path; Tag carries the signal.
            var fingerprint = string.Join(
                ";",
                map.OrderBy(k => k.Key, StringComparer.Ordinal)
                    .Select(k => $"{k.Key}:{string.Join(",", k.Value.Select(v => $"{v.Tag ?? string.Empty}_{v.Size}_{v.Width}x{v.Height}"))}"));

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

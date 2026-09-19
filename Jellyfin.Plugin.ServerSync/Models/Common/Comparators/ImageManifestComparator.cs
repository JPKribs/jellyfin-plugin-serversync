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
/// per-type counts and the set of image file sizes within each type (a source
/// size of zero is "unknown" and matches anything). Hashing produces a stable
/// fingerprint over the tag/size/dimensions tuples — order-independent both
/// across types and within a type.
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

            // Sizes are matched as a multiset, not by position. Jellyfin
            // persists an item's images with no order column and a fresh
            // random key on every save, so the order of a multi image type
            // (backdrops) reshuffles whenever either server reloads the item.
            // A positional compare then reports "backdrop[0] size differs"
            // on a set that is byte for byte identical, the apply re-pulls
            // every backdrop, and the row never settles.
            //
            // A source size of 0 means enrichment could not measure the image
            // (the /Items/{id}/Images call failed — a non-admin token gets
            // 403 here). That is indeterminate, NOT a difference: we cannot
            // assert the images differ, and an apply built on that guess
            // fails verification for the same reason, so queueing it only
            // burns bandwidth. Counts are equal at this point, so an
            // unmeasured source image pairs with whatever local image is left
            // over. Only a measured source size with no local twin is a real
            // difference, which also covers a local file that is missing or
            // unreadable (Size=0).
            var unmatchedLocal = new List<long>(localImages.Count);
            foreach (var l in localImages)
            {
                unmatchedLocal.Add(l.Size);
            }

            foreach (var s in sourceImages)
            {
                if (s.Size <= 0)
                {
                    continue;
                }

                if (!unmatchedLocal.Remove(s.Size))
                {
                    return $"type {type}: source image of size {s.Size} has no local match (unmatched local sizes: {string.Join(",", unmatchedLocal)})";
                }
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
                    .Select(k => $"{k.Key}:{string.Join(",", k.Value.Select(v => $"{v.Tag ?? string.Empty}_{v.Size}_{v.Width}x{v.Height}").OrderBy(v => v, StringComparer.Ordinal))}"));

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

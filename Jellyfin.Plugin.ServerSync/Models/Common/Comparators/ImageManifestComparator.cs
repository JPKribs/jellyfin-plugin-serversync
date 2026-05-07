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
    /// Returns a one-line description of the first detected mismatch
    /// between two serialized image manifests, or <c>null</c> if they
    /// match. Pinpoints the type/index involved so the verification
    /// failure log can name the specific divergence instead of just
    /// "count or per-type tag/size mismatch".
    /// </summary>
    /// <param name="source">Source-side manifest.</param>
    /// <param name="local">Local-side manifest.</param>
    /// <returns>Human-readable diff, or null when manifests match.</returns>
    public string? DescribeDifference(string? source, string? local)
    {
        if (string.IsNullOrEmpty(source) && string.IsNullOrEmpty(local))
        {
            return null;
        }

        if (string.IsNullOrEmpty(source))
        {
            return "source manifest empty, local non-empty";
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
                // Sizes of zero are treated as "unknown" — skip the size check
                // for those entries rather than reporting a spurious diff.
                if (s.Size > 0 && l.Size > 0 && s.Size != l.Size)
                {
                    return $"type {type}[{i}]: source size {s.Size}, local size {l.Size}";
                }
            }
        }

        // Local-only types are tolerated in equality (only source→local
        // direction matters), but surface them in the description so users
        // can see when local has extra image types beyond what source has.
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

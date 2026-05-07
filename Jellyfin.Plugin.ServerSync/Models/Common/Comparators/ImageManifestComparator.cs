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
    {
        if (string.IsNullOrEmpty(source) && string.IsNullOrEmpty(local))
        {
            return true;
        }

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(local))
        {
            return false;
        }

        try
        {
            var sourceMap = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(source);
            var localMap = JsonSerializer.Deserialize<Dictionary<string, List<ImageInfoDto>>>(local);

            if (sourceMap == null || localMap == null)
            {
                return sourceMap == null && localMap == null;
            }

            foreach (var (type, sourceImages) in sourceMap)
            {
                if (!localMap.TryGetValue(type, out var localImages))
                {
                    return false;
                }

                if (sourceImages.Count != localImages.Count)
                {
                    return false;
                }

                for (int i = 0; i < sourceImages.Count; i++)
                {
                    var s = sourceImages[i];
                    var l = localImages[i];
                    // Sizes of zero are treated as "unknown" — skip the size check
                    // for those entries rather than reporting a spurious diff.
                    if (s.Size > 0 && l.Size > 0 && s.Size != l.Size)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

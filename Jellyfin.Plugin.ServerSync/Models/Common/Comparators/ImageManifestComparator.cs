using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Compares image manifests by the count and the set of file sizes in each image type, where a source size of zero matches anything.
/// The hash is a content fingerprint over each image's tag, size, and dimensions, whatever their order.
/// </summary>
public sealed class ImageManifestComparator : ISyncComparator<string>
{
    /// <inheritdoc />
    public bool Equals(string? source, string? local)
        => DescribeDifference(source, local) == null;

    /// <summary>
    /// Describes the first difference between two manifests, naming the image type so a failed verification points at the cause.
    /// </summary>
    /// <param name="source">The source's manifest, as JSON.</param>
    /// <param name="local">This server's manifest, as JSON.</param>
    /// <returns>The first difference in words, or null when they match.</returns>
    public string? DescribeDifference(string? source, string? local)
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

        if (sourceMap == null && localMap == null) return null;
        if (sourceMap == null) return "source manifest deserialized to null";
        if (localMap == null) return "local manifest deserialized to null";

        foreach (var (type, sourceImages) in sourceMap)
        {
            if (!localMap.TryGetValue(type, out var localImages))
            {
                return $"type {type} present on source ({JPKribs.Jellyfin.Base.ActivityText.Count(sourceImages.Count, "image")}) but missing on local";
            }

            if (sourceImages.Count != localImages.Count)
            {
                return $"type {type}: source has {JPKribs.Jellyfin.Base.ActivityText.Count(sourceImages.Count, "image")}, local has {localImages.Count}";
            }

            // Jellyfin keeps images in no fixed order, so sizes match as a set.
            var unmatchedLocal = localImages.Select(l => l.Size).ToList();

            foreach (var s in sourceImages)
            {
                // A size of 0 was never measured, which is unknown and not a difference.
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

        // An image type only this server holds is left alone.
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

            // The tag changes with the file, so a manifest without sizes still shows changes.
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

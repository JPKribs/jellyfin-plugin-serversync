using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Compares image manifests by the count of each image type, then by tag for an image a scan or apply has matched before, and by file size for the rest, where a source size of zero matches anything.
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
        => ImageUtilities.DescribeManifestDifference(source, local);

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

            return HashUtilities.Sha256Hex(fingerprint);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

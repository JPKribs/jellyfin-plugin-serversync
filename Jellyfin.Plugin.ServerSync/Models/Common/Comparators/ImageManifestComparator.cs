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
        if (string.IsNullOrEmpty(source))
        {
            // Asymmetric: source is the source-of-truth. When source has no
            // images (empty manifest), there is nothing to sync — local
            // should keep whatever it has. Reporting this as "different"
            // forces an apply that can't do anything (nothing to download)
            // and a guaranteed verify failure. Treat as no-diff so the
            // record is left alone.
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

                // Both sides have real sizes — the canonical case after
                // refresh enrichment runs. Strict size compare.
                if (s.Size > 0 && l.Size > 0)
                {
                    if (s.Size != l.Size)
                    {
                        return $"type {type}[{i}]: source size {s.Size}, local size {l.Size}";
                    }

                    continue;
                }

                // Source size unknown (manifest is tag-only — refresh's
                // per-item enrichment failed or hasn't run, so we never got
                // real Size/Width/Height from /Items/{id}/Images). Local
                // has a real size from the filesystem. We can't confirm
                // parity by comparing sizes; treating the row as "equal"
                // silently hides every image divergence (the 10.11.x bug
                // pattern: modal shows different KB but row marked Synced).
                // Return diff so the row queues; the Apply path will
                // download, and VerifyImagesAppliedAsync re-runs enrichment
                // before comparing so post-apply verification passes when
                // the apply genuinely landed. Once MarkSynced records the
                // SourceHash, future refreshes short-circuit on
                // SourceHash == SyncedHash and never call back here.
                if (s.Size == 0 && l.Size > 0)
                {
                    return $"type {type}[{i}]: source manifest is tag-only (size unknown), local size {l.Size}; cannot confirm match without enrichment";
                }

                // Source has a real size, local has size 0 — local file is
                // missing or unreadable (filesystem builder leaves Size=0
                // when File.Exists is false or FileInfo.Length throws). The
                // image needs to be (re-)pulled from source. Without this
                // branch the loop falls through and the comparator returns
                // equal, leaving a hollow local image silently desynced.
                if (s.Size > 0 && l.Size == 0)
                {
                    return $"type {type}[{i}]: source size {s.Size}, local size 0 (file missing or unreadable)";
                }

                // Both sides Size=0: genuinely indeterminate (refresh
                // enrichment failed AND local file is missing). The Apply
                // path will retry enrichment and the download; if both
                // sides are still 0 next time, the row stays in this
                // limbo. Don't return diff here because there's nothing
                // actionable — apply can't write what source can't deliver.
                // Fall through (continue). The Tag is still part of the
                // SourceHash so a Tag change invalidates the short-circuit
                // and we re-attempt enrichment on the next refresh.
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

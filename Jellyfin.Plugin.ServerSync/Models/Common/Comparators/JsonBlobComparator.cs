// CA5351 — SHA256 here is a content fingerprint, not a security primitive.
using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Comparator for serialized JSON blobs. Equality goes through
/// <see cref="JsonComparisonUtility.JsonEquals"/>, which handles property
/// ordering, empty/null equivalence, and the timezone-aware date comparison.
/// The hash is SHA256 over the raw UTF-8 bytes of the blob and is therefore
/// only stable when the same code path produces the JSON each time — fine for
/// the source-vs-synced fast-path used by <see cref="SyncableValue{T}"/>, not
/// suitable for cross-server hash equality checks.
/// </summary>
public sealed class JsonBlobComparator : ISyncComparator<string>
{
    /// <inheritdoc />
    public bool Equals(string? source, string? local)
        => JsonComparisonUtility.JsonEquals(source, local);

    /// <inheritdoc />
    public string? ComputeHash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.ServerSync.Models.Common.Comparators;

/// <summary>
/// Compares JSON blobs by meaning through <see cref="JsonComparisonUtility.JsonEquals"/>.
/// The hash is a content fingerprint of the raw text, so it is stable only for JSON one code path wrote and never compared between servers.
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

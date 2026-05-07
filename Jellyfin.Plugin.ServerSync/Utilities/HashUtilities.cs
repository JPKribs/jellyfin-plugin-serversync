// CA5351 doesn't apply - we use SHA256 for content checksums, not cryptographic security
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utility methods for computing hashes.
/// </summary>
public static class HashUtilities
{
    /// <summary>
    /// Computes a SHA256 hash of a stream, returning a truncated hex string.
    /// </summary>
    /// <param name="stream">The stream to hash.</param>
    /// <returns>A 32-character lowercase hex string (first 16 bytes of the hash).</returns>
    public static string ComputeSha256Hash(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);
        // Return first 32 chars (16 bytes) for a shorter but still unique hash
        return Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
    }

    /// <summary>
    /// Computes a SHA256 hash of a string (encoded as UTF-8), returning a
    /// truncated hex string.
    /// </summary>
    /// <param name="value">The string to hash. Null is treated as empty.</param>
    /// <returns>A 32-character lowercase hex string (first 16 bytes of the hash).</returns>
    public static string ComputeSha256Hash(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
    }
}

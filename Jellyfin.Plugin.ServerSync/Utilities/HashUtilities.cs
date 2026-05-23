using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// SHA256-based content fingerprints (not security primitives).
/// </summary>
public static class HashUtilities
{
    /// <summary>
    /// Computes a 32-character lowercase hex fingerprint of a stream.
    /// </summary>
    public static string ComputeSha256Hash(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(stream);
        return System.Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
    }

    /// <summary>
    /// Computes a 32-character lowercase hex fingerprint of a UTF-8 string. Null is treated as empty.
    /// </summary>
    public static string ComputeSha256Hash(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var hashBytes = SHA256.HashData(bytes);
        return System.Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
    }
}

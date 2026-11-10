using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Writes and reads this server's .strm links. A link holds one line, a loopback URL to this server's
/// own link route, so no source credential is ever on disk. The URL carries a secret derived from a key
/// only this server holds, so a link cannot be guessed from its id.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkFiles
{
    /// <summary>The route the URL in every link points at.</summary>
    public const string Route = "Sync/Link";

    private const string KeyFile = "serversync-link.key";

    private readonly IServerApplicationHost _host;
    private readonly string _keyPath;
    private readonly object _keyLock = new();
    private byte[]? _key;

    public LinkFiles(IServerApplicationHost host, IApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _host = host;
        _keyPath = Path.Combine(paths.DataPath, KeyFile);
    }

    /// <summary>The URL a link with <paramref name="linkId"/> holds.</summary>
    public string UrlFor(string linkId)
        => $"{_host.GetLocalApiUrl("127.0.0.1", "http", _host.HttpPort)}/{Route}/{linkId}?k={Secret(linkId)}";

    /// <summary>Whether <paramref name="presented"/> is the secret for <paramref name="linkId"/>.</summary>
    public bool SecretMatches(string linkId, string? presented)
    {
        if (string.IsNullOrEmpty(linkId) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Secret(linkId)), Encoding.ASCII.GetBytes(presented));
    }

    /// <summary>Whether the file at <paramref name="path"/> already holds the current URL for <paramref name="linkId"/>.</summary>
    public bool IsCurrent(string path, string linkId)
    {
        try
        {
            return File.Exists(path) && string.Equals(ReadUrl(path), UrlFor(linkId), StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the link, replacing the file only once the new one is complete. Throws when a different
    /// file already sits there, so a user's own .strm or another link is never overwritten.
    /// </summary>
    public void Write(string path, string linkId, bool replace)
    {
        if (File.Exists(path) && !replace)
        {
            throw new IOException($"A file already exists at {path}");
        }

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var temp = path + ".serversync-tmp";
        File.WriteAllText(temp, UrlFor(linkId) + "\n", new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The first URL line in a .strm, the same line Jellyfin reads.</summary>
    public static string? ReadUrl(string path)
        => File.ReadLines(path)
            .Select(line => line.Replace("\t", string.Empty, StringComparison.Ordinal).Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'));

    /// <summary>Whether a .strm's URL points at this plugin's link route, whatever id and secret it holds.</summary>
    public static bool PointsAtLinkRoute(string? url)
        => url is not null && url.Contains("/" + Route + "/", StringComparison.OrdinalIgnoreCase);

    private string Secret(string linkId)
    {
        using var hmac = new HMACSHA256(Key());
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(linkId));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private byte[] Key()
    {
        if (_key is { } key)
        {
            return key;
        }

        lock (_keyLock)
        {
            if (_key is null)
            {
                if (File.Exists(_keyPath) && File.ReadAllBytes(_keyPath) is { Length: 32 } stored)
                {
                    _key = stored;
                }
                else
                {
                    var fresh = RandomNumberGenerator.GetBytes(32);
                    var temp = _keyPath + ".tmp";
                    File.WriteAllBytes(temp, fresh);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    File.Move(temp, _keyPath, overwrite: true);
                    _key = fresh;
                }
            }

            return _key;
        }
    }
}

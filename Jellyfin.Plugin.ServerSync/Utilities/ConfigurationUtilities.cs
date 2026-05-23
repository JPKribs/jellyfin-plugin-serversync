using System;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.ServerSync.Configuration;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Configuration-side validation helpers.
/// </summary>
public static class ConfigurationUtilities
{
    /// <summary>
    /// True when both SourceServerUrl and SourceServerApiKey are populated.
    /// </summary>
    public static bool HasValidAuthConfiguration(PluginConfiguration config)
    {
        return !string.IsNullOrWhiteSpace(config.SourceServerUrl) &&
               !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }

    /// <summary>
    /// Returns null when the URL is acceptable, otherwise an error string explaining
    /// why the URL was rejected for SSRF reasons. Loopback and RFC1918/ULA addresses
    /// are allowed when <paramref name="allowPrivateNetwork"/> is true.
    /// </summary>
    public static string? ValidateServerUrlForSsrf(string url, bool allowPrivateNetwork = true)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "URL cannot be empty";
        }

        if (url.Contains("..", StringComparison.Ordinal) || url.Contains("./", StringComparison.Ordinal))
        {
            return "URL contains invalid path sequences";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Invalid URL format";
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Only HTTP and HTTPS URLs are allowed";
        }

        // IP-literal hosts are validated here; DNS names go through the HTTP stack.
        if (IPAddress.TryParse(uri.Host, out var ipAddress))
        {
            var rejection = ClassifyIpAddress(ipAddress, allowPrivateNetwork);
            if (rejection != null)
            {
                return rejection;
            }
        }

        return null;
    }

    private static string? ClassifyIpAddress(IPAddress ipAddress, bool allowPrivateNetwork)
    {
        // Always-blocked: no legitimate use as a remote source server target.
        if (ipAddress.IsIPv6LinkLocal)
        {
            return "Link-local addresses are not allowed";
        }

        if (ipAddress.IsIPv6SiteLocal)
        {
            return "IPv6 site-local addresses are not allowed";
        }

        if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ipAddress.GetAddressBytes();

            // 0.0.0.0/8 — unspecified/this network.
            if (bytes[0] == 0)
            {
                return "0.0.0.0/8 addresses are not allowed";
            }

            // 169.254.0.0/16 — IPv4 link-local (covers AWS/GCP metadata 169.254.169.254).
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return "Link-local addresses are not allowed";
            }
        }

        if (allowPrivateNetwork)
        {
            return null;
        }

        if (IPAddress.IsLoopback(ipAddress))
        {
            return "Loopback addresses are not allowed when private-network access is disabled";
        }

        if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ipAddress.GetAddressBytes();

            // 10.0.0.0/8 — RFC1918 private network.
            if (bytes[0] == 10)
            {
                return "Private-network addresses (10.0.0.0/8) are not allowed when private-network access is disabled";
            }

            // 172.16.0.0/12 — RFC1918 private network.
            if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
            {
                return "Private-network addresses (172.16.0.0/12) are not allowed when private-network access is disabled";
            }

            // 192.168.0.0/16 — RFC1918 private network.
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return "Private-network addresses (192.168.0.0/16) are not allowed when private-network access is disabled";
            }
        }
        else if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ipAddress.GetAddressBytes();

            // fc00::/7 — IPv6 unique local address (ULA).
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return "IPv6 unique-local addresses are not allowed when private-network access is disabled";
            }
        }

        return null;
    }
}

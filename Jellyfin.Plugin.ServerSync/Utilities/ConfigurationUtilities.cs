using System;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.ServerSync.Configuration;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utilities for configuration validation.
/// </summary>
public static class ConfigurationUtilities
{
    /// <summary>
    /// Checks if valid authentication configuration is present.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>True if authentication is properly configured.</returns>
    public static bool HasValidAuthConfiguration(PluginConfiguration config)
    {
        return !string.IsNullOrWhiteSpace(config.SourceServerUrl) &&
               !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }

    /// <summary>
    /// Validates a server URL for SSRF protection. Always blocks dangerous
    /// addresses that have no legitimate use as a source-server target
    /// (link-local, IPv6 site-local, 0.0.0.0, cloud-metadata endpoints).
    /// Also blocks loopback and RFC1918/ULA addresses unless
    /// <paramref name="allowPrivateNetwork"/> is true (the home-server case).
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <param name="allowPrivateNetwork">
    /// When true, loopback and private-network ranges are allowed. When false,
    /// they are rejected (the URL must point at a public address).
    /// </param>
    /// <returns>Null if valid, or an error message describing why the URL was rejected.</returns>
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

        // Validate IP literal hosts directly. DNS-name hosts are validated
        // best-effort — we don't resolve them here because (a) that's an I/O
        // call inside a sync utility and (b) DNS rebinding makes it unreliable
        // anyway; runtime calls go through the same handler/firewall stack.
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
        // Always-blocked categories. These have no legitimate use as a
        // remote source server target, regardless of how the user has
        // configured the private-network flag.
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

            // 0.0.0.0/8 — unspecified/this network
            if (bytes[0] == 0)
            {
                return "0.0.0.0/8 addresses are not allowed";
            }

            // 169.254.0.0/16 — IPv4 link-local (covers AWS/GCP metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return "Link-local addresses are not allowed";
            }
        }

        // Caller-controlled categories. These are typically legitimate for
        // home Jellyfin installs but blocked when the URL must be public.
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

            // 10.0.0.0/8
            if (bytes[0] == 10)
            {
                return "Private-network addresses (10.0.0.0/8) are not allowed when private-network access is disabled";
            }

            // 172.16.0.0/12
            if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
            {
                return "Private-network addresses (172.16.0.0/12) are not allowed when private-network access is disabled";
            }

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return "Private-network addresses (192.168.0.0/16) are not allowed when private-network access is disabled";
            }
        }
        else if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ipAddress.GetAddressBytes();

            // fc00::/7 — IPv6 unique local address (ULA)
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return "IPv6 unique-local addresses are not allowed when private-network access is disabled";
            }
        }

        return null;
    }
}

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
    /// True when at least one configured server entry is one this server pulls from.
    /// </summary>
    public static bool HasValidAuthConfiguration(PluginConfiguration config)
    {
        return config.GetPullServers().Count > 0;
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

        // IP-literal hosts are validated here. DNS names go through the HTTP stack.
        // Uri.Host keeps the brackets on an IPv6 literal ("[::1]"), which
        // IPAddress.TryParse rejects, every IPv6 literal skipped classification
        // entirely until the brackets were trimmed.
        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            var rejection = ClassifyIpAddress(ipAddress, allowPrivateNetwork);
            if (rejection != null)
            {
                return rejection;
            }
        }

        return null;
    }

    /// <summary>
    /// Like <see cref="ValidateServerUrlForSsrf"/>, and when private networks are disallowed also resolves
    /// a host name and classifies every address it points at, so "localhost" or a LAN name is refused
    /// the same way its address would be. Used when an entry is saved or tested, not on every call.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <param name="allowPrivateNetwork">Whether private and loopback addresses are allowed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null when acceptable, else why not.</returns>
    public static async System.Threading.Tasks.Task<string?> ValidateServerUrlForSsrfAsync(string url, bool allowPrivateNetwork, System.Threading.CancellationToken cancellationToken = default)
    {
        var error = ValidateServerUrlForSsrf(url, allowPrivateNetwork);
        if (error is not null || allowPrivateNetwork || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
        {
            return error;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return $"The name '{uri.Host}' does not resolve";
        }

        foreach (var address in addresses)
        {
            var rejection = ClassifyIpAddress(address, allowPrivateNetwork: false);
            if (rejection is not null)
            {
                return $"The name '{uri.Host}' resolves to {address}: {rejection}";
            }
        }

        return addresses.Length == 0 ? $"The name '{uri.Host}' does not resolve" : null;
    }

    /// <summary>
    /// Connects for the client whose entries disallow private networks: every address the name resolves
    /// to is classified first, and the connection goes to the first allowed one. A name that resolves
    /// to a loopback or private address fails here the way the address itself fails the URL check.
    /// </summary>
    /// <param name="context">The connection the handler wants.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connected stream.</returns>
    public static async System.Threading.Tasks.ValueTask<System.IO.Stream> ConnectPublicOnlyAsync(System.Net.Http.SocketsHttpConnectionContext context, System.Threading.CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endPoint = context.DnsEndPoint;
        IPAddress[] addresses = IPAddress.TryParse(endPoint.Host.Trim('[', ']'), out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken).ConfigureAwait(false);

        string? rejection = null;
        foreach (var address in addresses)
        {
            rejection = ClassifyIpAddress(address, allowPrivateNetwork: false);
            if (rejection is not null)
            {
                // One private answer refuses the whole name. Mixed answers are how a rebinding attack
                // looks, so no address of such a name is used.
                throw new System.Net.Http.HttpRequestException($"'{endPoint.Host}' resolves to {address}: {rejection}");
            }
        }

        if (addresses.Length == 0)
        {
            throw new System.Net.Http.HttpRequestException($"'{endPoint.Host}' does not resolve");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Whether two server addresses name the same server, ignoring case and a trailing slash.</summary>
    /// <param name="a">One address.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when they are the same.</returns>
    public static bool SameServerUrl(string? a, string? b)
    {
        if (Uri.TryCreate((a ?? string.Empty).Trim(), UriKind.Absolute, out var ua) && Uri.TryCreate((b ?? string.Empty).Trim(), UriKind.Absolute, out var ub))
        {
            return string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)
                && ua.Port == ub.Port
                && string.Equals(ua.AbsolutePath.TrimEnd('/'), ub.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals((a ?? string.Empty).Trim().TrimEnd('/'), (b ?? string.Empty).Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ClassifyIpAddress(IPAddress ipAddress, bool allowPrivateNetwork)
    {
        // An IPv4 address written as IPv6 (::ffff:127.0.0.1) is judged as the IPv4 address it is.
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }

        // Always-blocked: no legitimate use as a remote source server target.
        // The IPv6 unspecified address (::, which is also IPv6None) reaches the local host on most
        // systems, the same as 0.0.0.0, and a multicast group is never a single server.
        if (ipAddress.Equals(IPAddress.IPv6Any) || ipAddress.Equals(IPAddress.IPv6None))
        {
            return "The unspecified address (::) is not allowed";
        }

        if (ipAddress.IsIPv6Multicast)
        {
            return "Multicast addresses are not allowed";
        }

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

            // 0.0.0.0/8, unspecified/this network.
            if (bytes[0] == 0)
            {
                return "0.0.0.0/8 addresses are not allowed";
            }

            // 169.254.0.0/16, IPv4 link-local (covers AWS/GCP metadata 169.254.169.254).
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return "Link-local addresses are not allowed";
            }

            // 224.0.0.0/4 is IPv4 multicast, a group address rather than one server.
            if ((bytes[0] & 0xF0) == 0xE0)
            {
                return "Multicast addresses are not allowed";
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

            // 10.0.0.0/8, RFC1918 private network.
            if (bytes[0] == 10)
            {
                return "Private-network addresses (10.0.0.0/8) are not allowed when private-network access is disabled";
            }

            // 172.16.0.0/12, RFC1918 private network.
            if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
            {
                return "Private-network addresses (172.16.0.0/12) are not allowed when private-network access is disabled";
            }

            // 192.168.0.0/16, RFC1918 private network.
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return "Private-network addresses (192.168.0.0/16) are not allowed when private-network access is disabled";
            }
        }
        else if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ipAddress.GetAddressBytes();

            // fc00::/7, IPv6 unique local address (ULA).
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return "IPv6 unique-local addresses are not allowed when private-network access is disabled";
            }
        }

        return null;
    }
}

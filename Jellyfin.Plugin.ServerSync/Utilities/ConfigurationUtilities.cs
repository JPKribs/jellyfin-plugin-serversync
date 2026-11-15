using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Checks Server URLs and connections against the SSRF rules.
/// </summary>
public static class ConfigurationUtilities
{
    /// <summary>
    /// Whether at least one server entry is one this server pulls from.
    /// </summary>
    /// <param name="config">The plugin settings holding the server entries.</param>
    /// <returns><c>true</c> when a source is set up with a Server URL and API Key.</returns>
    public static bool HasValidAuthConfiguration(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GetPullServers().Count > 0;
    }

    /// <summary>
    /// Checks a Server URL against the SSRF rules. Loopback and private addresses pass only when <paramref name="allowPrivateNetwork"/> is <c>true</c>.
    /// </summary>
    /// <param name="url">The address to check.</param>
    /// <param name="allowPrivateNetwork">Whether a loopback or private address is acceptable.</param>
    /// <returns>Null when the URL is acceptable, else why not.</returns>
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

        // Uri.Host keeps the brackets of an IPv6 literal, which IPAddress.TryParse rejects.
        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            var rejection = ClassifyIpAddress(ipAddress, allowPrivateNetwork);
            if (rejection != null)
            {
                return rejection;
            }
        }

        if (uri.Scheme == Uri.UriSchemeHttp
            && (!allowPrivateNetwork || (ipAddress is not null && !AllowsPlainHttp(ipAddress))))
        {
            return PlainHttpRefusal;
        }

        return null;
    }

    /// <summary>The reason a plain HTTP address to a public host is refused.</summary>
    public const string PlainHttpRefusal = "Plain HTTP sends the API key unencrypted, so it only reaches loopback and private network addresses. Use an https:// URL for this server";

    /// <summary>Whether plain HTTP may be used for an address, which only a loopback or private one allows.</summary>
    /// <param name="address">The address the name resolves to.</param>
    /// <returns><c>true</c> when the address is loopback or private.</returns>
    public static bool AllowsPlainHttp(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 100.64.0.0/10 is carrier NAT space, private in practice.
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64)
            {
                return true;
            }
        }

        return ClassifyIpAddress(address, allowPrivateNetwork: true) is null
            && ClassifyIpAddress(address, allowPrivateNetwork: false) is not null;
    }

    /// <summary>
    /// Checks a Server URL like <see cref="ValidateServerUrlForSsrf"/>, and also resolves a host name so a name is refused the way its address would be.
    /// </summary>
    /// <param name="url">The Server URL to check.</param>
    /// <param name="allowPrivateNetwork">Whether private and loopback addresses are allowed.</param>
    /// <param name="cancellationToken">Cancels the name lookup.</param>
    /// <returns>Null when acceptable, else why not.</returns>
    public static async Task<string?> ValidateServerUrlForSsrfAsync(string url, bool allowPrivateNetwork, CancellationToken cancellationToken = default)
    {
        var error = ValidateServerUrlForSsrf(url, allowPrivateNetwork);
        if (error is not null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
        {
            return error;
        }

        var plainHttp = uri.Scheme == Uri.UriSchemeHttp;
        if (allowPrivateNetwork && !plainHttp)
        {
            return null;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return allowPrivateNetwork ? null : $"The name '{uri.Host}' does not resolve";
        }

        foreach (var address in addresses)
        {
            var rejection = ClassifyIpAddress(address, allowPrivateNetwork);
            if (rejection is not null)
            {
                return $"The name '{uri.Host}' resolves to {address}: {rejection}";
            }

            if (plainHttp && !AllowsPlainHttp(address))
            {
                return $"The name '{uri.Host}' resolves to {address}, a public address. {PlainHttpRefusal}";
            }
        }

        return addresses.Length == 0 && !allowPrivateNetwork ? $"The name '{uri.Host}' does not resolve" : null;
    }

    /// <summary>
    /// Connects for server entries that disallow private networks, after checking every address the name resolves to.
    /// </summary>
    /// <param name="context">The connection the handler wants.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The connected stream.</returns>
    public static ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => ConnectCheckedAsync(context, allowPrivateNetwork: false, cancellationToken);

    /// <summary>
    /// Connects for server entries that allow private networks, after every other address check of <see cref="ConnectPublicOnlyAsync"/>.
    /// </summary>
    /// <param name="context">The connection the handler wants.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The connected stream.</returns>
    public static ValueTask<Stream> ConnectPrivateAllowedAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => ConnectCheckedAsync(context, allowPrivateNetwork: true, cancellationToken);

    /// <summary>Resolves the host, refuses the whole name when any address is disallowed, then connects.</summary>
    private static async ValueTask<Stream> ConnectCheckedAsync(SocketsHttpConnectionContext context, bool allowPrivateNetwork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plainHttp = context.InitialRequestMessage.RequestUri?.Scheme == Uri.UriSchemeHttp;
        var endPoint = context.DnsEndPoint;
        IPAddress[] addresses = IPAddress.TryParse(endPoint.Host.Trim('[', ']'), out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken).ConfigureAwait(false);

        foreach (var address in addresses)
        {
            var rejection = ClassifyIpAddress(address, allowPrivateNetwork);
            // Mixed answers are how a rebinding attack looks, so one refusal fails the name.
            if (rejection is not null)
            {
                throw new HttpRequestException($"'{endPoint.Host}' resolves to {address}: {rejection}");
            }

            if (plainHttp && !AllowsPlainHttp(address))
            {
                throw new HttpRequestException($"'{endPoint.Host}' resolves to {address}, a public address. {PlainHttpRefusal}");
            }
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"'{endPoint.Host}' does not resolve");
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
    /// <param name="a">The first Server URL.</param>
    /// <param name="b">The address compared with it.</param>
    /// <returns><c>true</c> when they are the same.</returns>
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

    /// <summary>Returns why an address is refused, or null when it is allowed.</summary>
    private static string? ClassifyIpAddress(IPAddress ipAddress, bool allowPrivateNetwork)
    {
        // An IPv4 address written as IPv6 is judged as the IPv4 address it is.
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }

        // The unspecified address reaches the local host on most systems.
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

            if (bytes[0] == 0)
            {
                return "0.0.0.0/8 addresses are not allowed";
            }

            // 169.254.0.0/16 holds the cloud metadata address.
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return "Link-local addresses are not allowed";
            }

            // 224.0.0.0/4 is multicast, a group and never one server.
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

            if (bytes[0] == 10)
            {
                return "Private-network addresses (10.0.0.0/8) are not allowed when private-network access is disabled";
            }

            if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
            {
                return "Private-network addresses (172.16.0.0/12) are not allowed when private-network access is disabled";
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return "Private-network addresses (192.168.0.0/16) are not allowed when private-network access is disabled";
            }
        }
        else if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ipAddress.GetAddressBytes();

            // fc00::/7 is the unique local range.
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return "IPv6 unique-local addresses are not allowed when private-network access is disabled";
            }
        }

        return null;
    }
}

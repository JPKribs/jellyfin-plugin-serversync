using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
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
            var rejection = SsrfGuard.Classify(ipAddress, allowPrivateNetwork);
            if (rejection != null)
            {
                return char.ToUpperInvariant(rejection[0]) + rejection[1..];
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
        return SsrfGuard.Classify(address, allowPrivateNetwork: true) is null
            && SsrfGuard.Classify(address, allowPrivateNetwork: false) is not null;
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
            var rejection = SsrfGuard.Classify(address, allowPrivateNetwork);
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
            var rejection = SsrfGuard.Classify(address, allowPrivateNetwork);
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
}

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
    private const string PathRefusal = "URL contains invalid path sequences";

    private static readonly SsrfGuard PublicGuard = new(allowPrivateNetwork: false, requireHttpsForPublic: true);

    private static readonly SsrfGuard PrivateGuard = new(allowPrivateNetwork: true, requireHttpsForPublic: true);

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
    /// Checks a Server URL before this server calls it. Only http and https are allowed, an address no server lives at is refused,
    /// and plain http reaches only loopback and private addresses, since it would send the API Key unencrypted.
    /// </summary>
    /// <param name="url">The Server URL to check.</param>
    /// <param name="allowPrivateNetwork">Whether a loopback, LAN, or carrier NAT address is allowed.</param>
    /// <returns>Why the URL is refused, as a sentence, or <c>null</c> when it may be called.</returns>
    public static string? ValidateServerUrlForSsrf(string url, bool allowPrivateNetwork = true)
        => ClimbsOut(url) ? PathRefusal : Sentence(Guard(allowPrivateNetwork).ValidateUrl(url));

    /// <summary>
    /// Checks a Server URL as <see cref="ValidateServerUrlForSsrf"/> does, and also every address its name resolves to.
    /// </summary>
    /// <param name="url">The Server URL to check.</param>
    /// <param name="allowPrivateNetwork">Whether a loopback, LAN, or carrier NAT address is allowed.</param>
    /// <param name="cancellationToken">Cancels the name lookup.</param>
    /// <returns>Why the URL is refused, as a sentence, or <c>null</c> when it may be called.</returns>
    public static async Task<string?> ValidateServerUrlForSsrfAsync(string url, bool allowPrivateNetwork, CancellationToken cancellationToken = default)
        => ClimbsOut(url) ? PathRefusal : Sentence(await Guard(allowPrivateNetwork).ValidateUrlAsync(url, allowUnresolved: allowPrivateNetwork, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Connects for the client that reaches public addresses only, checking each address at the moment of connecting.
    /// </summary>
    /// <param name="context">The connection the HTTP handler is opening.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The connected stream.</returns>
    public static ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => PublicGuard.ConnectAsync(context, cancellationToken);

    /// <summary>
    /// Connects for the client that may reach private addresses, checking each address at the moment of connecting.
    /// </summary>
    /// <param name="context">The connection the HTTP handler is opening.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The connected stream.</returns>
    public static ValueTask<Stream> ConnectPrivateAllowedAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => PrivateGuard.ConnectAsync(context, cancellationToken);

    // A path with a parent or current folder step is refused before it is read as a URL, which would fold the step away.
    private static bool ClimbsOut(string? url)
        => !string.IsNullOrEmpty(url) && (url.Contains("..", StringComparison.Ordinal) || url.Contains("./", StringComparison.Ordinal));

    private static SsrfGuard Guard(bool allowPrivateNetwork) => allowPrivateNetwork ? PrivateGuard : PublicGuard;

    private static string? Sentence(string? refusal)
        => string.IsNullOrEmpty(refusal) ? null : char.ToUpperInvariant(refusal[0]) + refusal[1..];

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

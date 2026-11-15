using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>Why a caller was let in or turned away.</summary>
public enum PeerCallerCode
{
    /// <summary>The caller may use the peer routes.</summary>
    Allowed,

    /// <summary>The caller is a user the Whitelist does not name.</summary>
    UserNotWhitelisted,

    /// <summary>The caller is an administrator or API key whose address the Whitelist does not allow.</summary>
    IpNotWhitelisted,

    /// <summary>The caller's user is on the Whitelist, but not for the address the call came from.</summary>
    UserNotWhitelistedForIp
}

/// <summary>The decision on one caller of a peer route.</summary>
/// <param name="Code">Why the caller was let in or turned away.</param>
/// <param name="Refusal">The reason to show a refused caller, or null when allowed.</param>
/// <param name="ScopedUserId">The user whose view the caller is limited to, or null for the whole library.</param>
public readonly record struct PeerCallerVerdict(PeerCallerCode Code, string? Refusal, Guid? ScopedUserId)
{
    /// <summary>Builds a verdict that lets the caller in.</summary>
    /// <param name="scopedUserId">The user whose view the caller is limited to, or null for the whole library.</param>
    /// <returns>The verdict.</returns>
    public static PeerCallerVerdict Allow(Guid? scopedUserId) => new(PeerCallerCode.Allowed, null, scopedUserId);

    /// <summary>Builds a verdict that turns the caller away.</summary>
    /// <param name="code">Why the caller is refused.</param>
    /// <param name="reason">The reason to show the caller.</param>
    /// <returns>The verdict.</returns>
    public static PeerCallerVerdict Refuse(PeerCallerCode code, string reason) => new(code, reason, null);
}

/// <summary>
/// Decides whether a caller may use this server's peer routes, by the Whitelist of users and locations.
/// An administrator or API key also passes from the address of a server entry that is turned on. A name
/// in the Whitelist is looked up again when its address has moved.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class PeerCallerPolicy
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";
    private const string AdministratorRole = "Administrator";

    private static readonly TimeSpan RecheckFloor = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RememberFor = TimeSpan.FromHours(1);

    private readonly IPluginConfigurationManager _configManager;
    private readonly ILogger<PeerCallerPolicy> _logger;
    private readonly ConcurrentDictionary<string, (DateTime ResolvedAt, IPAddress[] Addresses)> _resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerCallerPolicy"/> class.
    /// </summary>
    /// <param name="configManager">Reads the plugin configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public PeerCallerPolicy(IPluginConfigurationManager configManager, ILogger<PeerCallerPolicy> logger)
    {
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>Words a refusal for a caller.</summary>
    /// <param name="code">The refusal's code.</param>
    /// <returns>The text.</returns>
    public static string Describe(PeerCallerCode code) => code switch
    {
        PeerCallerCode.UserNotWhitelisted => "This user is not whitelisted",
        PeerCallerCode.IpNotWhitelisted => "This IP is not whitelisted",
        PeerCallerCode.UserNotWhitelistedForIp => "This user is not whitelisted for this IP",
        _ => "Allowed"
    };

    /// <summary>
    /// Checks a caller against the Whitelist.
    /// </summary>
    /// <param name="caller">The address the call came from, or null when it is unknown.</param>
    /// <param name="principal">The authenticated user or API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision.</returns>
    public async Task<PeerCallerVerdict> CheckAsync(IPAddress? caller, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var config = _configManager.Configuration;
        var isApiKey = bool.TryParse(principal.FindFirst(IsApiKeyClaim)?.Value, out var apiKey) && apiKey;
        var isAdmin = isApiKey || principal.IsInRole(AdministratorRole);
        var userKey = isApiKey ? PeerCallerGrant.ApiKeys : NormalizeId(principal.FindFirst(UserIdClaim)?.Value);
        var grant = (config.PeerCallerGrants ?? new List<PeerCallerGrant>())
            .FirstOrDefault(g => string.Equals(NormalizeId(g.UserId), userKey, StringComparison.OrdinalIgnoreCase));
        Guid? scoped = isAdmin ? null : Guid.TryParse(userKey, out var userId) ? userId : Guid.Empty;
        caller = caller is null ? null : Normalize(caller);

        if (grant is not null)
        {
            if (grant.Locations.Count == 0)
            {
                return PeerCallerVerdict.Allow(scoped);
            }

            if (caller is not null && await AnyMatchAsync(grant.Locations, caller, cancellationToken).ConfigureAwait(false))
            {
                return PeerCallerVerdict.Allow(scoped);
            }
        }

        if (isAdmin && caller is not null)
        {
            var servers = config.Servers
                .Where(s => s.IsEnabled)
                .SelectMany(s => new[] { s.Url, s.ExternalUrl })
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToList();
            if (await AnyMatchAsync(servers, caller, cancellationToken).ConfigureAwait(false))
            {
                return PeerCallerVerdict.Allow(scoped);
            }
        }

        if (grant is not null)
        {
            return PeerCallerVerdict.Refuse(PeerCallerCode.UserNotWhitelistedForIp, $"{Describe(PeerCallerCode.UserNotWhitelistedForIp)} ({caller})");
        }

        return isAdmin
            ? PeerCallerVerdict.Refuse(PeerCallerCode.IpNotWhitelisted, $"{Describe(PeerCallerCode.IpNotWhitelisted)} ({caller})")
            : PeerCallerVerdict.Refuse(PeerCallerCode.UserNotWhitelisted, Describe(PeerCallerCode.UserNotWhitelisted));
    }

    /// <summary>Reads the host out of a Whitelist entry, which may be an address, a range, or a URL.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The host, or null when the entry names none.</returns>
    public static string? HostOf(string entry)
    {
        var value = entry.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.IdnHost.Trim('[', ']') : null;
        }

        if (value.StartsWith('[') && value.Contains(']', StringComparison.Ordinal))
        {
            return value[1..value.IndexOf(']', StringComparison.Ordinal)];
        }

        if (IPAddress.TryParse(value, out _) || TryParseRange(value, out _))
        {
            return value;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            value = value[..slash];
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':', StringComparison.Ordinal) == colon)
        {
            value = value[..colon];
        }

        return value.Length == 0 ? null : value;
    }

    /// <summary>Reads an address range written as an address and a prefix length, such as <c>10.0.0.0/8</c>.</summary>
    /// <param name="value">The text to read.</param>
    /// <param name="network">The range, when the text is one.</param>
    /// <returns><c>true</c> when the text is a range.</returns>
    public static bool TryParseRange(string value, out IPNetwork network)
    {
        network = default;
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0
            || !IPAddress.TryParse(value[..slash], out var address)
            || !int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix))
        {
            return false;
        }

        address = Normalize(address);
        var bytes = address.GetAddressBytes();
        if (prefix < 0 || prefix > bytes.Length * 8)
        {
            return false;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        network = new IPNetwork(new IPAddress(bytes), prefix);
        return true;
    }

    private static string NormalizeId(string? id)
        => (id ?? string.Empty).Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private async Task<bool> AnyMatchAsync(IReadOnlyList<string> entries, IPAddress caller, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var entry in entries)
        {
            var host = HostOf(entry);
            if (host is null)
            {
                continue;
            }

            if (TryParseRange(host, out var network))
            {
                if (network.Contains(caller))
                {
                    return true;
                }

                continue;
            }

            if (IPAddress.TryParse(host, out var address))
            {
                if (Normalize(address).Equals(caller))
                {
                    return true;
                }

                continue;
            }

            if ((await ResolveAsync(host, fresh: false, cancellationToken).ConfigureAwait(false)).Any(a => Normalize(a).Equals(caller)))
            {
                return true;
            }

            names.Add(host);
        }

        foreach (var host in names)
        {
            if ((await ResolveAsync(host, fresh: true, cancellationToken).ConfigureAwait(false)).Any(a => Normalize(a).Equals(caller)))
            {
                _logger.LogInformation("Whitelist entry '{Host}' moved to a new address, which now matches {Caller}", FormatUtilities.SanitizeForLog(host), FormatUtilities.SanitizeForLog(caller?.ToString()));
                return true;
            }
        }

        return false;
    }

    private async Task<IPAddress[]> ResolveAsync(string host, bool fresh, CancellationToken cancellationToken)
    {
        var known = _resolved.TryGetValue(host, out var cached);
        var age = known ? DateTime.UtcNow - cached.ResolvedAt : TimeSpan.MaxValue;
        if (known && (fresh ? age < RecheckFloor : age < RememberFor))
        {
            return cached.Addresses;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            _resolved[host] = (DateTime.UtcNow, addresses);
            return addresses;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            _logger.LogError("Could not look up the whitelist entry '{Host}': {Reason}", FormatUtilities.SanitizeForLog(host), FormatUtilities.SanitizeForLog(ex.Message, 300));
            var stale = known ? cached.Addresses : Array.Empty<IPAddress>();
            _resolved[host] = (DateTime.UtcNow, stale);
            return stale;
        }
    }
}

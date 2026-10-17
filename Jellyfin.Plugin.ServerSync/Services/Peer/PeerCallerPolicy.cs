using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

public enum PeerCallerCode
{
    Allowed,
    UserNotWhitelisted,
    IpNotWhitelisted,
    UserNotWhitelistedForIp
}

public readonly record struct PeerCallerVerdict(PeerCallerCode Code, string? Refusal, Guid? ScopedUserId)
{
    public static PeerCallerVerdict Allow(Guid? scopedUserId) => new(PeerCallerCode.Allowed, null, scopedUserId);

    public static PeerCallerVerdict Refuse(PeerCallerCode code, string reason) => new(code, reason, null);
}

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

    public PeerCallerPolicy(IPluginConfigurationManager configManager, ILogger<PeerCallerPolicy> logger)
    {
        _configManager = configManager;
        _logger = logger;
    }

    public static string Describe(PeerCallerCode code) => code switch
    {
        PeerCallerCode.UserNotWhitelisted => "This user is not whitelisted",
        PeerCallerCode.IpNotWhitelisted => "This IP is not whitelisted",
        PeerCallerCode.UserNotWhitelistedForIp => "This user is not whitelisted for this IP",
        _ => "Allowed"
    };

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
                _logger.LogInformation("Whitelist entry '{Host}' moved to a new address, which now matches {Caller}", host, caller);
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
            _logger.LogError("Could not look up the whitelist entry '{Host}': {Reason}", host, ex.Message);
            var stale = known ? cached.Addresses : Array.Empty<IPAddress>();
            _resolved[host] = (DateTime.UtcNow, stale);
            return stale;
        }
    }
}

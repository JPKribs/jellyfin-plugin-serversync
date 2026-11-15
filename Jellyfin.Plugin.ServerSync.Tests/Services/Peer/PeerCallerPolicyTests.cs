using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Peer;

/// <summary>
/// The caller policy admits a peer by its address and its user, so only a listed server or a granted user gets in.
/// </summary>
public class PeerCallerPolicyTests
{
    private const string SelfId = "0f8fad5bd9cb469fa16570867728950e";
    private const string OtherId = "7c9e6679742540de944be07fc1f90ae7";

    /// <summary>
    /// A range written with host bits set still covers its whole network.
    /// True: an address typed as 192.168.1.5/24 allows every address in 192.168.1.0/24.
    /// False: a location would match only the exact host or none at all.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.5/24", "192.168.1.200", true)]
    [InlineData("192.168.1.5/24", "192.168.2.1", false)]
    [InlineData("10.0.0.0/8", "10.200.1.1", true)]
    [InlineData("fe80::1/64", "fe80::abcd", true)]
    public void Ranges_WithHostBitsSet_StillCoverTheirNetwork(string range, string address, bool inside)
    {
        Assert.True(PeerCallerPolicy.TryParseRange(range, out var network));
        Assert.Equal(inside, network.Contains(IPAddress.Parse(address)));
    }

    /// <summary>
    /// Every way of writing a location reduces to its host or range.
    /// True: a URL, a host with a port, a range, and a bracketed IPv6 address all read correctly.
    /// False: a location written with a scheme or port would never match a caller.
    /// </summary>
    [Theory]
    [InlineData("myserver.example.com", "myserver.example.com")]
    [InlineData("https://myserver.example.com:8920/jellyfin", "myserver.example.com")]
    [InlineData("myserver.example.com:8096", "myserver.example.com")]
    [InlineData("192.168.1.0/24", "192.168.1.0/24")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("[fe80::1]:8096", "fe80::1")]
    public void HostOf_ReadsEveryEntryForm(string entry, string expected)
    {
        Assert.Equal(expected, PeerCallerPolicy.HostOf(entry));
    }

    private static ClaimsPrincipal Admin() => Principal("Administrator", OtherId, false);

    private static ClaimsPrincipal ApiKey() => Principal("Administrator", "00000000000000000000000000000000", true);

    private static ClaimsPrincipal UserCaller(string id = SelfId) => Principal("User", id, false);

    private static ClaimsPrincipal Principal(string role, string userId, bool apiKey)
        => new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "caller"),
            new Claim(ClaimTypes.Role, role),
            new Claim("Jellyfin-UserId", userId),
            new Claim("Jellyfin-IsApiKey", apiKey.ToString())
        }, "Custom"));

    private static PluginConfiguration WithServer(string url = "http://198.51.100.7:8096") => new()
    {
        Servers = new List<SourceServer> { new() { Name = "peer", Url = url, ApiKey = "k" } }
    };

    /// <summary>
    /// An administrator is allowed from an address a Servers tab entry names, and only while that entry is enabled.
    /// True: a peer on the Servers tab can call, a stranger or a disabled entry cannot.
    /// False: any address could call, or a disabled server could still call.
    /// </summary>
    [Fact]
    public async Task Administrators_FromServersTabAddresses_AreAllowed()
    {
        var config = WithServer();
        var policy = Policy(config);

        Assert.Equal(PeerCallerCode.Allowed, (await policy.CheckAsync(IPAddress.Parse("198.51.100.7"), Admin(), CancellationToken.None)).Code);
        Assert.Equal(PeerCallerCode.Allowed, (await policy.CheckAsync(IPAddress.Parse("::ffff:198.51.100.7"), ApiKey(), CancellationToken.None)).Code);
        Assert.Equal(PeerCallerCode.IpNotWhitelisted, (await policy.CheckAsync(IPAddress.Parse("198.51.100.8"), Admin(), CancellationToken.None)).Code);

        config.Servers[0].IsEnabled = false;
        Assert.Equal(PeerCallerCode.IpNotWhitelisted, (await policy.CheckAsync(IPAddress.Parse("198.51.100.7"), Admin(), CancellationToken.None)).Code);
    }

    /// <summary>
    /// A standard user needs a grant, whatever address it calls from, and a grant scopes the call to that user.
    /// True: a user cannot call without a grant, and a granted call carries the user's id.
    /// False: a standard user could call from a server address with no grant, or a grant would not limit the call to that user.
    /// </summary>
    [Fact]
    public async Task StandardUsers_NeedAGrant_EvenFromAServerAddress()
    {
        var config = WithServer();
        var policy = Policy(config);

        Assert.Equal(PeerCallerCode.UserNotWhitelisted, (await policy.CheckAsync(IPAddress.Parse("198.51.100.7"), UserCaller(), CancellationToken.None)).Code);

        config.PeerCallerGrants.Add(new PeerCallerGrant { UserId = SelfId });
        var verdict = await policy.CheckAsync(IPAddress.Parse("203.0.113.9"), UserCaller(), CancellationToken.None);
        Assert.Equal(PeerCallerCode.Allowed, verdict.Code);
        Assert.Equal(Guid.Parse(SelfId), verdict.ScopedUserId);
    }

    /// <summary>
    /// A grant with locations locks its user to those locations.
    /// True: the user is allowed from the listed range and address, and refused elsewhere or when another user calls.
    /// False: a location grant would not restrict where the user may call from.
    /// </summary>
    [Fact]
    public async Task Grants_LockAUserToItsLocations()
    {
        var config = new PluginConfiguration
        {
            PeerCallerGrants = new List<PeerCallerGrant> { new() { UserId = SelfId, Locations = new List<string> { "192.168.1.0/24", "10.0.0.5" } } }
        };
        var policy = Policy(config);

        Assert.Equal(PeerCallerCode.Allowed, (await policy.CheckAsync(IPAddress.Parse("192.168.1.20"), UserCaller(), CancellationToken.None)).Code);
        Assert.Equal(PeerCallerCode.Allowed, (await policy.CheckAsync(IPAddress.Parse("10.0.0.5"), UserCaller(), CancellationToken.None)).Code);
        Assert.Equal(PeerCallerCode.UserNotWhitelistedForIp, (await policy.CheckAsync(IPAddress.Parse("192.168.2.20"), UserCaller(), CancellationToken.None)).Code);
        Assert.Equal(PeerCallerCode.UserNotWhitelisted, (await policy.CheckAsync(IPAddress.Parse("192.168.1.20"), UserCaller(OtherId), CancellationToken.None)).Code);
    }

    /// <summary>
    /// An API key grant with no locations allows every address and scopes to no user.
    /// True: a key granted everywhere works from any address.
    /// False: a key with an empty location list would be refused or tied to a user.
    /// </summary>
    [Fact]
    public async Task ApiKeyGrant_WithNoLocations_AllowsEverywhere()
    {
        var policy = Policy(new PluginConfiguration { PeerCallerGrants = new List<PeerCallerGrant> { new() { UserId = PeerCallerGrant.ApiKeys } } });

        var verdict = await policy.CheckAsync(IPAddress.Parse("203.0.113.9"), ApiKey(), CancellationToken.None);
        Assert.Equal(PeerCallerCode.Allowed, verdict.Code);
        Assert.Null(verdict.ScopedUserId);
    }

    /// <summary>
    /// A host name in a grant is resolved to its addresses.
    /// True: a caller at the name's address is allowed and another address is refused.
    /// False: a grant written as a host name would never match.
    /// </summary>
    [Fact]
    public async Task HostNames_AreResolved()
    {
        var policy = Policy(new PluginConfiguration
        {
            PeerCallerGrants = new List<PeerCallerGrant> { new() { UserId = PeerCallerGrant.ApiKeys, Locations = new List<string> { "localhost" } } }
        });
        var loopback = (await Dns.GetHostAddressesAsync("localhost")).First();

        Assert.Equal(PeerCallerCode.Allowed, (await policy.CheckAsync(loopback, ApiKey(), CancellationToken.None)).Code);
        Assert.Equal(PeerCallerCode.UserNotWhitelistedForIp, (await policy.CheckAsync(IPAddress.Parse("203.0.113.9"), ApiKey(), CancellationToken.None)).Code);
    }

    private static PeerCallerPolicy Policy(PluginConfiguration config)
        => new(new FakeConfigManager(config), NullLogger<PeerCallerPolicy>.Instance);
}

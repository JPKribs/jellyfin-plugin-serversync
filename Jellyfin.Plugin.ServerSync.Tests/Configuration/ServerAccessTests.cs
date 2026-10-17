using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Configuration;

public class ServerAccessTests
{
    private const string SelfId = "0f8fad5bd9cb469fa16570867728950e";
    private const string OtherId = "7c9e6679742540de944be07fc1f90ae7";

    private static SourceServer StandardUser(ServerMode mode = ServerMode.Pull) => new()
    {
        Name = "peer",
        Url = "http://peer:8096",
        ApiKey = "k",
        Mode = mode,
        AccessLevel = SourceServer.StandardUserAccess,
        AuthenticatedUser = "self",
        AuthenticatedUserId = SelfId,
        UserMappings = new List<UserMapping>
        {
            new() { IsEnabled = true, SourceUserId = "0f8fad5b-d9cb-469f-a165-70867728950e", LocalUserId = SelfId },
            new() { IsEnabled = true, SourceUserId = OtherId, LocalUserId = OtherId }
        }
    };

    [Fact]
    public void StandardUser_OnlyMapsItsOwnUser()
    {
        var server = StandardUser();

        var mappings = server.GetEnabledUserMappings();

        Assert.Single(mappings);
        Assert.True(server.MaySyncUser(SelfId));
        Assert.False(server.MaySyncUser(OtherId));
    }

    [Fact]
    public void Administrator_MapsEveryUser()
    {
        var server = StandardUser();
        server.AccessLevel = SourceServer.AdministratorAccess;

        Assert.Equal(2, server.GetEnabledUserMappings().Count);
    }

    [Fact]
    public void StandardUser_WithoutKnownId_KeepsEveryMapping()
    {
        var server = StandardUser();
        server.AuthenticatedUserId = string.Empty;

        Assert.Equal(2, server.GetEnabledUserMappings().Count);
    }

    [Fact]
    public void StandardUser_KeepsPushAndSync()
    {
        var server = StandardUser(ServerMode.Sync);
        Assert.True(server.Pushes);
        Assert.True(server.Pulls);

        var config = new PluginConfiguration { Servers = new List<SourceServer> { server } };
        config.SanitizeValues();

        Assert.Equal(ServerMode.Sync, config.Servers[0].Mode);
    }

    [Fact]
    public void UnknownAccess_KeepsEveryMappingAndMode()
    {
        var server = StandardUser(ServerMode.Push);
        server.AccessLevel = string.Empty;

        var config = new PluginConfiguration { Servers = new List<SourceServer> { server } };
        config.SanitizeValues();

        Assert.Equal(ServerMode.Push, config.Servers[0].Mode);
        Assert.True(config.Servers[0].Pushes);
        Assert.Equal(2, config.Servers[0].GetEnabledUserMappings().Count);
    }

    [Fact]
    public void Grants_AreOneRowPerUserWithCleanLocations()
    {
        var config = new PluginConfiguration
        {
            PeerCallerGrants = new List<PeerCallerGrant>
            {
                new() { UserId = "0f8fad5b-d9cb-469f-a165-70867728950e", Locations = new List<string> { " myserver.example.com , google.net", "" } },
                new() { UserId = SelfId, Locations = new List<string> { "MYSERVER.example.com", "10.0.0.0/8" } },
                new() { UserId = " " }
            }
        };

        config.SanitizeValues();

        var grant = Assert.Single(config.PeerCallerGrants);
        Assert.Equal(SelfId, grant.UserId);
        Assert.Equal(new[] { "myserver.example.com", "google.net", "10.0.0.0/8" }, grant.Locations);
    }

    [Fact]
    public void Grants_AnEmptyRowKeepsEveryLocation_AndInvalidOnlyRowsAreDropped()
    {
        var config = new PluginConfiguration
        {
            PeerCallerGrants = new List<PeerCallerGrant>
            {
                new() { UserId = SelfId, Locations = new List<string> { "10.0.0.5" } },
                new() { UserId = SelfId },
                new() { UserId = OtherId, Locations = new List<string> { new string('a', 300) } }
            }
        };

        config.SanitizeValues();

        var grant = Assert.Single(config.PeerCallerGrants);
        Assert.Equal(SelfId, grant.UserId);
        Assert.Empty(grant.Locations);
    }

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

    [Fact]
    public async Task ApiKeyGrant_WithNoLocations_AllowsEverywhere()
    {
        var policy = Policy(new PluginConfiguration { PeerCallerGrants = new List<PeerCallerGrant> { new() { UserId = PeerCallerGrant.ApiKeys } } });

        var verdict = await policy.CheckAsync(IPAddress.Parse("203.0.113.9"), ApiKey(), CancellationToken.None);
        Assert.Equal(PeerCallerCode.Allowed, verdict.Code);
        Assert.Null(verdict.ScopedUserId);
    }

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

    private sealed class FakeConfigManager : IPluginConfigurationManager
    {
        public FakeConfigManager(PluginConfiguration configuration) => Configuration = configuration;

        public PluginConfiguration Configuration { get; }

        public string DecryptApiKey(string protectedKey) => protectedKey;

        public string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl) => requestApiKey ?? string.Empty;

        public void SaveConfiguration()
        {
        }

        public string GetTempDownloadPath() => string.Empty;

        public string LocalServerName => string.Empty;

        public string PluginVersion => "0.0.0-test";
    }
}

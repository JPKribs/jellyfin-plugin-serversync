using System;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

/// <summary>
/// A Server URL is accepted only when it is a well formed HTTP address outside the ranges the SSRF rules close.
/// </summary>
public sealed class ConfigurationUtilitiesTests
{
    private static SourceServer Server(ServerMode mode = ServerMode.Pull, bool enabled = true, string url = "https://source.example", string apiKey = "key")
        => new() { Mode = mode, IsEnabled = enabled, Url = url, ApiKey = apiKey };

    private static PluginConfiguration Config(params SourceServer[] servers)
    {
        var config = new PluginConfiguration();
        config.Servers.AddRange(servers);
        return config;
    }

    /// <summary>
    /// One enabled entry with a Server URL and API Key that this server pulls from is enough.
    /// True: tasks run as soon as one source is set up.
    /// False: a working source would be reported as not configured and nothing would sync.
    /// </summary>
    [Theory]
    [InlineData(ServerMode.Pull)]
    [InlineData(ServerMode.Sync)]
    public void HasValidAuthConfiguration_OnePullingServer_IsTrue(ServerMode mode)
    {
        var config = Config(Server(ServerMode.Push), Server(mode));

        var valid = ConfigurationUtilities.HasValidAuthConfiguration(config);

        Assert.True(valid);
    }

    /// <summary>
    /// No entries, a push only entry, a disabled entry, and an entry missing its URL or key do not count.
    /// True: tasks stay idle until a source can really be pulled from.
    /// False: a task would start against a source with no address or key and fail every run.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HasValidAuthConfiguration_NothingToPullFrom_IsFalse(int scenario)
    {
        var config = scenario switch
        {
            0 => Config(),
            1 => Config(Server(ServerMode.Push)),
            2 => Config(Server(enabled: false)),
            3 => Config(Server(url: " ")),
            _ => Config(Server(apiKey: string.Empty))
        };

        var valid = ConfigurationUtilities.HasValidAuthConfiguration(config);

        Assert.False(valid);
    }

    /// <summary>
    /// Missing settings are refused.
    /// True: the caller fails at once with the argument named.
    /// False: a task would fail with a null reference and no cause.
    /// </summary>
    [Fact]
    public void HasValidAuthConfiguration_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigurationUtilities.HasValidAuthConfiguration(null!));
    }

    /// <summary>
    /// A URL that is empty, holds a path trick, is not a URL, or is not HTTP is refused with the reason.
    /// True: the settings page tells the operator what is wrong with the address.
    /// False: a file or FTP address, or a path that climbs, would be stored and called.
    /// </summary>
    [Theory]
    [InlineData(null, "The URL is empty")]
    [InlineData("", "The URL is empty")]
    [InlineData("   ", "The URL is empty")]
    [InlineData("https://source.example/../admin", "URL contains invalid path sequences")]
    [InlineData("https://source.example/./admin", "URL contains invalid path sequences")]
    [InlineData("not a url", "The URL is not a valid absolute URL")]
    [InlineData("source.example:8096", "Only http and https URLs are allowed")]
    [InlineData("ftp://source.example", "Only http and https URLs are allowed")]
    [InlineData("file:///etc/passwd", "Only http and https URLs are allowed")]
    public void ValidateServerUrlForSsrf_MalformedUrl_IsRefusedWithTheReason(string? url, string expected)
    {
        var error = ConfigurationUtilities.ValidateServerUrlForSsrf(url!);

        Assert.Equal(expected, error);
    }

    /// <summary>
    /// With private networks disallowed, each private range is closed exactly at its edges.
    /// True: the addresses just outside a private range stay reachable and those inside do not.
    /// False: a range drawn too wide would block public servers, and one too narrow would open the LAN.
    /// </summary>
    [Theory]
    [InlineData("https://10.0.0.0", false)]
    [InlineData("https://10.255.255.255", false)]
    [InlineData("https://9.255.255.255", true)]
    [InlineData("https://11.0.0.0", true)]
    [InlineData("https://172.16.0.0", false)]
    [InlineData("https://172.31.255.255", false)]
    [InlineData("https://172.15.255.255", true)]
    [InlineData("https://172.32.0.0", true)]
    [InlineData("https://192.168.0.0", false)]
    [InlineData("https://192.168.255.255", false)]
    [InlineData("https://192.167.255.255", true)]
    [InlineData("https://192.169.0.0", true)]
    [InlineData("https://[fc00::1]", false)]
    [InlineData("https://[fdff::1]", false)]
    [InlineData("https://[fe00::1]", true)]
    [InlineData("https://[2001:db8::1]", true)]
    public void ValidateServerUrlForSsrf_PrivateNetworksDisallowed_ClosesEachRangeAtItsEdges(string url, bool allowed)
    {
        var error = ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork: false);

        Assert.Equal(allowed, error is null);
    }

    /// <summary>
    /// The 0.0.0.0/8 block and the IPv6 link-local and site-local ranges are refused even over HTTPS with private networks allowed.
    /// True: an address that reaches this host or the local link is never stored as a server.
    /// False: a peer could be pointed at the plugin's own host through an odd address.
    /// </summary>
    [Theory]
    [InlineData("https://0.0.0.0:8096", "The unspecified address range 0.0.0.0/8 is not allowed")]
    [InlineData("https://0.1.2.3", "The unspecified address range 0.0.0.0/8 is not allowed")]
    [InlineData("https://[fe80::1]:8096", "Link local addresses (fe80::/10) are not allowed")]
    [InlineData("https://[fec0::1]:8096", "Site local addresses (fec0::/10) are not allowed")]
    [InlineData("https://169.254.0.1", "Link local addresses (169.254.0.0/16, which includes the cloud metadata address) are not allowed")]
    public void ValidateServerUrlForSsrf_AlwaysClosedRanges_AreRefusedEvenWhenPrivateIsAllowed(string url, string expected)
    {
        var error = ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork: true);

        Assert.Contains(expected, error ?? string.Empty, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// With no second argument, private networks are allowed.
    /// True: a server on the LAN validates by default, as most installs need.
    /// False: every home setup would be refused unless each caller opted in.
    /// </summary>
    [Fact]
    public void ValidateServerUrlForSsrf_DefaultArgument_AllowsPrivateNetworks()
    {
        var error = ConfigurationUtilities.ValidateServerUrlForSsrf("http://192.168.1.10:8096");

        Assert.Null(error);
    }

    /// <summary>
    /// An address literal and an HTTPS name with private networks allowed are answered without a name lookup, the same as the plain check.
    /// True: validating an address never waits on DNS.
    /// False: saving the settings would hang or fail when DNS is down.
    /// </summary>
    [Theory]
    [InlineData("https://203.0.113.10:8096", true)]
    [InlineData("https://[2001:db8::1]:8096", false)]
    [InlineData("https://jellyfin.example.com", true)]
    public async Task ValidateServerUrlForSsrfAsync_NoLookupNeeded_Accepts(string url, bool allowPrivateNetwork)
    {
        var error = await ConfigurationUtilities.ValidateServerUrlForSsrfAsync(url, allowPrivateNetwork);

        Assert.Null(error);
    }

    /// <summary>
    /// A URL the plain check refuses is refused with the same reason, before any lookup.
    /// True: both checks give the operator one answer.
    /// False: a malformed address would be sent to DNS, or pass one check and fail the other.
    /// </summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("ftp://source.example", true)]
    [InlineData("https://source.example/../x", false)]
    [InlineData("http://203.0.113.10:8096", true)]
    [InlineData("https://10.0.0.5", false)]
    public async Task ValidateServerUrlForSsrfAsync_RefusedByThePlainCheck_GivesTheSameReason(string url, bool allowPrivateNetwork)
    {
        var expected = ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork);

        var error = await ConfigurationUtilities.ValidateServerUrlForSsrfAsync(url, allowPrivateNetwork);

        Assert.NotNull(error);
        Assert.Equal(expected, error);
    }

    /// <summary>
    /// Plain HTTP is allowed at both ends of the carrier NAT block and refused just outside it, and for the always closed ranges.
    /// True: a tailnet address may use HTTP and its public neighbors may not.
    /// False: an API key would cross the internet unencrypted, or a tailnet server would be refused.
    /// </summary>
    [Theory]
    [InlineData("100.64.0.0", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::", false)]
    public void AllowsPlainHttp_RangeEdges_FollowTheRule(string address, bool expected)
    {
        var allowed = JPKribs.Jellyfin.Base.SsrfGuard.IsPrivateOrLoopback(IPAddress.Parse(address));

        Assert.Equal(expected, allowed);
    }

    /// <summary>
    /// A missing address is refused.
    /// True: the caller fails at once with the argument named.
    /// False: the connect guard would fail with a null reference mid connection.
    /// </summary>
    [Fact]
    public void AllowsPlainHttp_NullAddress_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => JPKribs.Jellyfin.Base.SsrfGuard.IsPrivateOrLoopback(null!));
    }

    /// <summary>
    /// Addresses that are not URLs compare as text, ignoring case, outer spaces, and a trailing slash, and two missing ones are the same.
    /// True: a half typed address still matches its stored copy.
    /// False: the same server would be added twice because of a space or a slash.
    /// </summary>
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "", true)]
    [InlineData("", "  ", true)]
    [InlineData("server-a", "SERVER-A/", true)]
    [InlineData("  server-a  ", "server-a", true)]
    [InlineData("server-a", "server-b", false)]
    [InlineData(null, "https://a", false)]
    public void SameServerUrl_NotBothUrls_ComparesAsText(string? a, string? b, bool expected)
    {
        var same = ConfigurationUtilities.SameServerUrl(a, b);

        Assert.Equal(expected, same);
    }

    /// <summary>
    /// Two URLs are the same server across outer spaces, the scheme's default port, path case, and a query, and differ on the port.
    /// True: one server is recognized however its address was typed.
    /// False: a retyped address would create a second entry for the same server.
    /// </summary>
    [Theory]
    [InlineData("  https://a.example  ", "https://a.example", true)]
    [InlineData("https://a.example", "https://a.example:443/", true)]
    [InlineData("HTTPS://A.EXAMPLE/Jellyfin", "https://a.example/jellyfin/", true)]
    [InlineData("https://a.example/?x=1", "https://a.example", true)]
    [InlineData("https://a.example", "https://a.example:8443", false)]
    public void SameServerUrl_BothUrls_ComparesSchemeHostPortAndPath(string a, string b, bool expected)
    {
        var same = ConfigurationUtilities.SameServerUrl(a, b);

        Assert.Equal(expected, same);
    }

    /// <summary>
    /// Carrier NAT addresses, which Tailscale uses, count as a private network, and the broadcast and reserved ranges are always refused.
    /// True: a server entry reaches 100.64.0.0/10 only with Allow Private Network Address on, and never the ranges no server lives in.
    /// False: a public-only entry could be pointed at a private overlay network.
    /// </summary>
    [Theory]
    [InlineData("https://100.64.0.1", true, true)]
    [InlineData("https://100.64.0.1", false, false)]
    [InlineData("https://100.127.255.254", false, false)]
    [InlineData("https://100.128.0.1", false, true)]
    [InlineData("https://255.255.255.255", true, false)]
    [InlineData("https://240.0.0.1", true, false)]
    public void ValidateServerUrlForSsrf_OverlayAndReservedRanges_FollowTheGuard(string url, bool allowPrivateNetwork, bool allowed)
    {
        var refusal = ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork);

        Assert.Equal(allowed, refusal is null);
    }

    /// <summary>
    /// Plain HTTP is allowed to a private or carrier NAT address and refused to a public one.
    /// True: an API key travels unencrypted only inside a private network.
    /// False: a key could be sent in the clear across the internet.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.169.254", false)]
    public void AllowsPlainHttp_OnlyToPrivateAddresses(string address, bool expected)
    {
        var allowed = JPKribs.Jellyfin.Base.SsrfGuard.IsPrivateOrLoopback(System.Net.IPAddress.Parse(address));

        Assert.Equal(expected, allowed);
    }
}

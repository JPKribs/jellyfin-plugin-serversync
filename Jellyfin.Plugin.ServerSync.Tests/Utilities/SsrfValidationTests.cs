using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

public class SsrfValidationTests
{
    /// <summary>
    /// A name that points at a loopback or private address is refused when private networks are
    /// disallowed, the same way the address itself is.
    /// True: "localhost" cannot stand in for 127.0.0.1 on an entry meant for the public internet.
    /// False: the private network rule is a check on spelling.
    /// </summary>
    [Fact]
    public async Task ResolvingValidator_RefusesANameThatPointsAtLoopback()
    {
        var error = await ConfigurationUtilities.ValidateServerUrlForSsrfAsync("http://localhost:8096", allowPrivateNetwork: false);
        Assert.NotNull(error);
        Assert.Contains("resolves to", error, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The same name passes when private networks are allowed, with no resolution at all.
    /// True: a home install naming its peers by LAN name keeps working.
    /// False: every LAN name is refused.
    /// </summary>
    [Fact]
    public async Task ResolvingValidator_AllowsPrivateNamesWhenAllowed()
    {
        Assert.Null(await ConfigurationUtilities.ValidateServerUrlForSsrfAsync("http://localhost:8096", allowPrivateNetwork: true));
    }

    /// <summary>
    /// An address written into the URL is refused without any resolution.
    /// True: the quick check still covers literals.
    /// False: a literal slips past when the resolver is skipped.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:8096")]
    [InlineData("http://10.1.2.3:8096")]
    [InlineData("http://172.19.0.4:8096")]
    [InlineData("http://192.168.1.10:8096")]
    [InlineData("http://[::1]:8096")]
    public async Task ResolvingValidator_RefusesPrivateLiterals(string url)
    {
        Assert.NotNull(await ConfigurationUtilities.ValidateServerUrlForSsrfAsync(url, allowPrivateNetwork: false));
    }

    /// <summary>
    /// The link local range that cloud metadata services live in is refused even when private networks
    /// are allowed.
    /// True: no entry can be pointed at an instance's credentials.
    /// False: the metadata block only works when the operator turns private networks off.
    /// </summary>
    [Fact]
    public void MetadataRange_IsAlwaysRefused()
    {
        Assert.NotNull(ConfigurationUtilities.ValidateServerUrlForSsrf("http://169.254.169.254/latest", allowPrivateNetwork: true));
    }

    /// <summary>
    /// The IPv6 unspecified address and multicast groups are refused even when private networks are
    /// allowed.
    /// True: "[::]" cannot reach this host the way 0.0.0.0 could, and no entry points at a group.
    /// False: "[::]" slips through as an ordinary IPv6 address.
    /// </summary>
    [Theory]
    [InlineData("http://[::]:8096")]
    [InlineData("http://[ff02::1]:8096")]
    [InlineData("http://224.0.0.1:8096")]
    [InlineData("http://239.255.255.250:8096")]
    public void UnspecifiedAndMulticast_AreAlwaysRefused(string url)
    {
        Assert.NotNull(ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork: true));
    }
}

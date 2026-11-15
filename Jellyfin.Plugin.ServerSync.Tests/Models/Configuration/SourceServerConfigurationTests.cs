using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Configuration;

/// <summary>
/// The server list replaces the single source. A configuration written by an older version must land in
/// the list, the list order must be the scan priority, and the legacy elements must never be written again.
/// </summary>
public class SourceServerConfigurationTests
{
    private static SourceServer Server(string name, ServerMode mode = ServerMode.Pull, bool enabled = true, string url = "http://peer:8096", string key = "k")
        => new() { Name = name, Url = url, ApiKey = key, Mode = mode, IsEnabled = enabled };

    /// <summary>
    /// Scan servers come back in list order and only when enabled, configured, and in a scanning mode.
    /// True: index zero is the priority the operator set on the page.
    /// </summary>
    [Fact]
    public void GetPullServers_KeepsOrderAndFiltersNonScanning()
    {
        var config = new PluginConfiguration();
        config.Servers.Add(Server("first"));
        config.Servers.Add(Server("send-only", ServerMode.Push));
        config.Servers.Add(Server("disabled", enabled: false));
        config.Servers.Add(Server("no-key", key: string.Empty));
        config.Servers.Add(Server("both", ServerMode.Sync));

        var names = config.GetPullServers().Select(s => s.Name).ToList();

        Assert.Equal(new[] { "first", "both" }, names);
    }

    /// <summary>
    /// The mapping helpers union every scan server's enabled mappings in priority order.
    /// </summary>
    [Fact]
    public void GetEnabledLibraryMappings_UnionsScanServersInOrder()
    {
        var config = new PluginConfiguration();
        var a = Server("a");
        a.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "a1", IsEnabled = true });
        a.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "a2", IsEnabled = false });
        var b = Server("b");
        b.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "b1", IsEnabled = true });
        var off = Server("off", enabled: false);
        off.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "x", IsEnabled = true });
        config.Servers.Add(a);
        config.Servers.Add(b);
        config.Servers.Add(off);

        Assert.Equal(new[] { "a1", "b1" }, config.GetEnabledLibraryMappings().Select(m => m.SourceLibraryId));
        Assert.Equal(new[] { "a1", "a2", "b1", "x" }, config.GetAllLibraryMappings().Select(m => m.SourceLibraryId));
    }

    /// <summary>
    /// A row with no key belongs to the first scan server. A row with a key finds its entry anywhere in the list.
    /// </summary>
    [Fact]
    public void ResolveServer_NullKeyIsFirstScanServer()
    {
        var config = new PluginConfiguration();
        config.Servers.Add(Server("sender", ServerMode.Push));
        var first = Server("first");
        var second = Server("second");
        config.Servers.Add(first);
        config.Servers.Add(second);

        Assert.Same(first, config.ResolveServer(null));
        Assert.Same(second, config.ResolveServer(second.Key));
        Assert.Null(config.FindServer("missing"));
    }
}

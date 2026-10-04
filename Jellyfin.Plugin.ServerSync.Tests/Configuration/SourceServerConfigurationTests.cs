using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Configuration;

/// <summary>
/// The server list replaces the single source. A configuration written by an older version must land in
/// the list, the list order must be the scan priority, and the legacy elements must never be written again.
/// </summary>
public class SourceServerConfigurationTests
{
    private static SourceServer Server(string name, ServerMode mode = ServerMode.Pull, bool enabled = true, string url = "http://peer:8096", string key = "k")
        => new() { Name = name, Url = url, ApiKey = key, Mode = mode, IsEnabled = enabled };

    /// <summary>
    /// A legacy configuration file loads into the first server entry with its mappings, key, and names.
    /// True: upgrading keeps the user's source without retyping anything.
    /// False: every upgrade silently drops the source and every mapping.
    /// </summary>
    [Fact]
    public void LegacySingleSource_MigratesIntoFirstServer()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration>
              <SourceServerUrl>http://old:8096</SourceServerUrl>
              <SourceServerExternalUrl>https://old.example.com</SourceServerExternalUrl>
              <SourceServerApiKey>secret</SourceServerApiKey>
              <SourceServerAuthenticatedUser>admin</SourceServerAuthenticatedUser>
              <SourceServerAuthenticatedUserId>abc</SourceServerAuthenticatedUserId>
              <SourceServerName>Old Server</SourceServerName>
              <SourceServerId>serverid</SourceServerId>
              <AllowSourceServerOnPrivateNetwork>false</AllowSourceServerOnPrivateNetwork>
              <LibraryMappings>
                <LibraryMapping><SourceLibraryId>s1</SourceLibraryId><LocalLibraryId>l1</LocalLibraryId><LocalRootPath>/media</LocalRootPath></LibraryMapping>
              </LibraryMappings>
              <UserMappings>
                <UserMapping><SourceUserId>u1</SourceUserId><LocalUserId>v1</LocalUserId></UserMapping>
              </UserMappings>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        var config = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.True(config.MigrateLegacyServer());

        var server = Assert.Single(config.Servers);
        Assert.Equal("http://old:8096", server.Url);
        Assert.Equal("https://old.example.com", server.ExternalUrl);
        Assert.Equal("secret", server.ApiKey);
        Assert.Equal("admin", server.AuthenticatedUser);
        Assert.Equal("abc", server.AuthenticatedUserId);
        Assert.Equal("Old Server", server.ServerName);
        Assert.Equal("serverid", server.ServerId);
        Assert.False(server.AllowPrivateNetwork);
        Assert.Equal(ServerMode.Pull, server.Mode);
        Assert.Equal("s1", Assert.Single(server.LibraryMappings).SourceLibraryId);
        Assert.Equal("u1", Assert.Single(server.UserMappings).SourceUserId);
        Assert.False(string.IsNullOrEmpty(server.Key));

        // The legacy fields are cleared so nothing reads them by mistake.
        Assert.Equal(string.Empty, config.SourceServerUrl);
        Assert.Empty(config.LibraryMappings);
    }

    /// <summary>
    /// Migration runs once. A second call with a populated list changes nothing.
    /// </summary>
    [Fact]
    public void MigrateLegacyServer_WithServersPresent_IsNoOp()
    {
        var config = new PluginConfiguration();
        config.Servers.Add(Server("a"));
        config.SourceServerUrl = "http://stale:8096";

        Assert.False(config.MigrateLegacyServer());
        Assert.Single(config.Servers);
        Assert.Equal(string.Empty, config.SourceServerUrl);
    }

    /// <summary>
    /// The legacy elements are read only. Saving writes the server list and never the old names.
    /// True: a downgrade cannot resurrect a stale single source over the list.
    /// </summary>
    [Fact]
    public void LegacyElements_NeverSerialized()
    {
        var config = new PluginConfiguration();
        config.Servers.Add(Server("a"));

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        var xml = writer.ToString();

        Assert.Contains("<Servers>", xml, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<SourceServerUrl>", xml, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<LibraryMappings>", xml, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<UserMappings>", xml, System.StringComparison.Ordinal);
    }

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

    /// <summary>
    /// Validation names the server with a problem and requires a scan server for each pull module.
    /// </summary>
    [Fact]
    public void ValidateConfiguration_ReportsPerServerProblems()
    {
        var config = new PluginConfiguration { EnableMetadataSync = true };
        config.Servers.Add(new SourceServer { Name = "broken", Url = "not a url", ApiKey = "k" });
        config.Servers.Add(Server("sender", ServerMode.Push));

        var errors = config.ValidateConfiguration();

        Assert.Contains(errors, e => e.Contains("'broken'", System.StringComparison.Ordinal) && e.Contains("URL", System.StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("metadata sync", System.StringComparison.Ordinal));
    }
}

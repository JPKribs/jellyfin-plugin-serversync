using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Configuration;

/// <summary>
/// Saves from the settings page and loading the saved file. The page never sees a stored API key
/// and posts a sentinel in its place, so which stored key that sentinel may stand for decides where
/// the key is sent.
/// </summary>
public class ConfigurationSavingTests
{
    private static PluginConfiguration Stored(string key, string url, string apiKey = "enc:v1:stored")
    {
        var config = new PluginConfiguration();
        config.Servers.Add(new SourceServer { Key = key, Name = "Peer", Url = url, ApiKey = apiKey });
        return config;
    }

    /// <summary>
    /// An entry saved with its address unchanged keeps its stored key, ignoring case and a trailing slash.
    /// True: renaming or reordering a server never asks for the key again.
    /// False: every save from the page drops every key.
    /// </summary>
    [Theory]
    [InlineData("https://peer.example:8920")]
    [InlineData("https://PEER.example:8920/")]
    public void KeptApiKey_SameAddress_KeepsTheStoredKey(string incomingUrl)
    {
        var stored = Stored("a", "https://peer.example:8920");

        var kept = Plugin.KeptApiKeyFor(new SourceServer { Key = "a", Url = incomingUrl }, stored);

        Assert.Equal("enc:v1:stored", kept);
    }

    /// <summary>
    /// An entry whose address was edited does not keep the stored key, the same rule the endpoints
    /// apply to a request that names a new address.
    /// True: pointing an entry at another server cannot hand that server the old server's key.
    /// False: the kept sentinel sends the stored key to whatever address the entry now names.
    /// </summary>
    [Theory]
    [InlineData("https://attacker.example:8920")]
    [InlineData("https://peer.example:9999")]
    [InlineData("http://peer.example:8920")]
    [InlineData("https://peer.example:8920/other")]
    public void KeptApiKey_EditedAddress_DropsTheStoredKey(string incomingUrl)
    {
        var stored = Stored("a", "https://peer.example:8920");

        var kept = Plugin.KeptApiKeyFor(new SourceServer { Key = "a", Url = incomingUrl }, stored);

        Assert.Equal(string.Empty, kept);
    }

    /// <summary>
    /// A new entry has no stored key to keep.
    /// True: a sentinel on an entry the server never saw resolves to no key.
    /// False: another entry's key could be borrowed.
    /// </summary>
    [Fact]
    public void KeptApiKey_UnknownEntry_HasNoKey()
    {
        var stored = Stored("a", "https://peer.example:8920");

        Assert.Equal(string.Empty, Plugin.KeptApiKeyFor(new SourceServer { Key = "b", Url = "https://peer.example:8920" }, stored));
    }

    /// <summary>
    /// A file saved by a version that still wrote LastConnectionCheck loads, the same way Jellyfin
    /// loads plugin configuration (a plain XmlSerializer over an XmlReader), with every other value intact.
    /// True: removing the unused setting cannot break an upgrade.
    /// False: the saved settings fail to load and the plugin starts from defaults.
    /// </summary>
    [Fact]
    public void SavedFile_WithRemovedLastConnectionCheck_StillLoads()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <MinimumFreeDiskSpaceGb>25</MinimumFreeDiskSpaceGb>
              <LastConnectionCheck>2026-10-01T12:00:00Z</LastConnectionCheck>
              <LastSyncStartTime>2026-10-02T12:00:00Z</LastSyncStartTime>
              <EnableRecyclingBin>true</EnableRecyclingBin>
            </PluginConfiguration>
            """;

        using var reader = XmlReader.Create(new StringReader(xml));
        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;

        Assert.Equal(25, config.MinimumFreeDiskSpaceGb);
        Assert.True(config.EnableRecyclingBin);
        Assert.Equal(new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), config.LastSyncStartTime!.Value.ToUniversalTime());
    }
}

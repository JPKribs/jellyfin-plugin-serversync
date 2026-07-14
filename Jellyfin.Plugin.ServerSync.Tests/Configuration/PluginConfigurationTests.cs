using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Configuration;

/// <summary>
/// SanitizeValues runs inside every configuration save — including the
/// settings page's — so it must never throw, and the XML surface must
/// round-trip legacy elements without silently resetting user choices.
/// </summary>
public class PluginConfigurationTests
{
    /// <summary>
    /// Hostile or malformed paths (embedded NUL, the classic
    /// Path.GetFullPath thrower) must be dropped, not crash the save with a
    /// 500 the admin can't diagnose.
    /// </summary>
    [Fact]
    public void SanitizeValues_MalformedPaths_DroppedNotThrown()
    {
        var config = new PluginConfiguration
        {
            TempDownloadPath = "/tmp/ok\0bad",
            RecyclingBinPath = "/tmp/also\0bad"
        };
        config.LibraryMappings.Add(new LibraryMapping
        {
            IsEnabled = true,
            SourceLibraryId = "s",
            LocalLibraryId = "l",
            LocalRootPath = "/media\0evil"
        });

        var exception = Xunit.Record.Exception(() => config.SanitizeValues());

        Assert.Null(exception);
        Assert.True(string.IsNullOrEmpty(config.TempDownloadPath));
        Assert.True(string.IsNullOrEmpty(config.RecyclingBinPath));
        Assert.Equal(string.Empty, config.LibraryMappings[0].LocalRootPath);
    }

    /// <summary>
    /// Valid paths still normalize (traversal sequences resolved) — the
    /// robustness fix must not disable the normalization it wraps.
    /// </summary>
    [Fact]
    public void SanitizeValues_ValidPaths_StillNormalized()
    {
        var config = new PluginConfiguration
        {
            TempDownloadPath = Path.Combine(Path.GetTempPath(), "a", "..", "b")
        };

        config.SanitizeValues();

        Assert.DoesNotContain("..", config.TempDownloadPath);
        Assert.EndsWith("b", config.TempDownloadPath!);
    }

    /// <summary>
    /// A bandwidth schedule ending at midnight is a real configuration; the
    /// sanitizer must keep hour 0 rather than treating it as unset.
    /// </summary>
    [Fact]
    public void SanitizeValues_MidnightEndHour_Preserved()
    {
        var config = new PluginConfiguration
        {
            ScheduledStartHour = 22,
            ScheduledEndHour = 0
        };

        config.SanitizeValues();

        Assert.Equal(0, config.ScheduledEndHour);
    }

    /// <summary>
    /// Upgrade path for the removed 10.11.64.0 per-module flags: a config XML
    /// carrying either legacy element must land as DeepImageVerification=true
    /// instead of silently resetting the feature for users who enabled it.
    /// </summary>
    [Theory]
    [InlineData("MetadataSyncDeepImageVerification")]
    [InlineData("PeopleSyncDeepImageVerification")]
    public void LegacyDeepImageVerificationElements_MigrateOnDeserialize(string legacyElement)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <{legacyElement}>true</{legacyElement}>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        var config = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.True(config.DeepImageVerification);
    }

    /// <summary>
    /// The legacy shims are read-only compatibility: serializing a config
    /// must not write the old element names back out (they'd shadow the new
    /// setting forever).
    /// </summary>
    [Fact]
    public void LegacyDeepImageVerificationElements_NeverSerialized()
    {
        var config = new PluginConfiguration { DeepImageVerification = true };

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        var xml = writer.ToString();

        Assert.DoesNotContain("MetadataSyncDeepImageVerification", xml);
        Assert.DoesNotContain("PeopleSyncDeepImageVerification", xml);
        Assert.Contains("DeepImageVerification", xml);
    }

    /// <summary>
    /// Collection whitelist entries carry Type="BoxSet"; the marker decides
    /// whether the Sync Collections task mirrors them, so it must survive the
    /// XML round-trip — and configs written before the field existed must
    /// deserialize with a null Type, not fail.
    /// </summary>
    [Fact]
    public void FilteredItemType_RoundTripsAndToleratesLegacyXml()
    {
        var config = new PluginConfiguration();
        config.LibraryMappings.Add(new LibraryMapping
        {
            IsEnabled = true,
            SourceLibraryId = "s",
            LocalLibraryId = "l",
            FilterMode = LibraryFilterMode.Whitelist,
            FilteredItems = new System.Collections.Generic.List<FilteredItem>
            {
                new() { ItemId = "abc", Name = "Favorites", Type = "BoxSet" },
                new() { ItemId = "ghi", Name = "Road Trip Mix", Type = "Playlist" },
                new() { ItemId = "def", Name = "Some Movie" }
            }
        });

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var roundTripped = (PluginConfiguration)serializer.Deserialize(reader)!;

        var items = roundTripped.LibraryMappings[0].FilteredItems!;
        Assert.Equal("BoxSet", items[0].Type);
        Assert.Equal("Playlist", items[1].Type);
        Assert.Null(items[2].Type);
    }

    /// <summary>
    /// A legacy element must not TURN OFF a new-style setting that is
    /// already on (the shim is OR semantics, one-directional).
    /// </summary>
    [Fact]
    public void LegacyElementFalse_DoesNotDisableNewSetting()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration>
              <DeepImageVerification>true</DeepImageVerification>
              <MetadataSyncDeepImageVerification>false</MetadataSyncDeepImageVerification>
            </PluginConfiguration>
            """;

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        var config = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.True(config.DeepImageVerification);
    }
}

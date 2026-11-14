using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Configuration;

/// <summary>
/// SanitizeValues runs inside every configuration save, including the
/// settings page's, so it must never throw, and the XML surface must
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
        var server = new SourceServer { Url = "http://source:8096", ApiKey = "k" };
        server.LibraryMappings.Add(new LibraryMapping
        {
            IsEnabled = true,
            SourceLibraryId = "s",
            LocalLibraryId = "l",
            LocalRootPath = "/media\0evil"
        });
        config.Servers.Add(server);

        var exception = Xunit.Record.Exception(() => config.SanitizeValues());

        Assert.Null(exception);
        Assert.True(string.IsNullOrEmpty(config.TempDownloadPath));
        Assert.True(string.IsNullOrEmpty(config.RecyclingBinPath));
        Assert.Equal(string.Empty, config.Servers[0].LibraryMappings[0].LocalRootPath);
    }

    /// <summary>
    /// Valid paths still normalize (traversal sequences resolved), the
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
    /// A bandwidth schedule ending at midnight is a real configuration. The
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
    /// Equal start and end hours mean the scheduled speed applies all day.
    /// True: the schedule the user configured actually takes effect.
    /// False: the same-day comparison reduces to "hour >= X && hour &lt. X",
    /// which is never true, so the window silently never applies.
    /// </summary>
    [Fact]
    public void GetEffectiveDownloadSpeedBytes_EqualStartAndEndHour_UsesScheduledSpeed()
    {
        var config = new PluginConfiguration
        {
            EnableBandwidthScheduling = true,
            ScheduledStartHour = 3,
            ScheduledEndHour = 3,
            ScheduledDownloadSpeed = 5,
            ScheduledDownloadSpeedUnit = "Mbps",
            MaxDownloadSpeed = 50,
            DownloadSpeedUnit = "Mbps"
        };

        Assert.Equal(5L * 125_000, config.GetEffectiveDownloadSpeedBytes());
    }

    /// <summary>
    /// A window applies inside its hours and not outside them, including one that wraps past midnight.
    /// True: the scheduled speed is used exactly in its window, whatever hour the test runs at.
    /// False: the unscheduled speed would never be used, or a wrapping window would be ignored.
    /// </summary>
    [Theory]
    [InlineData(9, 17, 8, false)]
    [InlineData(9, 17, 9, true)]
    [InlineData(9, 17, 16, true)]
    [InlineData(9, 17, 17, false)]
    [InlineData(22, 6, 23, true)]
    [InlineData(22, 6, 3, true)]
    [InlineData(22, 6, 6, false)]
    [InlineData(22, 6, 12, false)]
    public void GetEffectiveDownloadSpeedBytes_UsesTheWindowAtThatHour(int start, int end, int hour, bool scheduled)
    {
        var config = new PluginConfiguration
        {
            EnableBandwidthScheduling = true,
            ScheduledStartHour = start,
            ScheduledEndHour = end,
            ScheduledDownloadSpeed = 5,
            ScheduledDownloadSpeedUnit = "Mbps",
            MaxDownloadSpeed = 50,
            DownloadSpeedUnit = "Mbps"
        };

        Assert.Equal((scheduled ? 5L : 50L) * 125_000, config.GetEffectiveDownloadSpeedBytes(hour));
    }

    /// <summary>
    /// Collection whitelist entries carry Type="BoxSet". The marker decides
    /// whether the Sync Collections task mirrors them, so it must survive the
    /// XML round-trip, and configs written before the field existed must
    /// deserialize with a null Type, not fail.
    /// </summary>
    [Fact]
    public void FilteredItemType_RoundTripsAndToleratesLegacyXml()
    {
        var config = new PluginConfiguration();
        var server = new SourceServer { Url = "http://source:8096", ApiKey = "k" };
        config.Servers.Add(server);
        server.LibraryMappings.Add(new LibraryMapping
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

        var items = roundTripped.Servers[0].LibraryMappings[0].FilteredItems!;
        Assert.Equal("BoxSet", items[0].Type);
        Assert.Equal("Playlist", items[1].Type);
        Assert.Null(items[2].Type);
    }

    /// <summary>
    /// A download speed kept in KB, MB, or GB per second before 12.4.12.0 becomes Mbps on load,
    /// rounded up so a limit never turns into no limit, and 0 stays no limit.
    /// True: an updated server downloads at the speed it did before.
    /// False: a limit could become eight times faster, or vanish.
    /// </summary>
    [Fact]
    public void SanitizeValues_ConvertsOldDownloadSpeedsToMbps()
    {
        var config = new PluginConfiguration
        {
            MaxDownloadSpeed = 10,
            DownloadSpeedUnit = "MB",
            ScheduledDownloadSpeed = 1,
            ScheduledDownloadSpeedUnit = "KB"
        };

        config.SanitizeValues();

        Assert.Equal(84, config.MaxDownloadSpeed);
        Assert.Equal(1, config.ScheduledDownloadSpeed);
        Assert.Equal("Mbps", config.DownloadSpeedUnit);
        Assert.Equal("Mbps", config.ScheduledDownloadSpeedUnit);

        config.SanitizeValues();
        Assert.Equal(84, config.MaxDownloadSpeed);
    }

    /// <summary>
    /// Removed files is one value: the older split of Delete plus an approval mode reads as Delete
    /// After Approval, and an approval mode alone reads as its action.
    /// True: a server set to delete after approval before the update still asks first.
    /// False: deletions would start without approval after the update.
    /// </summary>
    [Fact]
    public void SanitizeValues_ReadsTheOldRemovedFilesSplit()
    {
        var split = new PluginConfiguration
        {
            MissingContentAction = Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration.MissingContentAction.Delete,
            DeleteMissingContentMode = ApprovalMode.RequireApproval
        };
        split.SanitizeValues();
        Assert.Equal(Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration.MissingContentAction.DeleteAfterApproval, split.MissingContentAction);
        Assert.Equal(ApprovalMode.RequireApproval, split.DeletionApproval);

        var modeOnly = new PluginConfiguration { MissingContentAction = null, DeleteMissingContentMode = ApprovalMode.Enabled };
        modeOnly.SanitizeValues();
        Assert.Equal(Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration.MissingContentAction.Delete, modeOnly.MissingContentAction);
    }

    /// <summary>
    /// Skipping watched items follows the user list, and the old switch, when off, clears a list kept
    /// with it so nothing starts skipping on the update.
    /// </summary>
    [Fact]
    public void SanitizeValues_SkipWatchedFollowsTheList()
    {
        var off = new PluginConfiguration { SkipWatchedByAllUsers = false, WatchedFilterUserIds = new() { "a" } };
        off.SanitizeValues();
        Assert.Empty(off.WatchedFilterUserIds);
        Assert.False(off.SkipWatchedByAllUsers);

        var on = new PluginConfiguration { SkipWatchedByAllUsers = true, WatchedFilterUserIds = new() { "a" } };
        on.SanitizeValues();
        Assert.True(on.SkipWatchedByAllUsers);
    }
}

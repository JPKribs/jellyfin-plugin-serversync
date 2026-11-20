using System;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Api;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Statistics;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Api;

/// <summary>
/// The Statistics route answers only for the sync types that are on, and lists link streams only while a server may link to this one.
/// </summary>
public sealed class ConfigurationControllerStatisticsTests : IDisposable
{
    private readonly ControllerTestHarness _harness = new();
    private readonly SyncStatisticsStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationControllerStatisticsTests"/> class.
    /// </summary>
    public ConfigurationControllerStatisticsTests()
        => _store = new SyncStatisticsStore(_harness.Provider, NullLogger<SyncStatisticsStore>.Instance);

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// The route requires elevation and answers GET at Statistics.
    /// True: only an administrator reads the statistics.
    /// False: any signed in user could read what the server syncs.
    /// </summary>
    [Fact]
    public void Route_RequiresElevation()
        => ControllerRouteAssert.Elevated(typeof(ConfigurationController), nameof(ConfigurationController.GetStatistics), "GET", "Statistics");

    /// <summary>
    /// Only a sync type that is on is listed, in the order of the Sync tab, each with its own scans.
    /// True: the tab shows one section for each sync type in use.
    /// False: the tab would show an empty section for a sync type that is off.
    /// </summary>
    [Fact]
    public void GetStatistics_ListsOnlyTheSyncTypesThatAreOn()
    {
        var config = _harness.Config.Configuration;
        config.EnableContentSync = false;
        config.EnableHistorySync = true;
        config.EnableMetadataSync = false;
        config.EnablePeopleSync = true;
        config.EnableUserSync = true;
        _store.RecordRun("Content", new ScanRun(DateTime.UtcNow, 9, 9, 9));
        _store.RecordRun("People", new ScanRun(DateTime.UtcNow, 1, 2, 3));

        var statistics = Read();

        Assert.Equal(new[] { "History", "People", "User" }, statistics.Types.Select(type => type.Key));
        Assert.Empty(statistics.Types[0].Runs);
        var run = Assert.Single(statistics.Types[1].Runs);
        Assert.Equal((1, 2, 3), (run.Added, run.Updated, run.Removed));
        Assert.Null(statistics.LinkStreams);
    }

    /// <summary>
    /// Link streams are listed whenever a server entry may link to this server, with content sync on or off.
    /// True: a server that only serves links still shows its streams.
    /// False: such a server would count streams and never show them, or a server that offers no links would show an empty chart.
    /// </summary>
    [Theory]
    [InlineData(true, true, ServerMode.Push, true)]
    [InlineData(true, true, ServerMode.Sync, true)]
    [InlineData(true, true, ServerMode.Pull, false)]
    [InlineData(true, false, ServerMode.Push, false)]
    [InlineData(false, true, ServerMode.Push, true)]
    public void GetStatistics_LinkStreams_NeedAServerThatMayLink(bool content, bool allowLinking, ServerMode mode, bool listed)
    {
        _harness.Config.Configuration.EnableContentSync = content;
        var peer = _harness.AddPeer(mode);
        peer.AllowLinking = allowLinking;
        _store.CountLinkStream(DateOnly.FromDateTime(DateTime.Now), peer.Key);

        var statistics = Read();

        Assert.Equal(listed, statistics.LinkStreams is not null);
        if (listed)
        {
            Assert.Equal(SyncStatisticsStore.DaysKept, statistics.LinkStreams!.Days.Count);
            Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), statistics.LinkStreams.Days[^1]);
            var server = Assert.Single(statistics.LinkStreams.Servers);
            Assert.Equal((peer.Key, peer.DisplayName), (server.Key, server.Name));
            Assert.Equal(SyncStatisticsStore.DaysKept, server.Streams.Count);
            Assert.Equal(1, server.Streams[^1]);
        }
    }

    /// <summary>
    /// Each server that may link here gets its own counts, a server with no stream gets zeros, and a server that may not link is left out.
    /// True: the chart draws one line for each server that can stream from this one.
    /// False: a server's streams would be hidden, or shown for a server that may no longer link.
    /// </summary>
    [Fact]
    public void GetStatistics_LinkStreams_ListEachServerThatMayLink()
    {
        _harness.Config.Configuration.EnableContentSync = true;
        var den = _harness.AddPeer(ServerMode.Push, "den");
        den.AllowLinking = true;
        var attic = _harness.AddPeer(ServerMode.Sync, "attic");
        attic.AllowLinking = true;
        _harness.AddPeer(ServerMode.Push, "shed");
        var today = DateOnly.FromDateTime(DateTime.Now);
        _store.CountLinkStream(today, "den");
        _store.CountLinkStream(today, "den");
        _store.CountLinkStream(today, "shed");

        var servers = Read().LinkStreams!.Servers;

        Assert.Equal(new[] { "den", "attic" }, servers.Select(server => server.Key));
        Assert.Equal(2, servers[0].Streams[^1]);
        Assert.Equal(0, servers[1].Streams.Sum());
        Assert.Equal(SyncStatisticsStore.DaysKept, servers[1].Streams.Count);
    }

    private SyncStatisticsDto Read()
        => Assert.IsType<SyncStatisticsDto>(Assert.IsType<OkObjectResult>(_harness.Configuration().GetStatistics(_store).Result).Value);
}

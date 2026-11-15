using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.Common;

/// <summary>
/// A run connects every source that answers in rank order, finds the source a row belongs to by its key, and records its finish without letting a failed save hide the result.
/// </summary>
public sealed class ScanSourcesTests
{
    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();

    private SourceServer AddServer(string key, ServerMode mode = ServerMode.Pull)
    {
        var server = new SourceServer { Key = key, Name = key, Url = "http://" + key + ":8096", ApiKey = "k", Mode = mode };
        _config.Configuration.Servers.Add(server);
        return server;
    }

    private Task<List<ScanSource>> ConnectAsync(Action<string>? onUnavailable = null, CancellationToken cancellationToken = default)
        => (Task<List<ScanSource>>)TaskInternals.Call("ScanSources", "ConnectAsync", _config, _factory, NullLogger.Instance, "Test Task", onUnavailable, cancellationToken)!;

    private ScanSource? Find(IReadOnlyList<ScanSource> sources, string? key)
        => (ScanSource?)TaskInternals.Call("ScanSources", "Find", sources, _config, key);

    private ScanSource Connected(SourceServer server, int priority) => new(server, _factory.ClientFor(server), priority);

    private static void DisposeAll(IEnumerable<ScanSource> sources)
    {
        foreach (var source in sources)
        {
            source.Dispose();
        }
    }

    /// <summary>
    /// Every server that answers is connected, in list order, with its place in the list as its rank.
    /// True: the first server in the list outranks the rest for the whole run.
    /// False: rank would follow whichever server answered first.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_AllAnswer_ConnectsInRankOrder()
    {
        AddServer("a");
        AddServer("b", ServerMode.Sync);

        var sources = await ConnectAsync();

        Assert.Equal(new[] { "a", "b" }, sources.Select(s => s.Key).ToArray());
        Assert.Equal(new[] { 0, 1 }, sources.Select(s => s.Priority).ToArray());
        DisposeAll(sources);
    }

    /// <summary>
    /// A server that answers the connection test with an error is left out and reported, and the rest still connect with their own rank.
    /// True: one server being down does not stop the others from syncing.
    /// False: the run would abort, or the next server would take the missing one's rank.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ServerAnswersWithError_SkipsItAndReports()
    {
        AddServer("a");
        AddServer("b");
        _factory.HandlerFor("a").SystemInfoStatus = HttpStatusCode.InternalServerError;
        var reported = new List<string>();

        var sources = await ConnectAsync(reported.Add);

        var only = Assert.Single(sources);
        Assert.Equal("b", only.Key);
        Assert.Equal(1, only.Priority);
        Assert.Equal(new[] { "server 'a' is unreachable" }, reported);
        DisposeAll(sources);
    }

    /// <summary>
    /// A server whose client cannot be built is left out and reported as having an invalid URL.
    /// True: a bad Server URL on one entry does not stop the run.
    /// False: one mistyped URL would fail every sync type.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ClientRejected_SkipsItAndReportsTheUrl()
    {
        AddServer("a");
        AddServer("b");
        _factory.Rejected.Add("a");
        var reported = new List<string>();

        var sources = await ConnectAsync(reported.Add);

        Assert.Equal("b", Assert.Single(sources).Key);
        Assert.Equal(new[] { "server 'a' has an invalid URL" }, reported);
        DisposeAll(sources);
    }

    /// <summary>
    /// Only servers this one pulls from are connected, so a Push server, a disabled one, and one with no API Key are never asked.
    /// True: a run reads only from the servers the administrator chose as sources.
    /// False: a Push only server would be scanned and its files pulled.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ServersThatDoNotPull_AreNeverAsked()
    {
        AddServer("push", ServerMode.Push);
        AddServer("off").IsEnabled = false;
        AddServer("nokey").ApiKey = string.Empty;

        var sources = await ConnectAsync(_ => { });

        Assert.Empty(sources);
        Assert.Empty(_factory.Created);
    }

    /// <summary>
    /// A cancelled token stops the connection and is not reported as an unreachable server.
    /// True: stopping the task ends the run at once.
    /// False: a cancelled run would be recorded as every server being down.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_CancelledToken_Throws()
    {
        AddServer("a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reported = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectAsync(reported.Add, cts.Token));

        Assert.Empty(reported);
    }

    /// <summary>
    /// A missing reporter is allowed.
    /// True: a caller that only wants the connected sources passes null.
    /// False: an unreachable server would crash the run with a null reference.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_NullReporter_StillSkipsTheServer()
    {
        AddServer("a");
        _factory.HandlerFor("a").SystemInfoStatus = HttpStatusCode.ServiceUnavailable;

        var sources = await ConnectAsync();

        Assert.Empty(sources);
    }

    /// <summary>
    /// A row's key finds its source without regard to case.
    /// True: a row is applied against the server it came from.
    /// False: a key stored in another casing would leave the row queued forever.
    /// </summary>
    [Fact]
    public void Find_KeyInAnotherCase_FindsTheSource()
    {
        var a = Connected(AddServer("server-a"), 0);
        var b = Connected(AddServer("server-b"), 1);

        var found = Find(new[] { a, b }, "SERVER-B");

        Assert.Same(b, found);
        DisposeAll(new[] { a, b });
    }

    /// <summary>
    /// A row with no key belongs to the first configured source, even when that server did not connect and another did.
    /// True: an old row is never applied against a lower ranked server because the first was down.
    /// False: the row would be applied with another server's item ids.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Find_NoKey_BelongsToTheFirstConfiguredSource(string? key)
    {
        var first = AddServer("server-a");
        var second = Connected(AddServer("server-b"), 1);

        var whenFirstIsDown = Find(new[] { second }, key);
        var firstSource = Connected(first, 0);
        var whenBothConnect = Find(new[] { second, firstSource }, key);

        Assert.Null(whenFirstIsDown);
        Assert.Same(firstSource, whenBothConnect);
        DisposeAll(new[] { second, firstSource });
    }

    /// <summary>
    /// A row with no key finds nothing when no source is configured.
    /// True: the caller sees that the row has no server this run.
    /// False: reading the first of an empty list would throw.
    /// </summary>
    [Fact]
    public void Find_NoKeyAndNoSources_ReturnsNull()
    {
        var found = Find(Array.Empty<ScanSource>(), null);

        Assert.Null(found);
    }

    /// <summary>
    /// A key whose server did not connect finds nothing.
    /// True: the row is left for a run in which its server answers.
    /// False: the row would fall through to some other server.
    /// </summary>
    [Fact]
    public void Find_KeyNotConnected_ReturnsNull()
    {
        var a = Connected(AddServer("server-a"), 0);

        var found = Find(new[] { a }, "server-z");

        Assert.Null(found);
        a.Dispose();
    }

    /// <summary>
    /// The task records its finish on the configuration, and the configuration is then saved.
    /// True: the last run time survives a restart.
    /// False: the dashboard would show a run that never finished.
    /// </summary>
    [Fact]
    public void RecordCompletedAndSave_RecordsThenSaves()
    {
        Action<PluginConfiguration, DateTime> record = (config, at) => config.LastHistorySyncTime = at;

        TaskInternals.Call("ScanSources", "RecordCompletedAndSave", _config, record, NullLogger.Instance, "Test Task");

        Assert.NotNull(_config.Configuration.LastHistorySyncTime);
        Assert.Equal(DateTimeKind.Utc, _config.Configuration.LastHistorySyncTime!.Value.Kind);
        Assert.Equal(1, _config.Saves);
    }

    /// <summary>
    /// A save that throws is swallowed.
    /// True: a run that applied its rows still ends as it really ended.
    /// False: a full disk would turn a clean run into a failed one.
    /// </summary>
    [Fact]
    public void RecordCompletedAndSave_SaveThrows_DoesNotThrow()
    {
        var (proxy, stub) = InterfaceStub.Create<IPluginConfigurationManager>();
        stub.Answer("get_Configuration", _ => new PluginConfiguration());
        stub.Answer(nameof(IPluginConfigurationManager.SaveConfiguration), _ => throw new InvalidOperationException("disk full"));
        Action<PluginConfiguration, DateTime> record = (_, _) => { };

        var thrown = Record.Exception(() => TaskInternals.Call("ScanSources", "RecordCompletedAndSave", proxy, record, NullLogger.Instance, "Test Task"));

        Assert.Null(thrown);
    }

    /// <summary>
    /// A missing recorder is refused.
    /// True: a wiring mistake fails at once.
    /// False: the swallow-all catch would hide it and no run would ever be stamped.
    /// </summary>
    [Fact]
    public void RecordCompletedAndSave_NullRecorder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TaskInternals.Call("ScanSources", "RecordCompletedAndSave", _config, null, NullLogger.Instance, "Test Task"));
    }
}

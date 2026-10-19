using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

/// <summary>
/// Last write wins between this server and a peer, on recorded versions only, against a real
/// version store. The peer is a real client over a stub handler, so what a peer answers is parsed
/// the way a live answer is.
/// </summary>
public sealed class VersionConflictResolverTests : IDisposable
{
    private const string ThisServer = "this-server";
    private const string PluginVersion = "12.2.7.3";

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly VersionStore _versions;
    private readonly VersionConflictResolver _resolver;

    public VersionConflictResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _versions = new VersionStore(new FixedProvider(_database), NullLogger<VersionStore>.Instance);
        var (host, stub) = InterfaceStub.Create<IServerApplicationHost>();
        stub.Answer("get_SystemId", _ => ThisServer);
        _resolver = new VersionConflictResolver(_versions, host, new FakeConfigManager(), NullLogger<VersionConflictResolver>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }

    private sealed class FakeConfigManager : IPluginConfigurationManager
    {
        public PluginConfiguration Configuration { get; } = new();

        public string DecryptApiKey(string protectedKey) => protectedKey;

        public string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl) => requestApiKey ?? string.Empty;

        public void SaveConfiguration()
        {
        }

        public string GetTempDownloadPath() => string.Empty;

        public string LocalServerName => string.Empty;

        public string PluginVersion => VersionConflictResolverTests.PluginVersion;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return _answer(request);
        }
    }

    // The peer endpoints speak enums as names.
    private static readonly JsonSerializerOptions PeerJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static HttpResponseMessage Json(object value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, PeerJson), Encoding.UTF8, "application/json") };

    private static ScanSource Peer(StubHandler handler, string key = "peer")
    {
        var server = new SourceServer { Key = key, Name = "Peer", Url = "http://peer:8096", ApiKey = "k", Mode = ServerMode.Sync };
        return new ScanSource(server, new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), server.Url, "k", "test", PluginVersion), 0);
    }

    private static ObjectVersion Version(string server, DateTime at, string key = "item") => new() { Kind = HintKind.Metadata, Key = key, ServerId = server, Timestamp = at };

    private static readonly DateTime Earlier = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 10, 1, 13, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Neither side recorded an edit: the source wins, as it does for a source without the plugin.
    /// True: the first pass between two servers follows the scan rule.
    /// False: an unedited local copy blocks the source forever.
    /// </summary>
    [Fact]
    public void Decide_NeitherSideRecorded_Applies()
        => Assert.Equal(VersionDecision.Apply, _resolver.Decide(HintKind.Metadata, "item", null));

    /// <summary>
    /// An edit recorded here beats a peer value with no recorded edit, and the reverse.
    /// True: a real edit always beats a value nobody edited.
    /// False: a freshly scanned copy overwrites a user's edit.
    /// </summary>
    [Fact]
    public void Decide_RecordedEditBeatsUnrecordedValue_OnEitherSide()
    {
        Assert.Equal(VersionDecision.Apply, _resolver.Decide(HintKind.Metadata, "item", Version("peer", Earlier)));

        _resolver.Record(Version(ThisServer, Earlier));

        Assert.Equal(VersionDecision.Keep, _resolver.Decide(HintKind.Metadata, "item", null));
    }

    /// <summary>
    /// With both sides recorded the newer edit wins, and the same edit holding different values
    /// takes the arriving copy.
    /// True: the newest edit wins wherever it was made, and drifted copies converge.
    /// False: an older edit overwrites a newer one, or two copies of one edit keep trading forever.
    /// </summary>
    [Fact]
    public void Decide_BothRecorded_NewerWins_AndTheSameEditApplies()
    {
        _resolver.Record(Version(ThisServer, Later, "newer-here"));
        Assert.Equal(VersionDecision.Keep, _resolver.Decide(HintKind.Metadata, "newer-here", Version("peer", Earlier, "newer-here")));

        _resolver.Record(Version(ThisServer, Earlier, "newer-there"));
        Assert.Equal(VersionDecision.Apply, _resolver.Decide(HintKind.Metadata, "newer-there", Version("peer", Later, "newer-there")));
        Assert.Equal(VersionDecision.Apply, _resolver.Decide(HintKind.Metadata, "newer-there", Version(ThisServer, Earlier, "newer-there")));
    }

    /// <summary>
    /// A local edit is stamped with this server's id, and a pending peer version becomes the recorded
    /// version only once promoted.
    /// True: the version a scan queued is recorded when its value applies, not before.
    /// False: a queued value that never applied would still outrank later edits.
    /// </summary>
    [Fact]
    public void LocalEdit_AndPendingVersions_RecordAtTheRightTime()
    {
        var edit = _resolver.LocalEditNow(HintKind.Metadata, "item");
        Assert.True(_resolver.IsThisServer(edit));
        Assert.False(_resolver.IsThisServer(Version("peer", Earlier)));

        _resolver.RecordPending(Version("peer", Later));
        Assert.Null(_resolver.Recorded(HintKind.Metadata, "item"));

        Assert.True(_resolver.PromotePending(HintKind.Metadata, "item"));
        Assert.Equal("peer", _resolver.Recorded(HintKind.Metadata, "item")!.ServerId);

        _resolver.RecordPending(Version("other", Later.AddHours(1)));
        _resolver.ClearPending(HintKind.Metadata, "item");
        Assert.False(_resolver.PromotePending(HintKind.Metadata, "item"));
        Assert.Equal("peer", _resolver.Recorded(HintKind.Metadata, "item")!.ServerId);
    }

    /// <summary>
    /// A peer's versions are read in batches of the size it accepts. One dated slightly ahead of this
    /// server's clock is read as now, and one far ahead is ignored.
    /// True: a peer with a fast or broken clock cannot win every conflict.
    /// False: a version dated next year beats every real edit made here until then.
    /// </summary>
    [Fact]
    public async Task PeerVersions_BatchesKeys_AndBoundsFutureVersions()
    {
        var now = DateTime.UtcNow;
        var handler = new StubHandler(request =>
        {
            var asked = JsonSerializer.Deserialize<VersionsRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), PeerJson)!;
            var items = new List<ObjectVersion>();
            if (asked.Keys.Contains("skewed"))
            {
                items.Add(Version("peer", now.AddMinutes(1), "skewed"));
                items.Add(Version("peer", now.AddYears(1), "broken"));
                items.Add(Version("peer", Earlier, "normal"));
            }

            return Json(new VersionsResponse { Items = items });
        });
        using var peer = Peer(handler);
        var keys = new List<string> { "skewed", "broken", "normal" };
        keys.AddRange(Enumerable.Range(0, HintProtocol.MaxHintsPerRequest).Select(i => "k" + i));

        var versions = await _resolver.PeerVersionsAsync(peer, HintKind.Metadata, keys, CancellationToken.None);

        Assert.NotNull(versions);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.False(versions!.ContainsKey("broken"));
        Assert.True(versions["skewed"].Timestamp <= DateTime.UtcNow);
        Assert.Equal(Earlier, versions["normal"].Timestamp);
    }

    /// <summary>
    /// A peer that does not answer yields no versions rather than an empty set.
    /// True: a scan cannot read a failed read as "the peer never edited anything" and overwrite it.
    /// False: an outage on the peer lets this server's values win every conflict.
    /// </summary>
    [Fact]
    public async Task PeerVersions_PeerFails_ReturnsNull()
    {
        using var peer = Peer(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        Assert.Null(await _resolver.PeerVersionsAsync(peer, HintKind.Metadata, new[] { "item" }, CancellationToken.None));
    }

    /// <summary>
    /// Whether a peer keeps versions: yes when it runs this version, no when it has no Server Sync,
    /// unknown when it runs another version. Real answers are cached, failures are asked again.
    /// True: a scan against a mismatched or unreachable peer waits instead of letting the source win.
    /// False: a failed read is taken as "no versions" for five minutes and the source overwrites edits.
    /// </summary>
    [Fact]
    public async Task PeerCarriesVersions_AnswersByVersion_AndCachesOnlyRealAnswers()
    {
        var same = new StubHandler(_ => Json(new PeerCapabilities { ServerId = "p", PluginVersion = PluginVersion }));
        using var samePeer = Peer(same, "same");
        Assert.True(await _resolver.PeerCarriesVersionsAsync(samePeer, CancellationToken.None));
        Assert.True(await _resolver.PeerCarriesVersionsAsync(samePeer, CancellationToken.None));
        Assert.Single(same.Bodies);

        using var noPlugin = Peer(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), "none");
        Assert.False(await _resolver.PeerCarriesVersionsAsync(noPlugin, CancellationToken.None));

        using var otherVersion = Peer(new StubHandler(_ => Json(new PeerCapabilities { ServerId = "p", PluginVersion = "12.2.6.0" })), "other");
        Assert.Null(await _resolver.PeerCarriesVersionsAsync(otherVersion, CancellationToken.None));

        var failing = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var failingPeer = Peer(failing, "failing");
        Assert.Null(await _resolver.PeerCarriesVersionsAsync(failingPeer, CancellationToken.None));
        Assert.Null(await _resolver.PeerCarriesVersionsAsync(failingPeer, CancellationToken.None));
        Assert.Equal(2, failing.Bodies.Count);
    }
}

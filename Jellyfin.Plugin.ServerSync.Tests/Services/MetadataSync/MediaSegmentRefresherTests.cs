using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.MetadataSync;

/// <summary>
/// The refresher must read each matched file's segments from the highest ranked connected source that holds it, in batches, and leave what was kept for a source that cannot answer.
/// </summary>
public sealed class MediaSegmentRefresherTests : IDisposable
{
    private const string ThisServer = "this-server";

    private static readonly DateTime At = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly DatabaseTempFolder _folder = new("segment-refresher");
    private readonly SyncDatabase _database;
    private readonly MetadataSyncTableManager _table;
    private readonly MediaSegmentStore _store;
    private readonly MediaSegmentRefresher _refresher;
    private readonly List<ScanSource> _sources = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentRefresherTests"/> class over an empty database and a fixed clock.
    /// </summary>
    public MediaSegmentRefresherTests()
    {
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        var provider = new FixedProvider(_database);
        _table = new MetadataSyncTableManager(provider, NullLogger<MetadataSyncTableManager>.Instance);
        _store = new MediaSegmentStore(provider, NullLogger<MediaSegmentStore>.Instance);
        _refresher = new MediaSegmentRefresher(_store, QueueSamples.Host(ThisServer), NullLogger<MediaSegmentRefresher>.Instance, new ManualTimeProvider());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _database.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// A file both sources hold is read from the higher ranked one only, and a file only the lower one holds is read from it.
    /// True: segments come from the server the rank says speaks for the file, with one request for each server.
    /// False: a lower ranked server's segments would win, or the same file would be asked of every server.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_FileOnSeveralSources_ReadsFromTheHighestRanked()
    {
        Track("a", "film-on-a", "local-film");
        Track("b", "film-on-b", "local-film");
        Track("b", "only-b", "local-only-b");
        var a = AddSource("a", Answer(("film-on-a", "Intro")));
        var b = AddSource("b", Answer(("only-b", "Outro")));

        var (kept, _) = await _refresher.RefreshAsync(_sources, CancellationToken.None);

        Assert.Equal(2, kept);
        Assert.Equal("Intro", Assert.Single(_store.Get("local-film")!.Segments).Type);
        Assert.Equal("a", _store.Get("local-film")!.ServerKey);
        Assert.Equal("Outro", Assert.Single(_store.Get("local-only-b")!.Segments).Type);
        Assert.Contains("film-on-a", Assert.Single(a.To("/Sync/Peer/Segments")).Body, StringComparison.Ordinal);
        var asked = Assert.Single(b.To("/Sync/Peer/Segments")).Body;
        Assert.Contains("only-b", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("film-on-b", asked, StringComparison.Ordinal);
    }

    /// <summary>
    /// More files than one request may name are read in batches.
    /// True: a large library is read with a few requests, each one the source accepts.
    /// False: the source would refuse an oversized request and no segments would be read.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ManyFiles_ReadsInBatches()
    {
        var count = PeerSegmentsRequest.MaxItems + 5;
        for (var i = 0; i < count; i++)
        {
            Track("a", "s" + i, "l" + i);
        }

        var server = AddSource("a", "{\"Items\":[]}");

        await _refresher.RefreshAsync(_sources, CancellationToken.None);

        Assert.Equal(2, server.To("/Sync/Peer/Segments").Count);
    }

    /// <summary>
    /// A source that cannot answer leaves what was kept for its files, and one that no longer holds a file makes the store forget it.
    /// True: a source that is down or on an older release removes nothing here, and a file removed from the source stops taking segments.
    /// False: an outage would clear every segment copied from that source, or a removed file would keep them forever.
    /// </summary>
    [Theory]
    [InlineData("refused")]
    [InlineData("not-found")]
    public async Task RefreshAsync_SourceCannotSay_KeepsOrForgets(string scenario)
    {
        Track("a", "film-on-a", "local-film");
        _store.Save(new[] { new SourceMediaSegments("local-film", "a", "film-on-a", new[] { new PeerSegment { Type = "Intro", StartTicks = 1, EndTicks = 2 } }, null, null, At) });
        var server = new ClientStubServer();
        if (scenario == "refused")
        {
            server.OnStatus("/Sync/Peer/Segments", HttpStatusCode.NotFound);
        }
        else
        {
            server.OnJson("/Sync/Peer/Segments", "{\"Items\":[{\"ItemId\":\"film-on-a\",\"Found\":false}]}");
        }

        AddSource("a", server);

        await _refresher.RefreshAsync(_sources, CancellationToken.None);

        Assert.Equal(scenario == "refused", _store.Get("local-film") is not null);
    }

    /// <summary>
    /// A file whose only source is not connected this run keeps what was kept, and a file the Metadata table no longer holds is forgotten.
    /// True: a server left out of a run removes nothing, and a file gone from this server stops being kept.
    /// False: a server that missed one scan would lose its segments, or kept rows would grow forever.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_UnconnectedAndUntracked_KeepsAndForgets()
    {
        Track("b", "film-on-b", "local-film");
        _store.Save(new[]
        {
            new SourceMediaSegments("local-film", "b", "film-on-b", Array.Empty<PeerSegment>(), null, null, At),
            new SourceMediaSegments("local-gone", "b", "gone-on-b", Array.Empty<PeerSegment>(), null, null, At)
        });

        var (_, forgotten) = await _refresher.RefreshAsync(_sources, CancellationToken.None);

        Assert.Equal(1, forgotten);
        Assert.NotNull(_store.Get("local-film"));
        Assert.Null(_store.Get("local-gone"));
    }

    /// <summary>
    /// A source that names no origin is the origin, one that names another server passes it on, and segments first made on this server are refused and forgotten.
    /// True: a segment can travel through a server that only copied it, and a server never takes back its own segments, so one it deleted never returns.
    /// False: relayed segments would be credited to the wrong server, or a deleted intro would come back from a server that copied it.
    /// </summary>
    [Theory]
    [InlineData(null, "a-id", true)]
    [InlineData("first-id", "first-id", true)]
    [InlineData(ThisServer, null, false)]
    public async Task RefreshAsync_Origin_IsKeptOrRefused(string? named, string? kept, bool saved)
    {
        Track("a", "film-on-a", "local-film");
        _store.Save(new[] { new SourceMediaSegments("local-film", "a", "film-on-a", Array.Empty<PeerSegment>(), null, null, At) });
        var origin = named is null ? string.Empty : ",\"OriginServerId\":\"" + named + "\"";
        AddSource("a", "{\"Items\":[{\"ItemId\":\"film-on-a\",\"Found\":true" + origin + ",\"Segments\":[{\"Type\":\"Intro\",\"StartTicks\":1,\"EndTicks\":2}]}]}");

        await _refresher.RefreshAsync(_sources, CancellationToken.None);

        var held = _store.Get("local-film");
        Assert.Equal(saved, held is not null);
        Assert.Equal(kept, held?.OriginServerId);
    }

    /// <summary>
    /// Each file goes to the highest ranked connected source that holds it, and a source with no files is left out.
    /// True: one source speaks for each file, by rank.
    /// False: a file would be read twice, or from the wrong server.
    /// </summary>
    [Fact]
    public void Assign_PicksTheHighestRankedConnectedSource()
    {
        AddSource("a", "{}");
        AddSource("b", "{}");
        AddSource("c", "{}");
        var candidates = new[]
        {
            new MediaSegmentCandidate("one", "b", "x"),
            new MediaSegmentCandidate("one", "a", "y"),
            new MediaSegmentCandidate("two", "b", "z"),
            new MediaSegmentCandidate("three", "gone", "w")
        };

        var assigned = MediaSegmentRefresher.Assign(candidates, _sources);

        Assert.Equal(new[] { "a", "b" }, assigned.Select(p => p.Source.Server.Key));
        Assert.Equal("y", Assert.Single(assigned[0].Files).SourceItemId);
        Assert.Equal("z", Assert.Single(assigned[1].Files).SourceItemId);
    }

    private void Track(string server, string sourceItem, string localItem)
        => _table.Upsert(new MetadataSyncItem
        {
            SourceLibraryId = "lib-" + server,
            LocalLibraryId = "local-lib",
            SourceItemId = sourceItem,
            LocalItemId = localItem,
            ItemType = "Movie",
            ServerKey = server,
            StatusDate = At
        });

    private ClientStubServer AddSource(string key, string answer)
        => AddSource(key, new ClientStubServer().OnJson("/Sync/Peer/Segments", answer));

    private ClientStubServer AddSource(string key, ClientStubServer server)
    {
        _sources.Add(new ScanSource(new SourceServer { Key = key, Name = key, ServerId = key + "-id" }, server.Client(), _sources.Count));
        return server;
    }

    private static string Answer(params (string ItemId, string Type)[] items)
        => "{\"Items\":[" + string.Join(",", items.Select(i => "{\"ItemId\":\"" + i.ItemId + "\",\"Found\":true,\"Segments\":[{\"Type\":\"" + i.Type + "\",\"StartTicks\":1,\"EndTicks\":2}]}")) + "]}";
}

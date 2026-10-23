using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.ServerSync.Tests.Playlists.PlaylistTestHarness;

namespace Jellyfin.Plugin.ServerSync.Tests.Playlists;

/// <summary>
/// The Sync Playlists run: which servers it reads, what a failed or mismatched server costs, and the
/// one-way prune of copies whose playlist is gone on the source. The peers are real clients over stub
/// handlers, the mirror is real over stubbed Jellyfin managers, so a delete the prune would make shows
/// up as a recorded DeleteItem call.
/// </summary>
[Collection(PlaylistCopyStoreCollection.Name)]
public sealed class SyncPlaylistsTaskTests : IDisposable
{
    private readonly PlaylistTestHarness _h = new();
    private readonly FakeConfigManager _config = new();
    private readonly FakeClientFactory _factory = new();
    private readonly SyncPlaylistsTask _task;

    public SyncPlaylistsTaskTests()
    {
        _config.Configuration.EnablePlaylistSync = true;
        _config.Configuration.Servers.Add(_h.Server);
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "this-server");
        var resolver = new VersionConflictResolver(
            new VersionStore(new FixedProvider(_h.Database), NullLogger<VersionStore>.Instance),
            host,
            _config,
            NullLogger<VersionConflictResolver>.Instance);
        _task = new SyncPlaylistsTask(NullLogger<SyncPlaylistsTask>.Instance, _config, _factory, _h.Mirror, resolver);
    }

    public void Dispose() => _h.Dispose();

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

        public string PluginVersion => "0.0.0-test";
    }

    private sealed class FakeClientFactory : ISourceServerClientFactory
    {
        public Dictionary<string, StubPeerHandler> Handlers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Created { get; } = new();

        public SourceServerClient Create(SourceServer server)
        {
            Created.Add(server.Key);
            if (!Handlers.TryGetValue(server.Key, out var handler))
            {
                handler = new StubPeerHandler();
                Handlers[server.Key] = handler;
            }

            return new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), server.Url, server.ApiKey, "test", "0.0.0-test");
        }
    }

    private StubPeerHandler Peer(string key = ServerKey)
    {
        if (!_factory.Handlers.TryGetValue(key, out var handler))
        {
            handler = new StubPeerHandler();
            _factory.Handlers[key] = handler;
        }

        return handler;
    }

    private Task Run() => _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

    private List<Playlist> Deleted() => _h.CallsTo(_h.Library, nameof(ILibraryManager.DeleteItem)).Select(a => (Playlist)a[0]!).ToList();

    /// <summary>
    /// With playlist sync off the run does nothing, not even connect.
    /// True: turning the feature off stops every playlist write.
    /// False: copies keep being made, or pruned, after the user turned the feature off.
    /// </summary>
    [Fact]
    public async Task Disabled_DoesNothing()
    {
        _config.Configuration.EnablePlaylistSync = false;

        await Run();

        Assert.Empty(_factory.Created);
    }

    /// <summary>
    /// A server with no user mapping is not read, since there is no one to give its playlists to.
    /// True: a content-only server costs no playlist calls.
    /// False: a server with no mapped users is connected, and its listing has nowhere to go.
    /// </summary>
    [Fact]
    public async Task NoUserMappings_ReadsNoServer()
    {
        _h.Server.UserMappings.Clear();

        await Run();

        Assert.Empty(_factory.Created);
    }

    /// <summary>
    /// On Pull a playlist there that has no copy here is created for the mapped owner, and recorded as
    /// a copy from that server.
    /// True: a Pull mirrors the other server's playlists.
    /// False: nothing is copied, or the copy is not recognised as one next run and is made again.
    /// </summary>
    [Fact]
    public async Task Pull_CreatesMissingCopies()
    {
        var root = Guid.NewGuid();
        Peer().Playlists = new PeerPlaylistsResponse { Playlists = { StubPeerHandler.Playlist("Theirs", SourceOwner, id: root) } };

        await Run();

        var create = (PlaylistCreationRequest)Assert.Single(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.CreatePlaylist)))[0]!;
        Assert.Equal("Theirs", create.Name);
        Assert.Equal(_h.Owner.Id, create.UserId);
        Assert.Equal(new PlaylistCopy(N(root), ServerKey), PlaylistCopyStore.Get(_h.CreatedId));
    }

    /// <summary>
    /// On Pull a copy from this server whose playlist is gone there is deleted. A copy that is still
    /// listed, a copy from another server, a copy owned by a user this server does not map, and a
    /// user's own playlist are all kept.
    /// True: deleting a playlist on the parent deletes only its copy here.
    /// False: the prune deletes another server's copies, or playlists users made here.
    /// </summary>
    [Fact]
    public async Task Pull_PrunesOnlyThisServersGoneCopiesOfMappedUsers()
    {
        var listedRoot = Guid.NewGuid();
        Peer().Playlists = new PeerPlaylistsResponse { Playlists = { StubPeerHandler.Playlist("Still there", SourceOwner, id: listedRoot) } };
        var listed = _h.AddPlaylist("Still there", _h.Owner.Id, taggedRoot: N(listedRoot), taggedServer: ServerKey);
        var gone = _h.AddPlaylist("Gone", _h.Owner.Id, taggedRoot: N(Guid.NewGuid()), taggedServer: ServerKey);
        var otherServer = _h.AddPlaylist("Other server", _h.Owner.Id, taggedRoot: N(Guid.NewGuid()), taggedServer: "server-b");
        var unmapped = _h.AddPlaylist("Unmapped owner", Guid.NewGuid(), taggedRoot: N(Guid.NewGuid()), taggedServer: ServerKey);
        var own = _h.AddPlaylist("Own", _h.Owner.Id);

        await Run();

        Assert.Same(gone, Assert.Single(Deleted()));
        Assert.Null(PlaylistCopyStore.Get(gone.Id));
        Assert.NotNull(PlaylistCopyStore.Get(listed.Id));
        Assert.All(new[] { listed, otherServer, unmapped, own }, kept => Assert.DoesNotContain(kept, Deleted()));
    }

    /// <summary>
    /// A Pull whose listing comes back empty while copies exist keeps them all this run.
    /// True: a peer that briefly lists nothing (a restart, a lost mapping there) does not wipe every copy here.
    /// False: one empty answer deletes every pulled playlist.
    /// </summary>
    [Fact]
    public async Task Pull_EmptyListing_KeepsEveryCopy()
    {
        _h.AddPlaylist("Copy 1", _h.Owner.Id, taggedRoot: N(Guid.NewGuid()), taggedServer: ServerKey);
        _h.AddPlaylist("Copy 2", _h.Viewer.Id, taggedRoot: N(Guid.NewGuid()), taggedServer: ServerKey);

        await Run();

        Assert.Empty(Deleted());
    }

    /// <summary>
    /// On Sync, without versions from the peer, a playlist that already has a copy here is left alone and
    /// nothing is pruned. A new one is still copied.
    /// True: a two-way pair never overwrites or deletes without knowing which side edited last.
    /// False: a peer without versions overwrites local edits, or the one-way prune runs on a two-way pair.
    /// </summary>
    [Fact]
    public async Task Sync_WithoutPeerVersions_LeavesExistingCopies_AndNeverPrunes()
    {
        _h.Server.Mode = ServerMode.Sync;
        var existingRoot = Guid.NewGuid();
        Peer().Playlists = new PeerPlaylistsResponse
        {
            Playlists =
            {
                StubPeerHandler.Playlist("Renamed there", SourceOwner, id: existingRoot),
                StubPeerHandler.Playlist("New there", SourceOwner)
            }
        };
        _h.AddPlaylist("Name here", _h.Owner.Id, taggedRoot: N(existingRoot), taggedServer: ServerKey);
        _h.AddPlaylist("Gone there", _h.Owner.Id, taggedRoot: N(Guid.NewGuid()), taggedServer: ServerKey);

        await Run();

        Assert.Empty(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.UpdatePlaylist)));
        Assert.Equal("New there", ((PlaylistCreationRequest)Assert.Single(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.CreatePlaylist)))[0]!).Name);
        Assert.Empty(Deleted());
    }

    /// <summary>
    /// When no server's playlists can be read the run fails, and when one server fails but another is
    /// read the run succeeds and the failed server's copies are left as they are.
    /// True: the dashboard shows a failed run only when nothing worked, and a failing server never costs its copies.
    /// False: one unreachable server fails every run, or an unreachable server's copies are pruned.
    /// </summary>
    [Fact]
    public async Task Failures_FailTheRunOnlyWhenEveryServerFails()
    {
        Peer().SystemInfoStatus = HttpStatusCode.Unauthorized;
        _h.AddPlaylist("Copy", _h.Owner.Id, taggedRoot: N(Guid.NewGuid()), taggedServer: ServerKey);

        await Assert.ThrowsAsync<InvalidOperationException>(Run);

        var second = _h.NewServer("server-b");
        _config.Configuration.Servers.Add(second);
        Peer("server-b").Playlists = new PeerPlaylistsResponse { Playlists = { StubPeerHandler.Playlist("From b", SourceOwner) } };

        await Run();

        Assert.Empty(Deleted());
        Assert.Single(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.CreatePlaylist)));
    }

    /// <summary>
    /// A peer on another Server Sync version waits rather than failing the run, and its copies are kept.
    /// True: a pair mid-upgrade shows no error and loses nothing.
    /// False: every run fails until both servers upgrade, or the mismatch reads as an empty listing and prunes.
    /// </summary>
    [Fact]
    public async Task VersionMismatch_Waits_WithoutFailingOrPruning()
    {
        Peer().PlaylistsStatus = (HttpStatusCode)426;
        _h.AddPlaylist("Copy", _h.Owner.Id, taggedRoot: N(Guid.NewGuid()), taggedServer: ServerKey);

        await Run();

        Assert.Empty(Deleted());
        Assert.Empty(_h.Playlists.Calls);
    }

    /// <summary>
    /// An empty copy registry is rebuilt from the tags at the start of the run, so copies made before
    /// the registry existed are registered again.
    /// True: after an upgrade or a database reset the copies are known as copies from the right server.
    /// False: tag-only copies stay unregistered, and anything that reads only the registry treats them as originals.
    /// </summary>
    [Fact]
    public async Task Run_RebuildsAnEmptyRegistryFromTags()
    {
        var root = Guid.NewGuid();
        Peer().Playlists = new PeerPlaylistsResponse { Playlists = { StubPeerHandler.Playlist("Listed", SourceOwner, id: root) } };
        var tagOnly = _h.AddPlaylist("Listed", _h.Owner.Id, taggedRoot: N(root), taggedServer: ServerKey);
        Assert.Equal(0, PlaylistCopyStore.Count());

        await Run();

        Assert.Equal(new PlaylistCopy(N(root), ServerKey), PlaylistCopyStore.Get(tagOnly.Id));
        Assert.Empty(Deleted());
        Assert.Empty(_h.CallsTo(_h.Playlists, nameof(IPlaylistManager.CreatePlaylist)));
    }
}

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
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Playlists;

/// <summary>
/// The playlist copy registry is process-wide static state (the store registers itself as the
/// current instance), so every test class that touches it runs in this one serial collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlaylistCopyStoreCollection
{
    public const string Name = "PlaylistCopyStore";
}

/// <summary>
/// A real playlist copy store over a temp-file database, a <see cref="PlaylistMirror"/> wired to
/// recording stubs of the Jellyfin library, playlist and user managers, and mapped users on both sides.
/// </summary>
internal sealed class PlaylistTestHarness : IDisposable
{
    public const string ServerKey = "server-a";

    public static readonly Guid SourceOwner = Guid.Parse("11111111111111111111111111111111");
    public static readonly Guid SourceViewer = Guid.Parse("22222222222222222222222222222222");
    public static readonly Guid SourceStranger = Guid.Parse("33333333333333333333333333333333");

    private readonly string _tempDir;

    public PlaylistTestHarness()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-playlists-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        Copies = new PlaylistCopyStore(new FixedProvider(Database), NullLogger<PlaylistCopyStore>.Instance);

        Owner = new User("owner", "Default", "Default");
        Viewer = new User("viewer", "Default", "Default");
        var users = new Dictionary<Guid, User> { [Owner.Id] = Owner, [Viewer.Id] = Viewer };

        (LibraryManager, Library) = InterfaceStub.Create<ILibraryManager>();
        Library
            .Answer(nameof(ILibraryManager.GetItemList), _ => new List<BaseItem>(LocalItems))
            .Answer(nameof(ILibraryManager.FindByPath), args => ItemsByPath.TryGetValue((string)args[0]!, out var item) ? item : null)
            .Answer(nameof(ILibraryManager.GetItemById), _ => null)
            .Answer(nameof(ILibraryManager.UpdateItemAsync), _ => Task.CompletedTask);

        (PlaylistManager, Playlists) = InterfaceStub.Create<IPlaylistManager>();
        Playlists
            .Answer(nameof(IPlaylistManager.CreatePlaylist), _ => Task.FromResult(new PlaylistCreationResult(CreatedId.ToString("N"))))
            .Answer(nameof(IPlaylistManager.UpdatePlaylist), _ => Task.CompletedTask);

        (UserManager, Users) = InterfaceStub.Create<IUserManager>();
        Users.Answer(nameof(IUserManager.GetUserById), args => users.TryGetValue((Guid)args[0]!, out var user) ? user : null);

        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "this-server");
        var (providers, providerStub) = InterfaceStub.Create<IProviderManager>();
        providerStub.Answer(nameof(IProviderManager.SaveMetadataAsync), _ => Task.CompletedTask);

        Mirror = new PlaylistMirror(LibraryManager, PlaylistManager, UserManager, host, providers, new ApplyGuard(), Copies, NullLogger<PlaylistMirror>.Instance);
        Server = NewServer();
    }

    public SyncDatabase Database { get; }

    public PlaylistCopyStore Copies { get; }

    public PlaylistMirror Mirror { get; }

    public User Owner { get; }

    public User Viewer { get; }

    public ILibraryManager LibraryManager { get; }

    public InterfaceStub Library { get; }

    public IPlaylistManager PlaylistManager { get; }

    public InterfaceStub Playlists { get; }

    public IUserManager UserManager { get; }

    public InterfaceStub Users { get; }

    /// <summary>Gets the items the library lists, playlists among them.</summary>
    public List<BaseItem> LocalItems { get; } = new();

    /// <summary>Gets the local items <see cref="ILibraryManager.FindByPath"/> resolves.</summary>
    public Dictionary<string, BaseItem> ItemsByPath { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the id the stubbed playlist manager gives a created playlist.</summary>
    public Guid CreatedId { get; set; } = Guid.NewGuid();

    public SourceServer Server { get; }

    /// <summary>Gets the user map the mirror builds for <see cref="Server"/>.</summary>
    public Dictionary<string, Guid> UserMap => Mirror.UserMap(Server);

    public static string N(Guid id) => id.ToString("N");

    public SourceServer NewServer(string key = ServerKey) => new()
    {
        Key = key,
        Name = "Peer " + key,
        Url = "http://" + key + ":8096",
        ApiKey = "k",
        Mode = ServerMode.Pull,
        LibraryMappings = new List<LibraryMapping>
        {
            new() { SourceLibraryId = "lib", SourceRootPath = "/src/movies", LocalRootPath = "/local/movies", IsEnabled = true }
        },
        UserMappings = new List<UserMapping>
        {
            new() { SourceUserId = N(SourceOwner), LocalUserId = N(Owner.Id) },
            new() { SourceUserId = N(SourceViewer), LocalUserId = N(Viewer.Id) }
        }
    };

    /// <summary>Adds a local movie at the translated path of a source path.</summary>
    public BaseItem AddMovie(string localPath)
    {
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = localPath, Name = Path.GetFileNameWithoutExtension(localPath) };
        ItemsByPath[localPath] = movie;
        return movie;
    }

    /// <summary>Adds a local playlist to what the library lists.</summary>
    public Playlist AddPlaylist(string name, Guid owner, string? taggedRoot = null, string? taggedServer = null)
    {
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = name, OwnerUserId = owner };
        if (taggedRoot is not null)
        {
            playlist.ProviderIds[PlaylistMirror.SourcePlaylistProviderKey] = taggedRoot;
        }

        if (taggedServer is not null)
        {
            playlist.ProviderIds[PlaylistMirror.SourceServerProviderKey] = taggedServer;
        }

        LocalItems.Add(playlist);
        return playlist;
    }

    public static SourcePlaylist Source(
        string name,
        string? id = null,
        string? root = null,
        string? owner = null,
        bool openAccess = false,
        List<(string UserId, bool CanEdit)>? shares = null,
        List<string?>? paths = null,
        bool ownerKnown = true,
        bool isCopy = false)
    {
        id ??= N(Guid.NewGuid());
        return new SourcePlaylist(id, root ?? id, name, "Video", owner ?? N(SourceOwner), openAccess, shares ?? new(), paths ?? new(), ownerKnown, isCopy);
    }

    public List<object?[]> CallsTo(InterfaceStub stub, string name) => stub.Calls.Where(c => c.Name == name).Select(c => c.Args).ToList();

    public void Dispose()
    {
        Database.Dispose();
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
}

/// <summary>
/// A peer over a stub HTTP handler: answers the connection test and the peer playlist listing the
/// way a live server does, so the real client parses them.
/// </summary>
internal sealed class StubPeerHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions PeerJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public HttpStatusCode SystemInfoStatus { get; set; } = HttpStatusCode.OK;

    public HttpStatusCode PlaylistsStatus { get; set; } = HttpStatusCode.OK;

    public PeerPlaylistsResponse Playlists { get; set; } = new();

    public List<string> Paths { get; } = new();

    public static PeerPlaylist Playlist(string name, Guid owner, Guid? id = null, Guid? root = null, bool isCopy = false, params string[] paths) => new()
    {
        Id = (id ?? Guid.NewGuid()).ToString("N"),
        RootId = root?.ToString("N") ?? string.Empty,
        IsCopy = isCopy,
        Name = name,
        MediaType = "Video",
        OwnerUserId = owner.ToString("N"),
        Entries = paths.Select(p => new PeerPlaylistEntry { ItemId = Guid.NewGuid().ToString("N"), Path = p }).ToList()
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Paths.Add(path);
        HttpResponseMessage response = path switch
        {
            "/System/Info" => SystemInfoStatus == HttpStatusCode.OK
                ? Json("{\"ServerName\":\"peer\",\"Id\":\"0123456789abcdef0123456789abcdef\"}")
                : new HttpResponseMessage(SystemInfoStatus),
            "/Users/Me" => new HttpResponseMessage(HttpStatusCode.BadRequest),
            "/Sync/Peer/Playlists" => PlaylistsStatus == HttpStatusCode.OK
                ? Json(JsonSerializer.Serialize(Playlists, PeerJson))
                : new HttpResponseMessage(PlaylistsStatus) { Content = new StringContent("refused") },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

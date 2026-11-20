using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Jellyfin.Plugin.ServerSync.Api;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Everything a controller test needs: a real sync database in a temp folder, the stores and table
/// managers over it, stubbed Jellyfin services, and a peer answered by a function the test sets.
/// </summary>
public sealed class ControllerTestHarness : IDisposable
{
    /// <summary>The Jellyfin ID this server answers with.</summary>
    public const string ThisServerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>The Jellyfin ID of the peer <see cref="AddPeer"/> lists.</summary>
    public const string PeerId = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";

    private PlaylistCopyStore? _playlistCopies;
    private OutboundHintWorker? _outboundWorker;
    private InboundHintWorker? _inboundWorker;

    /// <summary>
    /// Initializes a new instance of the <see cref="ControllerTestHarness"/> class.
    /// </summary>
    public ControllerTestHarness()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "serversync-controllers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);
        Database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, TempDir);
        Provider = new FixedProvider(Database);
        Content = new ContentSyncTableManager(Provider, NullLogger<ContentSyncTableManager>.Instance);
        History = new HistorySyncTableManager(Provider, NullLogger<HistorySyncTableManager>.Instance);
        Metadata = new MetadataSyncTableManager(Provider, NullLogger<MetadataSyncTableManager>.Instance);
        People = new PeopleSyncTableManager(Provider, NullLogger<PeopleSyncTableManager>.Instance);
        Users = new UserSyncTableManager(Provider, NullLogger<UserSyncTableManager>.Instance);
        Outbound = new OutboundHintStore(Provider, NullLogger<OutboundHintStore>.Instance);
        Inbound = new InboundHintStore(Provider, NullLogger<InboundHintStore>.Instance);
        Versions = new VersionStore(Provider, NullLogger<VersionStore>.Instance);
        Removed = new RemovedItemsStore(Provider, NullLogger<RemovedItemsStore>.Instance);
        Pairings = new PeerPairingStore(Provider, new SecretProtector("tests", NullLogger.Instance), NullLogger<PeerPairingStore>.Instance);

        (Library, LibraryStub) = InterfaceStub.Create<ILibraryManager>();
        LibraryStub.Answer(nameof(ILibraryManager.GetVirtualFolders), _ => new List<MediaBrowser.Model.Entities.VirtualFolderInfo>());
        (UserManager, UserManagerStub) = InterfaceStub.Create<IUserManager>();
        (TaskManager, TaskManagerStub) = InterfaceStub.Create<ITaskManager>();
        TaskManagerStub.Answer("get_ScheduledTasks", _ => ScheduledTasks.ToArray());
        (Host, HostStub) = InterfaceStub.Create<IServerApplicationHost>();
        HostStub.Answer("get_SystemId", _ => ThisServerId);
        HostStub.Answer("get_FriendlyName", _ => "This server");

        (LocalItems, LocalItemsStub) = InterfaceStub.Create<ILocalItems>();
        Triage = new HintTriage(Config, LocalItems, Versions);

        PeerHandler = new StubHandler(request => PeerAnswer(request));
        Clients = new ControllerClientFactory(PeerHandler);
        HttpClients = new ControllerHttpClientFactory(PeerHandler);
    }

    /// <summary>Gets the temp folder that holds the database and any files a test writes.</summary>
    public string TempDir { get; }

    /// <summary>Gets the sync database.</summary>
    public SyncDatabase Database { get; }

    /// <summary>Gets the provider that hands every store the one database.</summary>
    public FixedProvider Provider { get; }

    /// <summary>Gets the plugin configuration manager.</summary>
    public FakeConfigManager Config { get; } = new();

    /// <summary>Gets the Content sync table.</summary>
    public ContentSyncTableManager Content { get; }

    /// <summary>Gets the History sync table.</summary>
    public HistorySyncTableManager History { get; }

    /// <summary>Gets the Metadata sync table.</summary>
    public MetadataSyncTableManager Metadata { get; }

    /// <summary>Gets the People sync table.</summary>
    public PeopleSyncTableManager People { get; }

    /// <summary>Gets the Users sync table.</summary>
    public UserSyncTableManager Users { get; }

    /// <summary>Gets the hints owed to peers.</summary>
    public OutboundHintStore Outbound { get; }

    /// <summary>Gets the hints peers sent.</summary>
    public InboundHintStore Inbound { get; }

    /// <summary>Gets the stamps of edited objects.</summary>
    public VersionStore Versions { get; }

    /// <summary>
    /// Gets the playlist copies, built on first use. The store registers itself process wide, so only a
    /// test class in <see cref="PlaylistCopyStoreCollection"/> may read this.
    /// </summary>
    public PlaylistCopyStore PlaylistCopies => _playlistCopies ??= new PlaylistCopyStore(Provider, NullLogger<PlaylistCopyStore>.Instance);

    /// <summary>Gets the record of removed items.</summary>
    public RemovedItemsStore Removed { get; }

    /// <summary>Gets the pairing secrets.</summary>
    public PeerPairingStore Pairings { get; }

    /// <summary>Gets the stubbed library manager.</summary>
    public ILibraryManager Library { get; }

    /// <summary>Gets the recorder behind <see cref="Library"/>.</summary>
    public InterfaceStub LibraryStub { get; }

    /// <summary>Gets the stubbed user manager.</summary>
    public IUserManager UserManager { get; }

    /// <summary>Gets the recorder behind <see cref="UserManager"/>.</summary>
    public InterfaceStub UserManagerStub { get; }

    /// <summary>Gets the stubbed task manager, which lists <see cref="ScheduledTasks"/>.</summary>
    public ITaskManager TaskManager { get; }

    /// <summary>Gets the recorder behind <see cref="TaskManager"/>.</summary>
    public InterfaceStub TaskManagerStub { get; }

    /// <summary>Gets the scheduled tasks the task manager lists.</summary>
    public List<IScheduledTaskWorker> ScheduledTasks { get; } = new();

    /// <summary>Gets the stubbed application host.</summary>
    public IServerApplicationHost Host { get; }

    /// <summary>Gets the recorder behind <see cref="Host"/>.</summary>
    public InterfaceStub HostStub { get; }

    /// <summary>Gets the stubbed lookup of local items, which finds nothing until a test answers it.</summary>
    public ILocalItems LocalItems { get; }

    /// <summary>Gets the recorder behind <see cref="LocalItems"/>.</summary>
    public InterfaceStub LocalItemsStub { get; }

    /// <summary>Gets the triage that assesses each hint a peer sends.</summary>
    public HintTriage Triage { get; }

    /// <summary>Gets the worker that delivers hints, built on first use and never started.</summary>
    public OutboundHintWorker OutboundWorker
        => _outboundWorker ??= new OutboundHintWorker(Outbound, Config, Clients, Host, null!, new HintSuppression(), Library, NullLogger<OutboundHintWorker>.Instance);

    /// <summary>Gets the worker that applies hints, built on first use and never started.</summary>
    public InboundHintWorker InboundWorker
        => _inboundWorker ??= new InboundHintWorker(Inbound, Config, Clients, null!, null!, null!, null!, null!, Triage, Host, NullLogger<InboundHintWorker>.Instance);

    /// <summary>Gets the handler every client to another server sends through.</summary>
    public StubHandler PeerHandler { get; }

    /// <summary>Gets the paths the peer was asked for, in order.</summary>
    public List<string> PeerPaths { get; } = new();

    /// <summary>Gets or sets the function that answers each request to the peer, a 404 until a test sets one.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Peer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    /// <summary>Gets the client factory the controllers build clients with.</summary>
    public ControllerClientFactory Clients { get; }

    /// <summary>Gets the named HTTP client factory the controllers sign in through.</summary>
    public ControllerHttpClientFactory HttpClients { get; }

    /// <summary>
    /// Builds a JSON response.
    /// </summary>
    /// <param name="body">The JSON text.</param>
    /// <param name="status">The status code, 200 when left out.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Adds a configured server entry for the peer and returns it.
    /// </summary>
    /// <param name="mode">The entry's Direction.</param>
    /// <param name="key">The entry's key.</param>
    /// <returns>The entry, already in the configuration.</returns>
    public SourceServer AddPeer(ServerMode mode = ServerMode.Pull, string key = "peer")
    {
        var server = new SourceServer { Key = key, Name = "Peer", Url = "http://127.0.0.1:8096", ApiKey = "key", ServerId = PeerId, Mode = mode };
        Config.Configuration.Servers.Add(server);
        return server;
    }

    /// <summary>
    /// Adds a Content row and returns it as stored, with its row ID.
    /// </summary>
    /// <param name="sourceItemId">The item's ID on the source.</param>
    /// <param name="status">The row's status.</param>
    /// <param name="serverKey">The key of the entry the row came from.</param>
    /// <param name="change">Edits the row before it is stored, or null.</param>
    /// <returns>The stored row.</returns>
    public SyncItem AddContent(string sourceItemId, SyncStatus status = SyncStatus.Synced, string serverKey = "peer", Action<SyncItem>? change = null)
    {
        var row = new SyncItem
        {
            SourceLibraryId = "lib",
            LocalLibraryId = "local",
            SourceItemId = sourceItemId,
            SourcePath = "/src/" + sourceItemId + ".mkv",
            SourceSize = 100,
            SourceCreateDate = DateTime.UtcNow,
            LocalPath = "/local/" + sourceItemId + ".mkv",
            Status = status,
            StatusDate = DateTime.UtcNow,
            ServerKey = serverKey
        };
        change?.Invoke(row);
        Content.Upsert(row);
        return Content.GetBySourceItemId(sourceItemId)!;
    }

    /// <summary>
    /// Adds a scheduled task with a key to the task manager's list.
    /// </summary>
    /// <param name="key">The task's key.</param>
    /// <returns>The worker the task manager lists.</returns>
    public IScheduledTaskWorker AddScheduledTask(string key)
    {
        var (task, taskStub) = InterfaceStub.Create<IScheduledTask>();
        taskStub.Answer("get_Key", _ => key);
        var (worker, workerStub) = InterfaceStub.Create<IScheduledTaskWorker>();
        workerStub.Answer("get_ScheduledTask", _ => task);
        ScheduledTasks.Add(worker);
        return worker;
    }

    /// <summary>
    /// Builds the dashboard controller over the harness, with a request context.
    /// </summary>
    /// <param name="services">The services a route resolves from the request, or null for the table managers and stores.</param>
    /// <returns>The controller.</returns>
    public ConfigurationController Configuration(IServiceProvider? services = null)
        => WithContext(new ConfigurationController(Library, TaskManager, HttpClients, Config, Provider, Clients, NullLogger<ConfigurationController>.Instance), services ?? Services());

    /// <summary>
    /// Builds the Change queue controller over the harness. It reads <see cref="PlaylistCopies"/>, so only a
    /// test class in <see cref="PlaylistCopyStoreCollection"/> may call this.
    /// </summary>
    /// <returns>The controller and the observer it reads.</returns>
    public (HintsController Controller, LocalChangeObserver Observer) Hints()
    {
        var (userData, _) = InterfaceStub.Create<IUserDataManager>();
        var (accessor, _) = InterfaceStub.Create<IHttpContextAccessor>();
        var publisher = new LocalHintPublisher(Config, Outbound, Versions, OutboundWorker, Host, NullLogger<LocalHintPublisher>.Instance);
        var observer = new LocalChangeObserver(userData, UserManager, Library, accessor, Config, publisher, OutboundWorker, new ApplyGuard(), Versions, new HintSuppression(), InboundWorker, Host, PlaylistCopies, NullLogger<LocalChangeObserver>.Instance);
        var controller = new HintsController(Outbound, Inbound, OutboundWorker, InboundWorker, observer, Config, Clients, Host, Versions, Library, UserManager);
        return (WithContext(controller), observer);
    }

    /// <summary>
    /// Builds the peer controller over the harness, as called by a peer that presents a pairing secret.
    /// </summary>
    /// <param name="presentedSecret">The secret in the pairing header, or null to send none.</param>
    /// <param name="scopedUser">The user the caller's key is scoped to, as the guard would attach it, or null for a full caller.</param>
    /// <param name="services">The services the request resolves, or null for the table managers.</param>
    /// <returns>The controller.</returns>
    public PeerController PeerRoutes(string? presentedSecret = null, Guid? scopedUser = null, IServiceProvider? services = null)
    {
        var (historyStore, _) = InterfaceStub.Create<IUserHistoryStore>();
        var history = new PeerHistoryService(historyStore, NullLogger<PeerHistoryService>.Instance);
        var controller = WithContext(
            new PeerController(history, Config, Host, Inbound, Outbound, Versions, InboundWorker, OutboundWorker, Pairings, Clients, Triage, new PeerItemLookup(Library, LocalItems), NullLogger<PeerController>.Instance),
            services ?? Services());
        if (presentedSecret is not null)
        {
            controller.Request.Headers[HintProtocol.PairingHeader] = presentedSecret;
        }

        if (scopedUser is { } user)
        {
            controller.HttpContext.Items[PeerCallerGuard.ScopedUserKey] = user;
        }

        return controller;
    }

    /// <summary>
    /// Builds a service provider holding the table managers and the record of removed items.
    /// </summary>
    /// <param name="more">Adds further services, or null for none.</param>
    /// <returns>The provider.</returns>
    public ServiceProvider Services(Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(Content)
            .AddSingleton(History)
            .AddSingleton(Metadata)
            .AddSingleton(People)
            .AddSingleton(Users)
            .AddSingleton(Removed);
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Gives a controller a request context, so its routes can read the request and write the response.
    /// </summary>
    /// <typeparam name="T">The controller's type.</typeparam>
    /// <param name="controller">The controller.</param>
    /// <param name="services">The services the request resolves, or null for none.</param>
    /// <returns>The same controller.</returns>
    public static T WithContext<T>(T controller, IServiceProvider? services = null)
        where T : ControllerBase
    {
        var http = new DefaultHttpContext();
        if (services is not null)
        {
            http.RequestServices = services;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _outboundWorker?.Dispose();
        _inboundWorker?.Dispose();
        PeerHandler.Dispose();
        Database.Dispose();
        try
        {
            Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private HttpResponseMessage PeerAnswer(HttpRequestMessage request)
    {
        PeerPaths.Add(request.RequestUri!.AbsolutePath);
        return Peer(request);
    }
}

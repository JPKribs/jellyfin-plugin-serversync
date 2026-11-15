using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
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
        PlaylistCopies = new PlaylistCopyStore(Provider, NullLogger<PlaylistCopyStore>.Instance);
        Removed = new RemovedItemsStore(Provider, NullLogger<RemovedItemsStore>.Instance);
        Pairings = new PeerPairingStore(Provider, new SecretProtector("tests", NullLogger.Instance), NullLogger<PeerPairingStore>.Instance);

        (Library, LibraryStub) = InterfaceStub.Create<ILibraryManager>();
        LibraryStub.Answer(nameof(ILibraryManager.GetVirtualFolders), _ => new List<MediaBrowser.Model.Entities.VirtualFolderInfo>());
        (UserManager, UserManagerStub) = InterfaceStub.Create<IUserManager>();
        (TaskManager, TaskManagerStub) = InterfaceStub.Create<ITaskManager>();
        TaskManagerStub.Answer("get_ScheduledTasks", _ => ScheduledTasks.ToArray());
        (Host, HostStub) = InterfaceStub.Create<IServerApplicationHost>();
        HostStub.Answer("get_SystemId", _ => ThisServerId);
        HostStub.Answer("get_FriendlyName", _ => "This Server");

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

    /// <summary>Gets the playlist copies.</summary>
    public PlaylistCopyStore PlaylistCopies { get; }

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
    /// Builds a service provider holding the table managers and stores.
    /// </summary>
    /// <returns>The provider.</returns>
    public ServiceProvider Services()
        => new ServiceCollection()
            .AddSingleton(Content)
            .AddSingleton(History)
            .AddSingleton(Metadata)
            .AddSingleton(People)
            .AddSingleton(Users)
            .AddSingleton(PlaylistCopies)
            .AddSingleton(Removed)
            .BuildServiceProvider();

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

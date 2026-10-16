using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Peer;

public sealed class PeerTransportSecurityTests : IDisposable
{
    private const string OriginId = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";
    private static readonly Guid ItemId = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222222222222222222222222222");

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly PeerPairingStore _pairings;

    public PeerTransportSecurityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _pairings = new PeerPairingStore(new FixedProvider(_database), new SecretProtector("tests", NullLogger.Instance), NullLogger<PeerPairingStore>.Instance);
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

    [Fact]
    public async Task ContentHint_ForAnItemOutsideEveryMappedRoot_IsDropped()
    {
        var config = new FakeConfigManager();
        config.Configuration.EnableContentSync = true;
        var handler = new ContentHintHandler(null!, config, null!, null!, NullLogger<ContentHintHandler>.Instance);
        using var client = ClientAnswering(ItemsJson("/media/private/secret.mkv"));

        var result = await handler.ApplyAsync(Hint(HintKind.Content, ItemId.ToString("N"), "/media/movies/claimed.mkv"), Origin(), client, CancellationToken.None);

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Contains("/media/private/secret.mkv", result.Reason, StringComparison.Ordinal);
        Assert.Contains("not in a library mapped here", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetadataHint_ForAnItemOutsideEveryMappedRoot_IsDropped()
    {
        var config = new FakeConfigManager();
        config.Configuration.EnableMetadataSync = true;
        var handler = new ItemHintHandler(null!, config, null!, null!, null!, NullLogger<ItemHintHandler>.Instance);
        using var client = ClientAnswering("{}");
        var prefetched = new Jellyfin.Sdk.Generated.Models.BaseItemDto { Id = ItemId, Path = "/media/private/secret.mkv" };

        var result = await handler.ApplyMetadataAsync(Hint(HintKind.Metadata, ItemId.ToString("N"), "/media/movies/claimed.mkv"), Origin(), client, CancellationToken.None, prefetched);

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Contains("not in a library mapped here", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryHint_ForAnItemOutsideEveryMappedRoot_IsDropped()
    {
        var config = new FakeConfigManager();
        config.Configuration.EnableHistorySync = true;
        var handler = new HistoryHintHandler(null!, null!, null!, null!, null!, null!, null!, config, null!, NullLogger<HistoryHintHandler>.Instance);
        using var client = ClientAnswering(ItemsJson("/media/private/secret.mkv"));

        var result = await handler.ApplyAsync(Hint(HintKind.History, HintProtocol.HistoryKey(UserId, ItemId), "/media/movies/claimed.mkv"), Origin(), client, CancellationToken.None);

        Assert.Equal(HintApplyOutcome.Dropped, result.Outcome);
        Assert.Contains("not in a library mapped here", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectRefusingHandler_ThrowsOnRedirect()
    {
        using var handler = new RedirectRefusingHandler { InnerHandler = new StubHandler(_ => Redirect("https://elsewhere.example/steal?x=1")) };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://peer.example/ServerSync/Peer/Status");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Found, ex.StatusCode);
        Assert.Contains("https://elsewhere.example/steal", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x=1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectRefusingHandler_PassesOtherAnswersThrough()
    {
        using var handler = new RedirectRefusingHandler { InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified)) };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://peer.example/");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Fact]
    public async Task RegisteredClient_NeverFollowsARedirectOrForwardsThePairingSecret()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var seen = new List<(string Path, string? Secret)>();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                lock (seen)
                {
                    seen.Add((context.Request.Url!.AbsolutePath, context.Request.Headers[HintProtocol.PairingHeader]));
                }

                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = $"http://127.0.0.1:{port}/elsewhere";
                context.Response.Close();
            }
        });

        using var provider = RegisteredServices();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SourceServerClient.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/ServerSync/Peer/Status");
        request.Headers.TryAddWithoutValidation(HintProtocol.PairingHeader, "secret");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        listener.Stop();
        await serving;

        Assert.Contains("/elsewhere", ex.Message, StringComparison.Ordinal);
        Assert.Single(seen);
        Assert.Equal("/ServerSync/Peer/Status", seen[0].Path);
    }

    [Theory]
    [InlineData(SourceServerClient.HttpClientName)]
    [InlineData(SourceServerClient.PublicHttpClientName)]
    public async Task RegisteredClients_RefusePlainHttpToAPublicAddress(string clientName)
    {
        using var provider = RegisteredServices();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://203.0.113.10:8096/System/Info")));

        Assert.Contains(ConfigurationUtilities.PlainHttpRefusal, Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivateClient_RefusesTheMetadataRangeAtConnectTime()
    {
        using var provider = RegisteredServices();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SourceServerClient.HttpClientName);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://169.254.169.254/latest")));

        Assert.Contains("Link-local", Flatten(ex), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://203.0.113.10:8096", true)]
    [InlineData("http://[2001:db8::1]:8096", true)]
    [InlineData("http://jellyfin.example.com", false)]
    [InlineData("http://203.0.113.10:8096", false)]
    public void Validator_RefusesPlainHttpToPublicServers(string url, bool allowPrivateNetwork)
    {
        Assert.Equal(ConfigurationUtilities.PlainHttpRefusal, ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096")]
    [InlineData("http://10.0.0.5:8096")]
    [InlineData("http://172.20.1.2:8096")]
    [InlineData("http://192.168.1.10:8096")]
    [InlineData("http://100.101.102.103:8096")]
    [InlineData("http://[fd7a:115c:a1e0::1]:8096")]
    [InlineData("http://jellyfin:8096")]
    public void Validator_AllowsPlainHttpOnPrivateNetworks(string url)
    {
        Assert.Null(ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork: true));
    }

    [Theory]
    [InlineData("https://203.0.113.10:8096", true)]
    [InlineData("https://jellyfin.example.com", false)]
    public void Validator_AllowsHttpsToPublicServers(string url, bool allowPrivateNetwork)
    {
        Assert.Null(ConfigurationUtilities.ValidateServerUrlForSsrf(url, allowPrivateNetwork));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("::ffff:10.1.1.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("2001:db8::1", false)]
    public void AllowsPlainHttp_CoversLoopbackPrivateAndTailnetRanges(string address, bool expected)
    {
        Assert.Equal(expected, ConfigurationUtilities.AllowsPlainHttp(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task ResolvingValidator_AllowsPlainHttpToALoopbackName()
    {
        Assert.Null(await ConfigurationUtilities.ValidateServerUrlForSsrfAsync("http://localhost:8096", allowPrivateNetwork: true));
    }

    [Fact]
    public void Pair_IsOpenToAnySignedInUser_AndNothingElseIs()
    {
        var pairController = typeof(PeerPairController);
        var authorize = pairController.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.Single(authorize);
        Assert.Null(authorize[0].Policy);

        var actions = pairController.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(new[] { nameof(PeerPairController.Pair) }, actions.Select(a => a.Name).ToArray());
        Assert.Null(typeof(PeerController).GetMethod("Pair", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void Pair_StoresTheSecretForAConfiguredServer()
    {
        var config = new FakeConfigManager();
        var origin = Origin();
        config.Configuration.Servers.Add(origin);
        var controller = new PeerPairController(config, _pairings, NullLogger<PeerPairController>.Instance);

        var result = controller.Pair(new Models.Peer.PairRequest { ServerId = OriginId, Secret = "issued" });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("issued", _pairings.GetOutbound(origin.Key));
    }

    [Fact]
    public void Pair_RefusesAnUnknownServer()
    {
        var controller = new PeerPairController(new FakeConfigManager(), _pairings, NullLogger<PeerPairController>.Instance);

        var result = controller.Pair(new Models.Peer.PairRequest { ServerId = OriginId, Secret = "issued" });

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public void Queue_FromAnUnpairedSender_IsAlwaysRefused(string? presented)
    {
        var config = new FakeConfigManager();
        var origin = Origin();
        origin.Mode = ServerMode.Sync;
        config.Configuration.Servers.Add(origin);
        var controller = PeerControllerFor(config, presented);

        var result = controller.Queue(new QueueRequest { SenderServerId = OriginId, Items = new List<SyncHint>() });

        Assert.Equal(HintProtocol.UnpairedStatus, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public void Queue_FromAPairedSender_PassesTheSenderCheck()
    {
        var config = new FakeConfigManager();
        var origin = Origin();
        origin.Mode = ServerMode.Push;
        config.Configuration.Servers.Add(origin);
        _pairings.SetInbound(origin.Key, "issued");
        var controller = PeerControllerFor(config, "issued");

        var result = controller.Queue(new QueueRequest { SenderServerId = OriginId, Items = new List<SyncHint>() });

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    private PeerController PeerControllerFor(FakeConfigManager config, string? presented)
    {
        var controller = new PeerController(null!, config, null!, null!, null!, null!, null!, _pairings, null!, NullLogger<PeerController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (presented is not null)
        {
            controller.Request.Headers[HintProtocol.PairingHeader] = presented;
        }

        return controller;
    }

    private static SourceServer Origin() => new()
    {
        Key = "origin",
        Name = "Origin",
        Url = "http://origin:8096",
        ApiKey = "key",
        ServerId = OriginId,
        LibraryMappings =
        {
            new LibraryMapping { SourceLibraryId = "lib", SourceRootPath = "/media/movies", LocalLibraryId = "local", LocalRootPath = "/local/movies" }
        },
        UserMappings =
        {
            new UserMapping { SourceUserId = UserId.ToString("N"), LocalUserId = Guid.NewGuid().ToString("N") }
        }
    };

    private static InboundHint Hint(HintKind kind, string key, string itemPath) => new()
    {
        HintId = OriginId + ":1",
        OriginServerId = OriginId,
        Kind = kind,
        Key = key,
        ItemPath = itemPath
    };

    private static string ItemsJson(string path)
        => "{\"Items\":[{\"Id\":\"" + ItemId.ToString("D") + "\",\"Name\":\"Secret\",\"Path\":\"" + path + "\"}],\"TotalRecordCount\":1,\"StartIndex\":0}";

    private static SourceServerClient ClientAnswering(string json)
    {
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        return new SourceServerClient(NullLogger<SourceServerClient>.Instance, http, "http://origin:8096", "key", "test", "0");
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static ServiceProvider RegisteredServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        return services.BuildServiceProvider();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Flatten(Exception ex)
    {
        var text = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            text.Append(e.Message).Append(' ');
        }

        return text.ToString();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_answer(request));
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

        public string PluginVersion => "0.0.0-test";
    }
}

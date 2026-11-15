using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// The factory must refuse an address the SSRF guard refuses, pick the guarded HTTP client that fits the entry, and hand the pairing secret only to the address saved for the entry.
/// </summary>
public sealed class SourceServerClientFactoryTests : IDisposable
{
    private const string ThisServer = "this-server-id";
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly PeerPairingStore _pairings;
    private readonly SecretProtector _secrets;
    private readonly FakeConfigManager _config = new();
    private readonly ClientStubServer _server = new();
    private readonly List<string> _clientNames = new();
    private readonly SourceServerClientFactory _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceServerClientFactoryTests"/> class.
    /// </summary>
    public SourceServerClientFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-clientfactory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _secrets = ClientSecretKeys.Protector(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _pairings = new PeerPairingStore(new FixedProvider(_database), _secrets, NullLogger<PeerPairingStore>.Instance);
        _factory = new SourceServerClientFactory(HttpClients(), NullLoggerFactory.Instance, _config, _secrets, _pairings, Host());
    }

    /// <inheritdoc/>
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

    /// <summary>
    /// The constructor refuses each missing dependency.
    /// True: a broken registration fails when Jellyfin builds its services.
    /// False: it would fail on the first sync, far from the cause.
    /// </summary>
    [Fact]
    public void Constructor_MissingDependency_Throws()
    {
        var http = HttpClients();
        var host = Host();

        Assert.Throws<ArgumentNullException>(() => new SourceServerClientFactory(null!, NullLoggerFactory.Instance, _config, _secrets, _pairings, host));
        Assert.Throws<ArgumentNullException>(() => new SourceServerClientFactory(http, null!, _config, _secrets, _pairings, host));
        Assert.Throws<ArgumentNullException>(() => new SourceServerClientFactory(http, NullLoggerFactory.Instance, null!, _secrets, _pairings, host));
        Assert.Throws<ArgumentNullException>(() => new SourceServerClientFactory(http, NullLoggerFactory.Instance, _config, null!, _pairings, host));
        Assert.Throws<ArgumentNullException>(() => new SourceServerClientFactory(http, NullLoggerFactory.Instance, _config, _secrets, null!, host));
        Assert.Throws<ArgumentNullException>(() => new SourceServerClientFactory(http, NullLoggerFactory.Instance, _config, _secrets, _pairings, null!));
        Assert.Throws<ArgumentNullException>(() => _factory.Create(null!));
    }

    /// <summary>
    /// An address the SSRF guard refuses builds no client and opens no HTTP client.
    /// True: a bad Server URL is refused before anything is sent to it.
    /// False: the API Key could be sent in plain HTTP to a public host, or to an address the entry forbids.
    /// </summary>
    [Theory]
    [InlineData("http://203.0.113.5:8096", true)]
    [InlineData("https://10.0.0.5:8920", false)]
    [InlineData("ftp://peer.example", true)]
    [InlineData("not a url", true)]
    [InlineData("", true)]
    public void Create_AddressRefusedByTheGuard_Throws(string url, bool allowPrivateNetwork)
    {
        var server = new SourceServer { Key = "a", Url = url, ApiKey = "k", AllowPrivateNetwork = allowPrivateNetwork };

        var failure = Assert.Throws<ArgumentException>(() => _factory.Create(server));

        Assert.StartsWith("Invalid source server URL: ", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_clientNames);
    }

    /// <summary>
    /// The private network setting picks which guarded HTTP client the entry uses.
    /// True: an entry that disallows private addresses has every resolved address checked at connect time.
    /// False: a public name that resolves to a private address would be called.
    /// </summary>
    [Theory]
    [InlineData(true, SourceServerClient.HttpClientName)]
    [InlineData(false, SourceServerClient.PublicHttpClientName)]
    public void Create_PrivateNetworkSetting_PicksTheNamedClient(bool allowPrivateNetwork, string expectedName)
    {
        using var client = _factory.Create(new SourceServer { Key = "a", Url = ClientStubServer.Url, ApiKey = "k", AllowPrivateNetwork = allowPrivateNetwork });

        Assert.Equal(expectedName, Assert.Single(_clientNames));
    }

    /// <summary>
    /// A stored key is decrypted before it is sent, and the client carries the plugin version.
    /// True: the source receives the real API Key.
    /// False: every request would send the encrypted text and be refused.
    /// </summary>
    [Fact]
    public async Task Create_ProtectedKey_IsSentDecrypted()
    {
        var stored = _secrets.Protect("plain-key");
        using var client = _factory.Create(new SourceServer { Key = "a", Url = ClientStubServer.Url + "/", ApiKey = stored });

        await client.GetPeerCapabilitiesAsync();

        var sent = Assert.Single(_server.Seen);
        Assert.NotEqual("plain-key", stored);
        Assert.Contains("Token=\"plain-key\"", sent.Authorization, StringComparison.Ordinal);
        Assert.Equal(_config.PluginVersion, sent.Header(HintProtocol.VersionHeader));
        Assert.Equal(ClientStubServer.Url, client.ServerUrl);
    }

    /// <summary>
    /// A saved entry called at its saved address gets the pairing, whatever the case or trailing slash of the address.
    /// True: peer requests to a saved server carry its secret and can pair again.
    /// False: every hint to a saved peer would be refused as unpaired.
    /// </summary>
    [Theory]
    [InlineData("https://peer.example")]
    [InlineData("https://PEER.example/")]
    public void Create_SavedEntryAtItsSavedAddress_GetsThePairing(string url)
    {
        _config.Configuration.Servers.Add(new SourceServer { Key = "saved", Url = ClientStubServer.Url, ApiKey = "k" });
        var secret = PeerPairingStore.NewSecret();
        _pairings.SetOutbound("saved", secret);

        using var client = _factory.Create(new SourceServer { Key = "saved", Url = url, ApiKey = "k" });

        Assert.NotNull(client.Pairing);
        Assert.Equal(ThisServer, client.Pairing!.ThisServerId);
        Assert.Equal(secret, client.Pairing.CurrentSecret());
    }

    /// <summary>
    /// An entry that is not saved, or is called at another address than the saved one, gets no pairing.
    /// True: the pairing secret only ever goes to the address saved on the server entry.
    /// False: testing an edited URL from the settings page would hand the secret to whatever host was typed.
    /// </summary>
    [Theory]
    [InlineData("unsaved", "https://peer.example")]
    [InlineData("saved", "https://other.example")]
    [InlineData("saved", "https://peer.example:8920")]
    [InlineData("", "https://peer.example")]
    public void Create_UnsavedEntryOrAnotherAddress_GetsNoPairing(string key, string url)
    {
        _config.Configuration.Servers.Add(new SourceServer { Key = "saved", Url = ClientStubServer.Url, ApiKey = "k" });

        using var client = _factory.Create(new SourceServer { Key = key, Url = url, ApiKey = "k" });

        Assert.Null(client.Pairing);
    }

    private IHttpClientFactory HttpClients()
    {
        var (factory, stub) = InterfaceStub.Create<IHttpClientFactory>();
        stub.Answer(nameof(IHttpClientFactory.CreateClient), args =>
        {
            _clientNames.Add((string)args[0]!);
            return new HttpClient(_server, disposeHandler: false);
        });
        return factory;
    }

    private static IServerApplicationHost Host()
    {
        var (host, stub) = InterfaceStub.Create<IServerApplicationHost>();
        stub.Answer("get_SystemId", _ => ThisServer);
        return host;
    }
}

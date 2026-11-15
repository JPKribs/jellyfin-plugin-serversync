using System;
using System.Net.Http;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Builds a <see cref="SourceServerClient"/> for a server entry, after checking its Server URL against the SSRF guard.
/// </summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(ISourceServerClientFactory))]
public class SourceServerClientFactory : ISourceServerClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IPluginConfigurationManager _configManager;
    private readonly SecretProtector _secrets;
    private readonly Services.Peer.PeerPairingStore _pairings;
    private readonly MediaBrowser.Controller.IServerApplicationHost _applicationHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceServerClientFactory"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Builds the named HTTP clients that carry the SSRF guard.</param>
    /// <param name="loggerFactory">Builds each client's logger.</param>
    /// <param name="configManager">Reads the plugin configuration and version.</param>
    /// <param name="secrets">Decrypts a stored API key.</param>
    /// <param name="pairings">Holds the secrets exchanged with peers.</param>
    /// <param name="applicationHost">Names this server to a peer.</param>
    public SourceServerClientFactory(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IPluginConfigurationManager configManager,
        SecretProtector secrets,
        Services.Peer.PeerPairingStore pairings,
        MediaBrowser.Controller.IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(pairings);
        ArgumentNullException.ThrowIfNull(applicationHost);

        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _configManager = configManager;
        _secrets = secrets;
        _pairings = pairings;
        _applicationHost = applicationHost;
    }

    /// <inheritdoc />
    public SourceServerClient Create(Models.Configuration.SourceServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var client = Create(server.Url, server.ApiKey, server.AllowPrivateNetwork);

        // A pairing secret only goes to the address saved on the server entry.
        if (_configManager.Configuration.FindServer(server.Key) is { } saved && ConfigurationUtilities.SameServerUrl(saved.Url, server.Url))
        {
            client.Pairing = new PeerPairing(_pairings, saved.Key, _applicationHost.SystemId);
        }

        return client;
    }

    /// <summary>
    /// Builds a client for an address after the SSRF guard accepts it. The named client it picks applies the
    /// private network rule again at connect time, when a name resolves.
    /// </summary>
    private SourceServerClient Create(string serverUrl, string apiKey, bool allowPrivateNetwork)
    {
        apiKey = _secrets.Unprotect(apiKey);
        var ssrfError = ConfigurationUtilities.ValidateServerUrlForSsrf(serverUrl, allowPrivateNetwork);
        if (ssrfError != null)
        {
            throw new ArgumentException($"Invalid source server URL: {ssrfError}", nameof(serverUrl));
        }

        var httpClient = _httpClientFactory.CreateClient(allowPrivateNetwork ? SourceServerClient.HttpClientName : SourceServerClient.PublicHttpClientName);
        var logger = _loggerFactory.CreateLogger<SourceServerClient>();
        return new SourceServerClient(
            logger,
            httpClient,
            serverUrl,
            apiKey,
            _configManager.LocalServerName,
            _configManager.PluginVersion);
    }
}

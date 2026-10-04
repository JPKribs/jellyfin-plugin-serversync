using System;
using System.Net.Http;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Plugin.ServerSync.Configuration;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Factory for creating <see cref="SourceServerClient"/> instances using DI-provided dependencies.
/// Validates the server URL for SSRF protection before creating the client.
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

    public SourceServerClientFactory(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IPluginConfigurationManager configManager,
        SecretProtector secrets,
        Services.Peer.PeerPairingStore pairings,
        MediaBrowser.Controller.IServerApplicationHost applicationHost)
    {
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

        // Only a saved entry can hold a pairing. A transient entry built for a one off check has no key
        // the peer could pair against.
        if (_configManager.Configuration.FindServer(server.Key) is not null)
        {
            client.Pairing = new PeerPairing(_pairings, server.Key, _applicationHost.SystemId);
        }

        return client;
    }

    /// <inheritdoc />
    public SourceServerClient Create(string serverUrl, string apiKey) => Create(serverUrl, apiKey, allowPrivateNetwork: true);

    private SourceServerClient Create(string serverUrl, string apiKey, bool allowPrivateNetwork)
    {
        // The stored key may be encrypted at rest; decrypt for use. Plaintext (pre-migration) passes through.
        apiKey = _secrets.Unprotect(apiKey);

        // Validate URL for SSRF protection (same checks as the controller endpoint). Private
        // networks are allowed per server entry, since home installs run peers on the same LAN.
        var ssrfError = ConfigurationUtilities.ValidateServerUrlForSsrf(serverUrl, allowPrivateNetwork);
        if (ssrfError != null)
        {
            throw new ArgumentException($"Invalid source server URL: {ssrfError}", nameof(serverUrl));
        }

        // Names resolve at connect time, so the private network rule is enforced there too; the
        // validator above only classifies an address written into the URL.
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

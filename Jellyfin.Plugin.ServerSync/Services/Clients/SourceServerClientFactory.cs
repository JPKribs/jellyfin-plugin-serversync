using System;
using System.Net.Http;
using Jellyfin.Plugin.ServerSync.Utilities;
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

    public SourceServerClientFactory(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IPluginConfigurationManager configManager)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _configManager = configManager;
    }

    /// <inheritdoc />
    public SourceServerClient Create(string serverUrl, string apiKey)
    {
        // Validate URL for SSRF protection (same checks as the controller endpoint).
        // Allow private networks per the plugin configuration — typical home
        // installs run their source Jellyfin on the same LAN.
        var allowPrivate = _configManager.Configuration.AllowSourceServerOnPrivateNetwork;
        var ssrfError = ConfigurationUtilities.ValidateServerUrlForSsrf(serverUrl, allowPrivate);
        if (ssrfError != null)
        {
            throw new ArgumentException($"Invalid source server URL: {ssrfError}", nameof(serverUrl));
        }

        var httpClient = _httpClientFactory.CreateClient(SourceServerClient.HttpClientName);
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

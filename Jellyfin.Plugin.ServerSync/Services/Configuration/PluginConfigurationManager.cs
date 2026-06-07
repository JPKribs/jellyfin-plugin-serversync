using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Provides access to plugin configuration by delegating to the Plugin singleton.
/// </summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(IPluginConfigurationManager))]
public class PluginConfigurationManager : IPluginConfigurationManager
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly SecretProtector _secrets;

    public PluginConfigurationManager(
        IApplicationPaths applicationPaths,
        IServerConfigurationManager serverConfigurationManager,
        SecretProtector secrets)
    {
        _applicationPaths = applicationPaths;
        _serverConfigurationManager = serverConfigurationManager;
        _secrets = secrets;
    }

    /// <inheritdoc />
    public PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin is not initialized");

    /// <inheritdoc />
    public string DecryptedSourceServerApiKey => _secrets.Unprotect(Configuration.SourceServerApiKey);

    /// <inheritdoc />
    public void SaveConfiguration()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");

        // Sanitize values before saving to prevent invalid configuration from persisting
        plugin.Configuration.SanitizeValues();

        // Encrypt the source-server API key at rest. Protect() no-ops on already-encrypted or empty,
        // so this is idempotent across saves.
        plugin.Configuration.SourceServerApiKey = _secrets.Protect(plugin.Configuration.SourceServerApiKey);

        plugin.SaveConfiguration();
    }

    /// <inheritdoc />
    public string GetTempDownloadPath()
    {
        var config = Configuration;
        if (!string.IsNullOrWhiteSpace(config.TempDownloadPath))
        {
            return config.TempDownloadPath;
        }

        return Path.Combine(_applicationPaths.CachePath, "serversync");
    }

    /// <inheritdoc />
    public string LocalServerName =>
        _serverConfigurationManager.Configuration.ServerName ?? Environment.MachineName;

    /// <inheritdoc />
    public string PluginVersion =>
        Plugin.Instance?.Version.ToString() ?? "1.0.0";
}

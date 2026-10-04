using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
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
    public string DecryptApiKey(string protectedKey) => _secrets.Unprotect(protectedKey);

    /// <inheritdoc />
    public string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl)
    {
        if (!string.Equals(requestApiKey, JPKribs.Jellyfin.Base.SecretProtector.KeptSentinel, StringComparison.Ordinal))
        {
            return requestApiKey ?? string.Empty;
        }

        var entry = Configuration.FindServer(serverKey);
        if (entry is null)
        {
            return string.Empty;
        }

        // The stored key is a secret the page never sees. Sent to whatever address a request names, an
        // edited URL would hand it to any server, so it only goes to the address saved on the entry.
        if (requestUrl is not null && !Utilities.ConfigurationUtilities.SameServerUrl(entry.Url, requestUrl))
        {
            throw new ArgumentException("The stored key is only sent to the address saved for this server. Enter the key again to use a new address.");
        }

        return DecryptApiKey(entry.ApiKey);
    }

    /// <inheritdoc />
    public void SaveConfiguration()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");

        // Single plugin-wide lock: XmlSerializer enumerates LastRunFailures
        // while writing the file, so an unsynchronized save racing another
        // module's RunFailureLog mutation throws mid-write and can truncate
        // the config XML.
        lock (ConfigurationSaveLock.Sync)
        {
            // Sanitize values before saving to prevent invalid configuration from persisting
            plugin.Configuration.SanitizeValues();

            // Encrypt every server's API key at rest. Protect() no-ops on already-encrypted or empty,
            // so this is idempotent across saves.
            foreach (var server in plugin.Configuration.Servers)
            {
                server.ApiKey = _secrets.Protect(server.ApiKey);
            }

            plugin.SaveConfiguration();
        }
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

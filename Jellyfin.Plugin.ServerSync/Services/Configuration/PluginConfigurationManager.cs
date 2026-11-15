using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Configuration;

/// <summary>
/// Reads and saves the configuration held by the <see cref="Plugin"/> instance.
/// </summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(IPluginConfigurationManager))]
public class PluginConfigurationManager : IPluginConfigurationManager
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly SecretProtector _secrets;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfigurationManager"/> class.
    /// </summary>
    /// <param name="applicationPaths">Locates Jellyfin's cache folder.</param>
    /// <param name="serverConfigurationManager">Gives this server's name.</param>
    /// <param name="secrets">Protects and reads stored secrets.</param>
    public PluginConfigurationManager(
        IApplicationPaths applicationPaths,
        IServerConfigurationManager serverConfigurationManager,
        SecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(serverConfigurationManager);
        ArgumentNullException.ThrowIfNull(secrets);
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
        // A protected key posted back is held to the sentinel's rule, or any address could receive it.
        if (!string.Equals(requestApiKey, SecretProtector.KeptSentinel, StringComparison.Ordinal)
            && !SecretProtector.IsProtected(requestApiKey))
        {
            return requestApiKey ?? string.Empty;
        }

        var entry = Configuration.FindServer(serverKey);
        if (entry is null)
        {
            return string.Empty;
        }

        // The stored key only goes to the saved address, or an edited URL could take it.
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

        lock (ConfigurationSaveLock.Sync)
        {
            plugin.Configuration.SanitizeValues();

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

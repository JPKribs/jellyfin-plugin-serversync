using Jellyfin.Plugin.ServerSync.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Provides access to plugin configuration and related operations.
/// </summary>
public interface IPluginConfigurationManager
{
    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    PluginConfiguration Configuration { get; }

    /// <summary>
    /// Decrypts a stored API key for use. A key written before encryption existed passes through unchanged.
    /// </summary>
    /// <param name="protectedKey">The key as stored on a server entry.</param>
    /// <returns>The usable key.</returns>
    string DecryptApiKey(string protectedKey);

    /// <summary>
    /// Resolves the API key a dashboard request carries. The page never sees a stored key, only the kept
    /// sentinel, so the sentinel means the key stored on the named server entry.
    /// </summary>
    /// <param name="requestApiKey">The key as posted, or the sentinel.</param>
    /// <param name="serverKey">The server entry the request is about.</param>
    /// <returns>The usable key, or empty when there is none.</returns>
    string ResolveRequestApiKey(string? requestApiKey, string? serverKey);

    /// <summary>
    /// Saves the current configuration to disk.
    /// </summary>
    void SaveConfiguration();

    /// <summary>
    /// Gets the configured temp download path, falling back to the cache directory.
    /// </summary>
    /// <returns>Path to the temp download directory.</returns>
    string GetTempDownloadPath();

    /// <summary>
    /// Gets the local server's friendly name.
    /// </summary>
    string LocalServerName { get; }

    /// <summary>
    /// Gets the plugin version string.
    /// </summary>
    string PluginVersion { get; }
}

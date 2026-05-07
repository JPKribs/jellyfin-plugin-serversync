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

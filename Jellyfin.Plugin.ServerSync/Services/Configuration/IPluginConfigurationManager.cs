using Jellyfin.Plugin.ServerSync.Configuration;

namespace Jellyfin.Plugin.ServerSync.Services.Configuration;

/// <summary>
/// Reads and saves the plugin configuration and resolves the secrets it holds.
/// </summary>
public interface IPluginConfigurationManager
{
    /// <summary>Gets the current plugin configuration.</summary>
    PluginConfiguration Configuration { get; }

    /// <summary>
    /// Decrypts a stored API key for use. A key stored in plaintext passes through unchanged.
    /// </summary>
    /// <param name="protectedKey">The key as stored on a server entry.</param>
    /// <returns>The usable key.</returns>
    string DecryptApiKey(string protectedKey);

    /// <summary>
    /// Resolves the API key a dashboard request carries, where the kept sentinel means the key stored on the named server entry.
    /// </summary>
    /// <param name="requestApiKey">The key as posted, or the sentinel.</param>
    /// <param name="serverKey">The server entry the request is about.</param>
    /// <param name="requestUrl">The address the key is about to be sent to. The stored key only goes to the address saved on the entry.</param>
    /// <returns>The usable key, or empty when there is none.</returns>
    /// <exception cref="System.ArgumentException">The sentinel was posted for an address other than the one saved.</exception>
    string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl);

    /// <summary>
    /// Saves the current configuration to disk, protecting every API key first.
    /// </summary>
    void SaveConfiguration();

    /// <summary>
    /// Gets the configured temp download path, falling back to the cache directory.
    /// </summary>
    /// <returns>The folder downloads are written to before they move into a library.</returns>
    string GetTempDownloadPath();

    /// <summary>Gets this server's name.</summary>
    string LocalServerName { get; }

    /// <summary>Gets the plugin version.</summary>
    string PluginVersion { get; }
}

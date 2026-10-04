namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Factory for creating <see cref="SourceServerClient"/> instances.
/// </summary>
public interface ISourceServerClientFactory
{
    /// <summary>
    /// Creates a new <see cref="SourceServerClient"/> using the given server URL and API key.
    /// </summary>
    /// <param name="serverUrl">Source server URL.</param>
    /// <param name="apiKey">API key for authentication.</param>
    /// <returns>A configured <see cref="SourceServerClient"/>.</returns>
    SourceServerClient Create(string serverUrl, string apiKey);

    /// <summary>
    /// Creates a client for a configured server entry, honoring the entry's private network rule.
    /// </summary>
    /// <param name="server">The server entry.</param>
    /// <returns>A client bound to that server.</returns>
    SourceServerClient Create(Models.Configuration.SourceServer server);
}

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Factory for creating <see cref="SourceServerClient"/> instances.
/// </summary>
public interface ISourceServerClientFactory
{
    /// <summary>
    /// Creates a client for a configured server entry, honoring the entry's private network rule.
    /// </summary>
    /// <param name="server">The server entry.</param>
    /// <returns>A client bound to that server.</returns>
    SourceServerClient Create(Models.Configuration.SourceServer server);
}

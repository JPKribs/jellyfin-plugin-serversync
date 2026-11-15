namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Builds a <see cref="SourceServerClient"/> for a server entry.
/// </summary>
public interface ISourceServerClientFactory
{
    /// <summary>
    /// Creates a client for a server entry, honoring the entry's private network rule.
    /// </summary>
    /// <param name="server">The server entry to call.</param>
    /// <returns>A client bound to that server.</returns>
    SourceServerClient Create(Models.Configuration.SourceServer server);
}

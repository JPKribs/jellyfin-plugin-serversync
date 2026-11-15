using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Configuration;

/// <summary>
/// Reads the server entries, such as which are sources, which entry a row belongs to, and the mappings across every source in Rank order.
/// </summary>
public static class PluginConfigurationExtensions
{
    /// <summary>
    /// Returns the sources in Rank order, the enabled entries with a Server URL and API Key in the Pull or Sync direction.
    /// </summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <returns>The sources, highest ranked first, never null.</returns>
    public static List<SourceServer> GetPullServers(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.Where(s => s.Pulls).ToList() ?? new List<SourceServer>();
    }

    /// <summary>
    /// Returns the servers this one sends hints to, the enabled entries with a Server URL and API Key in the Push or Sync direction.
    /// </summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <returns>The servers hints go to, in list order, never null.</returns>
    public static List<SourceServer> GetPushServers(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.Where(s => s.Pushes).ToList() ?? new List<SourceServer>();
    }

    /// <summary>Returns every server that takes part in sync in any direction.</summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <returns>The active servers, in list order, never null.</returns>
    public static List<SourceServer> GetActiveServers(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.Where(s => s.Pulls || s.Pushes).ToList() ?? new List<SourceServer>();
    }

    /// <summary>
    /// Whether History for a row of this server and library is also written back to that server.
    /// Only the Sync direction negotiates, and only for a library mapping that chose to.
    /// </summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <param name="serverKey">The row's server.</param>
    /// <param name="sourceLibraryId">The row's library on that server.</param>
    /// <returns><c>true</c> when negotiated.</returns>
    public static bool NegotiatesHistory(this PluginConfiguration config, string? serverKey, string? sourceLibraryId)
        => config.FindServer(serverKey) is { IsEnabled: true, Mode: ServerMode.Sync } server
            && (server.LibraryMappings ?? new List<LibraryMapping>()).Any(m => m.IsEnabled && m.NegotiateHistory
                && string.Equals(m.SourceLibraryId, sourceLibraryId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether any library of any Sync server negotiates History.</summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <returns><c>true</c> when one does.</returns>
    public static bool NegotiatesAnyHistory(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return (config.Servers ?? new List<SourceServer>()).Any(s => s.IsEnabled && s.Mode == ServerMode.Sync
            && (s.LibraryMappings ?? new List<LibraryMapping>()).Any(m => m.IsEnabled && m.NegotiateHistory));
    }

    /// <summary>Finds a server entry by key.</summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <param name="key">The entry key carried on a sync row.</param>
    /// <returns>The entry, or null when no entry has that key.</returns>
    public static SourceServer? FindServer(this PluginConfiguration config, string? key)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return config.Servers?.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds a server entry by the server's own ID.</summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <param name="serverId">The Jellyfin server id, as a peer names itself.</param>
    /// <returns>The entry, or null when no entry has that id.</returns>
    public static SourceServer? FindServerById(this PluginConfiguration config, string? serverId)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(serverId))
        {
            return null;
        }

        return config.Servers?.FirstOrDefault(s => string.Equals(s.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the server a sync row came from. A row that carries no key belongs to the first source.
    /// </summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <param name="key">The entry key carried on the row, or null.</param>
    /// <returns>The entry, or null when none applies.</returns>
    public static SourceServer? ResolveServer(this PluginConfiguration config, string? key)
        => config.FindServer(key) ?? config.GetPullServers().FirstOrDefault();

    /// <summary>
    /// Returns the enabled library mappings of every source, in Rank order.
    /// </summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <returns>The enabled mappings across all sources, never null.</returns>
    public static List<LibraryMapping> GetEnabledLibraryMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GetPullServers().SelectMany(s => s.GetEnabledLibraryMappings()).ToList();
    }

    /// <summary>
    /// Returns the enabled user mappings of every source, in Rank order.
    /// </summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <returns>The enabled mappings across all sources, never null.</returns>
    public static List<UserMapping> GetEnabledUserMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GetPullServers().SelectMany(s => s.GetEnabledUserMappings()).ToList();
    }

    /// <summary>
    /// Returns every library mapping of every server, enabled or not, in Rank order.
    /// It serves code that only needs local folders, such as disk space and recycling.
    /// </summary>
    /// <param name="config">The plugin settings to read.</param>
    /// <returns>All library mappings.</returns>
    public static List<LibraryMapping> GetAllLibraryMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.SelectMany(s => s.LibraryMappings ?? new List<LibraryMapping>()).ToList() ?? new List<LibraryMapping>();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

namespace Jellyfin.Plugin.ServerSync.Configuration;

/// <summary>
/// Helpers over the server list: which servers scan, which entry a row belongs to, and the mappings
/// across every scan server in rank order.
/// </summary>
public static class PluginConfigurationExtensions
{
    /// <summary>
    /// Returns the servers this installation scans, in rank order: enabled, with a URL and key, and in
    /// Pull or Sync mode. Never returns null.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The scan servers, highest ranked first.</returns>
    public static List<SourceServer> GetPullServers(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.Where(s => s.Pulls).ToList() ?? new List<SourceServer>();
    }

    /// <summary>
    /// Returns the servers this installation sends hints to: enabled, with a URL and key, and in Push or
    /// Sync mode. Never returns null.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The send servers, in list order.</returns>
    public static List<SourceServer> GetPushServers(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.Where(s => s.Pushes).ToList() ?? new List<SourceServer>();
    }

    /// <summary>Returns every server that takes part in sync in any direction. Never returns null.</summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The active servers, in list order.</returns>
    public static List<SourceServer> GetActiveServers(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.Where(s => s.Pulls || s.Pushes).ToList() ?? new List<SourceServer>();
    }

    /// <summary>
    /// Whether watch history for a row of this server and library is negotiated: written back to that
    /// server as well. Only a Sync server negotiates, and only for a library mapping that chose to.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="serverKey">The row's server.</param>
    /// <param name="sourceLibraryId">The row's library on that server.</param>
    /// <returns><c>true</c> when negotiated.</returns>
    public static bool NegotiatesHistory(this PluginConfiguration config, string? serverKey, string? sourceLibraryId)
        => config.FindServer(serverKey) is { IsEnabled: true, Mode: ServerMode.Sync } server
            && (server.LibraryMappings ?? new List<LibraryMapping>()).Any(m => m.IsEnabled && m.NegotiateHistory
                && string.Equals(m.SourceLibraryId, sourceLibraryId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether any library of any Sync server negotiates watch history.</summary>
    /// <param name="config">The configuration.</param>
    /// <returns><c>true</c> when one does.</returns>
    public static bool NegotiatesAnyHistory(this PluginConfiguration config)
        => (config.Servers ?? new List<SourceServer>()).Any(s => s.IsEnabled && s.Mode == ServerMode.Sync
            && (s.LibraryMappings ?? new List<LibraryMapping>()).Any(m => m.IsEnabled && m.NegotiateHistory));

    /// <summary>Finds a server entry by key, or null.</summary>
    /// <param name="config">The configuration.</param>
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

    /// <summary>Finds a server entry by the server's own id, or null.</summary>
    /// <param name="config">The configuration.</param>
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
    /// Resolves the server a sync row came from. A row that carries no key belongs to the first scan server.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="key">The entry key carried on the row, or null.</param>
    /// <returns>The entry, or null when none applies.</returns>
    public static SourceServer? ResolveServer(this PluginConfiguration config, string? key)
        => config.FindServer(key) ?? config.GetPullServers().FirstOrDefault();

    /// <summary>
    /// Returns the enabled library mappings of every scan server, in rank order. Never returns null.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The enabled mappings across all scan servers.</returns>
    public static List<LibraryMapping> GetEnabledLibraryMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GetPullServers().SelectMany(s => s.GetEnabledLibraryMappings()).ToList();
    }

    /// <summary>
    /// Returns the enabled user mappings of every scan server, in rank order. Never returns null.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The enabled mappings across all scan servers.</returns>
    public static List<UserMapping> GetEnabledUserMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GetPullServers().SelectMany(s => s.GetEnabledUserMappings()).ToList();
    }

    /// <summary>
    /// Returns every library mapping of every server, enabled or not, in rank order. For code that
    /// only cares about local folders, such as disk space and recycling, where a disabled mapping's
    /// folder still exists.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>All library mappings.</returns>
    public static List<LibraryMapping> GetAllLibraryMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.SelectMany(s => s.LibraryMappings ?? new List<LibraryMapping>()).ToList() ?? new List<LibraryMapping>();
    }
}

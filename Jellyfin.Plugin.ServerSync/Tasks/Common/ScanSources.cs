using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// The run plumbing the refresh and queue task bases share: connecting the scan servers, finding the
/// one a row belongs to, and recording a finished run.
/// </summary>
internal static class ScanSources
{
    /// <summary>
    /// Connects every scan server and keeps the ones that answer, in priority order. A server that is
    /// rejected or does not answer is logged and reported through <paramref name="onUnavailable"/>.
    /// </summary>
    /// <param name="configManager">The configuration.</param>
    /// <param name="clientFactory">Creates a client per server.</param>
    /// <param name="logger">The task's logger.</param>
    /// <param name="taskName">The task's name, for the log.</param>
    /// <param name="onUnavailable">Told why each server was skipped, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connected sources. The caller disposes them.</returns>
    public static async Task<List<ScanSource>> ConnectAsync(
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        ILogger logger,
        string taskName,
        Action<string>? onUnavailable,
        CancellationToken cancellationToken)
    {
        var connected = new List<ScanSource>();
        var servers = configManager.Configuration.GetPullServers();
        for (var i = 0; i < servers.Count; i++)
        {
            var server = servers[i];
            SourceServerClient client;
            try
            {
                client = clientFactory.Create(server);
            }
            catch (ArgumentException ex)
            {
                logger.LogError("{Task}: server '{Server}' rejected: {Error}", taskName, server.DisplayName, ex.Message);
                onUnavailable?.Invoke($"server '{server.DisplayName}' has an invalid URL");
                continue;
            }

            Models.Configuration.ConnectionTestResult result;
            try
            {
                result = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The caller has not seen these clients yet, so its cleanup cannot dispose them.
                // Dispose them here before the failure or cancellation propagates.
                client.Dispose();
                foreach (var source in connected)
                {
                    source.Dispose();
                }

                throw;
            }

            if (!result.Success)
            {
                logger.LogError("{Task}: connection to '{Server}' failed: {Error}", taskName, server.DisplayName, result.ErrorMessage ?? "unknown");
                onUnavailable?.Invoke($"server '{server.DisplayName}' is unreachable");
                client.Dispose();
                continue;
            }

            connected.Add(new ScanSource(server, client, i));
        }

        return connected;
    }

    /// <summary>
    /// Finds the connected source for a server entry key. A null key is a row written before servers
    /// became a list and belongs to the first configured scan server, which is where the single source
    /// migrated to, not to whichever server happened to connect first.
    /// </summary>
    /// <param name="sources">The sources connected this run.</param>
    /// <param name="configManager">The configuration.</param>
    /// <param name="serverKey">The entry key carried on a row, or null.</param>
    /// <returns>The source, or null when that server did not connect this run.</returns>
    public static ScanSource? Find(IReadOnlyList<ScanSource> sources, IPluginConfigurationManager configManager, string? serverKey)
    {
        if (string.IsNullOrEmpty(serverKey))
        {
            var pullServers = configManager.Configuration.GetPullServers();
            if (pullServers.Count == 0)
            {
                return null;
            }

            serverKey = pullServers[0].Key;
        }

        foreach (var source in sources)
        {
            if (string.Equals(source.Key, serverKey, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>
    /// Lets the task stamp its module's last-run timestamp, then saves the configuration. Failures
    /// here are logged but never propagate, a failed save mustn't mask the actual run result.
    /// </summary>
    /// <param name="configManager">The configuration.</param>
    /// <param name="recordRunCompleted">Stamps the module's last-run time.</param>
    /// <param name="logger">The task's logger.</param>
    /// <param name="taskName">The task's name, for the log.</param>
    public static void RecordCompletedAndSave(
        IPluginConfigurationManager configManager,
        Action<PluginConfiguration, DateTime> recordRunCompleted,
        ILogger logger,
        string taskName)
    {
        try
        {
            recordRunCompleted(configManager.Configuration, DateTime.UtcNow);
            configManager.SaveConfiguration();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Task}: failed to save run-completion timestamp", taskName);
        }
    }
}

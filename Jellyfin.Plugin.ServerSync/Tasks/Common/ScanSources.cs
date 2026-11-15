using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// The run plumbing the scan and queue task bases share: connecting the sources, finding the one a row belongs to, and recording a finished run.
/// </summary>
internal static class ScanSources
{
    /// <summary>
    /// Connects every source and keeps the ones that answer, in rank order. A server that is
    /// rejected or does not answer is logged and reported through <paramref name="onUnavailable"/>.
    /// </summary>
    /// <param name="configManager">Supplies the server entries to connect.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="logger">Receives an error for each server that is skipped.</param>
    /// <param name="taskName">The task's name, for the log.</param>
    /// <param name="onUnavailable">Told why each server was skipped, or null.</param>
    /// <param name="cancellationToken">Cancels the connection tests.</param>
    /// <returns>The connected sources. The caller disposes them.</returns>
    public static async Task<List<ScanSource>> ConnectAsync(
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        ILogger logger,
        string taskName,
        Action<string>? onUnavailable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(logger);

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
                logger.LogError("{Task}: server '{Server}' rejected: {Error}", taskName, server.DisplayName, FormatUtilities.SanitizeForLog(ex.Message, 300));
                onUnavailable?.Invoke($"server '{server.DisplayName}' has an invalid URL");
                continue;
            }

            var handedOff = false;
            try
            {
                Models.Configuration.ConnectionTestResult result;
                try
                {
                    result = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The caller has not seen these sources yet, so nothing else would dispose them.
                    foreach (var source in connected)
                    {
                        source.Dispose();
                    }

                    throw;
                }

                if (!result.Success)
                {
                    logger.LogError("{Task}: connection to '{Server}' failed: {Error}", taskName, server.DisplayName, FormatUtilities.SanitizeForLog(result.ErrorMessage ?? "unknown", 300));
                    onUnavailable?.Invoke($"server '{server.DisplayName}' is unreachable");
                    continue;
                }

                connected.Add(new ScanSource(server, client, i));
                handedOff = true;
            }
            finally
            {
                // The loop owns the client until a ScanSource takes it.
                if (!handedOff)
                {
                    client.Dispose();
                }
            }
        }

        return connected;
    }

    /// <summary>
    /// Finds the connected source for a server entry key. A row with no key belongs to the first
    /// configured source, not to whichever connected first.
    /// </summary>
    /// <param name="sources">The sources connected this run.</param>
    /// <param name="configManager">Supplies the configured sources, for a row with no key.</param>
    /// <param name="serverKey">The entry key carried on a row, or null.</param>
    /// <returns>The source, or null when that server did not connect this run.</returns>
    public static ScanSource? Find(IReadOnlyList<ScanSource> sources, IPluginConfigurationManager configManager, string? serverKey)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(configManager);

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
    /// Lets the task record its sync type's last run time, then saves the configuration.
    /// A failure is logged and never thrown, so a failed save cannot hide the run result.
    /// </summary>
    /// <param name="configManager">Holds the configuration to stamp and save.</param>
    /// <param name="recordRunCompleted">Records the sync type's last run time.</param>
    /// <param name="logger">Receives a warning when the save fails.</param>
    /// <param name="taskName">The task's name, for the log.</param>
    public static void RecordCompletedAndSave(
        IPluginConfigurationManager configManager,
        Action<PluginConfiguration, DateTime> recordRunCompleted,
        ILogger logger,
        string taskName)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(recordRunCompleted);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            recordRunCompleted(configManager.Configuration, DateTime.UtcNow);
            configManager.SaveConfiguration();
        }
        // A failed save must never hide the run result.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Task}: failed to save run-completion timestamp", taskName);
        }
    }
}

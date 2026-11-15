using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Records each sync type's run outcomes on the plugin configuration for the dashboard's status.
/// Every change takes the configuration save lock, and a failed save is logged and never thrown, so it cannot hide the run result.
/// </summary>
internal static class RunFailureLog
{
    private static readonly object _lock = Services.Configuration.ConfigurationSaveLock.Sync;

    /// <summary>
    /// Records or replaces the most recent run failure for one phase of a sync type.
    /// A Refresh failure and a Sync failure of the same sync type are kept side by side.
    /// </summary>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="moduleKey">The sync type's mutex key, such as Content or History.</param>
    /// <param name="phase">Refresh or Sync.</param>
    /// <param name="reason">Why the run failed.</param>
    /// <param name="logger">Receives a warning when the save fails.</param>
    /// <param name="taskName">The task's name, for log entries.</param>
    public static void Record(
        IPluginConfigurationManager configManager,
        string moduleKey,
        string phase,
        string reason,
        ILogger logger,
        string taskName)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            lock (_lock)
            {
                var failures = configManager.Configuration.LastRunFailures;
                var before = failures.FirstOrDefault(f => Matches(f, moduleKey, phase));
                failures.RemoveAll(f => Matches(f, moduleKey, phase));
                var now = DateTime.UtcNow;
                failures.Add(new SyncRunFailure
                {
                    ModuleKey = moduleKey,
                    Phase = phase,
                    Reason = reason,
                    Timestamp = now,
                    FirstSeen = before?.FirstSeen ?? before?.Timestamp ?? now,
                    Reported = before?.Reported ?? false
                });
                configManager.SaveConfiguration();
            }
        }
        // A failed save must never hide the run result.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Task}: failed to record run-failure outcome", taskName);
        }
    }

    /// <summary>
    /// Clears the recorded failure for one phase of a sync type after a clean run of that phase.
    /// </summary>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="moduleKey">The sync type's mutex key, such as Content or History.</param>
    /// <param name="phase">Refresh or Sync.</param>
    /// <param name="logger">Receives a warning when the save fails.</param>
    /// <param name="taskName">The task's name, for log entries.</param>
    public static void Clear(
        IPluginConfigurationManager configManager,
        string moduleKey,
        string phase,
        ILogger logger,
        string taskName)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            lock (_lock)
            {
                var failures = configManager.Configuration.LastRunFailures;
                var removed = failures.RemoveAll(f => Matches(f, moduleKey, phase));
                if (removed > 0)
                {
                    configManager.SaveConfiguration();
                }
            }
        }
        // A failed save must never hide the run result.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Task}: failed to clear run-failure outcome", taskName);
        }
    }

    /// <summary>
    /// Takes the failures that have stood for longer than <paramref name="standing"/> and that the administrator has not been told about.
    /// Each is marked as told, so it is raised again only after a clean run clears it and it comes back.
    /// </summary>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="standing">How long a failure must stand before it counts.</param>
    /// <returns>The failures to raise now.</returns>
    public static IReadOnlyList<SyncRunFailure> TakeUnreported(IPluginConfigurationManager configManager, TimeSpan standing)
    {
        ArgumentNullException.ThrowIfNull(configManager);

        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - standing;
            var due = configManager.Configuration.LastRunFailures
                .Where(f => !f.Reported && (f.FirstSeen ?? f.Timestamp) < cutoff)
                .ToList();
            if (due.Count > 0)
            {
                foreach (var failure in due)
                {
                    failure.Reported = true;
                }

                configManager.SaveConfiguration();
            }

            return due;
        }
    }

    private static bool Matches(SyncRunFailure failure, string moduleKey, string phase)
        => string.Equals(failure.ModuleKey, moduleKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(failure.Phase, phase, StringComparison.OrdinalIgnoreCase);
}

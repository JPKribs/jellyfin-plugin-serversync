using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Records per-module run outcomes on the plugin configuration for the
/// dashboard's Status endpoint. All mutations go through the plugin-wide
/// <see cref="Services.Configuration.ConfigurationSaveLock"/>: different
/// modules hold different <see cref="SyncModuleMutex"/> keys, so a Content
/// refresh and a Metadata sync can both finish at the same moment and would
/// otherwise race on the shared <c>LastRunFailures</c> list, including
/// against the XmlSerializer enumerating it mid-save on another thread.
/// Best-effort, a failed config save is logged but never propagates, so it
/// can't mask the actual run result.
/// </summary>
internal static class RunFailureLog
{
    private static readonly object _lock = Services.Configuration.ConfigurationSaveLock.Sync;

    /// <summary>
    /// Records (or replaces) the most recent run failure for a module's phase. Entries are keyed by
    /// module and phase together, so a Refresh failure and a Sync failure of the same module are kept
    /// side by side and neither phase's clean run hides the other's failure.
    /// </summary>
    public static void Record(
        IPluginConfigurationManager configManager,
        string moduleKey,
        string phase,
        string reason,
        ILogger logger,
        string taskName)
    {
        try
        {
            lock (_lock)
            {
                var failures = configManager.Configuration.LastRunFailures;
                var before = failures.FirstOrDefault(f => Matches(f, moduleKey, phase));
                failures.RemoveAll(f => Matches(f, moduleKey, phase));
                failures.Add(new SyncRunFailure
                {
                    ModuleKey = moduleKey,
                    Phase = phase,
                    Reason = reason,
                    Timestamp = DateTime.UtcNow,
                    FirstSeen = before?.FirstSeen ?? before?.Timestamp ?? DateTime.UtcNow,
                    Reported = before?.Reported ?? false
                });
                configManager.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Task}: failed to record run-failure outcome", taskName);
        }
    }

    /// <summary>
    /// Clears the recorded failure for a module's phase after a clean run of that phase. The other
    /// phase's entry is left in place.
    /// </summary>
    public static void Clear(
        IPluginConfigurationManager configManager,
        string moduleKey,
        string phase,
        ILogger logger,
        string taskName)
    {
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
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Task}: failed to clear run-failure outcome", taskName);
        }
    }

    /// <summary>
    /// The failures that have stood for longer than <paramref name="standing"/> and that the
    /// administrator has not been told about. Each is marked as told, so it is raised once, and is
    /// raised again only after a clean run has cleared it and it comes back.
    /// </summary>
    public static IReadOnlyList<SyncRunFailure> TakeUnreported(IPluginConfigurationManager configManager, TimeSpan standing)
    {
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

using System;
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
/// otherwise race on the shared <c>LastRunFailures</c> list — including
/// against the XmlSerializer enumerating it mid-save on another thread.
/// Best-effort — a failed config save is logged but never propagates, so it
/// can't mask the actual run result.
/// </summary>
internal static class RunFailureLog
{
    private static readonly object _lock = Services.Configuration.ConfigurationSaveLock.Sync;

    /// <summary>
    /// Records (or replaces) the most-recent run failure for a module.
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
                failures.RemoveAll(f => string.Equals(f.ModuleKey, moduleKey, StringComparison.OrdinalIgnoreCase));
                failures.Add(new SyncRunFailure
                {
                    ModuleKey = moduleKey,
                    Phase = phase,
                    Reason = reason,
                    Timestamp = DateTime.UtcNow
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
    /// Clears any recorded failure for a module after a clean run.
    /// </summary>
    public static void Clear(
        IPluginConfigurationManager configManager,
        string moduleKey,
        ILogger logger,
        string taskName)
    {
        try
        {
            lock (_lock)
            {
                var failures = configManager.Configuration.LastRunFailures;
                var removed = failures.RemoveAll(f => string.Equals(f.ModuleKey, moduleKey, StringComparison.OrdinalIgnoreCase));
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
}

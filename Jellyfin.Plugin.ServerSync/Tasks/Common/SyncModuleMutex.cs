using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Per-module async mutex shared between a module's Refresh task and its
/// Sync task. Without this, both tasks can run concurrently and stomp on each
/// other's row writes — a Refresh upsert can revert a row from Synced back
/// to Queued by overwriting status with stale in-memory state.
/// </summary>
internal static class SyncModuleMutex
{
    private static readonly Dictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.Ordinal);
    private static readonly object _lock = new();

    /// <summary>
    /// Returns the singleton semaphore for a module key (e.g. "Content",
    /// "History"). The same key always returns the same instance, so a
    /// Refresh and a Sync task using the same key serialize against each
    /// other.
    /// </summary>
    /// <param name="moduleKey">Module key (typically the module name).</param>
    /// <returns>Semaphore with capacity 1.</returns>
    public static SemaphoreSlim ForModule(string moduleKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleKey);
        lock (_lock)
        {
            if (!_semaphores.TryGetValue(moduleKey, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _semaphores[moduleKey] = sem;
            }

            return sem;
        }
    }
}

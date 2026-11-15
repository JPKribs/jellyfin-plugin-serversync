using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// An async mutex for each sync type, shared by its scan task and its sync task.
/// It stops a scan upsert from overwriting a row the sync task marked Synced with stale state.
/// </summary>
internal static class SyncModuleMutex
{
    private static readonly Dictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a single row refresh or apply, run for a hint, waits for the sync type before giving up.
    /// A scheduled run can hold it for an hour, so the hint is put off and tried again.
    /// </summary>
    public static readonly TimeSpan SingleRowWait = TimeSpan.FromSeconds(5);
    private static readonly object _lock = new();

    /// <summary>
    /// Returns the one semaphore for a sync type key, such as Content or History, so tasks that share the key run one at a time.
    /// </summary>
    /// <param name="moduleKey">The sync type's key.</param>
    /// <returns>The semaphore, which admits one holder.</returns>
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

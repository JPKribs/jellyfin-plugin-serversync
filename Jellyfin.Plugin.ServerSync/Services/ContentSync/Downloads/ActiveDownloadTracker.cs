using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;

/// <summary>
/// Tracks the downloads in progress so the cleanup task never deletes a temp file a download is writing.
/// </summary>
public static class ActiveDownloadTracker
{
    /// <summary>
    /// The downloads in progress, keyed by source item id.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DownloadInfo> ActiveDownloads = new();

    /// <summary>
    /// The age in hours past which a download counts as abandoned.
    /// </summary>
    private const int StaleDownloadHours = 2;

    /// <summary>
    /// Registers a download unless the item is already downloading.
    /// </summary>
    /// <param name="sourceItemId">The source item being downloaded.</param>
    /// <param name="tempFilePath">The temp file the download writes to.</param>
    /// <returns><c>true</c> when the download was registered, <c>false</c> when the item is already downloading.</returns>
    public static bool TryStartDownload(string sourceItemId, string tempFilePath)
    {
        ArgumentNullException.ThrowIfNull(sourceItemId);

        var info = new DownloadInfo(DateTime.UtcNow, tempFilePath);
        return ActiveDownloads.TryAdd(sourceItemId, info);
    }

    /// <summary>
    /// Stops tracking a download that ended.
    /// </summary>
    /// <param name="sourceItemId">The source item whose download ended.</param>
    public static void CompleteDownload(string sourceItemId)
    {
        ArgumentNullException.ThrowIfNull(sourceItemId);

        ActiveDownloads.TryRemove(sourceItemId, out _);
    }

    /// <summary>
    /// Checks whether a download in progress is writing to a temp file.
    /// </summary>
    /// <param name="tempFilePath">The temp file to check.</param>
    /// <returns><c>true</c> when a download is writing to it.</returns>
    public static bool IsTempFileInUse(string tempFilePath)
    {
        return ActiveDownloads.Values.Any(d =>
            string.Equals(d.TempFilePath, tempFilePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the temp file of every download in progress.
    /// </summary>
    /// <returns>The temp file paths that must not be deleted.</returns>
    public static IReadOnlyCollection<string> GetActiveTempFiles()
    {
        return ActiveDownloads.Values
            .Select(d => d.TempFilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
    }

    /// <summary>
    /// Stops tracking downloads that have run long enough to count as abandoned.
    /// </summary>
    /// <returns>How many entries were removed.</returns>
    public static int CleanupStaleEntries()
    {
        var staleThreshold = DateTime.UtcNow.AddHours(-StaleDownloadHours);
        var staleEntries = ActiveDownloads
            .Where(kvp => kvp.Value.StartTime < staleThreshold)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleEntries)
        {
            ActiveDownloads.TryRemove(key, out _);
        }

        return staleEntries.Count;
    }

    /// <summary>
    /// When one download started and the temp file it writes to.
    /// </summary>
    private sealed class DownloadInfo
    {
        public DateTime StartTime { get; }

        public string TempFilePath { get; }

        public DownloadInfo(DateTime startTime, string tempFilePath)
        {
            StartTime = startTime;
            TempFilePath = tempFilePath;
        }
    }
}

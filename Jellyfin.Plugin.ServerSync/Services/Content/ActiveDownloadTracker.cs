using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Thread-safe tracker for active downloads.
/// Shared between download task and cleanup task to prevent temp file deletion during downloads.
/// </summary>
public static class ActiveDownloadTracker
{
    /// <summary>
    /// Tracks items currently being downloaded with their temp file paths.
    /// Key: SourceItemId, Value: (StartTime, TempFilePath)
    /// </summary>
    private static readonly ConcurrentDictionary<string, DownloadInfo> ActiveDownloads = new();

    /// <summary>
    /// Maximum age before a download is considered stale (likely crashed/abandoned).
    /// </summary>
    private const int StaleDownloadHours = 2;

    /// <summary>
    /// Attempts to register a download. Returns false if already downloading.
    /// </summary>
    public static bool TryStartDownload(string sourceItemId, string tempFilePath)
    {
        var info = new DownloadInfo(DateTime.UtcNow, tempFilePath);
        return ActiveDownloads.TryAdd(sourceItemId, info);
    }

    /// <summary>
    /// Marks a download as complete and removes it from tracking.
    /// </summary>
    public static void CompleteDownload(string sourceItemId)
    {
        ActiveDownloads.TryRemove(sourceItemId, out _);
    }

    /// <summary>
    /// Checks if a source item is currently being downloaded.
    /// </summary>
    public static bool IsDownloading(string sourceItemId)
    {
        return ActiveDownloads.ContainsKey(sourceItemId);
    }

    /// <summary>
    /// Checks if a temp file path is currently in use by an active download.
    /// </summary>
    public static bool IsTempFileInUse(string tempFilePath)
    {
        return ActiveDownloads.Values.Any(d =>
            string.Equals(d.TempFilePath, tempFilePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets all currently active temp file paths.
    /// </summary>
    public static IReadOnlyCollection<string> GetActiveTempFiles()
    {
        return ActiveDownloads.Values
            .Select(d => d.TempFilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
    }

    /// <summary>
    /// Removes stale download entries (downloads that have been running too long).
    /// Returns the count of removed entries.
    /// </summary>
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
    /// Gets the current count of active downloads.
    /// </summary>
    public static int Count => ActiveDownloads.Count;

    /// <summary>
    /// Information about an active download.
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

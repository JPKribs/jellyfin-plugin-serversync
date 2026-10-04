using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for checking disk space availability.
/// </summary>
public static class DiskSpaceService
{
    private const long BytesPerGigabyte = 1024L * 1024L * 1024L;

    // Paths already reported as unmeasurable, so a share that can never be measured logs one warning
    // instead of one per item on every run.
    private static readonly ConcurrentDictionary<string, byte> _unmeasurableReported = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the logger used to report a path whose free space cannot be read. The plugin sets
    /// it at startup. This service is static and called from places with no logger of their own.
    /// </summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Converts gigabytes to bytes.
    /// </summary>
    private static long GigabytesToBytes(int gigabytes) => gigabytes * BytesPerGigabyte;

    /// <summary>
    /// Gets disk space information for all enabled library paths. A path whose free space cannot be
    /// read is listed with zero sizes and counted as sufficient, since an unknown reading must not
    /// stop downloads.
    /// </summary>
    /// <param name="config">Plugin configuration containing library mappings.</param>
    /// <returns>List of disk space info for each library path.</returns>
    public static List<DiskSpaceInfo> GetDiskSpaceInfo(PluginConfiguration config)
    {
        var requiredBytes = GigabytesToBytes(config.MinimumFreeDiskSpaceGb);
        var results = new List<DiskSpaceInfo>();

        foreach (var mapping in config.GetEnabledLibraryMappings().Where(m => !string.IsNullOrEmpty(m.LocalRootPath)))
        {
            if (TryGetSpace(mapping.LocalRootPath, out var freeBytes, out var totalBytes))
            {
                results.Add(new DiskSpaceInfo
                {
                    Path = mapping.LocalRootPath,
                    FreeBytes = freeBytes,
                    TotalBytes = totalBytes,
                    RequiredBytes = requiredBytes,
                    IsSufficient = freeBytes >= requiredBytes
                });
            }
            else
            {
                results.Add(new DiskSpaceInfo
                {
                    Path = mapping.LocalRootPath,
                    FreeBytes = 0,
                    TotalBytes = 0,
                    RequiredBytes = requiredBytes,
                    IsSufficient = true
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Gets the minimum disk space info across all configured library paths.
    /// </summary>
    /// <param name="config">Plugin configuration containing library mappings.</param>
    /// <returns>Disk space info for the path with least free space, or null if none configured.</returns>
    public static DiskSpaceInfo? GetMinimumDiskSpaceInfo(PluginConfiguration config)
    {
        // An unmeasured path reports zero free bytes, which would always win the comparison and show
        // the dashboard a full disk. Prefer the paths that were actually measured.
        var allInfo = GetDiskSpaceInfo(config);
        return allInfo.Where(i => i.TotalBytes > 0).MinBy(i => i.FreeBytes) ?? allInfo.FirstOrDefault();
    }

    /// <summary>
    /// Checks if there is sufficient disk space across all library paths. A path whose free space
    /// cannot be read does not fail the check.
    /// </summary>
    /// <param name="config">Plugin configuration containing library mappings.</param>
    /// <param name="insufficientPath">Output parameter containing the path with insufficient space.</param>
    /// <returns>True if all paths have sufficient space.</returns>
    public static bool HasSufficientSpace(PluginConfiguration config, out string? insufficientPath)
    {
        insufficientPath = null;
        var requiredBytes = GigabytesToBytes(config.MinimumFreeDiskSpaceGb);

        foreach (var mapping in config.GetEnabledLibraryMappings().Where(m => !string.IsNullOrEmpty(m.LocalRootPath)))
        {
            if (TryGetSpace(mapping.LocalRootPath, out var freeBytes, out _) && freeBytes < requiredBytes)
            {
                insufficientPath = mapping.LocalRootPath;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if there is sufficient disk space for a specific file. A target whose free space cannot
    /// be read passes, so an unmeasurable share does not stop every download.
    /// </summary>
    /// <param name="filePath">Target file path.</param>
    /// <param name="fileSize">Size of the file in bytes.</param>
    /// <param name="minimumReserveGb">Minimum free space to reserve in GB.</param>
    /// <returns>True if there is sufficient space for the file plus reserve.</returns>
    public static bool HasSufficientSpaceForFile(string? filePath, long fileSize, int minimumReserveGb)
    {
        if (string.IsNullOrEmpty(filePath) || fileSize <= 0)
        {
            return true;
        }

        if (!TryGetSpace(filePath, out var freeBytes, out _))
        {
            return true;
        }

        return freeBytes >= fileSize + GigabytesToBytes(minimumReserveGb);
    }

    /// <summary>
    /// Reads the free and total space of the filesystem that holds a path. On Linux and macOS the
    /// path root is always "/", so measuring the root would read the container's own disk in Docker
    /// instead of the mounted library. The nearest existing directory of the path is measured
    /// instead, and the system resolves the mount that holds it. Windows measures the drive root,
    /// and a UNC path it cannot measure counts as unknown.
    /// </summary>
    /// <param name="path">A library root or a target file path.</param>
    /// <param name="freeBytes">The free space available to this process.</param>
    /// <param name="totalBytes">The filesystem size.</param>
    /// <returns>True when the space was read, false when it is unknown.</returns>
    private static bool TryGetSpace(string path, out long freeBytes, out long totalBytes)
    {
        freeBytes = 0;
        totalBytes = 0;
        string? target = null;
        try
        {
            target = ResolveMeasurablePath(path);
            if (string.IsNullOrEmpty(target))
            {
                ReportUnmeasurable(path, null);
                return false;
            }

            var drive = new DriveInfo(target);
            freeBytes = drive.AvailableFreeSpace;
            totalBytes = drive.TotalSize;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Report by the folder or root that was measured, so every file on one unreadable share
            // shares a single warning.
            ReportUnmeasurable(string.IsNullOrEmpty(target) ? path : target, ex);
            return false;
        }
    }

    private static string? ResolveMeasurablePath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.GetPathRoot(path);
        }

        // The path may name a file or a folder that does not exist yet, so walk up to the first
        // folder that does. That folder sits on the same mount the new file will land on.
        var directory = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory;
    }

    private static void ReportUnmeasurable(string path, Exception? ex)
    {
        if (_unmeasurableReported.TryAdd(path, 0))
        {
            Logger.LogWarning(ex, "Could not read the free space for {Path}, so downloads there are not held back by the free space check", path);
        }
    }

    /// <summary>
    /// Formats a disk space check failure message.
    /// </summary>
    /// <param name="path">Path that failed the check.</param>
    /// <param name="availableBytes">Available bytes on the drive.</param>
    /// <param name="minimumReserveGb">Required minimum GB.</param>
    /// <returns>Formatted error message.</returns>
    public static string FormatInsufficientSpaceMessage(string path, long availableBytes, int minimumReserveGb)
    {
        return $"Insufficient disk space on {path}: {FormatUtilities.FormatBytes(availableBytes)} free, {minimumReserveGb} GB required";
    }
}

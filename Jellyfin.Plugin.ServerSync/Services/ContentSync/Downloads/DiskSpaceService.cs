using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for checking disk space availability.
/// </summary>
public static class DiskSpaceService
{
    private const long BytesPerGigabyte = 1024L * 1024L * 1024L;

    /// <summary>
    /// Converts gigabytes to bytes.
    /// </summary>
    private static long GigabytesToBytes(int gigabytes) => gigabytes * BytesPerGigabyte;

    /// <summary>
    /// Gets disk space information for all enabled library paths.
    /// </summary>
    /// <param name="config">Plugin configuration containing library mappings.</param>
    /// <returns>List of disk space info for each unique drive.</returns>
    public static List<DiskSpaceInfo> GetDiskSpaceInfo(PluginConfiguration config)
    {
        var requiredBytes = GigabytesToBytes(config.MinimumFreeDiskSpaceGb);
        var results = new List<DiskSpaceInfo>();

        foreach (var mapping in config.LibraryMappings.Where(m => m.IsEnabled && !string.IsNullOrEmpty(m.LocalRootPath)))
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(mapping.LocalRootPath) ?? mapping.LocalRootPath);
                results.Add(new DiskSpaceInfo
                {
                    Path = mapping.LocalRootPath,
                    FreeBytes = driveInfo.AvailableFreeSpace,
                    TotalBytes = driveInfo.TotalSize,
                    RequiredBytes = requiredBytes,
                    IsSufficient = driveInfo.AvailableFreeSpace >= requiredBytes
                });
            }
            catch (IOException)
            {
                results.Add(new DiskSpaceInfo
                {
                    Path = mapping.LocalRootPath,
                    FreeBytes = 0,
                    TotalBytes = 0,
                    RequiredBytes = requiredBytes,
                    IsSufficient = false
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
        var allInfo = GetDiskSpaceInfo(config);
        return allInfo.MinBy(i => i.FreeBytes);
    }

    /// <summary>
    /// Checks if there is sufficient disk space across all library paths.
    /// </summary>
    /// <param name="config">Plugin configuration containing library mappings.</param>
    /// <param name="insufficientPath">Output parameter containing the path with insufficient space.</param>
    /// <returns>True if all paths have sufficient space.</returns>
    public static bool HasSufficientSpace(PluginConfiguration config, out string? insufficientPath)
    {
        insufficientPath = null;
        var requiredBytes = GigabytesToBytes(config.MinimumFreeDiskSpaceGb);

        foreach (var mapping in config.LibraryMappings.Where(m => m.IsEnabled && !string.IsNullOrEmpty(m.LocalRootPath)))
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(mapping.LocalRootPath) ?? mapping.LocalRootPath);
                if (driveInfo.AvailableFreeSpace < requiredBytes)
                {
                    insufficientPath = mapping.LocalRootPath;
                    return false;
                }
            }
            catch (IOException)
            {
                // Cannot determine disk space — treat as insufficient to prevent filling the disk
                insufficientPath = mapping.LocalRootPath;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if there is sufficient disk space for a specific file.
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

        try
        {
            var pathRoot = Path.GetPathRoot(filePath);
            if (string.IsNullOrEmpty(pathRoot))
            {
                return true;
            }

            var driveInfo = new DriveInfo(pathRoot);
            var requiredBytes = GigabytesToBytes(minimumReserveGb);

            return driveInfo.AvailableFreeSpace >= fileSize + requiredBytes;
        }
        catch (IOException)
        {
            // Cannot determine disk space — assume insufficient to prevent filling the disk
            return false;
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

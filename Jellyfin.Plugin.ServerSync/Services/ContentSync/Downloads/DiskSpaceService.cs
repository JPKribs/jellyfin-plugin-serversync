using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;

/// <summary>
/// Checks free disk space and holds space for downloads in progress.
/// </summary>
public static class DiskSpaceService
{

    /// <summary>
    /// Paths already reported as unmeasurable, so each logs one warning instead of one per item.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> _unmeasurableReported = new(StringComparer.Ordinal);

    /// <summary>
    /// Bytes held per volume by downloads in progress, so large files started together cannot each pass the check and fill the disk.
    /// </summary>
    private static readonly Dictionary<string, long> _held = new(StringComparer.Ordinal);
    private static readonly object _heldLock = new();

    /// <summary>
    /// Completes when a hold is released, which wakes the downloads waiting for space.
    /// </summary>
    private static TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets or sets the logger that reports a path whose free space cannot be read, which the plugin sets at startup.
    /// </summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Converts gigabytes to bytes.
    /// </summary>
    private static long GigabytesToBytes(int gigabytes) => gigabytes * ByteSize.Gigabyte;

    /// <summary>
    /// Gets the disk space of every enabled library mapping's root folder.
    /// A root folder whose free space cannot be read is listed with zero sizes and counted as sufficient, since an unknown reading must not stop downloads.
    /// </summary>
    /// <param name="config">Supplies the library mappings and the minimum free space.</param>
    /// <returns>One entry for each root folder.</returns>
    public static List<DiskSpaceInfo> GetDiskSpaceInfo(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

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
    /// Checks that every enabled root folder has the minimum free space, passing any whose free space cannot be read.
    /// </summary>
    /// <param name="config">Supplies the library mappings and the minimum free space.</param>
    /// <param name="insufficientPath">The first root folder short of space, or null when all pass.</param>
    /// <returns><c>true</c> when every root folder has enough space.</returns>
    public static bool HasSufficientSpace(PluginConfiguration config, out string? insufficientPath)
    {
        ArgumentNullException.ThrowIfNull(config);

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
    /// Checks that a file fits on its volume with the reserve left over.
    /// A target whose free space cannot be read passes, so an unmeasurable share does not stop every download.
    /// </summary>
    /// <param name="filePath">Where the file will be written.</param>
    /// <param name="fileSize">The file's size in bytes.</param>
    /// <param name="minimumReserveGb">The free space in GB that must remain after the file.</param>
    /// <returns><c>true</c> when the file and the reserve fit.</returns>
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
    /// Holds space for a file about to download, on top of what other downloads hold on the same volume.
    /// A file that fits only once those finish waits for them, and a target whose free space cannot be read passes.
    /// </summary>
    /// <param name="filePath">Where the file will be written.</param>
    /// <param name="fileSize">The file's size in bytes.</param>
    /// <param name="minimumReserveGb">The free space in GB that must remain after the file.</param>
    /// <param name="cancellationToken">Cancels the wait for other downloads to finish.</param>
    /// <returns>The hold, which the caller disposes when the download ends, or null when the file does not fit.</returns>
    public static async Task<IDisposable?> ReserveForFileAsync(string? filePath, long fileSize, int minimumReserveGb, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(filePath) || fileSize <= 0)
        {
            return NoHold.Instance;
        }

        var needed = fileSize + GigabytesToBytes(minimumReserveGb);
        while (true)
        {
            if (!TryGetSpace(filePath, out var freeBytes, out var totalBytes, out var format))
            {
                return NoHold.Instance;
            }

            Task released;
            var volume = totalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + format;
            lock (_heldLock)
            {
                var held = _held.GetValueOrDefault(volume);
                if (freeBytes - held >= needed)
                {
                    _held[volume] = held + fileSize;
                    return new Hold(volume, fileSize);
                }

                if (held == 0 || freeBytes < needed)
                {
                    return null;
                }

                released = _released.Task;
            }

            await released.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the free space on the volume that holds a path.</summary>
    /// <param name="path">A file or folder on the volume.</param>
    /// <param name="freeBytes">The bytes free on that volume, or zero when unknown.</param>
    /// <returns><c>true</c> when the space was read, <c>false</c> when it is unknown.</returns>
    public static bool TryGetFreeBytes(string path, out long freeBytes) => TryGetSpace(path, out freeBytes, out _);

    /// <summary>Gets the bytes held on all volumes, for tests.</summary>
    public static long HeldBytes
    {
        get
        {
            lock (_heldLock)
            {
                return _held.Values.Sum();
            }
        }
    }

    /// <summary>
    /// Returns held bytes to a volume and wakes the downloads waiting for space.
    /// </summary>
    private static void Release(string volume, long bytes)
    {
        TaskCompletionSource released;
        lock (_heldLock)
        {
            var left = _held.GetValueOrDefault(volume) - bytes;
            if (left > 0)
            {
                _held[volume] = left;
            }
            else
            {
                _held.Remove(volume);
            }

            released = _released;
            _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        released.TrySetResult();
    }

    /// <summary>
    /// Space held on one volume until the download ends.
    /// </summary>
    private sealed class Hold : IDisposable
    {
        private readonly string _volume;
        private readonly long _bytes;
        private int _disposed;

        public Hold(string volume, long bytes)
        {
            _volume = volume;
            _bytes = bytes;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release(_volume, _bytes);
            }
        }
    }

    /// <summary>
    /// The hold given when there is nothing to hold or the volume cannot be measured.
    /// </summary>
    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Reads the free and total space of the filesystem that holds a path.
    /// </summary>
    /// <param name="path">A root folder or a target file path.</param>
    /// <param name="freeBytes">The free space available to this process.</param>
    /// <param name="totalBytes">The filesystem size.</param>
    /// <returns><c>true</c> when the space was read, <c>false</c> when it is unknown.</returns>
    private static bool TryGetSpace(string path, out long freeBytes, out long totalBytes)
        => TryGetSpace(path, out freeBytes, out totalBytes, out _);

    /// <summary>
    /// Reads the free space, total size, and format of the filesystem that holds a path.
    /// The format and total size together tell volumes apart, since on Linux and macOS DriveInfo names the path it was given.
    /// </summary>
    private static bool TryGetSpace(string path, out long freeBytes, out long totalBytes, out string format)
    {
        freeBytes = 0;
        totalBytes = 0;
        format = string.Empty;
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
            format = drive.DriveFormat;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            ReportUnmeasurable(string.IsNullOrEmpty(target) ? path : target, ex);
            return false;
        }
    }

    /// <summary>
    /// Finds what to measure for a path, the drive root on Windows and the nearest existing folder elsewhere.
    /// On Linux and macOS the path root is always "/", which in Docker is the container's disk and not the mounted library.
    /// </summary>
    private static string? ResolveMeasurablePath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.GetPathRoot(path);
        }

        var directory = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory;
    }

    /// <summary>
    /// Logs one warning for each folder or root whose free space cannot be read.
    /// </summary>
    private static void ReportUnmeasurable(string path, Exception? ex)
    {
        if (_unmeasurableReported.TryAdd(path, 0))
        {
            Logger.LogWarning(ex, "Could not read the free space for {Path}, so downloads there are not held back by the free space check", FormatUtilities.SanitizeForLog(path));
        }
    }

    /// <summary>
    /// Builds the message for a path that failed the free space check.
    /// </summary>
    /// <param name="path">The path that is short of space.</param>
    /// <param name="availableBytes">The bytes free on its volume.</param>
    /// <param name="minimumReserveGb">The free space in GB the settings require.</param>
    /// <returns>The message shown for the failed check.</returns>
    public static string FormatInsufficientSpaceMessage(string path, long availableBytes, int minimumReserveGb)
    {
        return $"Insufficient disk space on {path}: {FormatUtilities.FormatBytes(availableBytes)} free, {minimumReserveGb} GB required";
    }
}

using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for validating file operations before execution.
/// </summary>
public static class FileValidationService
{
    /// <summary>
    /// Returns true if a local file's byte count is close enough to the recorded source size
    /// to be considered the same file. Use this instead of strict equality whenever the
    /// "source size" comes from Jellyfin metadata rather than an HTTP Content-Length.
    /// </summary>
    /// <param name="localSize">Actual size of the local file in bytes.</param>
    /// <param name="sourceSize">Recorded source size in bytes.</param>
    /// <param name="toleranceBytes">Tolerance in bytes (0 = strict equality required).</param>
    /// <returns>True if sizes are equal or within the drift tolerance.</returns>
    public static bool IsSizeWithinDriftTolerance(long localSize, long sourceSize, long toleranceBytes)
    {
        if (sourceSize <= 0)
        {
            return true;
        }

        return Math.Abs(localSize - sourceSize) <= Math.Max(0, toleranceBytes);
    }

    /// <summary>
    /// Validates a path for download operations.
    /// Checks for empty path, path traversal, library boundaries, and collisions.
    /// </summary>
    /// <param name="localPath">The local path to validate.</param>
    /// <param name="sourceItemId">Source item ID for collision checking.</param>
    /// <param name="config">Plugin configuration for library boundaries.</param>
    /// <param name="manager">Sync table manager for collision checking.</param>
    /// <returns>Validation result indicating if path is valid.</returns>
    public static PathValidationResult ValidatePath(
        string? localPath,
        string sourceItemId,
        PluginConfiguration config,
        ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (string.IsNullOrEmpty(localPath))
        {
            return new PathValidationResult(false, "No local path configured");
        }

        // Normalize and verify the path is within an allowed library boundary
        var normalizedPath = Path.GetFullPath(localPath);
        if (!IsPathWithinLibrary(normalizedPath, config))
        {
            return new PathValidationResult(false, "Path traversal detected - path escapes library boundaries");
        }

        // Check for path collision
        if (manager.CheckPathCollision(localPath, sourceItemId))
        {
            return new PathValidationResult(false, "Path collision - another item is already using this path");
        }

        return new PathValidationResult(true, null);
    }

    /// <summary>
    /// Validates that a path is within one of the configured library paths.
    /// Uses <see cref="Path.GetRelativePath"/> to ensure proper path boundary checking
    /// (prevents "/media/videos_evil" from matching library root "/media/videos").
    /// </summary>
    /// <param name="path">Path to validate.</param>
    /// <param name="config">Plugin configuration containing library mappings.</param>
    /// <returns>True if path is within a configured library.</returns>
    public static bool IsPathWithinLibrary(string? path, PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path);

        foreach (var mapping in config.LibraryMappings.Where(m => m.IsEnabled && !string.IsNullOrEmpty(m.LocalRootPath)))
        {
            var normalizedLibraryPath = Path.GetFullPath(mapping.LocalRootPath);

            // Use GetRelativePath to safely check containment.
            // If the path is within the library, the relative path will NOT start with ".."
            // This correctly handles "/media/videos_evil" vs "/media/videos" —
            // GetRelativePath("/media/videos", "/media/videos_evil") => "../videos_evil" (starts with "..")
            // GetRelativePath("/media/videos", "/media/videos/movie.mkv") => "movie.mkv" (no "..")
            var relativePath = Path.GetRelativePath(normalizedLibraryPath, normalizedPath);
            if (!relativePath.StartsWith("..", StringComparison.Ordinal)
                && !Path.IsPathRooted(relativePath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a download should be skipped because the local file already exists and is valid.
    /// </summary>
    /// <param name="item">Sync item to check.</param>
    /// <param name="sizeMatchToleranceBytes">Tolerance in bytes for size comparison (0 = strict).</param>
    /// <param name="skipReason">Output parameter with reason for skipping.</param>
    /// <returns>True if download should be skipped.</returns>
    public static bool ShouldSkipDownload(SyncItem item, long sizeMatchToleranceBytes, out string? skipReason)
    {
        skipReason = null;

        if (string.IsNullOrEmpty(item.LocalPath))
        {
            return false;
        }

        if (!File.Exists(item.LocalPath))
        {
            return false;
        }

        try
        {
            var localInfo = new FileInfo(item.LocalPath);

            if (IsSizeWithinDriftTolerance(localInfo.Length, item.SourceSize, sizeMatchToleranceBytes))
            {
                skipReason = item.SourceSize == 0
                    ? $"Local file already exists (source size unknown, local size: {FormatUtilities.FormatBytes(localInfo.Length)})"
                    : $"Local file already exists with matching size ({FormatUtilities.FormatBytes(localInfo.Length)})";
                return true;
            }

            // Size differs beyond drift tolerance - needs re-download
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if write permission exists for a file's target directory.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    /// <param name="filePath">Target file path.</param>
    /// <returns>True if write permission exists.</returns>
    public static bool HasWritePermission(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        return FileOperationUtilities.HasWritePermission(directory);
    }
}

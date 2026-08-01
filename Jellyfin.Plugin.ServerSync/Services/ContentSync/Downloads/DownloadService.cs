using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for downloading files from the source server.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class DownloadService
{
    private readonly ILogger<DownloadService> _logger;

    /// <summary>
    /// Default retry count for network operations.
    /// </summary>
    private const int DefaultNetworkRetries = 3;

    public DownloadService(ILogger<DownloadService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Downloads a single item and its companion files with retry support.
    /// </summary>
    /// <param name="client">Source server client.</param>
    /// <param name="item">Sync item to download.</param>
    /// <param name="tempPath">Temporary download path.</param>
    /// <param name="speedLimitBytesPerSecond">Download speed limit in bytes per second.</param>
    /// <param name="includeCompanionFiles">Whether to download companion files.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="progress">Optional per-item progress (fraction 0–1 of the main file's bytes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Download result.</returns>
    public async Task<DownloadResult> DownloadItemAsync(
        SourceServerClient client,
        SyncItem item,
        string tempPath,
        long speedLimitBytesPerSecond,
        bool includeCompanionFiles,
        PluginConfiguration config,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(item.LocalPath))
        {
            return new DownloadResult(false, "No local path configured");
        }

        var tempFileName = FileNameSanitizer.SanitizeTempFileName(item.SourceItemId, item.LocalPath);
        var tempFilePath = Path.Combine(tempPath, tempFileName);
        var itemId = Guid.Parse(item.SourceItemId);
        var fileName = Path.GetFileName(item.LocalPath);
        var networkRetries = config.MaxRetryCount > 0 ? config.MaxRetryCount : DefaultNetworkRetries;

        try
        {
            // Download with retry support
            await RetryPolicy.ExecuteWithRetryAsync(
                async ct =>
                {
                    var download = await client.DownloadFileAsync(itemId, ct).ConfigureAwait(false);

                    if (download == null)
                    {
                        throw new InvalidOperationException("No response from server");
                    }

                    var (sourceStream, contentLength) = download.Value;
                    using (sourceStream)
                    {
                        // Ensure we don't have a partial file from previous attempt
                        CleanupTempFile(tempFilePath);

                        using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            // Content-Length is authoritative; the recorded
                            // SourceSize is the fallback (can be stale but is
                            // the right order of magnitude). No basis → no
                            // in-file reporting; the item still completes its
                            // share when it finishes.
                            var expectedBytes = contentLength ?? (item.SourceSize > 0 ? item.SourceSize : (long?)null);
                            Stream destination = progress != null && expectedBytes.HasValue
                                ? new ProgressReportingStream(fileStream, expectedBytes.Value, progress)
                                : fileStream;
                            await StreamUtilities.CopyWithSpeedLimitAsync(sourceStream, destination, speedLimitBytesPerSecond, ct).ConfigureAwait(false);
                        }
                    }

                    var downloadedInfo = new FileInfo(tempFilePath);
                    if (contentLength.HasValue && downloadedInfo.Length != contentLength.Value)
                    {
                        var errorMsg = $"Truncated download: expected {contentLength.Value} bytes, got {downloadedInfo.Length} bytes";
                        CleanupTempFile(tempFilePath);
                        throw new InvalidOperationException(errorMsg);
                    }

                    if (item.SourceSize > 0 && downloadedInfo.Length != item.SourceSize)
                    {
                        _logger.LogDebug(
                            "Downloaded {FileName}: actual {Actual} bytes differs from recorded SourceSize {Expected} bytes (likely stale source metadata)",
                            fileName,
                            downloadedInfo.Length,
                            item.SourceSize);
                    }
                },
                networkRetries,
                _logger,
                $"Download {fileName}",
                cancellationToken).ConfigureAwait(false);

            var targetDir = Path.GetDirectoryName(item.LocalPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Two-phase replacement so a rename failure does not leave the user
            // with the previous version stuck in the recycling bin and no working
            // file at the target path:
            //   1. Move existing file (and its companions) to *.replacing.<guid>
            //      sidecars on the same volume — atomic, so rolling back is fast.
            //   2. Atomically rename the just-downloaded temp file into place.
            //   3. Only after step 2 succeeds, archive sidecars to the recycling
            //      bin (or delete them).
            // If step 2 throws, the catch block restores the sidecars so the
            // user's previous version stays intact at item.LocalPath.
            string? backupMain = null;
            var backupCompanions = new List<(string Original, string Backup)>();
            var existingFile = File.Exists(item.LocalPath);

            try
            {
                if (existingFile)
                {
                    backupMain = item.LocalPath + ".replacing." + Guid.NewGuid().ToString("N");
                    File.Move(item.LocalPath, backupMain);

                    foreach (var companionPath in FileOperationUtilities.GetCompanionFiles(item.LocalPath))
                    {
                        try
                        {
                            var companionBackup = companionPath + ".replacing." + Guid.NewGuid().ToString("N");
                            File.Move(companionPath, companionBackup);
                            backupCompanions.Add((companionPath, companionBackup));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to move existing companion aside: {CompanionPath}", companionPath);
                        }
                    }
                }

                await FileOperationUtilities.MoveFileWithOverwriteAsync(tempFilePath, item.LocalPath, _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Roll back so the user keeps their previous version.
                try
                {
                    if (backupMain != null && File.Exists(backupMain) && !File.Exists(item.LocalPath))
                    {
                        // No need to clear backupMain: this block always
                        // rethrows, so the archive step below is unreachable.
                        File.Move(backupMain, item.LocalPath);
                    }
                }
                catch (Exception restoreEx)
                {
                    _logger.LogError(restoreEx, "Failed to restore previous file from sidecar to {Path}", item.LocalPath);
                }

                foreach (var (orig, bk) in backupCompanions)
                {
                    try
                    {
                        if (File.Exists(bk) && !File.Exists(orig))
                        {
                            File.Move(bk, orig);
                        }
                    }
                    catch (Exception restoreEx)
                    {
                        _logger.LogError(restoreEx, "Failed to restore companion from sidecar to {Path}", orig);
                    }
                }

                throw;
            }

            // Rename succeeded — archive or delete the sidecars.
            ArchiveOrDeleteSidecar(backupMain, item.LocalPath, config);
            foreach (var (orig, bk) in backupCompanions)
            {
                ArchiveOrDeleteSidecar(bk, orig, config);
            }

            string? companionFilesList = null;
            if (includeCompanionFiles)
            {
                var downloadedCompanions = await DownloadCompanionFilesAsync(
                    client,
                    itemId,
                    targetDir ?? Path.GetDirectoryName(item.LocalPath) ?? string.Empty,
                    tempPath,
                    speedLimitBytesPerSecond,
                    config,
                    cancellationToken).ConfigureAwait(false);

                if (downloadedCompanions.Count > 0)
                {
                    companionFilesList = string.Join(",", downloadedCompanions);
                }
            }

            return new DownloadResult(true, null, companionFilesList);
        }
        catch (OperationCanceledException)
        {
            CleanupTempFile(tempFilePath);
            throw;
        }
        catch (Exception ex)
        {
            CleanupTempFile(tempFilePath);
            return new DownloadResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Downloads external companion files (subtitles, etc.) for an item with retry support.
    /// </summary>
    /// <param name="client">Source server client.</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="targetDir">Target directory for companion files.</param>
    /// <param name="tempPath">Temporary download path.</param>
    /// <param name="speedLimitBytesPerSecond">Download speed limit in bytes per second.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of successfully downloaded companion file names.</returns>
    public async Task<List<string>> DownloadCompanionFilesAsync(
        SourceServerClient client,
        Guid itemId,
        string targetDir,
        string tempPath,
        long speedLimitBytesPerSecond,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var downloadedFiles = new List<string>();
        var networkRetries = config.MaxRetryCount > 0 ? config.MaxRetryCount : DefaultNetworkRetries;

        try
        {
            var companions = await client.GetCompanionFilesAsync(itemId, cancellationToken).ConfigureAwait(false);

            if (companions.Count == 0)
            {
                return downloadedFiles;
            }

            _logger.LogDebug("Downloading {Count} companion files for item {ItemId}", companions.Count, itemId);

            foreach (var companion in companions)
            {
                // Validate companion file name to prevent path traversal from remote server
                var companionFileName = Path.GetFileName(companion.FileName);
                if (string.IsNullOrEmpty(companionFileName) || companionFileName != companion.FileName)
                {
                    _logger.LogWarning("Skipping companion file with suspicious name: {FileName}", companion.FileName);
                    continue;
                }

                var tempFileName = FileNameSanitizer.SanitizeTempFileName(itemId.ToString(), companionFileName);
                var tempFilePath = Path.Combine(tempPath, tempFileName);
                var targetPath = Path.Combine(targetDir, companionFileName);

                // Verify the resolved target path is still within the target directory
                var resolvedTargetPath = Path.GetFullPath(targetPath);
                var resolvedTargetDir = Path.GetFullPath(targetDir);
                if (!resolvedTargetPath.StartsWith(resolvedTargetDir, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Companion file path traversal blocked: {FileName} resolved to {Resolved}", companion.FileName, resolvedTargetPath);
                    continue;
                }

                try
                {
                    await RetryPolicy.ExecuteWithRetryAsync(
                        async ct =>
                        {
                            using var stream = await client.DownloadCompanionFileAsync(itemId, companion, ct).ConfigureAwait(false);

                            if (stream == null)
                            {
                                throw new InvalidOperationException($"No response for companion file {companion.FileName}");
                            }

                            // Clean up any partial file from previous attempt
                            CleanupTempFile(tempFilePath);

                            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await StreamUtilities.CopyWithSpeedLimitAsync(stream, fileStream, speedLimitBytesPerSecond, ct).ConfigureAwait(false);
                            }
                        },
                        networkRetries,
                        _logger,
                        $"Download companion {companion.FileName}",
                        cancellationToken).ConfigureAwait(false);

                    await FileOperationUtilities.MoveFileWithOverwriteAsync(tempFilePath, targetPath, _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
                    downloadedFiles.Add(companion.FileName);
                    _logger.LogDebug("Downloaded companion file {FileName}", companion.FileName);
                }
                catch (OperationCanceledException)
                {
                    CleanupTempFile(tempFilePath);
                    throw;
                }
                catch (Exception ex)
                {
                    CleanupTempFile(tempFilePath);
                    _logger.LogWarning(ex, "Failed to download companion file {FileName} after retries", companion.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get companion files for item {ItemId}", itemId);
        }

        return downloadedFiles;
    }

    /// <summary>
    /// Validates an item before downloading.
    /// </summary>
    /// <param name="item">Sync item to validate.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="manager">Sync table manager for collision checking.</param>
    /// <returns>Validation result with error message if invalid.</returns>
    public static (bool IsValid, string? ErrorMessage) ValidateForDownload(
        SyncItem item,
        PluginConfiguration config,
        ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(item);
        // Validate path
        var pathValidation = FileValidationService.ValidatePath(item.LocalPath, item.SourceItemId, config, manager);
        if (!pathValidation.IsValid)
        {
            return (false, pathValidation.ErrorMessage);
        }

        // Check disk space
        if (!DiskSpaceService.HasSufficientSpaceForFile(item.LocalPath, item.SourceSize, config.MinimumFreeDiskSpaceGb))
        {
            return (false, "Insufficient disk space");
        }

        // Check write permissions
        if (!FileValidationService.HasWritePermission(item.LocalPath))
        {
            return (false, "No write permission to target directory");
        }

        return (true, null);
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
        return FileValidationService.ShouldSkipDownload(item, sizeMatchToleranceBytes, out skipReason);
    }

    /// <summary>
    /// Cleans up a temporary file, ignoring any errors.
    /// </summary>
    private static void CleanupTempFile(string tempFilePath)
    {
        if (File.Exists(tempFilePath))
        {
            try
            {
                File.Delete(tempFilePath);
            }
            catch (IOException)
            {
                // Ignore cleanup errors - temp file will be cleaned up by OS
            }
            catch (UnauthorizedAccessException)
            {
                // File is read-only or ACL denies; leave for OS cleanup.
            }
        }
    }

    /// <summary>
    /// Archives a sidecar (the file we kept aside while replacing) to the
    /// recycling bin under the original file's name, or deletes it outright if
    /// the recycling bin is disabled. Quiet on missing sidecars (caller may
    /// have already cleaned up via rollback).
    /// </summary>
    private void ArchiveOrDeleteSidecar(string? sidecarPath, string originalDisplayPath, PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(sidecarPath) || !File.Exists(sidecarPath))
        {
            return;
        }

        if (config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath))
        {
            RecyclingBinService.ArchiveBackupToRecyclingBin(sidecarPath, originalDisplayPath, config.RecyclingBinPath, _logger);
            return;
        }

        try
        {
            File.Delete(sidecarPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete sidecar {Path}", sidecarPath);
        }
    }
}

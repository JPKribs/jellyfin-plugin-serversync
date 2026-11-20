using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;

/// <summary>
/// Downloads files from a source and puts them in place on this server.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class DownloadService
{
    private readonly ILogger<DownloadService> _logger;

    /// <summary>
    /// How many times a download is retried when the settings give no count.
    /// </summary>
    private const int DefaultNetworkRetries = 3;

    /// <summary>
    /// How long a download may go without receiving a byte before it counts as stalled.
    /// </summary>
    public static readonly TimeSpan DefaultStallTimeout = StreamUtilities.DefaultIdleTimeout;

    /// <summary>
    /// The size of the buffer a copy reads in, which the stall window has to outlast at the speed limit.
    /// </summary>
    private const int CopyBufferBytes = 81920;

    /// <summary>
    /// The most downloads that can share the speed at once, which is the top of Downloads at once.
    /// </summary>
    private const int MaxSharers = 10;

    /// <summary>The one speed every Content download draws from, so the limit is the total across Downloads at once.</summary>
    private static readonly SpeedBucket SharedSpeed = new(1, StreamCaps.BurstSeconds);

    /// <summary>
    /// The most bytes a subtitle from the source may hold, well above the tens of megabytes an image based subtitle runs to.
    /// </summary>
    public const long MaxCompanionBytes = 256L * 1024 * 1024;

    /// <summary>
    /// The fixed part of how far past its recorded size a body with no stated length may run, to allow for stale source metadata.
    /// </summary>
    private const long UnstatedLengthSlackBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Marks a download staged beside its target while it waits to be renamed into place.
    /// </summary>
    private const string StagedMarker = ".syncing.";

    /// <summary>
    /// The external subtitle formats Jellyfin reads beside a video.
    /// A companion from the source is only written under one of these, so a source cannot name a video, an .nfo, or a .ignore file.
    /// </summary>
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".smi", ".sami", ".sup", ".pgs", ".mks", ".ttml", ".dfxp", ".lrc"
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadService"/> class.
    /// </summary>
    /// <param name="logger">Records resumed downloads, skipped companions, and failed file moves.</param>
    public DownloadService(ILogger<DownloadService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <summary>
    /// Gets or sets how long a download may receive nothing before it is abandoned as stalled and retried.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = DefaultStallTimeout;

    /// <summary>
    /// Downloads one item and its companion files, resuming a partial file across retries.
    /// A file already in place is set aside first and restored when the new file cannot be put in place, so the previous version is never lost.
    /// </summary>
    /// <param name="client">The source the file is downloaded from.</param>
    /// <param name="item">The row that names the source item, its recorded size, and the local path.</param>
    /// <param name="tempPath">The folder the download is written to before it moves into place.</param>
    /// <param name="speedLimitBytesPerSecond">The speed limit used when the settings give none, where 0 is no limit.</param>
    /// <param name="includeCompanionFiles">Whether to download the item's external subtitles too.</param>
    /// <param name="config">Supplies the retry count, speed, free space, and Recycling bin settings.</param>
    /// <param name="progress">Receives the fraction of the main file downloaded, from 0 to 1, or null.</param>
    /// <param name="cancellationToken">Cancels the download and removes its temp file.</param>
    /// <returns>Whether the download succeeded, with the reason when it did not and the companions written when it did.</returns>
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
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(tempPath);
        ArgumentNullException.ThrowIfNull(config);

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
            CleanupTempFile(tempFilePath);
            string? validator = null;
            await RetryPolicy.ExecuteWithRetryAsync(
                async ct =>
                {
                    var kept = validator is null ? 0 : ExistingLength(tempFilePath);
                    var download = await client.DownloadFileFromAsync(itemId, kept, validator, ct).ConfigureAwait(false);
                    validator = download.Validator;
                    using (download.Stream)
                    {
                        // An offset of zero means the source sent the whole file again.
                        if (download.Offset == 0)
                        {
                            CleanupTempFile(tempFilePath);
                        }
                        else
                        {
                            _logger.LogInformation("Resuming {FileName} from {Offset} bytes", FormatUtilities.SanitizeForLog(fileName), download.Offset);
                        }

                        var mode = download.Offset > 0 ? FileMode.Append : FileMode.Create;
                        var most = MostBytesToAccept(download.TotalLength, download.Offset, item, tempFilePath, config);
                        using (var fileStream = OpenForDownload(tempFilePath, mode))
                        {
                            var expectedBytes = download.TotalLength ?? (item.SourceSize > 0 ? item.SourceSize : (long?)null);
                            Stream destination = progress != null && expectedBytes > 0
                                ? new ProgressReportingStream(fileStream, expectedBytes.Value, progress, download.Offset)
                                : fileStream;
                            await CopyWithStallWatchdogAsync(new CappedReadStream(download.Stream, most), destination, speedLimitBytesPerSecond, config, ct).ConfigureAwait(false);
                        }
                    }

                    var downloadedInfo = new FileInfo(tempFilePath);
                    if (download.TotalLength.HasValue && downloadedInfo.Length != download.TotalLength.Value)
                    {
                        var errorMsg = $"Truncated download: expected {download.TotalLength.Value} bytes, got {downloadedInfo.Length} bytes";
                        // Bytes past the stated length cannot belong to the file, so none are kept.
                        if (downloadedInfo.Length > download.TotalLength.Value)
                        {
                            validator = null;
                            CleanupTempFile(tempFilePath);
                        }

                        throw new IOException(errorMsg);
                    }

                    // With no stated length, a size mismatch must never replace a file already here.
                    if (!download.TotalLength.HasValue && item.SourceSize > 0 && downloadedInfo.Length != item.SourceSize
                        && !string.IsNullOrEmpty(item.LocalPath) && File.Exists(item.LocalPath))
                    {
                        validator = null;
                        CleanupTempFile(tempFilePath);
                        throw new InvalidDataException($"The source gave no length and sent {downloadedInfo.Length} bytes where {item.SourceSize} were expected, so the file already here is kept");
                    }

                    if (item.SourceSize > 0 && downloadedInfo.Length != item.SourceSize)
                    {
                        _logger.LogDebug(
                            "Downloaded {FileName}: actual {Actual} bytes differs from recorded SourceSize {Expected} bytes (likely stale source metadata)",
                            FormatUtilities.SanitizeForLog(fileName),
                            downloadedInfo.Length,
                            item.SourceSize);
                    }
                },
                networkRetries,
                _logger,
                $"Download {FormatUtilities.SanitizeForLog(fileName)}",
                cancellationToken).ConfigureAwait(false);

            var targetDir = Path.GetDirectoryName(item.LocalPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            RemoveStaleStaged(item.LocalPath);
            var stagedPath = item.LocalPath + StagedMarker + Guid.NewGuid().ToString("N");
            try
            {
                await FileOperationUtilities.MoveFileWithOverwriteAsync(tempFilePath, stagedPath, _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                CleanupTempFile(stagedPath);
                throw;
            }

            string? backupMain = null;
            var backupCompanions = new List<(string Original, string Backup)>();
            var existingFile = File.Exists(item.LocalPath);

            try
            {
                if (existingFile)
                {
                    backupMain = item.LocalPath + ".replacing." + Guid.NewGuid().ToString("N");
                    File.Move(item.LocalPath, backupMain);
                    TouchBackup(backupMain);

                    foreach (var companionPath in FileOperationUtilities.GetCompanionFiles(item.LocalPath))
                    {
                        try
                        {
                            var companionBackup = companionPath + ".replacing." + Guid.NewGuid().ToString("N");
                            File.Move(companionPath, companionBackup);
                            TouchBackup(companionBackup);
                            backupCompanions.Add((companionPath, companionBackup));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _logger.LogWarning(ex, "Failed to move existing companion aside: {CompanionPath}", FormatUtilities.SanitizeForLog(companionPath));
                        }
                    }
                }

                await FileOperationUtilities.MoveFileWithOverwriteAsync(stagedPath, item.LocalPath, _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                CleanupTempFile(stagedPath);

                try
                {
                    if (backupMain != null && File.Exists(backupMain) && !File.Exists(item.LocalPath))
                    {
                        File.Move(backupMain, item.LocalPath);
                    }
                }

                // A failed restore must not replace the error being rethrown.
                catch (Exception restoreEx)
                {
                    _logger.LogError(restoreEx, "Failed to restore previous file from sidecar to {Path}", FormatUtilities.SanitizeForLog(item.LocalPath));
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

                    // A failed restore must not replace the error being rethrown.
                    catch (Exception restoreEx)
                    {
                        _logger.LogError(restoreEx, "Failed to restore companion from sidecar to {Path}", FormatUtilities.SanitizeForLog(orig));
                    }
                }

                throw;
            }

            ArchiveOrDeleteSidecar(backupMain, item.LocalPath, config);

            string? companionFilesList = null;
            try
            {
                if (includeCompanionFiles)
                {
                    var downloadedCompanions = await DownloadCompanionFilesAsync(
                        client,
                        itemId,
                        targetDir ?? string.Empty,
                        Path.GetFileNameWithoutExtension(item.LocalPath),
                        tempPath,
                        speedLimitBytesPerSecond,
                        config,
                        cancellationToken).ConfigureAwait(false);

                    if (downloadedCompanions.Count > 0)
                    {
                        companionFilesList = string.Join(",", downloadedCompanions);
                    }
                }
            }
            finally
            {
                SettleCompanionSidecars(backupCompanions, config);
            }

            Queue.WrittenFiles.Mark(item.LocalPath);
            return new DownloadResult(true, null, companionFilesList);
        }
        catch (OperationCanceledException)
        {
            CleanupTempFile(tempFilePath);
            throw;
        }

        // One item's failure becomes its result, so it never stops the other downloads.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CleanupTempFile(tempFilePath);
            return new DownloadResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Downloads an item's external subtitles beside its video, each with retries.
    /// A name from the source is only written when it is a subtitle of this video that stays inside the target folder.
    /// </summary>
    /// <param name="client">The source the companion files are downloaded from.</param>
    /// <param name="itemId">The source item whose companion files are listed.</param>
    /// <param name="targetDir">The folder the companion files are written to.</param>
    /// <param name="mainStem">The video's file name without its extension, which every companion's name must start with.</param>
    /// <param name="tempPath">The folder each download is written to before it moves into place.</param>
    /// <param name="speedLimitBytesPerSecond">The speed limit used when the settings give none, where 0 is no limit.</param>
    /// <param name="config">Supplies the retry count and speed settings.</param>
    /// <param name="cancellationToken">Cancels the downloads and removes the temp file in progress.</param>
    /// <returns>The names of the companion files written.</returns>
    public async Task<List<string>> DownloadCompanionFilesAsync(
        SourceServerClient client,
        Guid itemId,
        string targetDir,
        string mainStem,
        string tempPath,
        long speedLimitBytesPerSecond,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);

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
                var companionFileName = CompanionNameFor(companion.FileName, mainStem);
                if (companionFileName is null)
                {
                    _logger.LogWarning(
                        "Skipping companion file {FileName}: it is not a subtitle named after {Stem}",
                        FormatUtilities.SanitizeForLog(companion.FileName ?? string.Empty, 300),
                        FormatUtilities.SanitizeForLog(mainStem));
                    continue;
                }

                var tempFileName = FileNameSanitizer.SanitizeTempFileName(itemId.ToString(), companionFileName);
                var tempFilePath = Path.Combine(tempPath, tempFileName);
                var targetPath = Path.Combine(targetDir, companionFileName);

                // A companion name from the source must not lead out of the target folder.
                var resolvedTargetPath = Path.GetFullPath(targetPath);
                var resolvedTargetDir = Path.GetFullPath(targetDir);
                if (!string.Equals(Path.GetDirectoryName(resolvedTargetPath), resolvedTargetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal))
                {
                    _logger.LogWarning("Companion file path traversal blocked: {FileName} resolved to {Resolved}", FormatUtilities.SanitizeForLog(companion.FileName, 300), FormatUtilities.SanitizeForLog(resolvedTargetPath));
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

                            CleanupTempFile(tempFilePath);

                            using (var fileStream = OpenForDownload(tempFilePath, FileMode.Create))
                            {
                                await CopyWithStallWatchdogAsync(new CappedReadStream(stream, MaxCompanionBytes), fileStream, speedLimitBytesPerSecond, config, ct).ConfigureAwait(false);
                            }
                        },
                        networkRetries,
                        _logger,
                        $"Download companion {FormatUtilities.SanitizeForLog(companion.FileName, 300)}",
                        cancellationToken).ConfigureAwait(false);

                    await FileOperationUtilities.MoveFileWithOverwriteAsync(tempFilePath, targetPath, _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
                    downloadedFiles.Add(companionFileName);
                    _logger.LogDebug("Downloaded companion file {FileName}", FormatUtilities.SanitizeForLog(companionFileName));
                }
                catch (OperationCanceledException)
                {
                    CleanupTempFile(tempFilePath);
                    throw;
                }

                // One companion's failure must not stop the others.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    CleanupTempFile(tempFilePath);
                    _logger.LogWarning(ex, "Failed to download companion file {FileName} after retries", FormatUtilities.SanitizeForLog(companion.FileName, 300));
                }
            }
        }

        // A cancelled run stops here, and any other failure keeps the existing companions.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not list companion files for item {ItemId}, keeping the existing companions", itemId);
        }

        return downloadedFiles;
    }

    /// <summary>
    /// Checks an item's path, free space, and write permission before it downloads.
    /// </summary>
    /// <param name="item">The row whose local path and source size are checked.</param>
    /// <param name="config">Supplies the root folders and the minimum free space.</param>
    /// <param name="manager">The Content sync table searched for another item on the same path.</param>
    /// <returns>Whether the item may download, with the reason when it may not.</returns>
    public static (bool IsValid, string? ErrorMessage) ValidateForDownload(
        SyncItem item,
        PluginConfiguration config,
        ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(manager);

        var pathValidation = FileValidationService.ValidatePath(item.LocalPath, item.SourceItemId, config, manager);
        if (!pathValidation.IsValid)
        {
            return (false, pathValidation.ErrorMessage);
        }

        if (!DiskSpaceService.HasSufficientSpaceForFile(item.LocalPath, item.SourceSize, config.MinimumFreeDiskSpaceGb))
        {
            return (false, "Insufficient disk space");
        }

        if (!FileValidationService.HasWritePermission(item.LocalPath))
        {
            return (false, "No write permission to target directory");
        }

        return (true, null);
    }

    /// <summary>
    /// Checks whether a download can be skipped because the local file exists with a matching size.
    /// </summary>
    /// <param name="item">The row whose local path and source size are compared.</param>
    /// <param name="sizeMatchToleranceBytes">How many bytes the sizes may differ by, where 0 requires them to be equal.</param>
    /// <param name="skipReason">Why the download is skipped, or null when it is not.</param>
    /// <returns><c>true</c> when the download should be skipped.</returns>
    public static bool ShouldSkipDownload(SyncItem item, long sizeMatchToleranceBytes, out string? skipReason)
    {
        ArgumentNullException.ThrowIfNull(item);

        return FileValidationService.ShouldSkipDownload(item, sizeMatchToleranceBytes, out skipReason);
    }

    /// <summary>
    /// Copies a download under a stall watchdog, so a source that stops sending cannot hold the copy forever.
    /// A stall fails with an <see cref="IOException"/>, which the retry policy retries.
    /// </summary>
    private async Task CopyWithStallWatchdogAsync(Stream source, Stream destination, long speedLimitBytesPerSecond, PluginConfiguration config, CancellationToken cancellationToken)
    {
        long Rate()
        {
            var live = config.GetEffectiveDownloadSpeedBytes();
            return live > 0 || config.EnableBandwidthScheduling ? live : speedLimitBytesPerSecond;
        }

        var window = StallTimeout;
        var rate = Rate();
        if (rate > 0)
        {
            // Downloads share one speed, so a read can wait a buffer from each of the others.
            var throttleSleep = TimeSpan.FromSeconds(2.0 * CopyBufferBytes * MaxSharers / rate);
            if (throttleSleep > window)
            {
                window = throttleSleep;
            }
        }

        await StreamUtilities.CopyWithIdleTimeoutAsync(
            source,
            destination,
            window,
            (watched, to, token) => StreamUtilities.CopyThrottledAsync(watched, to, SharedSpeed, Rate, cancellationToken: token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the name a companion from the source is written under, or null when it must not be written.
    /// The name is the last part of what the source gave, on either kind of separator, and must carry a subtitle extension and start with the video's own name.
    /// </summary>
    /// <param name="remoteName">The name or path the source gave, which is not trusted.</param>
    /// <param name="mainStem">The video's file name without its extension, which the companion's name must start with.</param>
    /// <returns>The safe file name, or null when the companion must not be written.</returns>
    public static string? CompanionNameFor(string? remoteName, string mainStem)
    {
        if (string.IsNullOrWhiteSpace(remoteName) || string.IsNullOrEmpty(mainStem))
        {
            return null;
        }

        var cut = Math.Max(remoteName.LastIndexOf('/'), remoteName.LastIndexOf('\\'));
        var name = cut >= 0 ? remoteName[(cut + 1)..] : remoteName;
        if (name.Length == 0
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(':', StringComparison.Ordinal)
            || !SubtitleExtensions.Contains(Path.GetExtension(name))
            || !name.StartsWith(mainStem, StringComparison.OrdinalIgnoreCase)
            || name.Length == mainStem.Length)
        {
            return null;
        }

        // A tag such as ".en" must follow the video's name, never more of a title.
        return name[mainStem.Length] is '.' or '-' or '_' or ' ' ? name : null;
    }

    /// <summary>
    /// Opens a file for async sequential writes with no second buffer, since the copy already hands over large blocks.
    /// </summary>
    private static FileStream OpenForDownload(string path, FileMode mode) => new(path, new FileStreamOptions
    {
        Mode = mode,
        Access = FileAccess.Write,
        Share = FileShare.None,
        BufferSize = 0,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    /// <summary>
    /// Gets the most bytes one attempt may write, from the source's stated length, else the recorded size plus slack, else the free space.
    /// A body the temp folder or the library has no room for is refused before a byte is written, so a source cannot fill a disk.
    /// </summary>
    private static long MostBytesToAccept(long? totalLength, long offset, SyncItem item, string tempFilePath, PluginConfiguration config)
    {
        long most;
        if (totalLength.HasValue)
        {
            most = Math.Max(0, totalLength.Value - offset);
            if (totalLength.Value > item.SourceSize
                && !DiskSpaceService.HasSufficientSpaceForFile(item.LocalPath, totalLength.Value, config.MinimumFreeDiskSpaceGb))
            {
                throw new InvalidOperationException(
                    $"The source sends {FormatUtilities.FormatBytes(totalLength.Value)}, more than the {FormatUtilities.FormatBytes(item.SourceSize)} it listed, and the library has no room for it");
            }
        }
        else if (item.SourceSize > 0)
        {
            most = Math.Max(0, item.SourceSize - offset) + (item.SourceSize / 10) + UnstatedLengthSlackBytes;
        }
        else
        {
            most = long.MaxValue;
        }

        if (DiskSpaceService.TryGetFreeBytes(tempFilePath, out var tempFree))
        {
            if (most == long.MaxValue)
            {
                most = tempFree;
            }
            else if (most > tempFree)
            {
                throw new InvalidOperationException(
                    $"The temporary download folder has {FormatUtilities.FormatBytes(tempFree)} free, less than the {FormatUtilities.FormatBytes(most)} still to download");
            }
        }

        return most;
    }

    /// <summary>
    /// Deletes the staged copies a crashed run left beside a target, which no run is writing since one download per item runs at a time.
    /// </summary>
    private void RemoveStaleStaged(string localPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(localPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var prefix = Path.GetFileName(localPath) + StagedMarker;
            foreach (var stale in Directory.EnumerateFiles(directory))
            {
                if (Path.GetFileName(stale).StartsWith(prefix, StringComparison.Ordinal))
                {
                    CleanupTempFile(stale);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not look for staged downloads beside {Path}", FormatUtilities.SanitizeForLog(localPath));
        }
    }

    /// <summary>
    /// Gets the bytes a failed attempt left behind, or 0 when there is nothing to resume from.
    /// </summary>
    private static long ExistingLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Deletes a temp file, leaving it behind when it cannot be deleted.
    /// </summary>
    private static void CleanupTempFile(string tempFilePath)
    {
        if (File.Exists(tempFilePath))
        {
            try
            {
                File.Delete(tempFilePath);
            }

            // Cleanup must never fail the download, so a locked file is left behind.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Settles the companions set aside while replacing a file, removing the old copy of each one the download wrote again.
    /// Every other companion goes back to its original path, since the source only serves external subtitles and the rest would be lost.
    /// </summary>
    private void SettleCompanionSidecars(List<(string Original, string Backup)> backupCompanions, PluginConfiguration config)
    {
        foreach (var (orig, bk) in backupCompanions)
        {
            if (File.Exists(orig))
            {
                ArchiveOrDeleteSidecar(bk, orig, config);
                continue;
            }

            try
            {
                if (File.Exists(bk))
                {
                    File.Move(bk, orig);
                }
            }

            // This runs in a finally block, so it must never throw.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to put companion back from sidecar {Sidecar} to {Path}", FormatUtilities.SanitizeForLog(bk), FormatUtilities.SanitizeForLog(orig));
            }
        }
    }

    /// <summary>
    /// Sets the write time of a file just set aside to the current time, so its age counts from the replacement.
    /// </summary>
    private void TouchBackup(string backupPath)
    {
        try
        {
            File.SetLastWriteTimeUtc(backupPath, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not stamp the replacement backup {Path}", FormatUtilities.SanitizeForLog(backupPath));
        }
    }

    /// <summary>
    /// Moves a file set aside during a replacement to the Recycling bin under the original file's name, or deletes it when the Recycling bin is off.
    /// A missing file is skipped, since the rollback may have put it back.
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete sidecar {Path}", FormatUtilities.SanitizeForLog(sidecarPath));
        }
    }
}

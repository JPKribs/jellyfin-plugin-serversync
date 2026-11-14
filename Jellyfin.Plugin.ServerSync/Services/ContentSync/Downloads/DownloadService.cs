using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
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

    /// <summary>
    /// How long a download may go without receiving a byte before it counts as stalled.
    /// </summary>
    public static readonly TimeSpan DefaultStallTimeout = StallWatchdogStream.DefaultIdleTimeout;

    /// <summary>
    /// The buffer the base copy reads in. With a speed limit it sleeps after each buffer, so the
    /// stall window has to outlast one buffer's worth of sleep at the configured rate.
    /// </summary>
    private const int CopyBufferBytes = 81920;

    // The most downloads that can share the speed at once: the top of Downloads At Once.
    private const int MaxSharers = 10;

    private static readonly TimeSpan RateCheckEvery = TimeSpan.FromSeconds(30);
    private static readonly object SpeedLock = new();
    private static SpeedBucket? _sharedSpeed;

    /// <summary>
    /// The most a subtitle from the source may weigh. Image based subtitles run to tens of megabytes,
    /// and nothing a source calls a subtitle is larger than this.
    /// </summary>
    public const long MaxCompanionBytes = 256L * 1024 * 1024;

    /// <summary>
    /// How far past its announced size a body with no stated length may run, as a fraction of that
    /// size plus a fixed floor, to allow for source metadata that has gone stale.
    /// </summary>
    private const long UnstatedLengthSlackBytes = 64L * 1024 * 1024;

    // Marks a download staged beside its target while it waits to be renamed into place.
    private const string StagedMarker = ".syncing.";

    /// <summary>
    /// The external subtitle formats Jellyfin reads beside a video. A companion from the source is
    /// only written under one of these, so a source cannot name a video, an .nfo or a .ignore file.
    /// </summary>
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".smi", ".sami", ".sup", ".pgs", ".mks", ".ttml", ".dfxp", ".lrc"
    };

    public DownloadService(ILogger<DownloadService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets how long a download may receive nothing before it is abandoned as stalled and
    /// handed to the retry policy.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = DefaultStallTimeout;

    /// <summary>
    /// Downloads a single item and its companion files with retry support.
    /// </summary>
    /// <param name="client">Source server client.</param>
    /// <param name="item">Sync item to download.</param>
    /// <param name="tempPath">Temporary download path.</param>
    /// <param name="speedLimitBytesPerSecond">Download speed limit in bytes per second.</param>
    /// <param name="includeCompanionFiles">Whether to download companion files.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="progress">Optional per-item progress (fraction 0 to 1 of the main file's bytes).</param>
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
            // Download with retry support. A partial file from a failed attempt is kept and resumed,
            // so a drop at 90% of a large file costs the last 10%, not the whole file again. The
            // validator ties the resume to the same file on the source.
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
                        if (download.Offset == 0)
                        {
                            // Not resumed: nothing kept, or the source sent the whole file again.
                            CleanupTempFile(tempFilePath);
                        }
                        else
                        {
                            _logger.LogInformation("Resuming {FileName} from {Offset} bytes", fileName, download.Offset);
                        }

                        var mode = download.Offset > 0 ? FileMode.Append : FileMode.Create;
                        var most = MostBytesToAccept(download.TotalLength, download.Offset, item, tempFilePath, config);
                        using (var fileStream = OpenForDownload(tempFilePath, mode))
                        {
                            // The total length is authoritative. The recorded
                            // SourceSize is the fallback (can be stale but is
                            // the right order of magnitude). No basis → no
                            // in-file reporting. The item still completes its
                            // share when it finishes.
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
                        if (downloadedInfo.Length > download.TotalLength.Value)
                        {
                            // Longer than the file can be: the kept bytes do not belong to it.
                            validator = null;
                            CleanupTempFile(tempFilePath);
                        }

                        // An IOException is retried, and the retry resumes from the bytes kept.
                        throw new IOException(errorMsg);
                    }

                    // With no length from the source there is nothing to hold the download to but the
                    // size the scan recorded. A download that does not match it never replaces a file
                    // that is already here: the local file is kept and the row errors, and the next
                    // scan records the size the source has now.
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
            //      sidecars on the same volume, atomic, so rolling back is fast.
            //   2. Atomically rename the just-downloaded temp file into place.
            //   3. Only after step 2 succeeds, archive the old main file to the
            //      recycling bin (or delete it).
            //   4. After the companion download, put back every companion the
            //      download did not replace. The source only serves external
            //      subtitles, so nfo files, artwork and subtitles it no longer
            //      lists stay with the new file instead of being thrown away.
            // If step 2 throws, the catch block restores the sidecars so the
            // user's previous version stays intact at item.LocalPath.
            // The temp folder is usually on another volume, where a move is a copy. That copy is made
            // under a staged name beside the target, so the library never sees a partial file under
            // the real name and a crash mid copy leaves the previous file untouched.
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
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to move existing companion aside: {CompanionPath}", companionPath);
                        }
                    }
                }

                await FileOperationUtilities.MoveFileWithOverwriteAsync(stagedPath, item.LocalPath, _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                CleanupTempFile(stagedPath);

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

            // The rename succeeded, so the old main file is no longer needed.
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
                // Runs on cancellation and on a failed companion lookup too, since both leave the old
                // companions aside with nothing written in their place.
                SettleCompanionSidecars(backupCompanions, config);
            }

            // Jellyfin finds the file in a later scan. This keeps that from being announced to peers as a
            // new local file.
            Queue.WrittenFiles.Mark(item.LocalPath);
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
    /// <param name="mainStem">The video's file name without its extension. Every companion's name starts with it.</param>
    /// <param name="tempPath">Temporary download path.</param>
    /// <param name="speedLimitBytesPerSecond">Download speed limit in bytes per second.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of successfully downloaded companion file names.</returns>
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
                // The name comes from the other server. It is cut to its last part on either kind of
                // separator, since a Windows source sends backslashes a Linux server does not split on,
                // and it must be a subtitle of this video and nothing else.
                var companionFileName = CompanionNameFor(companion.FileName, mainStem);
                if (companionFileName is null)
                {
                    _logger.LogWarning(
                        "Skipping companion file {FileName}: it is not a subtitle named after {Stem}",
                        FormatUtilities.SanitizeForLog(companion.FileName ?? string.Empty, 300),
                        mainStem);
                    continue;
                }

                var tempFileName = FileNameSanitizer.SanitizeTempFileName(itemId.ToString(), companionFileName);
                var tempFilePath = Path.Combine(tempPath, tempFileName);
                var targetPath = Path.Combine(targetDir, companionFileName);

                // Verify the resolved target path is still within the target directory
                var resolvedTargetPath = Path.GetFullPath(targetPath);
                var resolvedTargetDir = Path.GetFullPath(targetDir);
                if (!string.Equals(Path.GetDirectoryName(resolvedTargetPath), resolvedTargetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal))
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

                            using (var fileStream = OpenForDownload(tempFilePath, FileMode.Create))
                            {
                                await CopyWithStallWatchdogAsync(new CappedReadStream(stream, MaxCompanionBytes), fileStream, speedLimitBytesPerSecond, config, ct).ConfigureAwait(false);
                            }
                        },
                        networkRetries,
                        _logger,
                        $"Download companion {companion.FileName}",
                        cancellationToken).ConfigureAwait(false);

                    await FileOperationUtilities.MoveFileWithOverwriteAsync(tempFilePath, targetPath, _logger, cancellationToken: cancellationToken).ConfigureAwait(false);
                    downloadedFiles.Add(companionFileName);
                    _logger.LogDebug("Downloaded companion file {FileName}", companionFileName);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled run must stop here rather than report the item as downloaded.
            throw;
        }
        catch (Exception ex)
        {
            // The lookup failing is not the same as the item having no companions. Say so plainly,
            // and the caller puts back the companions it moved aside since nothing replaced them.
            _logger.LogWarning(ex, "Could not list companion files for item {ItemId}, keeping the existing companions", itemId);
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
    /// Copies a download under a stall watchdog, so a source that stops sending mid-body cannot hold
    /// the copy, and the Content lock with it, forever. The stall fails with an IOException, which the
    /// retry policy treats as transient, and a download that keeps stalling fails like any other.
    /// </summary>
    private async Task CopyWithStallWatchdogAsync(Stream source, Stream destination, long speedLimitBytesPerSecond, PluginConfiguration config, CancellationToken cancellationToken)
    {
        // The speed in force now: the configured one for this hour, or the caller's when none is configured.
        long Rate()
        {
            var live = config.GetEffectiveDownloadSpeedBytes();
            return live > 0 || config.EnableBandwidthScheduling ? live : speedLimitBytesPerSecond;
        }

        var window = StallTimeout;
        var rate = Rate();
        if (rate > 0)
        {
            // Every download waits its turn on one shared speed, so the pause between two reads of one
            // download can be as long as a buffer from each of the others takes.
            var throttleSleep = TimeSpan.FromSeconds(2.0 * CopyBufferBytes * MaxSharers / rate);
            if (throttleSleep > window)
            {
                window = throttleSleep;
            }
        }

        await StallWatchdogStream.CopyAsync(
            source,
            destination,
            window,
            (watched, to, token) => CopyAtSharedSpeedAsync(watched, to, Rate, token),
            cancellationToken).ConfigureAwait(false);
    }

    // Copies under the one speed all content downloads share, so the limit is the total across
    // Downloads At Once and not a limit for each file. The speed is read again as the copy runs, so a
    // long download follows the scheduled hours as they begin and end.
    private static async Task CopyAtSharedSpeedAsync(Stream source, Stream destination, Func<long> rate, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            var bucket = SharedSpeed(rate());
            var readAt = System.Diagnostics.Stopwatch.GetTimestamp();
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (System.Diagnostics.Stopwatch.GetElapsedTime(readAt) > RateCheckEvery)
                {
                    bucket = SharedSpeed(rate());
                    readAt = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                if (bucket is not null && bucket.Take(read) is { } wait && wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static SpeedBucket? SharedSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return null;
        }

        lock (SpeedLock)
        {
            if (_sharedSpeed is null)
            {
                _sharedSpeed = new SpeedBucket(bytesPerSecond);
            }
            else
            {
                _sharedSpeed.SetRate(bytesPerSecond);
            }

            return _sharedSpeed;
        }
    }

    /// <summary>
    /// The name a companion from the source is written under, or null when it must not be written.
    /// It is the last part of the name the source gave, it carries a subtitle extension, and it
    /// starts with the video's own name, which is also what makes Jellyfin attach it to the video.
    /// </summary>
    /// <param name="remoteName">The name or path the source gave.</param>
    /// <param name="mainStem">The video's file name without its extension.</param>
    /// <returns>The safe file name, or null.</returns>
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

        // What follows the video's name is a tag such as ".en" or ".forced", never more of a title.
        return name[mainStem.Length] is '.' or '-' or '_' or ' ' ? name : null;
    }

    // Async sequential writes without a second buffer: the copy already hands over large blocks.
    private static FileStream OpenForDownload(string path, FileMode mode) => new(path, new FileStreamOptions
    {
        Mode = mode,
        Access = FileAccess.Write,
        Share = FileShare.None,
        BufferSize = 0,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    // The most bytes one attempt may write. The source's stated length is the limit when it gives one.
    // Without one the recorded size plus some slack is, and with neither the free space is. A body
    // longer than the free space in the temp folder, or than the library has room for, is refused
    // before a byte is written, so a source cannot fill a disk by sending more than it announced.
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

    // Staged copies a crashed run left beside this target. No run is writing them: one download per
    // item runs at a time.
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
            _logger.LogDebug(ex, "Could not look for staged downloads beside {Path}", localPath);
        }
    }

    // The bytes a failed attempt left behind, or 0 when there is nothing to resume from.
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
                // File is read-only or ACL denies. Leave for OS cleanup.
            }
        }
    }

    /// <summary>
    /// Settles the companions moved aside while replacing a file. A companion the download wrote again
    /// is in place at its original path, so its old copy is archived or deleted. Every other companion
    /// goes back to its original path, because the source only serves external subtitles and the rest
    /// (nfo files, artwork, subtitles it does not list) would otherwise be lost with the old file.
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to put companion back from sidecar {Sidecar} to {Path}", bk, orig);
            }
        }
    }

    // Stamps a file just moved aside with the current time, so its age counts from the replacement
    // rather than from when the old file was last written.
    private void TouchBackup(string backupPath)
    {
        try
        {
            File.SetLastWriteTimeUtc(backupPath, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not stamp the replacement backup {Path}", backupPath);
        }
    }

    /// <summary>
    /// Archives a sidecar (the file kept aside while replacing) to the
    /// recycling bin under the original file's name, or deletes it outright if
    /// the recycling bin is disabled. Quiet on missing sidecars (the rollback
    /// may have already put it back).
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

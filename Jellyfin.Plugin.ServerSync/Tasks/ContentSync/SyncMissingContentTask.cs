using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Apply phase for Content sync. Downloads queued items in parallel
/// (bounded by <see cref="PluginConfiguration.MaxConcurrentDownloads"/>),
/// processes pending deletions, and triggers a library refresh on
/// completion. Pre-flight (disk space, connection, circuit breaker) lives
/// in <see cref="BeforeRunAsync"/>; post-flight in <see cref="FinalizeAsync"/>.
/// </summary>
public class SyncMissingContentTask
    : SyncQueueTaskBase<SyncItem, string>
{
    private const int DefaultMaxRetries = 3;

    /// <summary>
    /// Circuit breakers keyed by source server URL — state survives across
    /// runs but resets when the URL changes.
    /// </summary>
    private static readonly Dictionary<string, CircuitBreaker> _circuitBreakers = new();
    private static readonly object _circuitBreakerLock = new();

    private readonly ILibraryManager _libraryManager;
    private readonly DownloadService _downloadService;

    private CircuitBreaker? _circuitBreaker;
    private string? _tempPath;
    private long _speedLimit;
    private int _successCount;
    private int _deletedCount;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public SyncMissingContentTask(
        ILogger<SyncMissingContentTask> logger,
        ILibraryManager libraryManager,
        IPluginConfigurationManager configManager,
        ContentSyncTableManager manager,
        ISourceServerClientFactory clientFactory,
        DownloadService downloadService)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _downloadService = downloadService;
    }

    /// <inheritdoc />
    public override string Name => "Sync Content";

    /// <inheritdoc />
    public override string Key => "ServerSyncDownloadContent";

    /// <inheritdoc />
    public override string Description => "Downloads queued content from the source server, processes deletions, and triggers a library refresh.";

    /// <inheritdoc />
    public override string Category => "Content Sync";

    /// <inheritdoc />
    protected override int MaxDegreeOfParallelism => Math.Max(1, ConfigManager.Configuration.MaxConcurrentDownloads);

    /// <inheritdoc />
    protected override string ModuleMutexKey => "Content";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableContentSync) return false;
        if (!ConfigurationUtilities.HasValidAuthConfiguration(config))
        {
            Logger.LogError("Sync skipped: no valid authentication configured");
            return false;
        }

        return true;
    }

    // Errored-for-retry rows are pulled in alongside Queued so a previously
    // failed download gets another shot, capped by
    // <see cref="PluginConfiguration.MaxRetryCount"/>.
    /// <inheritdoc />
    protected override IList<SyncItem> GetItemsToApply()
    {
        var typedManager = TypedManager;
        var maxRetries = ConfigManager.Configuration.MaxRetryCount > 0
            ? ConfigManager.Configuration.MaxRetryCount
            : DefaultMaxRetries;
        return typedManager.GetByStatusStrict(SyncStatus.Queued)
            .Concat(typedManager.GetErroredItemsForRetry(maxRetries))
            .ToList();
    }

    /// <inheritdoc />
    protected override async Task<bool> BeforeRunAsync(CancellationToken cancellationToken)
    {
        var config = ConfigManager.Configuration;

        if (!DiskSpaceService.HasSufficientSpace(config, out var insufficientPath))
        {
            var diskInfo = DiskSpaceService.GetDiskSpaceInfo(config).FirstOrDefault(d => d.Path == insufficientPath);
            var message = diskInfo != null
                ? DiskSpaceService.FormatInsufficientSpaceMessage(insufficientPath!, diskInfo.FreeBytes, config.MinimumFreeDiskSpaceGb)
                : $"Insufficient disk space on {insufficientPath}";
            Logger.LogError("Sync skipped: {Message}", message);
            return false;
        }

        _circuitBreaker = GetOrCreateCircuitBreaker(config.SourceServerUrl);
        if (!_circuitBreaker.AllowOperation(out var circuitReason))
        {
            Logger.LogWarning("Sync skipped: {Reason}", circuitReason);
            return false;
        }

        // Delegate connection test to the base — it sets Client and logs on
        // failure. Wrap with circuit-breaker recording.
        if (!await base.BeforeRunAsync(cancellationToken).ConfigureAwait(false))
        {
            _circuitBreaker.RecordFailure("connection test failed");
            return false;
        }

        _circuitBreaker.RecordSuccess();

        var staleCount = ActiveDownloadTracker.CleanupStaleEntries();
        if (staleCount > 0)
        {
            Logger.LogInformation("Cleaned up {Count} stale download entries", staleCount);
        }

        _tempPath = ConfigManager.GetTempDownloadPath();
        Directory.CreateDirectory(_tempPath);
        _speedLimit = config.GetEffectiveDownloadSpeedBytes();
        _successCount = 0;
        _deletedCount = 0;

        config.LastSyncStartTime = DateTime.UtcNow;
        ConfigManager.SaveConfiguration();

        return true;
    }

    // Weight items by file size so the run's percentage tracks bytes moved,
    // not item count — one 50 GB movie plus nine small episodes used to jump
    // 10% per episode and then freeze for the movie's entire download.
    /// <inheritdoc />
    protected override long GetApplyWeight(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Math.Max(1, record.SourceSize);
    }

    /// <inheritdoc />
    protected override Task ApplyAsync(SyncItem record, CancellationToken cancellationToken)
        => ApplyAsync(record, itemProgress: null, cancellationToken);

    /// <inheritdoc />
    protected override async Task ApplyAsync(SyncItem record, IProgress<double>? itemProgress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (Client == null || _circuitBreaker == null || _tempPath == null)
        {
            throw new InvalidOperationException("BeforeRunAsync did not complete; aborting apply.");
        }

        if (string.IsNullOrEmpty(record.LocalPath))
        {
            throw new InvalidOperationException(
                $"Item has no local path configured (source: {record.SourcePath ?? record.SourceItemId})");
        }

        var config = ConfigManager.Configuration;
        var fileName = Path.GetFileName(record.LocalPath);
        var fileSize = FormatUtilities.FormatBytes(record.SourceSize);

        if (!DiskSpaceService.HasSufficientSpaceForFile(record.LocalPath, record.SourceSize, config.MinimumFreeDiskSpaceGb))
        {
            throw new IOException($"Insufficient disk space for {fileName} ({fileSize}). Required: {fileSize} + {config.MinimumFreeDiskSpaceGb} GB reserve");
        }

        var (isValid, validationError) = DownloadService.ValidateForDownload(record, config, TypedManager);
        if (!isValid)
        {
            // Include filename + source path so the Reason field surfaces
            // which item failed validation, not just "Validation failed".
            throw new InvalidOperationException(
                $"Validation failed for {fileName}: {validationError ?? "unknown reason"} (source: {record.SourcePath ?? record.SourceItemId})");
        }

        if (DownloadService.ShouldSkipDownload(record, config.SizeMatchToleranceBytes, out var skipReason))
        {
            Logger.LogDebug("Skipped: {FileName} ({Size}) - {Reason}", fileName, fileSize, skipReason);
            Interlocked.Increment(ref _successCount);
            return;
        }

        var tempFileName = FileNameSanitizer.SanitizeTempFileName(record.SourceItemId, record.LocalPath);
        var tempFilePath = Path.Combine(_tempPath, tempFileName);

        if (!ActiveDownloadTracker.TryStartDownload(record.SourceItemId, tempFilePath))
        {
            // Already in flight on this run — let the base treat it as success
            // so the row isn't re-flagged as Errored. The other thread will
            // persist the actual outcome.
            Logger.LogDebug("Item {SourceItemId} is already being downloaded, skipping", record.SourceItemId);
            return;
        }

        try
        {
            var result = await _downloadService.DownloadItemAsync(
                Client, record, _tempPath, _speedLimit,
                config.IncludeCompanionFiles, config, itemProgress, cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                _circuitBreaker.RecordSuccess();
                record.CompanionFiles = result.CompanionFiles;
                Interlocked.Increment(ref _successCount);
                Logger.LogInformation("DOWNLOADED: {FileName} ({Size}) -> {LocalPath}", fileName, fileSize, record.LocalPath);
            }
            else
            {
                _circuitBreaker.RecordFailure(result.ErrorMessage);
                Logger.LogError("FAILED: {FileName} ({Size}) - {Error}. Source: {SourcePath}",
                    fileName, fileSize, result.ErrorMessage, record.SourcePath);
                // Include filename + size + source path in the message so the
                // Reason field surfaces actionable context. Bare "Connection
                // timeout" is useless when 50 items errored — user can't tell
                // which file or whether the issue is network or filesystem.
                throw new InvalidOperationException(
                    $"Download failed for {fileName} ({fileSize}): {result.ErrorMessage ?? "unknown error"} (source: {record.SourcePath ?? record.SourceItemId})");
            }
        }
        finally
        {
            ActiveDownloadTracker.CompleteDownload(record.SourceItemId);
        }
    }

    // No-op — Content has no SyncableValue fields and the base's
    // post-apply Status/LastSyncTime/Reason write covers everything we
    // need. <see cref="ApplyAsync"/> already populated CompanionFiles on
    // the record.
    /// <inheritdoc />
    protected override void OnApplySucceeded(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.RetryCount = 0;
    }

    // Increments <see cref="SyncItem.RetryCount"/> so the
    // <c>MaxRetryCount</c> cap in <see cref="GetItemsToApply"/> is honored.
    /// <inheritdoc />
    protected override void OnApplyFailed(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.RetryCount++;
    }

    /// <inheritdoc />
    protected override async Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var config = ConfigManager.Configuration;

        // Progress allocation within finalize:
        //   0– 10 %  pending-deletion processing
        //  10– 95 %  library refresh (the slow part)
        //  95–100 %  config save + cleanup
        progress.Report(0);

        // Process pending-deletion rows (separate from Queued items — these
        // were soft-deleted by the Refresh task). Always run so the user's
        // "approve deletion" action gets picked up even when nothing was
        // queued.
        var (deleted, _) = FileDeletionService.ProcessPendingDeletions(TypedManager, config, Logger, cancellationToken);
        Interlocked.Add(ref _deletedCount, deleted);

        progress.Report(10);

        // Library refresh is conditional on actual filesystem changes
        // (downloads or deletions). A no-op run skips the refresh because
        // ValidateMediaLibrary is expensive on large libraries and there's
        // nothing for Jellyfin to discover when we wrote/removed nothing.
        if (_successCount > 0 || _deletedCount > 0)
        {
            try
            {
                Logger.LogInformation("Triggering library refresh");
                var refreshProgress = new Progress<double>(p =>
                    progress.Report(10 + (85.0 * Math.Clamp(p, 0, 100) / 100.0)));
                await _libraryManager.ValidateMediaLibrary(refreshProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to trigger library refresh");
            }
        }

        progress.Report(95);

        _circuitBreaker = null;
        _tempPath = null;
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastSyncEndTime = utcNow;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        }
    };

    private ContentSyncTableManager TypedManager => (ContentSyncTableManager)Manager;

    private CircuitBreaker GetOrCreateCircuitBreaker(string sourceUrl)
    {
        lock (_circuitBreakerLock)
        {
            if (_circuitBreakers.TryGetValue(sourceUrl, out var existing))
            {
                return existing;
            }

            // Evict stale entries for old server URLs to prevent unbounded growth.
            var stale = _circuitBreakers.Keys
                .Where(k => !string.Equals(k, sourceUrl, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in stale)
            {
                _circuitBreakers.Remove(key);
            }

            var breaker = new CircuitBreaker(
                Logger,
                "SourceServer",
                failureThreshold: 5,
                cooldownPeriod: TimeSpan.FromMinutes(5));
            _circuitBreakers[sourceUrl] = breaker;
            return breaker;
        }
    }
}

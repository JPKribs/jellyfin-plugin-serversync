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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Apply phase for Content sync. Downloads queued items in parallel
/// (bounded by <see cref="PluginConfiguration.MaxConcurrentDownloads"/>),
/// processes pending deletions, and triggers a library refresh on
/// completion. Pre-flight (disk space, connection, circuit breaker) lives
/// in <see cref="BeforeRunAsync"/>. Post-flight in <see cref="FinalizeAsync"/>.
/// </summary>
public class SyncMissingContentTask
    : SyncQueueTaskBase<SyncItem, string>
{
    private const int DefaultMaxRetries = 3;

    /// <summary>
    /// Circuit breakers keyed by source server URL, one per configured scan
    /// server. State survives across runs. A breaker is dropped once its URL
    /// is no longer configured, so a changed URL starts with a fresh one.
    /// </summary>
    private static readonly Dictionary<string, CircuitBreaker> _circuitBreakers = new();
    private static readonly object _circuitBreakerLock = new();

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly DownloadService _downloadService;

    // Every file this run wrote, skipped as already present, or removed, so the refresh afterwards
    // only looks at the folders that changed.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _changedFiles = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _skippedByBreaker = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _downloadAttempted = new(StringComparer.OrdinalIgnoreCase);
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
        ILibraryMonitor libraryMonitor,
        IPluginConfigurationManager configManager,
        ContentSyncTableManager manager,
        ISourceServerClientFactory clientFactory,
        DownloadService downloadService,
        Services.Links.LinkWriter? linkWriter = null,
        Services.Links.LinkVerifier? linkVerifier = null,
        Services.Links.LinkCacheFiller? linkCacheFiller = null)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _downloadService = downloadService;
        _linkWriter = linkWriter;
        _linkVerifier = linkVerifier;
        _linkCacheFiller = linkCacheFiller;
    }

    private readonly Services.Links.LinkWriter? _linkWriter;
    private readonly Services.Links.LinkVerifier? _linkVerifier;
    private readonly Services.Links.LinkCacheFiller? _linkCacheFiller;

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
        _downloadAttempted.Clear();

        if (!DiskSpaceService.HasSufficientSpace(config, out var insufficientPath) && !OnlyLinksQueued())
        {
            var diskInfo = DiskSpaceService.GetDiskSpaceInfo(config).FirstOrDefault(d => d.Path == insufficientPath);
            var message = diskInfo != null
                ? DiskSpaceService.FormatInsufficientSpaceMessage(insufficientPath!, diskInfo.FreeBytes, config.MinimumFreeDiskSpaceGb)
                : $"Insufficient disk space on {insufficientPath}";
            FailPreflight(message);
            return false;
        }

        // The base connects every scan server. A server that did not connect feeds its circuit
        // breaker, and a server whose breaker is open is left out of this run: its rows stay Queued
        // untouched, so a pause never costs them retries, while the other servers' downloads go on.
        if (!await base.BeforeRunAsync(cancellationToken).ConfigureAwait(false) && !OnlyLinksQueued())
        {
            return false;
        }

        // A server left out of this run is disposed here, since the run's cleanup only sees the ones kept.
        // That happens in the finally so a throw partway through still disposes those already left out.
        var allowed = new List<ScanSource>();
        var leftOut = new List<ScanSource>();
        try
        {
            foreach (var server in config.GetPullServers())
            {
                var breaker = GetOrCreateCircuitBreaker(server.Url);
                var source = Sources.FirstOrDefault(s => string.Equals(s.Key, server.Key, StringComparison.OrdinalIgnoreCase));
                if (source is null)
                {
                    breaker.RecordFailure("connection test failed");
                    continue;
                }

                if (server.CanDownload == false)
                {
                    leftOut.Add(source);
                    Logger.LogWarning("{Task}: leaving '{Server}' out of this run: the account its key belongs to may not download media there. Allow media downloading for that user on '{Server}', or use an administrator's API key", Name, source.Name, source.Name);
                    continue;
                }

                if (!AdmitServer(breaker, out var circuitReason))
                {
                    leftOut.Add(source);
                    Logger.LogWarning("{Task}: leaving '{Server}' out of this run: {Reason}", Name, source.Name, circuitReason);
                    continue;
                }

                allowed.Add(source);
            }

            Sources = allowed;
        }
        finally
        {
            foreach (var source in leftOut)
            {
                source.Dispose();
            }
        }
        if (allowed.Count == 0 && !OnlyLinksQueued())
        {
            FailPreflight("the circuit breaker is open for every reachable server");
            return false;
        }

        PrepareDownloads(config);

        config.LastSyncStartTime = DateTime.UtcNow;
        ConfigManager.SaveConfiguration();

        return true;
    }

    // A single row from a hint: the same disk check and download setup as a run, without connecting
    // every server, since the hint already names the one to pull from.
    /// <inheritdoc />
    protected override Task<bool> BeforeRowAsync(CancellationToken cancellationToken)
    {
        var config = ConfigManager.Configuration;
        if (!DiskSpaceService.HasSufficientSpace(config, out var insufficientPath))
        {
            FailPreflight($"insufficient disk space on {insufficientPath}");
            return Task.FromResult(false);
        }

        PrepareDownloads(config);
        return Task.FromResult(true);
    }

    // A file that arrived through a hint is reported to the library as changed, the way the library
    // monitor reports a file it saw, so Jellyfin refreshes that one folder rather than every library.
    // When Jellyfin could not take the folder, as for a first file in an empty library, a scan is queued.
    /// <inheritdoc />
    protected override Task AfterRowAsync(bool applied, CancellationToken cancellationToken)
    {
        if (applied && _successCount > 0 && !_changedFiles.IsEmpty && !RefreshChangedFolders())
        {
            _libraryManager.QueueLibraryScan();
        }

        _tempPath = null;
        return Task.CompletedTask;
    }

    private void PrepareDownloads(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config)
    {
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
        _changedFiles.Clear();
    }

    // A library's own folder or one inside it. The roots above every library never lead a refresh anywhere.
    private bool IsLibraryFolder(string path)
        => _libraryManager.FindByPath(path, true) is MediaBrowser.Controller.Entities.Folder folder
            and not MediaBrowser.Controller.Entities.AggregateFolder
            and not MediaBrowser.Controller.Entities.UserRootFolder
            and not MediaBrowser.Controller.Entities.CollectionFolder;

    /// <summary>
    /// Tells Jellyfin which folders changed, so it refreshes those and nothing else. Returns false when
    /// there are too many for that to be cheaper than one full scan, which the caller then runs.
    /// </summary>
    private bool RefreshChangedFolders()
    {
        var folders = ChangedFolders.For(_changedFiles);
        if (folders.Count == 0 || folders.Count > ChangedFolders.MaxTargeted)
        {
            return false;
        }

        if (!ChangedFolders.AllReachable(folders, IsLibraryFolder))
        {
            Logger.LogInformation("Jellyfin has not yet scanned the library folder for a changed file, as happens on a first sync into an empty library, so the whole library is scanned");
            return false;
        }

        foreach (var folder in folders)
        {
            _libraryMonitor.ReportFileSystemChanged(folder);
        }

        Logger.LogInformation("Asked Jellyfin to refresh {Count} changed folder(s)", folders.Count);
        return true;
    }

    // Weight items by file size so the run's percentage tracks bytes moved,
    // not item count, one 50 GB movie plus nine small episodes used to jump
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

        if (_tempPath == null)
        {
            throw new InvalidOperationException("BeforeRunAsync did not complete. Aborting apply.");
        }

        // A link downloads nothing, so it branches off before the download steps and never touches them.
        if (record.StorageMode == StorageMode.Link)
        {
            // An .nfo that is gone while its link is still there was removed by the user. One that went
            // with its link, such as a deleted folder, was not, and is written again with the link.
            var linkWasThere = !string.IsNullOrEmpty(record.LocalPath) && File.Exists(record.LocalPath);
            (_linkWriter ?? throw new InvalidOperationException("Links cannot be written on this server")).Write(record, TypedManager, ConfigManager.Configuration);

            // Read before the companions replace the row's list, which is where a written .nfo is recorded.
            var nfoRecorded = Services.Links.LinkNfo.Recorded(record);
            var nfoRemoved = linkWasThere && Services.Links.LinkNfo.RemovedByUser(record);
            await DownloadLinkCompanionsAsync(record, cancellationToken).ConfigureAwait(false);
            if (nfoRemoved)
            {
                Services.Links.LinkNfo.Record(record);
            }
            else
            {
                await WriteLinkNfoAsync(record, nfoRecorded, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _successCount);
            _changedFiles.Enqueue(record.LocalPath!);
            return;
        }

        // A link becoming a copy downloads through the copy path below, to where the copy belongs, and
        // the .strm goes only once the copy is in place. Until then it keeps playing as a link.
        if (record.StorageMode == StorageMode.Promoting)
        {
            var linkPath = record.LocalPath;
            var copyPath = Services.Links.LinkPaths.CopiedPathOf(record) ?? throw new InvalidOperationException($"The link has no path (source: {record.SourcePath})");
            record.LocalPath = copyPath;
            try
            {
                await DownloadAsync(record, itemProgress, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                record.LocalPath = linkPath;
                throw;
            }

            if (!File.Exists(copyPath))
            {
                record.LocalPath = linkPath;
                throw new InvalidOperationException($"The copy of {Path.GetFileName(copyPath)} did not arrive, so the link stays");
            }

            if (!string.IsNullOrEmpty(linkPath))
            {
                (_linkWriter ?? throw new InvalidOperationException("Links cannot be removed on this server")).Remove(linkPath);
                _changedFiles.Enqueue(linkPath);
            }

            record.StorageMode = StorageMode.Copy;
            record.LocalItemId = null;
            return;
        }

        await DownloadAsync(record, itemProgress, cancellationToken).ConfigureAwait(false);
    }

    // Subtitles and other companions are small and Jellyfin pairs them by name, so a link gets them
    // beside it the same way a copy does. A link is still written when they cannot be fetched.
    private async Task DownloadLinkCompanionsAsync(SyncItem record, CancellationToken cancellationToken)
    {
        var config = ConfigManager.Configuration;
        if (!config.IncludeCompanionFiles || _tempPath is null || SourceFor(record) is not { } source
            || !Guid.TryParse(record.SourceItemId, out var itemId) || Path.GetDirectoryName(record.LocalPath) is not { Length: > 0 } folder)
        {
            return;
        }

        var companions = await _downloadService.DownloadCompanionFilesAsync(source.Client, itemId, folder, Path.GetFileNameWithoutExtension(record.LocalPath) ?? string.Empty, _tempPath, _speedLimit, config, cancellationToken).ConfigureAwait(false);
        if (companions.Count > 0)
        {
            record.CompanionFiles = string.Join(",", companions);
        }
    }

    // An .nfo beside the link tells Jellyfin what it is from the source's own details, rather than from
    // the file name. A link is still written when the details cannot be read, and an .nfo the plugin did
    // not write is never replaced.
    private async Task WriteLinkNfoAsync(SyncItem record, bool recorded, CancellationToken cancellationToken)
    {
        // An .nfo written on an earlier run stays recorded when this run cannot write it again.
        if (recorded)
        {
            Services.Links.LinkNfo.Record(record);
        }

        if (SourceFor(record) is not { } source || !Guid.TryParse(record.SourceItemId, out var itemId) || string.IsNullOrEmpty(record.LocalPath))
        {
            return;
        }

        try
        {
            var items = await source.Client.GetItemsByIdsAsync(new[] { itemId }, Services.Links.LinkNfo.Fields, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (items.FirstOrDefault() is not { } item)
            {
                return;
            }

            if (Services.Links.LinkNfo.Write(record.LocalPath, item))
            {
                Services.Links.LinkNfo.Record(record);
            }
            else
            {
                Logger.LogDebug("Left the .nfo beside {Path} as it is, since Server Sync did not write it", record.LocalPath);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Wrote the link {Path} without an .nfo: {Reason}", record.LocalPath, ex.Message);
        }
    }

    // The copy path: disk space, validation, and the download itself.
    private async Task DownloadAsync(SyncItem record, IProgress<double>? itemProgress, CancellationToken cancellationToken)
    {
        if (_tempPath == null)
        {
            throw new InvalidOperationException("BeforeRunAsync did not complete. Aborting apply.");
        }

        // The breaker may open during the run. A row skipped for that reason is not a failed attempt.
        var source = RequireSource(record);
        var breaker = GetOrCreateCircuitBreaker(source.Server.Url);
        if (!breaker.AllowOperation(out var circuitReason))
        {
            _skippedByBreaker[record.Id] = 0;
            throw new InvalidOperationException($"Skipped: {circuitReason}");
        }

        if (string.IsNullOrEmpty(record.LocalPath))
        {
            throw new InvalidOperationException(
                $"Item has no local path configured (source: {record.SourcePath ?? record.SourceItemId})");
        }

        var config = ConfigManager.Configuration;
        var fileName = Path.GetFileName(record.LocalPath);
        var fileSize = FormatUtilities.FormatBytes(record.SourceSize);

        // Held until this download ends, so downloads running side by side cannot together overfill
        // the disk that each would fit on alone.
        using var spaceHold = await DiskSpaceService.ReserveForFileAsync(record.LocalPath, record.SourceSize, config.MinimumFreeDiskSpaceGb, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Insufficient disk space for {fileName} ({fileSize}). Required: {fileSize} + {config.MinimumFreeDiskSpaceGb} GB reserve");

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
            _changedFiles.Enqueue(record.LocalPath);
            return;
        }

        var tempFileName = FileNameSanitizer.SanitizeTempFileName(record.SourceItemId, record.LocalPath);
        var tempFilePath = Path.Combine(_tempPath, tempFileName);

        if (!ActiveDownloadTracker.TryStartDownload(record.SourceItemId, tempFilePath))
        {
            // Already in flight on this run, let the base treat it as success
            // so the row isn't re-flagged as Errored. The other thread will
            // persist the actual outcome.
            Logger.LogDebug("Item {SourceItemId} is already being downloaded, skipping", record.SourceItemId);
            return;
        }

        _downloadAttempted[source.Server.Url] = 0;
        try
        {
            var result = await _downloadService.DownloadItemAsync(
                source.Client, record, _tempPath, _speedLimit,
                config.IncludeCompanionFiles, config, itemProgress, cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                breaker.RecordSuccess();
                record.CompanionFiles = result.CompanionFiles;
                Interlocked.Increment(ref _successCount);
                _changedFiles.Enqueue(record.LocalPath);
                Logger.LogInformation("DOWNLOADED: {FileName} ({Size}) -> {LocalPath}", fileName, fileSize, record.LocalPath);
            }
            else
            {
                breaker.RecordFailure(result.ErrorMessage);
                Logger.LogError("FAILED: {FileName} ({Size}) - {Error}. Source: {SourcePath}",
                    fileName, fileSize, result.ErrorMessage, record.SourcePath);
                // Include filename + size + source path in the message so the
                // Reason field surfaces actionable context. Bare "Connection
                // timeout" is useless when 50 items errored, user can't tell
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

    // Content has no SyncableValue fields, so there is nothing to mark synced.
    // The base's post-apply Status, LastSyncTime, and Reason write covers the
    // rest, and <see cref="ApplyAsync"/> already populated CompanionFiles.
    // The retry count resets so a later failure gets the full allowance again.
    /// <inheritdoc />
    protected override void OnApplySucceeded(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.RetryCount = 0;
    }

    // Increments <see cref="SyncItem.RetryCount"/> so the
    // <c>MaxRetryCount</c> cap in <see cref="GetItemsToApply"/> is honored. A row the circuit
    // breaker skipped was never tried, so it keeps its count.
    /// <inheritdoc />
    protected override void OnApplyFailed(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_skippedByBreaker.TryRemove(record.Id, out _))
        {
            return;
        }

        record.RetryCount++;
    }

    /// <inheritdoc />
    protected override async Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var config = ConfigManager.Configuration;

        // Progress allocation within finalize:
        //   0 to 10 %  pending-deletion processing
        //  10 to 95 %  library refresh (the slow part)
        //  95 to 100 %  config save + cleanup
        progress.Report(0);

        // Process pending-deletion rows (separate from Queued items, these
        // were soft-deleted by the Refresh task). Always run so the user's
        // "approve deletion" action gets picked up even when nothing was
        // queued.
        var removed = new List<string>();
        var (deleted, _) = FileDeletionService.ProcessPendingDeletions(TypedManager, config, Logger, removed, cancellationToken);
        foreach (var path in removed)
        {
            _changedFiles.Enqueue(path);
        }

        Interlocked.Add(ref _deletedCount, deleted);

        // A server that connected and had nothing to download has proven itself as far as this run can
        // tell, so old connection failures are forgotten. A server that downloaded keeps whatever its
        // downloads recorded.
        if (!cancellationToken.IsCancellationRequested)
        {
            foreach (var source in Sources)
            {
                SettleAfterRun(GetOrCreateCircuitBreaker(source.Server.Url), _downloadAttempted.ContainsKey(source.Server.Url));
            }
        }

        progress.Report(10);

        // Library refresh is conditional on actual filesystem changes
        // (downloads or deletions). A no-op run skips the refresh because
        // ValidateMediaLibrary is expensive on large libraries and there's
        // nothing for Jellyfin to discover when we wrote/removed nothing.
        // A run that touched a few folders has Jellyfin refresh just those,
        // and only a run that touched many falls back to the full scan.
        if ((_successCount > 0 || _deletedCount > 0) && !RefreshChangedFolders())
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

        await VerifyLinksAsync(cancellationToken).ConfigureAwait(false);
        ReconcileLinkCache();

        _tempPath = null;
    }

    // Every run checks this server's links against the servers they stream from, so a link that can
    // no longer play shows on the Sync tab before someone presses play, and a link still without
    // media info takes the source's.
    private async Task VerifyLinksAsync(CancellationToken cancellationToken)
    {
        if (_linkVerifier is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        WarnAboutChapterImages();
        try
        {
            var found = await _linkVerifier.VerifyAsync(
                TypedManager,
                key => Sources.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))?.Client,
                cancellationToken).ConfigureAwait(false);
            if (found.Checked > 0)
            {
                Logger.LogInformation(
                    "{Task}: checked {Checked} link(s): {Dead} cannot play, {Restored} can play again, {Described} took media info from the source",
                    Name,
                    found.Checked,
                    found.Dead,
                    found.Restored,
                    found.Described);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Task}: could not check links", Name);
        }
    }

    // Every run brings the link cache in line with the settings: the items to always keep are queued,
    // files whose link is gone are dropped, and retention and the size limit are applied.
    private void ReconcileLinkCache()
    {
        try
        {
            var queued = _linkCacheFiller?.Reconcile() ?? 0;
            if (queued > 0)
            {
                Logger.LogInformation("{Task}: queued {Count} item(s) chosen to always keep for the link cache", Name, queued);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Task}: could not bring the link cache in line with its settings", Name);
        }
    }

    // Jellyfin never makes chapter images for a link, and on a refresh it drops the ones taken from the
    // source unless the library has chapter image extraction on. So a Link mapping into a library with
    // it off is called out, since its links would lose their chapter images.
    private void WarnAboutChapterImages()
    {
        var config = ConfigManager.Configuration;
        if (!config.TakeLinkImagesFromSource)
        {
            return;
        }

        try
        {
            var linked = config.GetPullServers()
                .SelectMany(s => s.GetEnabledLibraryMappings())
                .Where(m => m.StorageMode == StorageMode.Link)
                .Select(m => m.LocalLibraryId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var library in _libraryManager.GetVirtualFolders() ?? new List<MediaBrowser.Model.Entities.VirtualFolderInfo>())
            {
                if (library.LibraryOptions is { EnableChapterImageExtraction: false }
                    && Guid.TryParse(library.ItemId, out var id)
                    && linked.Any(l => Guid.TryParse(l, out var mapped) && mapped == id))
                {
                    Logger.LogWarning(
                        "{Task}: the library '{Library}' holds links and has chapter image extraction off, so Jellyfin drops the chapter images its links take from their source. Turn chapter image extraction on for that library to keep them. Jellyfin never extracts them from a link itself",
                        Name,
                        library.Name);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{Task}: could not check the libraries that hold links", Name);
        }
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastSyncEndTime = utcNow;
    }


    private ContentSyncTableManager TypedManager => (ContentSyncTableManager)Manager;

    // Writing a link reads nothing from its server, so it goes ahead when that server is down, refused
    // by its breaker, or holds a key that may not download.
    /// <inheritdoc />
    protected override bool AppliesWithoutSource(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.StorageMode == StorageMode.Link
            && ConfigManager.Configuration.FindServer(record.ServerKey) is { IsEnabled: true };
    }

    // A run with only links to write needs no disk space to speak of and no server to download from,
    // so the checks that guard downloads do not stop it. Any copy queued keeps them in force.
    // Rows from a server that is not set up here are left alone by every run, so they neither count as
    // copies to guard nor as links to write.
    private bool OnlyLinksQueued()
    {
        var pulling = ConfigManager.Configuration.GetPullServers().Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queued = GetItemsToApply()
            .Where(r => AppliesWithoutSource(r) || (r.ServerKey is { } key && pulling.Contains(key)) || (string.IsNullOrEmpty(r.ServerKey) && pulling.Count > 0))
            .ToList();
        return queued.Count > 0 && queued.All(r => r.StorageMode == StorageMode.Link);
    }

    /// <summary>
    /// Whether a connected server takes part in this run. Connecting is not a download, so it leaves
    /// the breaker's failure count alone: only a finished download records success. Resetting it here
    /// cleared the count at the start of every run, so failures spread over several runs never added
    /// up, and a server whose cooldown had passed was fully closed again before a single download
    /// proved it healthy.
    /// </summary>
    /// <param name="breaker">The server's breaker.</param>
    /// <param name="reason">Why the server is left out, when it is.</param>
    /// <returns>True when the server's downloads may run.</returns>
    protected static bool AdmitServer(CircuitBreaker breaker, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(breaker);
        return breaker.AllowOperation(out reason);
    }

    /// <summary>
    /// Settles a connected server's breaker at the end of a run. Connection failures count toward the
    /// breaker so a server that keeps dropping off pauses, but a server that connected and then had
    /// nothing to download clears them, so occasional failed connects do not add up over weeks. When
    /// the run downloaded from it, the downloads already recorded success or failure and that stands.
    /// </summary>
    /// <param name="breaker">The server's breaker.</param>
    /// <param name="downloadsAttempted">Whether this run downloaded anything from the server.</param>
    protected static void SettleAfterRun(CircuitBreaker breaker, bool downloadsAttempted)
    {
        ArgumentNullException.ThrowIfNull(breaker);
        if (!downloadsAttempted && breaker.ConsecutiveFailures > 0)
        {
            breaker.RecordSuccess();
        }
    }

    private CircuitBreaker GetOrCreateCircuitBreaker(string sourceUrl)
    {
        lock (_circuitBreakerLock)
        {
            if (_circuitBreakers.TryGetValue(sourceUrl, out var existing))
            {
                return existing;
            }

            // Evict entries for URLs no server is configured with any more, to
            // prevent unbounded growth. Every current server keeps its breaker,
            // or with two servers each lookup would evict the other's state.
            var current = new HashSet<string>(
                ConfigManager.Configuration.GetPullServers().Select(s => s.Url),
                StringComparer.OrdinalIgnoreCase)
            {
                sourceUrl
            };
            var stale = _circuitBreakers.Keys
                .Where(k => !current.Contains(k))
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

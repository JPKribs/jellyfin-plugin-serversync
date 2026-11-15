using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Downloads;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.ContentSync;

/// <summary>
/// Downloads the queued Content rows in parallel, up to <see cref="PluginConfiguration.MaxConcurrentDownloads"/> at once, then processes pending deletions and refreshes the library.
/// The checks before a run live in <see cref="BeforeRunAsync"/>, and the work after it in <see cref="FinalizeAsync"/>.
/// </summary>
public class SyncMissingContentTask
    : SyncQueueTaskBase<SyncItem, string>
{
    private const int DefaultMaxRetries = 3;

    /// <summary>
    /// The circuit breakers, one for each configured source, by server URL.
    /// Their state lasts across runs, and a breaker is dropped once its URL is no longer configured.
    /// </summary>
    private static readonly Dictionary<string, CircuitBreaker> _circuitBreakers = new();
    private static readonly object _circuitBreakerLock = new();

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly DownloadService _downloadService;

    /// <summary>
    /// Every file this run wrote, skipped as already present, or removed, so the refresh afterwards
    /// only looks at the folders that changed.
    /// </summary>
    private readonly ConcurrentQueue<string> _changedFiles = new();

    private readonly ConcurrentDictionary<long, byte> _skippedByBreaker = new();
    private readonly ConcurrentDictionary<string, byte> _downloadAttempted = new(StringComparer.OrdinalIgnoreCase);
    private string? _tempPath;
    private long _speedLimit;
    private int _successCount;
    private int _deletedCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingContentTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="libraryManager">Reads and changes Jellyfin's library.</param>
    /// <param name="libraryMonitor">Tells Jellyfin to ignore a path while a file is written to it.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="downloadService">Downloads files from a source.</param>
    /// <param name="linkWriter">Writes link files, or null when linking is not available.</param>
    /// <param name="linkVerifier">Checks that a link still streams, or null when linking is not available.</param>
    /// <param name="linkCacheFiller">Fills the link cache, or null when linking is not available.</param>
    /// <param name="linkCache">Holds local copies of links that were played, or null when linking is not available.</param>
    public SyncMissingContentTask(
        ILogger<SyncMissingContentTask> logger,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IPluginConfigurationManager configManager,
        ContentSyncTableManager manager,
        ISourceServerClientFactory clientFactory,
        DownloadService downloadService,
        LinkWriter? linkWriter = null,
        LinkVerifier? linkVerifier = null,
        LinkCacheFiller? linkCacheFiller = null,
        LinkCache? linkCache = null)
        : base(logger, manager, clientFactory, configManager)
    {
        _linkCache = linkCache;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _downloadService = downloadService;
        _linkWriter = linkWriter;
        _linkVerifier = linkVerifier;
        _linkCacheFiller = linkCacheFiller;
    }

    private readonly LinkWriter? _linkWriter;
    private readonly LinkVerifier? _linkVerifier;
    private readonly LinkCacheFiller? _linkCacheFiller;
    private readonly LinkCache? _linkCache;

    /// <inheritdoc />
    public override string Name => "Copy Content";

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

    /// <summary>
    /// Returns the Queued rows along with Errored rows that have retries left, and copies whose retries ran out, which are tried again so a failure whose cause has passed clears without a Retry.
    /// A row whose library mapping or server is off is left as it is, since applying it could only fail.
    /// </summary>
    /// <inheritdoc />
    protected override IList<SyncItem> GetItemsToApply()
    {
        var typedManager = TypedManager;
        var maxRetries = ConfigManager.Configuration.MaxRetryCount > 0
            ? ConfigManager.Configuration.MaxRetryCount
            : DefaultMaxRetries;
        // Rows under a mapping that is off could only fail, so they are skipped.
        var mapped = new HashSet<string>(
            ConfigManager.Configuration.GetPullServers().SelectMany(server => server.GetEnabledLibraryMappings().Select(mapping => server.Key + "|" + mapping.SourceLibraryId)),
            StringComparer.OrdinalIgnoreCase);
        return typedManager.GetByStatusStrict(SyncStatus.Queued)
            .Concat(typedManager.GetErroredItemsForRetry(maxRetries))
            .Concat(typedManager.GetErroredCopiesSince(maxRetries, DateTime.UtcNow - TimeSpan.FromHours(1)))
            .Where(row => string.IsNullOrEmpty(row.ServerKey) || mapped.Contains(row.ServerKey + "|" + row.SourceLibraryId))
            .ToList();
    }

    /// <inheritdoc />
    protected override async Task<bool> BeforeRunAsync(CancellationToken cancellationToken)
    {
        var config = ConfigManager.Configuration;
        _downloadAttempted.Clear();

        // Links first, since writing one needs no source and no disk space.
        RepairLinks(config, cancellationToken);

        // The waiting removals may free the disk, so they run before giving up.
        if (!DiskSpaceService.HasSufficientSpace(config, out _) && !OnlyLinksQueued())
        {
            var (freed, _) = FileDeletionService.ProcessPendingDeletions(TypedManager, config, Logger, new List<string>(), cancellationToken);
            Interlocked.Add(ref _deletedCount, freed);
        }

        if (!DiskSpaceService.HasSufficientSpace(config, out var insufficientPath) && !OnlyLinksQueued())
        {
            var diskInfo = DiskSpaceService.GetDiskSpaceInfo(config).FirstOrDefault(d => d.Path == insufficientPath);
            var message = diskInfo != null
                ? DiskSpaceService.FormatInsufficientSpaceMessage(insufficientPath!, diskInfo.FreeBytes, config.MinimumFreeDiskSpaceGb)
                : $"Insufficient disk space on {insufficientPath}";
            FailPreflight(message);
            return false;
        }

        // A server whose breaker is open is left out, and its rows stay Queued.
        if (!await base.BeforeRunAsync(cancellationToken).ConfigureAwait(false) && !OnlyLinksQueued())
        {
            return false;
        }

        // The run's cleanup sees only the sources kept, so the rest are disposed here.
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
                    Logger.LogWarning("{Task}: leaving '{Server}' out of this run: {Reason}", Name, source.Name, FormatUtilities.SanitizeForLog(circuitReason, 300));
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

    /// <summary>
    /// Checks a single row from a hint with the same disk check and download setup as a run.
    /// It connects no servers, since the hint already names the one to pull from.
    /// </summary>
    /// <inheritdoc />
    protected override Task<bool> BeforeRowAsync(SyncItem record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var config = ConfigManager.Configuration;
        // A link needs no disk space, so a full disk does not stop it.
        if (record.StorageMode != StorageMode.Link && !DiskSpaceService.HasSufficientSpace(config, out var insufficientPath))
        {
            FailPreflight($"insufficient disk space on {insufficientPath}");
            return Task.FromResult(false);
        }

        PrepareDownloads(config);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Reports a file that arrived through a hint to the library as changed, so Jellyfin refreshes that one folder and not every library.
    /// When Jellyfin cannot take the folder, as for a first file in an empty library, a library scan is queued.
    /// </summary>
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

    /// <summary>Clears stale download entries and resets the state a run of downloads keeps.</summary>
    private void PrepareDownloads(PluginConfiguration config)
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

    /// <summary>
    /// A library's own folder or one inside it. The roots above every library never lead a refresh anywhere.
    /// </summary>
    private bool IsLibraryFolder(string path)
        => _libraryManager.FindByPath(path, true) is Folder
            and not AggregateFolder
            and not UserRootFolder
            and not CollectionFolder;

    /// <summary>
    /// Tells Jellyfin which folders changed, so it refreshes those and nothing else.
    /// It returns <c>false</c> when there are too many for that to be cheaper than one full scan, which the caller then runs.
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

    /// <summary>
    /// Weighs a row by its file size, so the run's progress follows bytes moved and not files finished.
    /// </summary>
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

        // A link downloads nothing, so it never reaches the download steps.
        if (record.StorageMode == StorageMode.Link)
        {
            await WriteLinkAsync(record, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A kept copy becomes a link again, and the copy moves into the cache.
        if (record.StorageMode == StorageMode.Unkeeping)
        {
            await UnkeepAsync(record, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The .strm goes only once the copy is in place, and plays until then.
        if (record.StorageMode == StorageMode.Promoting)
        {
            var linkPath = record.LocalPath;
            var copyPath = LinkPaths.CopiedPathOf(record) ?? throw new InvalidOperationException($"The link has no path (source: {record.SourcePath})");
            record.LocalPath = copyPath;

            // A move out of the cache that was cut short leaves a useless file.
            try
            {
                File.Delete(copyPath + ".serversync-move");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogDebug(ex, "Could not clear a half-moved file beside {Path}", FormatUtilities.SanitizeForLog(copyPath));
            }

            try
            {
                // A copy that already arrived is not fetched again, whatever the disk holds.
                var arrived = record.SourceSize > 0 && File.Exists(copyPath) && new FileInfo(copyPath).Length == record.SourceSize;
                if (!arrived && !await MoveFromCacheAsync(record, cancellationToken).ConfigureAwait(false))
                {
                    await DownloadAsync(record, itemProgress, cancellationToken).ConfigureAwait(false);
                }
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

    /// <summary>Writes a row's link with its companions and .nfo, and takes the source's media info again when the file behind it changed.</summary>
    private async Task WriteLinkAsync(SyncItem record, CancellationToken cancellationToken, string? replacing = null)
    {
        // An .nfo gone while its link is still there was removed by the user.
        var linkWasThere = !string.IsNullOrEmpty(record.LocalPath) && File.Exists(record.LocalPath);
        await TakeOverLinkPathAsync(record, cancellationToken).ConfigureAwait(false);
        (_linkWriter ?? throw new InvalidOperationException("Links cannot be written on this server")).Write(record, TypedManager, ConfigManager.Configuration, replacing);

        // Read before the companions replace the row's list, where a written .nfo is recorded.
        var nfoRecorded = LinkNfo.Recorded(record);
        var nfoRemoved = linkWasThere && LinkNfo.RemovedByUser(record);
        await DownloadLinkCompanionsAsync(record, cancellationToken).ConfigureAwait(false);
        if (nfoRemoved)
        {
            LinkNfo.Record(record);
        }
        else
        {
            await WriteLinkNfoAsync(record, nfoRecorded, cancellationToken).ConfigureAwait(false);
        }

        // A link already in the library keeps the media info of its old file.
        if (linkWasThere && _linkVerifier is not null && SourceFor(record) is { } described
            && await _linkVerifier.RedescribeAsync(record, described.Client, cancellationToken).ConfigureAwait(false))
        {
            Logger.LogDebug("Took the media info for {Path} again, since the file behind it changed", FormatUtilities.SanitizeForLog(record.LocalPath));
        }

        Interlocked.Increment(ref _successCount);
        _changedFiles.Enqueue(record.LocalPath!);
    }

    /// <summary>
    /// Lets a new link take over the .strm of the link for the old file it replaces, as when a source file changes only its extension.
    /// It happens once the old file is gone from its server or its removal is scheduled, and until then the write is refused.
    /// </summary>
    private async Task TakeOverLinkPathAsync(SyncItem record, CancellationToken cancellationToken)
    {
        if (_linkWriter is null || LinkWriter.Predecessor(record, TypedManager) is not { } old)
        {
            return;
        }

        var gone = old.Status == SyncStatus.Deleting
            || (_linkVerifier is not null && SourceFor(old) is { } source
                && await _linkVerifier.GoneFromSourceAsync(old, source.Client, cancellationToken).ConfigureAwait(false));
        if (!gone)
        {
            return;
        }

        _linkWriter.TakeOver(record, old, TypedManager);
        _linkCache?.Remove(old.SourceItemId);
    }

    /// <summary>
    /// Turns a kept copy back into a link, but only when the source still has the file as it was and offers it as a link.
    /// The link is written beside the copy, which then moves into the link cache or is removed, and until then the copy stays and plays.
    /// </summary>
    private async Task UnkeepAsync(SyncItem record, CancellationToken cancellationToken)
    {
        var copyPath = record.LocalPath ?? throw new InvalidOperationException($"The copy has no path (source: {record.SourcePath})");
        var name = Path.GetFileName(copyPath);
        var linkPath = LinkPaths.LinkPathFor(copyPath) ?? throw new InvalidOperationException($"{name} cannot be a link, so the copy stays");
        var source = RequireSource(record);
        if (!Guid.TryParse(record.SourceItemId, out var itemId))
        {
            throw new InvalidOperationException($"{name} has no source item, so the copy stays");
        }

        var item = await source.Client.FindItemDetailsAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null || string.IsNullOrEmpty(item.Path) || source.Client.IsPeerLink(item.Id)
            || item.Path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase)
            || MediaItemUtilities.GetItemSize(item) != record.SourceSize)
        {
            throw new InvalidOperationException($"'{source.Name}' no longer has {name} as it was, so the copy stays");
        }

        var capabilities = await source.Client.GetPeerCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (capabilities?.Features?.Contains(LinkFeature.Name, StringComparer.OrdinalIgnoreCase) != true)
        {
            throw new InvalidOperationException($"'{source.Name}' does not offer links to this server, so the copy of {name} stays");
        }

        record.StorageMode = StorageMode.Link;
        record.LocalPath = linkPath;
        record.LocalItemId = null;
        try
        {
            await WriteLinkAsync(record, cancellationToken, copyPath).ConfigureAwait(false);
        }
        catch
        {
            record.StorageMode = StorageMode.Unkeeping;
            record.LocalPath = copyPath;
            throw;
        }

        record.Kept = KeptReason.None;
        if (File.Exists(copyPath))
        {
            if (_linkCache is not null && await Task.Run(() => _linkCache.Adopt(record.SourceItemId, copyPath, record.SourceSize), cancellationToken).ConfigureAwait(false))
            {
                Logger.LogInformation("{File} is a link again, and its copy moved into the link cache", FormatUtilities.SanitizeForLog(name));
            }
            else
            {
                File.Delete(copyPath);
                Logger.LogInformation("{File} is a link again, and its copy was removed, since the link cache could not take it", FormatUtilities.SanitizeForLog(name));
            }

            _changedFiles.Enqueue(copyPath);
        }
    }

    /// <summary>
    /// Downloads subtitles and other companions beside a link, the same way a copy gets them.
    /// The link is still written when they cannot be fetched.
    /// </summary>
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

    /// <summary>
    /// Writes an .nfo beside the link, so Jellyfin identifies it from the source's own details and not from the file name.
    /// The link is still written when the details cannot be read, and an .nfo the plugin did not write is never replaced.
    /// </summary>
    private async Task WriteLinkNfoAsync(SyncItem record, bool recorded, CancellationToken cancellationToken)
    {
        // An .nfo from an earlier run stays recorded when this run cannot write it.
        if (recorded)
        {
            LinkNfo.Record(record);
        }

        if (SourceFor(record) is not { } source || !Guid.TryParse(record.SourceItemId, out var itemId) || string.IsNullOrEmpty(record.LocalPath))
        {
            return;
        }

        try
        {
            var items = await source.Client.GetItemsByIdsAsync(new[] { itemId }, LinkNfo.Fields, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (items.FirstOrDefault() is not { } item)
            {
                return;
            }

            if (LinkNfo.Write(record.LocalPath, item))
            {
                LinkNfo.Record(record);
            }
            else
            {
                Logger.LogDebug("Left the .nfo beside {Path} as it is, since Server Sync did not write it", FormatUtilities.SanitizeForLog(record.LocalPath));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning("Wrote the link {Path} without an .nfo: {Reason}", FormatUtilities.SanitizeForLog(record.LocalPath), FormatUtilities.SanitizeForLog(ex.Message, 300));
        }
    }

    /// <summary>
    /// Moves the cached file into the library for a link becoming a copy, under the same checks as a download.
    /// It returns <c>false</c> when it cannot, and the copy is downloaded instead.
    /// </summary>
    private async Task<bool> MoveFromCacheAsync(SyncItem record, CancellationToken cancellationToken)
    {
        if (_linkCache?.HoldsWhole(record.SourceItemId, record.SourceSize) != true || string.IsNullOrEmpty(record.LocalPath))
        {
            return false;
        }

        var config = ConfigManager.Configuration;
        if (!DownloadService.ValidateForDownload(record, config, TypedManager).IsValid)
        {
            return false;
        }

        using var spaceHold = await DiskSpaceService.ReserveForFileAsync(record.LocalPath, record.SourceSize, config.MinimumFreeDiskSpaceGb, cancellationToken).ConfigureAwait(false);
        if (spaceHold is null || !ActiveDownloadTracker.TryStartDownload(record.SourceItemId, record.LocalPath))
        {
            return false;
        }

        try
        {
            var path = record.LocalPath;
            if (!await Task.Run(() => _linkCache.MoveTo(record.SourceItemId, path, record.SourceSize), cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        finally
        {
            ActiveDownloadTracker.CompleteDownload(record.SourceItemId);
        }

        Logger.LogInformation("Moved {FileName} from the link cache into the library", FormatUtilities.SanitizeForLog(Path.GetFileName(record.LocalPath)));
        Interlocked.Increment(ref _successCount);
        _changedFiles.Enqueue(record.LocalPath);
        return true;
    }

    /// <summary>
    /// Downloads a copy: disk space, validation, and the download itself.
    /// </summary>
    private async Task DownloadAsync(SyncItem record, IProgress<double>? itemProgress, CancellationToken cancellationToken)
    {
        if (_tempPath == null)
        {
            throw new InvalidOperationException("BeforeRunAsync did not complete. Aborting apply.");
        }

        // The breaker may open mid run, and a skipped row is not a failure.
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

        // Held until the download ends, so parallel downloads cannot overfill the disk together.
        using var spaceHold = await DiskSpaceService.ReserveForFileAsync(record.LocalPath, record.SourceSize, config.MinimumFreeDiskSpaceGb, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Insufficient disk space for {fileName} ({fileSize}). Required: {fileSize} + {config.MinimumFreeDiskSpaceGb} GB reserve");

        var (isValid, validationError) = DownloadService.ValidateForDownload(record, config, TypedManager);
        if (!isValid)
        {
            throw new InvalidOperationException(
                $"Validation failed for {fileName}: {validationError ?? "unknown reason"} (source: {record.SourcePath ?? record.SourceItemId})");
        }

        // What an interrupted replacement set aside goes back first.
        ReplacementRecovery.Restore(record.LocalPath, Logger);

        if (DownloadService.ShouldSkipDownload(record, 0, out var skipReason))
        {
            Logger.LogDebug("Skipped: {FileName} ({Size}) - {Reason}", FormatUtilities.SanitizeForLog(fileName), fileSize, FormatUtilities.SanitizeForLog(skipReason, 300));
            Interlocked.Increment(ref _successCount);
            _changedFiles.Enqueue(record.LocalPath);
            return;
        }

        var tempFileName = FileNameSanitizer.SanitizeTempFileName(record.SourceItemId, record.LocalPath);
        var tempFilePath = Path.Combine(_tempPath, tempFileName);

        if (!ActiveDownloadTracker.TryStartDownload(record.SourceItemId, tempFilePath))
        {
            // Already downloading on another worker, which stores the outcome.
            Logger.LogDebug("Item {SourceItemId} is already being downloaded, skipping", FormatUtilities.SanitizeForLog(record.SourceItemId));
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
                record.OriginServerId = await OriginOfDownloadAsync(source, record, cancellationToken).ConfigureAwait(false);

                // The file here is the source's now.
                if (record.Kept == KeptReason.Own)
                {
                    record.Kept = KeptReason.None;
                }
                await RetireReplacedCopiesAsync(record, source, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _successCount);
                _changedFiles.Enqueue(record.LocalPath);
                Logger.LogInformation("DOWNLOADED: {FileName} ({Size}) -> {LocalPath}", FormatUtilities.SanitizeForLog(fileName), fileSize, FormatUtilities.SanitizeForLog(record.LocalPath));
            }
            else
            {
                breaker.RecordFailure(result.ErrorMessage);
                Logger.LogError("FAILED: {FileName} ({Size}) - {Error}. Source: {SourcePath}",
                    FormatUtilities.SanitizeForLog(fileName), fileSize, FormatUtilities.SanitizeForLog(result.ErrorMessage, 300), FormatUtilities.SanitizeForLog(record.SourcePath));
                throw new InvalidOperationException(
                    $"Download failed for {fileName} ({fileSize}): {result.ErrorMessage ?? "unknown error"} (source: {record.SourcePath ?? record.SourceItemId})");
            }
        }
        finally
        {
            ActiveDownloadTracker.CompleteDownload(record.SourceItemId);
        }
    }

    /// <summary>
    /// Retires an old copy once a file of the same name in another container replaced it on the source, as Film.mkv by Film.mp4.
    /// The old copy follows Removed files as soon as the source answers that it no longer holds the old file, and nothing changes while it cannot be asked or holds both.
    /// </summary>
    private async Task RetireReplacedCopiesAsync(SyncItem record, ScanSource source, CancellationToken cancellationToken)
    {
        var config = ConfigManager.Configuration;
        if (!config.DeletesMissing || string.IsNullOrEmpty(record.LocalPath)
            || Path.GetDirectoryName(record.LocalPath) is not { Length: > 0 } folder || !Directory.Exists(folder))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(record.LocalPath);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (string.Equals(file, record.LocalPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase)
                || TypedManager.GetByLocalPath(file) is not { } old
                || string.Equals(old.SourceItemId, record.SourceItemId, StringComparison.OrdinalIgnoreCase)
                || old.Status != SyncStatus.Synced
                || old.StorageMode != StorageMode.Copy
                || !string.Equals(old.ServerKey, record.ServerKey, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(old.SourceItemId, out var oldId))
            {
                continue;
            }

            try
            {
                if (await source.Client.FindItemDetailsAsync(oldId, cancellationToken).ConfigureAwait(false) is not null)
                {
                    continue;
                }

                // While both files exist, Jellyfin lists the old one as a version.
                var replacement = Guid.TryParse(record.SourceItemId, out var newId)
                    ? await source.Client.FindItemDetailsAsync(newId, cancellationToken).ConfigureAwait(false)
                    : null;
                if (replacement is null
                    || replacement.MediaSources?.Any(version => string.Equals(version.Path, old.SourcePath, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.LogDebug(ex, "Could not ask '{Server}' whether {Path} is still there, so the copy is left for the next full scan", source.Name, FormatUtilities.SanitizeForLog(old.SourcePath));
                continue;
            }

            if (SyncStateService.ProcessMissingItem(TypedManager, old, config.DeletionApproval, Logger).Changed)
            {
                Logger.LogInformation("{Old} was replaced on '{Server}' by {New}, so the old copy follows Removed files now", FormatUtilities.SanitizeForLog(Path.GetFileName(old.LocalPath)), source.Name, FormatUtilities.SanitizeForLog(Path.GetFileName(record.LocalPath)));
            }
        }
    }

    /// <summary>
    /// Gets the server a downloaded file first came from, which is the origin its source names or else the source itself.
    /// It is left empty when the source cannot say, so a file of unknown origin is never treated as another server's.
    /// </summary>
    private async Task<string?> OriginOfDownloadAsync(ScanSource source, SyncItem record, CancellationToken cancellationToken)
    {
        try
        {
            var named = await source.Client.OriginOfAsync(record.SourcePath, cancellationToken).ConfigureAwait(false);
            return !string.IsNullOrEmpty(named) ? named : string.IsNullOrEmpty(source.Server.ServerId) ? null : source.Server.ServerId;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Could not read where {Path} first came from, so no origin is recorded for it", FormatUtilities.SanitizeForLog(record.SourcePath));
            return null;
        }
    }

    /// <summary>
    /// Resets the retry count, so a later failure gets the full allowance again.
    /// Content has no values to mark synced, and the base stores the rest.
    /// </summary>
    /// <inheritdoc />
    protected override void OnApplySucceeded(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.RetryCount = 0;
    }

    /// <summary>
    /// Counts a failed try on <see cref="SyncItem.RetryCount"/>, which the cap in <see cref="GetItemsToApply"/> reads.
    /// A row the circuit breaker skipped was never tried, so it keeps its count.
    /// </summary>
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

        progress.Report(0);

        // Runs even when nothing was queued, so an approved deletion is picked up.
        var removed = new List<string>();
        var (deleted, _) = FileDeletionService.ProcessPendingDeletions(TypedManager, config, Logger, removed, cancellationToken);
        foreach (var path in removed)
        {
            _changedFiles.Enqueue(path);
        }

        Interlocked.Add(ref _deletedCount, deleted);

        // A server that connected with nothing to download has its old connection failures forgotten.
        if (!cancellationToken.IsCancellationRequested)
        {
            foreach (var source in Sources)
            {
                SettleAfterRun(GetOrCreateCircuitBreaker(source.Server.Url), _downloadAttempted.ContainsKey(source.Server.Url));
            }
        }

        progress.Report(10);

        // A run that changed no files skips the library refresh, which is costly.
        if ((_successCount > 0 || _deletedCount > 0) && !RefreshChangedFolders())
        {
            try
            {
                Logger.LogInformation("Triggering library refresh");
                var refreshProgress = new Progress<double>(p =>
                    progress.Report(10 + (85.0 * Math.Clamp(p, 0, 100) / 100.0)));
                await _libraryManager.ValidateMediaLibrary(refreshProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Failed to trigger library refresh");
            }
        }

        progress.Report(95);

        await VerifyLinksAsync(cancellationToken).ConfigureAwait(false);
        ReconcileLinkCache();

        _tempPath = null;
    }

    /// <summary>
    /// Checks this server's links against the servers they stream from, so a link that cannot play shows on the Sync tab before someone presses play.
    /// A link still without media info takes the source's.
    /// </summary>
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "{Task}: could not check links", Name);
        }
    }

    /// <summary>
    /// Brings the link cache in line with the settings: files whose link is gone or became a copy are dropped, and retention and the size limit are applied.
    /// </summary>
    private void ReconcileLinkCache()
    {
        try
        {
            _linkCacheFiller?.Reconcile();
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "{Task}: could not bring the link cache in line with its settings", Name);
        }
    }

    /// <summary>
    /// Warns about a Link mapping into a library with chapter image extraction off.
    /// Jellyfin never makes chapter images for a link, and on a refresh it drops the ones taken from the source unless extraction is on.
    /// </summary>
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
        // A warning that cannot be worked out must not fail the run.
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "{Task}: could not check the libraries that hold links", Name);
        }
    }

    /// <inheritdoc />
    protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastSyncEndTime = utcNow;
    }

    private ContentSyncTableManager TypedManager => (ContentSyncTableManager)Manager;

    /// <summary>
    /// Says a link is written even when its server is down, refused by its breaker, or holds a key that may not download, since writing it reads nothing from that server.
    /// </summary>
    /// <inheritdoc />
    protected override bool AppliesWithoutSource(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.StorageMode == StorageMode.Link
            && ConfigManager.Configuration.FindServer(record.ServerKey) is { IsEnabled: true };
    }

    /// <summary>
    /// Writes again every settled link that is missing or points at an address this server no longer answers on, on every run.
    /// The writer refuses whatever is not a link of its own, such as a real copy at the name or a .strm someone else wrote.
    /// </summary>
    private void RepairLinks(PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (_linkWriter is null)
        {
            return;
        }

        var repaired = 0;
        foreach (var row in TypedManager.GetLinks())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.StorageMode != StorageMode.Link || row.Status is not (SyncStatus.Synced or SyncStatus.Errored)
                || string.IsNullOrEmpty(row.LocalPath) || _linkWriter.IsCurrent(row))
            {
                continue;
            }

            try
            {
                _linkWriter.Write(row, TypedManager, config);
                repaired++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Logger.LogDebug(ex, "Did not write the link {Path} again", FormatUtilities.SanitizeForLog(row.LocalPath));
            }
        }

        if (repaired > 0)
        {
            Logger.LogInformation("{Task}: wrote {Count} link(s) again that were missing or out of date", Name, repaired);
        }
    }

    /// <summary>
    /// Says whether the run has only links to write, which need no disk space and no server to download from, so the checks that guard downloads do not stop it.
    /// Rows from a server that is not set up here count as neither copies nor links.
    /// </summary>
    private bool OnlyLinksQueued()
    {
        var pulling = ConfigManager.Configuration.GetPullServers().Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queued = GetItemsToApply()
            .Where(r => AppliesWithoutSource(r) || (r.ServerKey is { } key && pulling.Contains(key)) || (string.IsNullOrEmpty(r.ServerKey) && pulling.Count > 0))
            .ToList();
        // Nothing queued counts too, so the run's upkeep does not wait for disk space.
        return queued.All(r => r.StorageMode == StorageMode.Link);
    }

    /// <summary>
    /// Says whether a connected server takes part in this run.
    /// Connecting is not a download, so it leaves the breaker's failure count alone, and only a finished download records success.
    /// </summary>
    /// <param name="breaker">The server's breaker.</param>
    /// <param name="reason">Why the server is left out, when it is.</param>
    /// <returns><c>true</c> when the server's downloads may run.</returns>
    protected static bool AdmitServer(CircuitBreaker breaker, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(breaker);
        return breaker.AllowOperation(out reason);
    }

    /// <summary>
    /// Settles a connected server's breaker at the end of a run.
    /// A server that connected and had nothing to download clears its connection failures, and one that downloaded keeps what its downloads recorded.
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

    /// <summary>Gets the breaker for a source URL, creating it and dropping the breakers of URLs no longer configured.</summary>
    private CircuitBreaker GetOrCreateCircuitBreaker(string sourceUrl)
    {
        lock (_circuitBreakerLock)
        {
            if (_circuitBreakers.TryGetValue(sourceUrl, out var existing))
            {
                return existing;
            }

            // Every configured server keeps its breaker, or one lookup would evict another's state.
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

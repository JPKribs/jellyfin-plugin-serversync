using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.ContentSync;

/// <summary>
/// Scans Content: walks every enabled library mapping, fetches its items, and turns each into a <see cref="SyncItem"/> whose status follows the approval settings.
/// Items the sources no longer hold are settled by Removed files.
/// </summary>
public class UpdateSyncTablesTask
    : RefreshSyncTaskBase<SyncItem, ContentRefreshWork, string>
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// How many items each source reported for each mapping this run.
    /// A 0 stops the prune of that mapping's rows, since an empty answer cannot be told from a real removal, and -1 marks a whitelist that is empty on purpose.
    /// </summary>
    private readonly Dictionary<string, int> _seenPerMapping = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The source item ids left out this run because a blacklisted collection or playlist holds them, by the blacklisting mapping.
    /// It is kept for each mapping because a container can hold items from any library, and it is read at prune time so those rows go through approval.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _blacklistExcludedByMapping = new(StringComparer.OrdinalIgnoreCase);

    private bool IsBlacklistExcluded(string? serverKey, string sourceLibraryId, string sourceItemIdN)
        => _blacklistExcludedByMapping.TryGetValue(MapKey(serverKey, sourceLibraryId), out var excluded)
            && excluded.Contains(sourceItemIdN);

    /// <summary>
    /// Two servers set up the same way give their libraries the same id, so a library id alone does
    /// not say whose library it is. Everything kept per mapping is keyed by the server entry as well.
    /// </summary>
    private static string MapKey(string? serverKey, string? sourceLibraryId) => (serverKey ?? string.Empty) + "|" + (sourceLibraryId ?? string.Empty);

    /// <summary>
    /// What a row's own server reported for its library this run. A row with no server on it, or one
    /// counted without a server, reads the count kept under the library id alone.
    /// </summary>
    private int SeenFor(SyncItem row)
        => _seenPerMapping.TryGetValue(MapKey(row.ServerKey, row.SourceLibraryId), out var own) ? own
            : _seenPerMapping.GetValueOrDefault(MapKey(null, row.SourceLibraryId), 0);

    private static LibraryMapping? MappingOf(SyncItem row, Dictionary<string, LibraryMapping> mappings)
        => mappings.TryGetValue(MapKey(row.ServerKey, row.SourceLibraryId), out var own) ? own
            : mappings.GetValueOrDefault(MapKey(null, row.SourceLibraryId));

    /// <summary>
    /// Every given server's mappings under the server's key, and the first of each library id under
    /// the id alone for a row that carries no server.
    /// </summary>
    private static Dictionary<string, LibraryMapping> MappingsByKey(IEnumerable<SourceServer> servers, bool enabledOnly)
    {
        var mappings = new Dictionary<string, LibraryMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers)
        {
            foreach (var mapping in enabledOnly ? server.GetEnabledLibraryMappings() : server.LibraryMappings ?? new List<LibraryMapping>())
            {
                mappings.TryAdd(MapKey(server.Key, mapping.SourceLibraryId), mapping);
                mappings.TryAdd(MapKey(null, mapping.SourceLibraryId), mapping);
            }
        }

        return mappings;
    }

    /// <summary>
    /// Records how many items a mapping's fetch produced, merging with any earlier count for the same library id.
    /// A 0 sticks, a -1 never replaces a real answer, and positive counts add up.
    /// </summary>
    /// <param name="sourceLibraryId">The library's id on the source.</param>
    /// <param name="count">How many items the fetch produced, or -1 for a whitelist that is empty on purpose.</param>
    protected void RecordMappingSeen(string sourceLibraryId, int count) => RecordMappingSeen(null, sourceLibraryId, count);

    /// <summary>
    /// Records the count for one server's mapping. Another server's library with the same id is
    /// counted on its own, so a server that holds nothing never blocks the prune of this server's rows.
    /// </summary>
    /// <param name="serverKey">The server entry key, or null for the first source.</param>
    /// <param name="sourceLibraryId">The library's id on the source.</param>
    /// <param name="count">How many items the fetch produced, or -1 for a whitelist that is empty on purpose.</param>
    protected void RecordMappingSeen(string? serverKey, string sourceLibraryId, int count)
    {
        sourceLibraryId = MapKey(serverKey, sourceLibraryId);
        var hasExisting = _seenPerMapping.TryGetValue(sourceLibraryId, out var existing);
        if (hasExisting && existing == 0)
        {
            return;
        }

        if (count == 0)
        {
            _seenPerMapping[sourceLibraryId] = 0;
            return;
        }

        if (count == -1)
        {
            if (!hasExisting)
            {
                _seenPerMapping[sourceLibraryId] = -1;
            }

            return;
        }

        _seenPerMapping[sourceLibraryId] = !hasExisting || existing == -1 ? count : existing + count;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateSyncTablesTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="libraryManager">Reads and changes Jellyfin's library.</param>
    /// <param name="watermarks">Holds when each source was last scanned, or null when scans are never incremental.</param>
    /// <param name="linkFiles">Reads and writes link files, or null when linking is not available.</param>
    /// <param name="applicationHost">Gives this server's id and name.</param>
    /// <param name="rehomer">Points links at another server, or null when linking is not available.</param>
    public UpdateSyncTablesTask(
        ILogger<UpdateSyncTablesTask> logger,
        IPluginConfigurationManager configManager,
        ContentSyncTableManager manager,
        ISourceServerClientFactory clientFactory,
        ILibraryManager libraryManager,
        ScanWatermarkStore watermarks,
        LinkFiles? linkFiles = null,
        MediaBrowser.Controller.IServerApplicationHost? applicationHost = null,
        LinkRehomer? rehomer = null)
        : base(logger, manager, clientFactory, configManager, watermarks)
    {
        _libraryManager = libraryManager;
        _linkFiles = linkFiles;
        _localServerId = applicationHost?.SystemId;
        _rehomer = rehomer;
    }

    private readonly LinkRehomer? _rehomer;

    /// <summary>
    /// Says whether a file this server already had is never replaced by the other server's.
    /// That holds in a Link library, unless the film is picked to always keep, and in any library of a server this one syncs with as an equal.
    /// </summary>
    private static bool OwnFilesStay(ContentRefreshWork source, bool picked)
        => (source.Mapping.StorageMode == StorageMode.Link && !picked) || source.Source.Server.Mode == ServerMode.Sync;

    /// <summary>
    /// What a settled row says when this server's own file is not the file the other server holds.
    /// </summary>
    private static string? OwnFileNote(ContentRefreshWork source, string localPath, long sourceSize)
    {
        try
        {
            return sourceSize > 0 && new FileInfo(localPath).Length != sourceSize
                ? $"This server's own file, which differs from the one on {source.Source.Name}. Queue it to take theirs"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Settles the removals a source names on a run that lists only what changed, each checked with the source once more and then settled as a full scan would.
    /// A copy another source covers waits for the full scan, and so does a source that names more than half its rows as removed.
    /// </summary>
    /// <inheritdoc />
    protected override async Task<int> ApplyReportedRemovalsAsync(IReadOnlyDictionary<string, SyncItem> existing, HashSet<string> seenKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(seenKeys);
        var config = ConfigManager.Configuration;
        var typedManager = (ContentSyncTableManager)Manager;
        var mappingById = MappingsByKey(config.GetPullServers(), enabledOnly: true);
        var settled = 0;
        foreach (var source in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ListedCleanly(source.Key) || ChangedSince(source) is not { } since)
            {
                continue;
            }

            Models.Peer.PeerChangesResponse? changes;
            try
            {
                // Reaches further back, so two clocks that disagree lose nothing.
                changes = await source.Client.GetPeerChangesAsync(since.AddMinutes(-10), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                Logger.LogDebug(ex, "{Task}: '{Server}' did not say what it removed, so removals wait for the full scan", Name, source.Name);
                continue;
            }

            // A short answer may miss removals, which wait for the full scan.
            if (changes is null || changes.RemovedItemIds.Count == 0)
            {
                continue;
            }

            var named = new HashSet<string>(changes.RemovedItemIds.Select(id => id.Replace("-", string.Empty, StringComparison.Ordinal)), StringComparer.OrdinalIgnoreCase);
            // Read again from the table, since a contested row was built without being stored.
            var stored = existing.Keys.Where(named.Contains)
                .Select(key => typedManager.GetByKey(key))
                .Where(row => row is not null)
                .Select(row => row!)
                .ToList();
            var ofServer = existing.Values.Count(row => string.Equals(row.ServerKey, source.Key, StringComparison.OrdinalIgnoreCase));
            var rows = stored
                .Where(row => string.Equals(row.ServerKey, source.Key, StringComparison.OrdinalIgnoreCase)
                    && IsInScope(row)
                    && row.Status is not (SyncStatus.Ignored or SyncStatus.Deleting)
                    && !(row.Status == SyncStatus.Pending && row.PendingType == PendingType.Deletion))
                .ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            if (ofServer >= PruneGuardRows && rows.Count > ofServer * PruneFractionLimit)
            {
                Logger.LogWarning("{Task}: '{Server}' names {Count} of its {Total} rows as removed, which is left for the full scan to judge", Name, source.Name, rows.Count, ofServer);
                continue;
            }

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // An item put back at the same place has the same id again.
                    if (!Guid.TryParse(row.SourceItemId, out var id) || await source.Client.FindItemDetailsAsync(id, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        continue;
                    }

                    if (row.StorageMode.IsLink())
                    {
                        if (_rehomer is not null && row.StorageMode == StorageMode.Link && IsGoneLink(row, mappingById))
                        {
                            await _rehomer.SettleGoneAsync(row, cancellationToken).ConfigureAwait(false);
                            settled++;
                        }
                    }
                    else if (config.DeletesMissing && !CoveredByAnotherServer(config, row)
                        && SyncStateService.ProcessMissingItem(typedManager, row, config.DeletionApproval, Logger).Changed)
                    {
                        settled++;
                    }
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    Logger.LogDebug(ex, "{Task}: could not confirm that {Path} left '{Server}', so it waits for the full scan", Name, FormatUtilities.SanitizeForLog(row.SourcePath), source.Name);
                }
            }
        }

        if (settled > 0)
        {
            Logger.LogInformation("{Task}: settled {Count} row(s) for files their server says it removed", Name, settled);
        }

        return settled;
    }

    /// <summary>Says whether another source's enabled mapping covers the folder a row's file sits in.</summary>
    private static bool CoveredByAnotherServer(PluginConfiguration config, SyncItem row)
        => !string.IsNullOrEmpty(row.LocalPath)
            && config.GetPullServers().Any(server => !string.Equals(server.Key, row.ServerKey, StringComparison.OrdinalIgnoreCase)
                && server.GetEnabledLibraryMappings().Any(mapping => IsUnder(row.LocalPath, mapping.LocalRootPath)));

    private readonly string? _localServerId;

    /// <summary>
    /// Source item ids whose offers were left out this run because the file is not the offering
    /// server's own. Same lifecycle rules as <see cref="_seenPerMapping"/>.
    /// </summary>
    private readonly HashSet<string> _notTheirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Says who decides about a file a server offers when that server was itself given the file.
    /// A file that first came from this server, or from another server this one syncs the same folder from, is that server's to decide, and any other offer counts.
    /// </summary>
    /// <param name="config">The plugin settings, for the configured sources.</param>
    /// <param name="localServerId">This server's id.</param>
    /// <param name="offeringKey">The key of the server entry making the offer.</param>
    /// <param name="originServerId">The id of the server the file first came from, or empty.</param>
    /// <param name="localPath">Where the file would sit here.</param>
    /// <param name="answered">Whether a server entry answered this run, or null when that is not known.</param>
    /// <returns>The name of the server that decides, or null when the offer counts.</returns>
    public static string? DecidedElsewhere(PluginConfiguration config, string? localServerId, string offeringKey, string? originServerId, string localPath, Func<string, bool>? answered = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(originServerId))
        {
            return null;
        }

        if (SameServer(originServerId, localServerId))
        {
            return "this server";
        }

        foreach (var server in config.GetPullServers())
        {
            if (string.Equals(server.Key, offeringKey, StringComparison.OrdinalIgnoreCase)
                || !SameServer(originServerId, server.ServerId)
                || (answered is not null && !answered(server.Key)))
            {
                continue;
            }

            if (server.GetEnabledLibraryMappings().Any(mapping => IsUnder(localPath, mapping.LocalRootPath)))
            {
                return server.DisplayName;
            }
        }

        return null;
    }

    private static bool SameServer(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && string.Equals(a.Replace("-", string.Empty, StringComparison.Ordinal), b.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var folder = root.TrimEnd('/', '\\');
        return path.Length > folder.Length + 1
            && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && (path[folder.Length] == '/' || path[folder.Length] == '\\');
    }

    /// <summary>
    /// Says whether a row's offer was left out this run and the row records no origin.
    /// Nothing says this server was given the file, so the row and the file are left as they are.
    /// </summary>
    private bool KeptAsItIs(SyncItem row) => _notTheirs.Contains(row.SourceItemId) && string.IsNullOrEmpty(row.OriginServerId);

    /// <inheritdoc />
    protected override async Task<List<ContentRefreshWork>> DropOffersNotTheirsAsync(List<ContentRefreshWork> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var config = ConfigManager.Configuration;
        var kept = new List<ContentRefreshWork>(items.Count);
        foreach (var item in items)
        {
            var path = item.SourceItem.Path;
            string? origin = null;
            if (!string.IsNullOrEmpty(path) && item.SourceItem.Id.HasValue)
            {
                origin = await item.Source.Client.OriginOfAsync(path, cancellationToken).ConfigureAwait(false);
            }

            // In a Link library every holder's offer counts, so only copies are checked.
            if (origin is not null
                && AlwaysKeep.StorageFor(item.Mapping, path) != StorageMode.Link
                && DecidedElsewhere(config, _localServerId, item.Source.Key, origin, PathUtilities.TranslatePath(path!, item.Mapping.SourceRootPath, item.Mapping.LocalRootPath), ListedCleanly) is { } decider)
            {
                _notTheirs.Add(item.SourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture));
                Logger.LogDebug("Left out {Path} offered by '{Server}': the file first came from {Decider}, which decides about it", FormatUtilities.SanitizeForLog(path), item.Source.Name, decider);
                continue;
            }

            kept.Add(item);
        }

        if (kept.Count < items.Count)
        {
            Logger.LogInformation("{Task}: {Count} file(s) offered by a server that was given them were left to the server they first came from", Name, items.Count - kept.Count);
        }

        return kept;
    }

    private readonly LinkFiles? _linkFiles;

    /// <summary>
    /// Whether each server offers links, asked once per run and only for a server with a Link mapping.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<bool>> _linkingOffered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What each server last answered about links, kept across runs, and the copy this run started with.
    /// A server's scope carries the starting answer, so the run after a server starts offering links lists it in full.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _linkingKnown = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, bool> _linkingAtStart = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override string ScopeOfSource(ScanSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Server.GetEnabledLibraryMappings().Any(m => m.StorageMode == StorageMode.Link))
        {
            return string.Empty;
        }

        return _linkingAtStart.TryGetValue(source.Key, out var offered) && offered ? "links" : "no-links";
    }

    /// <summary>Says whether this run asked a server for links and was told no, or got no answer.</summary>
    /// <param name="sourceKey">The server's entry key.</param>
    /// <returns><c>true</c> when the server did not offer links.</returns>
    internal bool LinkingRefusedBy(string sourceKey)
        => _linkingKnown.TryGetValue(sourceKey, out var offered) && !offered;

    /// <summary>Asks a server once for each run whether it offers links.</summary>
    private Task<bool> LinkingOfferedAsync(ScanSource source, CancellationToken cancellationToken)
        => _linkingOffered.GetOrAdd(source.Key, async _ =>
        {
            var offered = await AskLinkingAsync(source, cancellationToken).ConfigureAwait(false);
            _linkingKnown[source.Key] = offered;
            return offered;
        });

    /// <summary>Asks a server whether it offers links, and takes no answer as no.</summary>
    private async Task<bool> AskLinkingAsync(ScanSource source, CancellationToken cancellationToken)
    {
        try
        {
            var capabilities = await source.Client.GetPeerCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (capabilities?.Features?.Contains(LinkFeature.Name, StringComparer.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            Logger.LogWarning("{Task}: '{Server}' does not offer links, so files new to a Link mapping wait until Server Sync there allows linking for this server", Name, source.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning("{Task}: could not ask '{Server}' whether it offers links, so files new to a Link mapping wait: {Reason}", Name, source.Name, FormatUtilities.SanitizeForLog(ex.Message, 300));
        }

        return false;
    }

    /// <inheritdoc />
    protected override bool SupportsIncremental => true;

    /// <inheritdoc />
    protected override string ScopeSettings
    {
        get
        {
            var config = ConfigManager.Configuration;
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                config.SkipWatchedByAllUsers,
                config.WatchedFilterUserIds,
                config.DownloadNewContentMode,
                config.ReplaceExistingContentMode,
                config.DeletionApproval,
                config.DetectUpdatedFiles
            });
        }
    }

    /// <inheritdoc />
    public override string Name => "Scan Content";

    /// <inheritdoc />
    public override string Key => "ServerSyncUpdateTables";

    /// <inheritdoc />
    public override string Description => "Fetches item list from source server and updates the sync tracking table.";

    /// <inheritdoc />
    public override string Category => "Content Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "Content";

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        if (!config.EnableContentSync) return false;
        if (config.GetPullServers().Count == 0) return false;
        return config.GetEnabledLibraryMappings().Count > 0;
    }

    /// <summary>
    /// Resets the state kept for a run.
    /// It is reset once for each run and not for each server, since the counts add up across every connected server.
    /// </summary>
    /// <inheritdoc />
    protected override void OnRunStarting()
    {
        _seenPerMapping.Clear();
        _scopeForRun = null;
        _notTheirs.Clear();
        _blacklistExcludedByMapping.Clear();
        _linkingOffered.Clear();
        _linkingAtStart = new Dictionary<string, bool>(_linkingKnown, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lists one server's items for every enabled mapping.
    /// A whitelist resolves each entry on its own and expands folders to their leaves, and any other filter fetches the whole library and drops what the blacklist names.
    /// </summary>
    /// <inheritdoc />
    protected override async Task<IList<ContentRefreshWork>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);

        var config = ConfigManager.Configuration;
        var client = source.Client;
        var enabledMappings = source.Server.GetEnabledLibraryMappings();

        // A server that cannot say which files it was given fails its listing here.
        await client.OriginOfAsync("/", cancellationToken).ConfigureAwait(false);

        Guid? playlistUserId = Guid.TryParse(source.Server.AuthenticatedUserId, out var authUserId) ? authUserId : null;

        var libraryCounts = new Dictionary<string, int>(enabledMappings.Count);
        var totalExpected = 0;
        foreach (var mapping in enabledMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(mapping.SourceLibraryId, out var sourceLibraryId))
            {
                continue;
            }

            if (mapping.FilterMode == LibraryFilterMode.Whitelist)
            {
                // A whitelist's count is not known up front, so its progress is coarse.
                libraryCounts[mapping.SourceLibraryId] = 0;
                continue;
            }

            try
            {
                var count = await client.GetLibraryItemCountAsync(sourceLibraryId, cancellationToken).ConfigureAwait(false);
                libraryCounts[mapping.SourceLibraryId] = count;
                totalExpected += count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logger.LogDebug(ex, "Failed to fetch item count for {Library}. Progress for this library will be coarse", mapping.SourceLibraryName);
            }
        }

        progress.Report(totalExpected > 0 ? 1 : 50);

        var watchedFilterUsers = await ResolveWatchedFilterUsersAsync(
            source,
            config.SkipWatchedByAllUsers,
            config.WatchedFilterUserIds,
            cancellationToken).ConfigureAwait(false);

        var work = new List<ContentRefreshWork>();
        var fetched = 0;

        foreach (var mapping in enabledMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(mapping.SourceLibraryId, out var sourceLibraryId))
            {
                Logger.LogWarning("Invalid source library ID '{LibraryId}' in mapping for {LibraryName}, skipping",
                    mapping.SourceLibraryId, mapping.SourceLibraryName);
                continue;
            }

            var watchedByAll = await BuildWatchedByAllSetAsync(
                client,
                sourceLibraryId,
                watchedFilterUsers,
                cancellationToken).ConfigureAwait(false);

            if (mapping.FilterMode == LibraryFilterMode.Whitelist)
            {
                var rawItems = mapping.FilteredItems ?? new List<FilteredItem>();
                var ids = new List<Guid>(rawItems.Count);
                var droppedIds = 0;
                foreach (var fi in rawItems)
                {
                    if (Guid.TryParse(fi.ItemId, out var g))
                    {
                        ids.Add(g);
                    }
                    else
                    {
                        droppedIds++;
                    }
                }

                if (droppedIds > 0)
                {
                    Logger.LogWarning(
                        "{Library} whitelist has {Count} unparseable ID(s), those entries will not sync until corrected.",
                        mapping.SourceLibraryName, droppedIds);
                }

                if (ids.Count == 0)
                {
                    Logger.LogInformation(
                        "{Library} is in whitelist mode but FilteredItems is empty, nothing to fetch.",
                        mapping.SourceLibraryName);
                    RecordMappingSeen(source.Key, mapping.SourceLibraryId, -1);
                    continue;
                }

                var seen = new HashSet<Guid>();
                var collected = 0;
                var pathless = 0;
                var outsideRoot = 0;
                var sourceRoot = (mapping.SourceRootPath ?? string.Empty).TrimEnd('/', '\\');
                foreach (var whitelistedId in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    List<BaseItemDto> leaves;
                    try
                    {
                        leaves = await client.GetWhitelistedItemLeavesAsync(whitelistedId, playlistUserId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // One failed entry skips the prune, and the remaining entries are still read.
                        MarkSourceUnavailable($"source server unavailable resolving a whitelist entry in '{mapping.SourceLibraryName}'");
                        Logger.LogWarning(ex,
                            "Whitelist lookup failed for ID {Id} in {Library}. Continuing with remaining entries",
                            whitelistedId, mapping.SourceLibraryName);
                        continue;
                    }

                    foreach (var item in leaves)
                    {
                        if (!item.Id.HasValue || !seen.Add(item.Id.Value))
                        {
                            continue;
                        }

                        // Pathless leaves must not count as seen, or a weak API key prunes everything.
                        if (string.IsNullOrEmpty(item.Path))
                        {
                            pathless++;
                            continue;
                        }

                        // A collection can hold items from other libraries, which belong to another mapping.
                        if (sourceRoot.Length > 0
                            && !item.Path.Replace('\\', '/').StartsWith(sourceRoot.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            outsideRoot++;
                            continue;
                        }

                        // A link on the peer is not a file to copy.
                        if (client.IsPeerLink(item.Id) || IsStrm(item))
                        {
                            continue;
                        }

                        var hitWatched = watchedByAll != null && watchedByAll.Contains(item.Id.Value);
                        work.Add(new ContentRefreshWork(source, mapping, item, hitWatched));
                        AddOtherVersions(work, source, mapping, item, hitWatched);
                        collected++;
                        fetched++;
                        if (totalExpected > 0)
                        {
                            progress.Report(Math.Min(100, 100.0 * fetched / totalExpected));
                        }
                    }
                }

                if (pathless > 0)
                {
                    Logger.LogWarning(
                        "{Library}: {Count} whitelisted leaf item(s) came back without a file path and were skipped, this usually means the API key lacks admin rights on the source",
                        mapping.SourceLibraryName, pathless);
                }

                if (outsideRoot > 0)
                {
                    Logger.LogInformation(
                        "{Library}: {Count} whitelisted leaf item(s) live outside this mapping's source root and were skipped, to sync them, whitelist the same entry under a mapping for their library",
                        mapping.SourceLibraryName, outsideRoot);
                }

                Logger.LogInformation(
                    "{Library} whitelist resolved to {Count} leaf item(s) from {WhitelistCount} whitelisted entry/entries.",
                    mapping.SourceLibraryName,
                    collected,
                    ids.Count);

                RecordMappingSeen(source.Key, mapping.SourceLibraryId, collected);
                continue;
            }

            // Blacklisted containers are expanded to ids, since a path filter cannot see membership.
            var excludedThisMapping = 0;
            if (mapping.FilterMode == LibraryFilterMode.Blacklist && mapping.FilteredItems != null)
            {
                var mappingExcluded = _blacklistExcludedByMapping.TryGetValue(MapKey(source.Key, mapping.SourceLibraryId), out var existingSet)
                    ? existingSet
                    : (_blacklistExcludedByMapping[MapKey(source.Key, mapping.SourceLibraryId)] = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                var failedExpansion = false;
                foreach (var fi in mapping.FilteredItems)
                {
                    var isContainer = string.Equals(fi.Type, "BoxSet", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fi.Type, "Playlist", StringComparison.OrdinalIgnoreCase);
                    if (!isContainer || !Guid.TryParse(fi.ItemId, out var blacklistedCollectionId))
                    {
                        continue;
                    }

                    try
                    {
                        var members = await client.GetWhitelistedItemLeavesAsync(blacklistedCollectionId, playlistUserId, cancellationToken).ConfigureAwait(false);
                        foreach (var member in members)
                        {
                            if (member.Id.HasValue
                                && mappingExcluded.Add(member.Id.Value.ToString("N", CultureInfo.InvariantCulture)))
                            {
                                excludedThisMapping++;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        MarkSourceUnavailable($"failed to expand blacklisted {fi.Type} '{fi.Name}' in '{mapping.SourceLibraryName}'");
                        Logger.LogWarning(ex,
                            "Blacklisted container {Id} in {Library} could not be expanded. Skipping this mapping's discovery so excluded items aren't synced",
                            fi.ItemId, mapping.SourceLibraryName);
                        failedExpansion = true;
                        break;
                    }
                }

                // Fetching without the exclusions would sync everything the operator left out.
                if (failedExpansion)
                {
                    continue;
                }

                if (excludedThisMapping > 0)
                {
                    Logger.LogInformation(
                        "{Library}: {Count} item(s) excluded via blacklisted collection(s)/playlist(s)",
                        mapping.SourceLibraryName, excludedThisMapping);
                }
            }

            var workBefore = work.Count;
            var outcome = await PaginatedFetchUtility.FetchAllPagesAsync(
                fetchPage: (startIndex, batchSize, ct) => client.GetLibraryItemsAsync(sourceLibraryId, startIndex, batchSize, ChangedSince(source), ct),
                processItem: (item, _) =>
                {
                    if (item.Id.HasValue
                        && IsBlacklistExcluded(source.Key, mapping.SourceLibraryId, item.Id.Value.ToString("N", CultureInfo.InvariantCulture)))
                    {
                        return Task.FromResult(false);
                    }

                    if (client.IsPeerLink(item.Id) || IsStrm(item))
                    {
                        return Task.FromResult(false);
                    }

                    var hitWatched = watchedByAll != null && item.Id.HasValue && watchedByAll.Contains(item.Id.Value);
                    work.Add(new ContentRefreshWork(source, mapping, item, hitWatched));
                    AddOtherVersions(work, source, mapping, item, hitWatched);
                    return Task.FromResult(true);
                },
                libraryName: mapping.SourceLibraryName,
                sourceRootPath: mapping.SourceRootPath,
                filterMode: mapping.FilterMode,
                filteredItems: mapping.FilteredItems,
                logger: Logger,
                cancellationToken: cancellationToken,
                onItemProcessed: () =>
                {
                    fetched++;
                    if (totalExpected > 0)
                    {
                        progress.Report(Math.Min(100, 100.0 * fetched / totalExpected));
                    }
                }).ConfigureAwait(false);

            if (!outcome.CompletedFully)
            {
                MarkSourceUnavailable($"source server unavailable during '{mapping.SourceLibraryName}' discovery");
            }

            RecordMappingSeen(source.Key, mapping.SourceLibraryId, work.Count - workBefore);
        }

        progress.Report(100);
        return work;
    }

    /// <inheritdoc />
    protected override Task<SyncItem?> BuildRecordAsync(
        ContentRefreshWork source,
        IReadOnlyDictionary<string, SyncItem> existing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sourcePath = source.SourceItem.Path;
        if (string.IsNullOrEmpty(sourcePath))
        {
            return Task.FromResult<SyncItem?>(null);
        }

        // A peer's own link is not a file to copy, however it arrived.
        if (source.Source.Client.IsPeerLink(source.SourceItem.Id) || IsStrm(source.SourceItem))
        {
            return Task.FromResult<SyncItem?>(null);
        }

        var sourceItemId = source.SourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourceSize = MediaItemUtilities.GetItemSize(source.SourceItem);
        var sourceCreateDate = source.SourceItem.DateCreated?.DateTime ?? DateTime.UtcNow;
        var localPath = PathUtilities.TranslatePath(sourcePath, source.Mapping.SourceRootPath, source.Mapping.LocalRootPath);

        var storage = AlwaysKeep.StorageFor(source.Mapping, sourcePath);
        var picked = source.Mapping.StorageMode == StorageMode.Link && storage == StorageMode.Copy;

        existing.TryGetValue(sourceItemId, out var existingItem);

        // Two servers can report the same id, so another server's row is left alone.
        if (existingItem is not null && BelongsToAnotherServer(existingItem, source.Source.Key, localPath))
        {
            Logger.LogDebug("Skipped {Path}: its id is already tracked for another server at {LocalPath}", FormatUtilities.SanitizeForLog(sourcePath), FormatUtilities.SanitizeForLog(existingItem.LocalPath));
            return Task.FromResult<SyncItem?>(null);
        }

        // Watched by all wins over the approval settings.
        if (source.WatchedByAll)
        {
            var watched = BuildWatchedFiltered(source.Mapping, source.SourceItem, sourceItemId, sourceSize, sourceCreateDate, localPath, existingItem);

            // A link stays at its .strm, so the row never names a missing copy.
            if (existingItem is not null && watched is not null && existingItem.StorageMode.IsLink() && LinkPaths.LinkPathFor(localPath) is { } existingLink)
            {
                watched.LocalPath = existingLink;
            }

            // A file new to a Link mapping is recorded as a link.
            if (existingItem is null && watched is not null && _linkFiles is not null
                && storage == StorageMode.Link && LinkPaths.CanLink(source.SourceItem) && LinkPaths.LinkPathFor(localPath) is { } watchedLink)
            {
                watched.StorageMode = StorageMode.Link;
                watched.LocalPath = watchedLink;
            }

            return Task.FromResult(Stamp(watched, source));
        }

        var config = ConfigManager.Configuration;

        // A crashed replacement leaves the file set aside, so it is put back.
        if (existingItem != null
            && existingItem.StorageMode == StorageMode.Copy
            && !string.IsNullOrEmpty(existingItem.LocalPath)
            && !File.Exists(existingItem.LocalPath))
        {
            ReplacementRecovery.Restore(existingItem.LocalPath, Logger);
        }

        // A link whose .strm is gone is not a removed file.
        if (existingItem != null
            && existingItem.Status == SyncStatus.Synced
            && !existingItem.StorageMode.IsLink()
            && !string.IsNullOrEmpty(existingItem.LocalPath)
            && !File.Exists(existingItem.LocalPath))
        {
            if (config.DeletesMissing
                && !File.Exists(existingItem.LocalPath))
            {
                return Task.FromResult<SyncItem?>(null);
            }
        }

        // A copy stays a copy whatever the mapping says, and only video is linked.
        if (_linkFiles is not null
            && (existingItem?.StorageMode ?? (LinkPaths.CanLink(source.SourceItem) ? storage : StorageMode.Copy)).IsLink()
            && LinkPaths.LinkPathFor(localPath) is { } linkPath)
        {
            if (existingItem != null)
            {
                var promote = storage == StorageMode.Copy ? config.DownloadNewContentMode : (ApprovalMode?)null;
                var link = LinkStateService.ProcessExistingLink(existingItem, sourcePath, sourceSize, sourceCreateDate, linkPath, _linkFiles, Logger, promote);
                if (link.StorageMode == StorageMode.Promoting && picked && link.Kept != KeptReason.Hand)
                {
                    link.Kept = KeptReason.Pick;
                }

                return Task.FromResult(Stamp(link, source));
            }

            // A real copy already at the name wins, and is tracked as a copy.
            if (!File.Exists(localPath))
            {
                return NewLinkAsync(source, sourceItemId, sourcePath, sourceSize, sourceCreateDate, linkPath, config.DownloadNewContentMode, cancellationToken);
            }
        }

        // A kept copy turning back into a link waits for the download run.
        if (existingItem is { StorageMode: StorageMode.Unkeeping })
        {
            if (picked)
            {
                existingItem.StorageMode = StorageMode.Copy;
                existingItem.Kept = KeptReason.Pick;
                existingItem.Status = SyncStatus.Synced;
                existingItem.StatusDate = DateTime.UtcNow;
            }

            return Task.FromResult(Stamp(existingItem, source));
        }

        // Between equals this server's own file stays, and the row says when they differ.
        if (existingItem is { Kept: KeptReason.Own, StorageMode: StorageMode.Copy, Status: SyncStatus.Synced or SyncStatus.Pending }
            && OwnFilesStay(source, picked) && File.Exists(localPath))
        {
            existingItem.SourcePath = sourcePath;
            existingItem.SourceSize = sourceSize;
            existingItem.Status = SyncStatus.Synced;
            existingItem.PendingType = null;
            existingItem.Reason = OwnFileNote(source, localPath, sourceSize);
            return Task.FromResult(Stamp(existingItem, source));
        }

        if (existingItem != null)
        {
            var updated = SyncStateService.ProcessExistingItem(
                existingItem,
                sourcePath,
                sourceSize,
                sourceCreateDate,
                localPath,
                config.ReplaceExistingContentMode,
                config.DetectUpdatedFiles,
                0,
                Logger);
            if (updated is not null)
            {
                Unkeep(updated, source, picked, localPath);
            }

            return Task.FromResult(Stamp(updated, source));
        }

        var fresh = SyncStateService.ProcessNewItem(
            source.Mapping,
            sourceItemId,
            sourcePath,
            sourceSize,
            sourceCreateDate,
            localPath,
            config.DownloadNewContentMode,
            config.ReplaceExistingContentMode,
            0);
        // A file here before any row is this server's own and stays.
        if (fresh is not null && OwnFilesStay(source, picked) && File.Exists(localPath))
        {
            fresh.Status = SyncStatus.Synced;
            fresh.PendingType = null;
            fresh.Kept = KeptReason.Own;
            fresh.Reason = OwnFileNote(source, localPath, sourceSize);
        }
        else if (fresh is { Status: SyncStatus.Synced })
        {
            fresh.Kept = KeptReason.Own;
        }
        else if (fresh is not null && picked)
        {
            fresh.Kept = KeptReason.Pick;
        }

        return Task.FromResult(Stamp(fresh, source));
    }

    /// <summary>
    /// Records a copy in a Link mapping that a pick covers as kept by that pick, and queues it to become a link again once no pick covers it.
    /// A copy kept by hand stays until it is turned back by hand.
    /// </summary>
    private void Unkeep(SyncItem row, ContentRefreshWork source, bool picked, string localPath)
    {
        if (source.Mapping.StorageMode != StorageMode.Link || row.StorageMode != StorageMode.Copy)
        {
            return;
        }

        if (picked)
        {
            if (row.Kept == KeptReason.None)
            {
                row.Kept = KeptReason.Pick;
            }

            return;
        }

        if (row.Kept == KeptReason.Pick
            && row.Status == SyncStatus.Synced
            && _linkFiles is not null
            && LinkPaths.CanLink(source.SourceItem)
            && LinkPaths.LinkPathFor(localPath) is not null
            && File.Exists(localPath))
        {
            row.StorageMode = StorageMode.Unkeeping;
            row.Status = SyncStatus.Queued;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
            row.RetryCount = 0;
            row.Reason = null;
        }
    }

    /// <summary>Builds the row for a new link, or nothing when the server does not offer links.</summary>
    private async Task<SyncItem?> NewLinkAsync(ContentRefreshWork source, string sourceItemId, string sourcePath, long sourceSize, DateTime sourceCreateDate, string linkPath, ApprovalMode downloadMode, CancellationToken cancellationToken)
    {
        if (!await LinkingOfferedAsync(source.Source, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Stamp(LinkStateService.ProcessNewLink(source.Mapping, sourceItemId, sourcePath, sourceSize, sourceCreateDate, linkPath, downloadMode, _linkFiles!), source);
    }

    /// <summary>
    /// Adds every other version of a film in a Link mapping, so a user the source refuses one version can play another.
    /// A version outside the mapping's root is passed over, and a Copy mapping copies the first version only.
    /// </summary>
    /// <param name="work">The work list the other versions are added to.</param>
    /// <param name="source">The server the item came from.</param>
    /// <param name="mapping">The library mapping the item belongs to.</param>
    /// <param name="item">The film as the source reports it.</param>
    /// <param name="watchedByAll">Whether every user picked for the watched filter has played the film.</param>
    internal static void AddOtherVersions(List<ContentRefreshWork> work, ScanSource source, LibraryMapping mapping, BaseItemDto item, bool watchedByAll)
    {
        if (mapping.StorageMode != StorageMode.Link)
        {
            return;
        }

        var root = (mapping.SourceRootPath ?? string.Empty).Replace('\\', '/').TrimEnd('/') + "/";
        foreach (var version in LinkPaths.OtherVersions(item))
        {
            // A whitelist admits the film with its versions, and a blacklist checks each path.
            if (version.Path!.Replace('\\', '/').StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (mapping.FilterMode != LibraryFilterMode.Blacklist
                    || !PathUtilities.IsItemFiltered(version.Path, mapping.SourceRootPath ?? string.Empty, mapping.FilterMode, mapping.FilteredItems)))
            {
                work.Add(new ContentRefreshWork(source, mapping, version, watchedByAll));
            }
        }
    }

    /// <summary>
    /// A .strm on a peer is never a file to copy or link, whether it is one of the peer's links or one its
    /// users made: either way it points at someone else's file, so passing it on would be a second hop.
    /// </summary>
    /// <param name="item">The item as the source reports it.</param>
    /// <returns><c>true</c> when the item is a .strm file.</returns>
    internal static bool IsStrm(BaseItemDto item)
        => item.Path?.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Says whether a stored row was built by another enabled server for a different local file.
    /// </summary>
    private bool BelongsToAnotherServer(SyncItem row, string serverKey, string localPath)
    {
        if (string.IsNullOrEmpty(row.ServerKey)
            || string.Equals(row.ServerKey, serverKey, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(row.LocalPath)
            || ConfigManager.Configuration.FindServer(row.ServerKey) is not { IsEnabled: true })
        {
            return false;
        }

        return !string.Equals(row.LocalPath, localPath, StringComparison.Ordinal)
            && !string.Equals(row.LocalPath, LinkPaths.LinkPathFor(localPath), StringComparison.Ordinal);
    }

    /// <summary>Records the server that built a row, so the sync task downloads from it.</summary>
    private static SyncItem? Stamp(SyncItem? item, ContentRefreshWork source)
    {
        if (item != null)
        {
            item.ServerKey = source.Source.Key;
        }

        return item;
    }

    /// <inheritdoc />
    protected override string ExtractKey(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.SourceItemId;
    }

    /// <summary>
    /// Two servers collide when they would land the same local file. The higher ranked server's
    /// offer wins, and a row from the other server for that file is retired and not treated as
    /// a removal, so a rank change never schedules a local file for deletion.
    /// </summary>
    /// <inheritdoc />
    protected override string? PriorityKeyOf(ContentRefreshWork source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = source.SourceItem.Path;
        return string.IsNullOrEmpty(path) ? null : PathUtilities.TranslatePath(path, source.Mapping.SourceRootPath, source.Mapping.LocalRootPath);
    }

    /// <inheritdoc />
    protected override string? PriorityKeyOf(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A link is keyed by the file it stands in for.
        if (record.StorageMode.IsLink())
        {
            return LinkPaths.CopiedPathOf(record);
        }

        return string.IsNullOrEmpty(record.LocalPath) ? null : record.LocalPath;
    }

    /// <summary>
    /// A row is in scope when its <see cref="SyncItem.SourceLibraryId"/> is
    /// still mapped via an enabled <see cref="LibraryMapping"/>. Rows under a
    /// disabled mapping are inert, neither pruned nor scheduled for deletion.
    /// </summary>
    /// <inheritdoc />
    protected override bool IsInScope(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Two servers can share library ids, so scope follows the row's own server.
        if (string.IsNullOrEmpty(record.ServerKey))
        {
            return IsEnabledSourceLibrary(record.SourceLibraryId);
        }

        _scopeForRun ??= new HashSet<string>(
            ConfigManager.Configuration.GetPullServers().SelectMany(server => server.GetEnabledLibraryMappings().Select(mapping => MapKey(server.Key, mapping.SourceLibraryId))),
            StringComparer.OrdinalIgnoreCase);
        return _scopeForRun.Contains(MapKey(record.ServerKey, record.SourceLibraryId));
    }

    /// <summary>
    /// The enabled mappings by server and library, built on first use in a run and dropped when the
    /// next one starts.
    /// </summary>
    private HashSet<string>? _scopeForRun;

    /// <summary>
    /// <summary>
    /// Says whether the prune would act on a row, which leaves out rows that are Ignored, being deleted, or waiting for deletion approval.
    /// Leaving them out keeps real removals from adding up until the prune safety limit refuses every prune.
    /// </summary>
    /// <inheritdoc />
    protected override bool IsPruneCandidate(SyncItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        // With Removed files on Keep only a link is ever pruned.
        return !KeptAsItIs(record)
            && record.Status != SyncStatus.Ignored
            && record.Status != SyncStatus.Deleting
            && !(record.Status == SyncStatus.Pending && record.PendingType == PendingType.Deletion)
            && (record.StorageMode.IsLink() || ConfigManager.Configuration.EffectiveMissingContentAction != MissingContentAction.Keep);
    }

    /// <summary>
    /// Says whether a link's file is offered by no server any more and its .strm is this plugin's own or already gone, so it is removed whatever Removed files says.
    /// A link the library filter leaves out, or a .strm someone else wrote, follows Removed files as a copy does.
    /// </summary>
    private bool IsGoneLink(SyncItem row, Dictionary<string, LibraryMapping> mappingById)
        => row.StorageMode.IsLink()
            && !ScopeChanged(row, mappingById)
            && (string.IsNullOrEmpty(row.LocalPath) || !File.Exists(row.LocalPath) || LinkFiles.IsOwnLink(row.LocalPath));

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".iso", ".flv",
        ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".opus", ".wav", ".wma", ".ape", ".epub", ".pdf"
    };

    /// <summary>Says whether a row is stale because the library filter changed and not because the source removed the item.</summary>
    private bool ScopeChanged(SyncItem row, Dictionary<string, LibraryMapping> mappingById)
        => IsBlacklistExcluded(row.ServerKey, row.SourceLibraryId, row.SourceItemId)
            || (MappingOf(row, mappingById) is { } mapping
                && ((mapping.FilterMode == LibraryFilterMode.Blacklist
                        && PathUtilities.IsItemFiltered(row.SourcePath, mapping.SourceRootPath ?? string.Empty, mapping.FilterMode, mapping.FilteredItems))
                    || (mapping.FilterMode == LibraryFilterMode.Whitelist && HasContainerEntry(mapping))));

    private static bool IsHiddenTitle(SyncItem row)
        => row.Status == SyncStatus.Ignored && row.Reason == SyncStateService.HiddenReason;

    /// <summary>Builds a lookup from a row to its title's folder.</summary>
    private Func<SyncItem, string?> TitleFolders()
    {
        var mappings = MappingsByKey(ConfigManager.Configuration.Servers, enabledOnly: false);
        return row => !string.IsNullOrEmpty(row.LocalPath) && MappingOf(row, mappings) is { } mapping
            ? IgnoreFileWriter.TitleFolder(row.LocalPath, mapping.LocalRootPath)
            : null;
    }

    /// <summary>Removes the .ignore file of each hidden title the predicate picks and marks its rows Synced.</summary>
    private int ShowTitles(ContentSyncTableManager manager, IEnumerable<SyncItem> hidden, Func<SyncItem, string?> folderOf, Func<string, bool> show, string why)
    {
        var changed = 0;
        foreach (var title in hidden.GroupBy(row => folderOf(row) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            if (!show(title.Key))
            {
                continue;
            }

            if (title.Key.Length > 0)
            {
                try
                {
                    IgnoreFileWriter.UnhideFolder(title.Key);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.LogWarning("Could not remove the .ignore file in {Folder}: {Error}", FormatUtilities.SanitizeForLog(title.Key), FormatUtilities.SanitizeForLog(ex.Message, 300));
                    continue;
                }
            }

            foreach (var row in title)
            {
                manager.UpdateStatus(row.SourceItemId, SyncStatus.Synced);
                changed++;
            }

            Logger.LogInformation("Showed {Folder} again, {Why}", title.Key.Length > 0 ? FormatUtilities.SanitizeForLog(title.Key) : "a title", why);
        }

        return changed;
    }

    private int ShowAllTitles(ContentSyncTableManager manager, IList<SyncItem> current)
    {
        var hidden = current.Where(IsHiddenTitle).ToList();
        return hidden.Count == 0 ? 0 : ShowTitles(manager, hidden, TitleFolders(), _ => true, "since Removed files is no longer Ignore");
    }

    /// <summary>
    /// Cancels the deletions that are waiting when Removed files is no longer Delete.
    /// The rows change in the list as well as the table, so a later step reading the list sees them.
    /// </summary>
    private int StandDownDeletions(ContentSyncTableManager manager, IList<SyncItem> current, HashSet<string> goneLinks)
    {
        var waiting = current
            .Where(row => !goneLinks.Contains(row.SourceItemId)
                && (row.Status == SyncStatus.Deleting || (row.Status == SyncStatus.Pending && row.PendingType == PendingType.Deletion)))
            .ToList();
        foreach (var row in waiting)
        {
            row.Status = SyncStatus.Synced;
            row.PendingType = null;
            row.StatusDate = DateTime.UtcNow;
        }

        var changed = manager.UpsertMany(waiting, (row, ex) => Logger.LogWarning(ex, "Could not cancel the waiting deletion of {Path}", FormatUtilities.SanitizeForLog(row.LocalPath ?? row.SourcePath)));

        if (changed > 0)
        {
            Logger.LogInformation("Cancelled {Count} deletion(s) that were waiting, since Removed files is no longer Delete", changed);
        }

        return changed;
    }

    /// <summary>Hides each title folder whose every file is gone from the source with a .ignore file, and shows the titles that came back.</summary>
    private int HideGoneTitles(ContentSyncTableManager manager, IList<SyncItem> current, List<SyncItem> stale, HashSet<string> returned, Dictionary<string, LibraryMapping> mappingById)
    {
        var folderOf = TitleFolders();
        var gone = stale.Where(row => !ScopeChanged(row, mappingById)).ToList();
        var goneKeys = new HashSet<string>(gone.Select(row => row.SourceItemId), StringComparer.OrdinalIgnoreCase);
        var alive = new HashSet<string>(
            current.Where(row => !goneKeys.Contains(row.SourceItemId) && (!IsHiddenTitle(row) || returned.Contains(row.SourceItemId)))
                .Select(folderOf)
                .Where(folder => folder is not null)!,
            StringComparer.OrdinalIgnoreCase);
        var tracked = new HashSet<string>(current.Where(row => !string.IsNullOrEmpty(row.LocalPath)).Select(row => row.LocalPath!), StringComparer.OrdinalIgnoreCase);

        var changed = ShowTitles(manager, current.Where(IsHiddenTitle), folderOf, folder => folder.Length > 0 && alive.Contains(folder), "since some of it is back on the source");

        foreach (var title in gone.Where(row => folderOf(row) is not null).GroupBy(row => folderOf(row)!, StringComparer.OrdinalIgnoreCase))
        {
            if (alive.Contains(title.Key) || title.All(row => row.Status == SyncStatus.Ignored) || HasUntrackedMedia(title.Key, tracked))
            {
                continue;
            }

            try
            {
                if (!IgnoreFileWriter.HideFolder(title.Key))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning("Could not hide {Folder} with a .ignore file: {Error}", FormatUtilities.SanitizeForLog(title.Key), FormatUtilities.SanitizeForLog(ex.Message, 300));
                continue;
            }

            Logger.LogInformation("Hid {Folder} with a .ignore file, since everything in it is gone from the source", FormatUtilities.SanitizeForLog(title.Key));
            foreach (var row in title.Where(row => row.Status != SyncStatus.Ignored))
            {
                if (SyncStateService.MarkHidden(manager, row, Logger).Changed)
                {
                    changed++;
                }
            }
        }

        return changed;
    }

    /// <summary>Says whether a folder holds media no row tracks, which is assumed when the folder cannot be read.</summary>
    private bool HasUntrackedMedia(string folder, HashSet<string> tracked)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Any(file => MediaExtensions.Contains(Path.GetExtension(file)) && !tracked.Contains(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug(ex, "Could not read {Folder}, so it is not hidden", FormatUtilities.SanitizeForLog(folder));
            return true;
        }
    }

    /// <inheritdoc />
    protected override bool PruneGuardApplies =>
        ConfigManager.Configuration.EffectiveMissingContentAction != MissingContentAction.Keep
        || ConfigManager.Configuration.GetEnabledLibraryMappings().Any(m => m.StorageMode == StorageMode.Link);

    /// <summary>
    /// Does nothing, since <see cref="BuildRecordAsync"/> sets the status itself from the approval settings.
    /// </summary>
    /// <inheritdoc />
    protected override void DecideStatus(SyncItem record)
    {
    }

    /// <inheritdoc />
    protected override Task<int> PruneStaleAsync(
        IReadOnlyDictionary<string, SyncItem> existing,
        HashSet<string> seenKeys,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var deleteMode = ConfigManager.Configuration.DeletionApproval;
        var action = ConfigManager.Configuration.EffectiveMissingContentAction;
        var deletes = ConfigManager.Configuration.DeletesMissing;
        var hide = action == MissingContentAction.Ignore;
        var typedManager = (ContentSyncTableManager)Manager;

        var mappingById = MappingsByKey(ConfigManager.Configuration.GetPullServers(), enabledOnly: true);

        var stale = new List<SyncItem>();
        var fileless = new List<SyncItem>();
        var goneLinks = new List<SyncItem>();
        var blockedMappings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in existing)
        {
            if (seenKeys.Contains(kvp.Key) || !IsInScope(kvp.Value) || KeptAsItIs(kvp.Value))
            {
                continue;
            }

            var goneLink = IsGoneLink(kvp.Value, mappingById);
            if (!goneLink && !deletes && !hide)
            {
                // A row with no file keeps nothing and would fail to download every run.
                var kept = kvp.Value;
                if (kept.StorageMode == StorageMode.Copy && kept.Status != SyncStatus.Ignored && SeenFor(kept) != 0
                    && (string.IsNullOrEmpty(kept.LocalPath) || !File.Exists(kept.LocalPath)))
                {
                    fileless.Add(kept);
                }

                continue;
            }

            // An empty answer cannot be told from a real removal, so the mapping is skipped.
            if (SeenFor(kvp.Value) == 0)
            {
                blockedMappings.Add(MapKey(kvp.Value.ServerKey, kvp.Value.SourceLibraryId));
                continue;
            }

            (goneLink ? goneLinks : stale).Add(kvp.Value);
        }

        if (blockedMappings.Count > 0)
        {
            Logger.LogWarning(
                "Refusing to prune rows under {Count} mapping(s) the source reported no items for, a stale library ID or transient empty answer is indistinguishable from real removal. If the library really is empty (or was re-created with a new ID), fix or reset the mapping from the dashboard",
                blockedMappings.Count);
            MarkPruneBlocked($"Prune blocked for {JPKribs.Jellyfin.Base.ActivityText.Count(blockedMappings.Count, "library mapping")}. The source reported 0 items while local rows exist, so check the mapping's source library ID.");
        }

        // The snapshot passed in predates this run's writes, so the table is read again.
        var goneLinkKeys = new HashSet<string>(goneLinks.Select(row => row.SourceItemId), StringComparer.OrdinalIgnoreCase);
        var current = typedManager.GetAllStrict();
        var settled = deletes ? 0 : StandDownDeletions(typedManager, current, goneLinkKeys);
        if (!hide)
        {
            settled += ShowAllTitles(typedManager, current);
        }

        settled += RemoveGoneLinks(typedManager, goneLinks, cancellationToken);
        foreach (var row in fileless)
        {
            typedManager.Delete(row.SourceItemId);
            settled++;
        }

        if (fileless.Count > 0)
        {
            Logger.LogInformation("{Task}: dropped {Count} row(s) for items the source no longer has and that hold no file here", Name, fileless.Count);
        }

        if (!deletes && !hide)
        {
            progress.Report(100);
            return Task.FromResult(settled);
        }

        if (hide)
        {
            progress.Report(100);
            var returned = new HashSet<string>(
                existing.Where(kvp => seenKeys.Contains(kvp.Key) && IsHiddenTitle(kvp.Value)).Select(kvp => kvp.Value.SourceItemId),
                StringComparer.OrdinalIgnoreCase);
            var remaining = current.Where(row => !goneLinkKeys.Contains(row.SourceItemId)).ToList();
            return Task.FromResult(settled + HideGoneTitles(typedManager, remaining, stale, returned, mappingById));
        }

        var pruned = 0;
        var reportEvery = Math.Max(1, stale.Count / 100);
        for (var i = 0; i < stale.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // A filter edit is a scope change, so its removals wait for approval.
                var effectiveMode = deleteMode == ApprovalMode.Enabled && ScopeChanged(stale[i], mappingById)
                    ? ApprovalMode.RequireApproval
                    : deleteMode;

                var result = SyncStateService.ProcessMissingItem(typedManager, stale[i], effectiveMode, Logger);
                if (result.Changed)
                {
                    pruned++;
                }
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Failed to process missing item {SourceItemId}", FormatUtilities.SanitizeForLog(stale[i].SourceItemId));
            }

            if ((i + 1) % reportEvery == 0)
            {
                progress.Report(100.0 * (i + 1) / stale.Count);
            }
        }

        progress.Report(100);
        return Task.FromResult(settled + pruned);
    }

    /// <summary>
    /// Schedules each gone link's .strm for removal, or drops the row when the .strm is already gone.
    /// </summary>
    private int RemoveGoneLinks(ContentSyncTableManager manager, List<SyncItem> goneLinks, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var link in goneLinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (SyncStateService.ProcessMissingItem(manager, link, ApprovalMode.Enabled, Logger, ownLink: true).Changed)
                {
                    removed++;
                }
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Failed to process the gone link {SourceItemId}", FormatUtilities.SanitizeForLog(link.SourceItemId));
            }
        }

        if (removed > 0)
        {
            Logger.LogInformation("Removing {Count} link(s) whose file no server offers any more", removed);
        }

        return removed;
    }

    /// <inheritdoc />
    protected override Task FinalizeAsync(CancellationToken cancellationToken)
    {
        ResolveLocalItemIds();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Picks the configured watched filter users that exist on one source.
    /// It returns null when the filter does not apply to that server, as when it is off, an id is invalid, or none of the users exist there.
    /// </summary>
    private async Task<List<Guid>?> ResolveWatchedFilterUsersAsync(
        ScanSource source,
        bool skipWatchedByAllUsers,
        List<string> watchedFilterUserIds,
        CancellationToken cancellationToken)
    {
        if (!skipWatchedByAllUsers || watchedFilterUserIds == null || watchedFilterUserIds.Count == 0)
        {
            return null;
        }

        var configured = new List<Guid>(watchedFilterUserIds.Count);
        foreach (var userIdStr in watchedFilterUserIds)
        {
            if (!Guid.TryParse(userIdStr, out var userId))
            {
                // Dropping the user would widen the filter, so the whole filter is turned off.
                Logger.LogWarning(
                    "Watched-by-all filter disabled: invalid user ID {UserId} in WatchedFilterUserIds. Fix the configuration to re-enable the filter.",
                    userIdStr);
                return null;
            }

            configured.Add(userId);
        }

        HashSet<Guid> serverUserIds;
        try
        {
            var users = await source.Client.GetUsersAsync(source.Server.AuthenticatedUserId, cancellationToken).ConfigureAwait(false);
            serverUserIds = new HashSet<Guid>(users.Where(u => u.Id.HasValue && source.Server.MaySyncUser(u.Id.Value.ToString("N", CultureInfo.InvariantCulture))).Select(u => u.Id!.Value));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Without the server's users the filter cannot apply, and the listing still runs.
            Logger.LogWarning(ex, "Watched-by-all filter skipped for '{Server}': its users could not be read", source.Name);
            return null;
        }

        var present = configured.Where(serverUserIds.Contains).Distinct().ToList();
        if (present.Count == 0)
        {
            Logger.LogDebug("Watched-by-all filter does not apply to '{Server}': none of the selected users exist there", source.Name);
            return null;
        }

        return present;
    }

    /// <summary>
    /// <summary>
    /// Builds the set of source items that every given user has played in the given library.
    /// It returns null when there are no users, which means the filter does not apply.
    /// </summary>
    /// </summary>
    private static async Task<HashSet<Guid>?> BuildWatchedByAllSetAsync(
        SourceServerClient client,
        Guid libraryId,
        List<Guid>? userIds,
        CancellationToken cancellationToken)
    {
        if (userIds == null || userIds.Count == 0)
        {
            return null;
        }

        HashSet<Guid>? intersection = null;

        foreach (var userId in userIds)
        {
            var played = await client.GetUserPlayedItemIdsAsync(userId, libraryId, cancellationToken).ConfigureAwait(false);

            if (intersection == null)
            {
                intersection = played;
            }
            else
            {
                intersection.IntersectWith(played);
            }

            if (intersection.Count == 0)
            {
                break;
            }
        }

        return intersection ?? new HashSet<Guid>();
    }

    /// <summary>
    /// Builds an Ignored row for an item every selected user has played.
    /// A row already Synced or Ignored is left alone.
    /// </summary>
    private static SyncItem? BuildWatchedFiltered(
        LibraryMapping mapping,
        BaseItemDto sourceItem,
        string sourceItemId,
        long sourceSize,
        DateTime sourceCreateDate,
        string localPath,
        SyncItem? existing)
    {
        if (existing != null && (existing.Status == SyncStatus.Ignored || existing.Status == SyncStatus.Synced))
        {
            return existing;
        }

        var item = existing ?? new SyncItem
        {
            SourceLibraryId = mapping.SourceLibraryId,
            LocalLibraryId = mapping.LocalLibraryId,
            SourceItemId = sourceItemId
        };

        item.SourcePath = sourceItem.Path!;
        item.SourceSize = sourceSize;
        item.SourceCreateDate = sourceCreateDate;
        item.LocalPath = localPath;
        item.Status = SyncStatus.Ignored;
        item.PendingType = null;
        item.StatusDate = DateTime.UtcNow;
        return item;
    }

    /// <summary>
    /// Says whether a whitelist mapping holds a collection or playlist entry.
    /// A stale row under one can mean a membership edit and not a real removal, so its deletion waits for approval.
    /// </summary>
    private static bool HasContainerEntry(LibraryMapping mapping)
    {
        if (mapping.FilteredItems == null)
        {
            return false;
        }

        foreach (var fi in mapping.FilteredItems)
        {
            if (string.Equals(fi.Type, "BoxSet", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fi.Type, "Playlist", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves <see cref="SyncItem.LocalItemId"/> for synced items that
    /// do not have one yet, by looking the path up in the local library.
    /// </summary>
    private void ResolveLocalItemIds()
    {
        var typedManager = (ContentSyncTableManager)Manager;
        var syncedItems = typedManager.GetByStatus(SyncStatus.Synced);
        var resolved = 0;

        foreach (var item in syncedItems)
        {
            if (!string.IsNullOrEmpty(item.LocalItemId) || string.IsNullOrEmpty(item.LocalPath))
            {
                continue;
            }

            try
            {
                var localItem = _libraryManager.FindByPath(item.LocalPath, isFolder: false);
                if (localItem != null)
                {
                    item.LocalItemId = localItem.Id.ToString();
                    typedManager.Upsert(item);
                    resolved++;
                }
            }
            // One path the library cannot resolve must not stop the others.
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to resolve LocalItemId for {FileName}", FormatUtilities.SanitizeForLog(Path.GetFileName(item.LocalPath)));
            }
        }

        if (resolved > 0)
        {
            Logger.LogInformation("Resolved {Count} local item IDs", resolved);
        }
    }
}

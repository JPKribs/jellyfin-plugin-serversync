using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Database;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Scans the sources, stores a row with a snapshot and source hash for each item, and decides each row's status in the same pass.
/// <see cref="SyncStatus.Ignored"/> rows are the operator's choice and are never changed.
/// </summary>
/// <typeparam name="TRecord">The sync table's row type.</typeparam>
/// <typeparam name="TSource">The type of a work item a source's listing yields.</typeparam>
/// <typeparam name="TKey">The type of the natural key that matches a source item to a row.</typeparam>
public abstract class RefreshSyncTaskBase<TRecord, TSource, TKey> : IScheduledTask, IConfigurableScheduledTask
    where TRecord : SyncRecord
    where TKey : notnull
{
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ScanWatermarkStore? _watermarks;
    private readonly Dictionary<string, DateTime> _changedSince = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _runStartedAt;

    /// <summary>How far before its last scan an incremental scan reaches back, so a change made while that scan ran is not missed.</summary>
    public static readonly TimeSpan IncrementalOverlap = TimeSpan.FromHours(1);

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshSyncTaskBase{TRecord, TSource, TKey}"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="watermarks">Holds when each source was last scanned, or null when scans are never incremental.</param>
    protected RefreshSyncTaskBase(
        ILogger logger,
        ISyncTableManager<TRecord, TKey> manager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        ScanWatermarkStore? watermarks = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(configManager);
        Logger = logger;
        Manager = manager;
        _clientFactory = clientFactory;
        _configManager = configManager;
        _watermarks = watermarks;
    }

    /// <summary>Gets a value indicating whether the sync type can scan only what changed since the last scan, which is off unless overridden.</summary>
    protected virtual bool SupportsIncremental => false;

    /// <summary>Gets the settings that decide what a scan reads, which an incremental scan compares so a change to them forces a full scan.</summary>
    protected virtual string ScopeSettings => string.Empty;

    /// <summary>Gets a value indicating whether the current run reads only what changed since each source's last scan.</summary>
    protected bool IsIncrementalRun { get; private set; }

    /// <summary>Gets the rows stored when the current run started, by key.</summary>
    protected IReadOnlyDictionary<TKey, TRecord> ExistingRows { get; private set; } = new Dictionary<TKey, TRecord>();

    /// <summary>Gets the time an incremental run reads changes since for one source.</summary>
    /// <param name="source">The server being read.</param>
    /// <returns>The time, or null when the run reads everything from that source.</returns>
    protected DateTime? ChangedSince(ScanSource source)
        => IsIncrementalRun && source is not null && _changedSince.TryGetValue(source.Key, out var since) ? since : null;

    /// <summary>
    /// Gets the logger for subclass use.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the table manager for subclass use.
    /// </summary>
    protected ISyncTableManager<TRecord, TKey> Manager { get; }

    /// <summary>
    /// Gets the plugin configuration accessor.
    /// </summary>
    protected IPluginConfigurationManager ConfigManager => _configManager;

    /// <summary>
    /// Gets or sets the sources connected for the current run, in rank order.
    /// It is filled by <see cref="TestConnectionAsync"/>, disposed when the run ends, and settable so a subclass or test can provide its own.
    /// </summary>
    protected IReadOnlyList<ScanSource> Sources { get; set; } = Array.Empty<ScanSource>();

    /// <summary>
    /// Finds the connected source for a server entry key. A row with no key belongs to the first
    /// configured source, not to whichever connected first.
    /// </summary>
    /// <param name="serverKey">The entry key carried on a row, or null.</param>
    /// <returns>The source, or null when that server did not connect this run.</returns>
    protected ScanSource? SourceFor(string? serverKey) => ScanSources.Find(Sources, _configManager, serverKey);

    /// <summary>
    /// The per sync type tasks run inside Sync Content and Sync Information and from the dashboard, so
    /// they stay registered but leave the scheduled task list.
    /// </summary>
    /// <inheritdoc />
    public bool IsHidden => true;

    /// <inheritdoc />
    public bool IsLogged => true;

#pragma warning disable CA1033 // The tasks have their own IsEnabled method, so this one is explicit.
    /// <inheritdoc />
    bool IConfigurableScheduledTask.IsEnabled => true;
#pragma warning restore CA1033

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Key { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract string Category { get; }

    /// <summary>
    /// Says whether the sync type is turned on in the current settings.
    /// </summary>
    /// <returns><c>true</c> when the sync type is on.</returns>
    protected abstract bool IsEnabled();

    /// <summary>
    /// Gets the key that serializes this task against its sync type's sync task through <see cref="SyncModuleMutex"/>.
    /// </summary>
    protected abstract string ModuleMutexKey { get; }

    /// <summary>
    /// Gets how many <see cref="BuildRecordAsync"/> calls run at once, which is one by default.
    /// Raise it only when the build waits on HTTP, since the table's single write lock serializes everything else.
    /// </summary>
    protected virtual int BuildRecordParallelism => 1;

    /// <summary>
    /// Connects every source into <see cref="Sources"/> and keeps the ones that answer, in rank order.
    /// A server that does not answer is skipped, and its rows are not pruned this run because they cannot be confirmed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connection tests.</param>
    /// <returns><c>false</c> only when no server connected.</returns>
    protected virtual async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var connected = await ScanSources.ConnectAsync(_configManager, _clientFactory, Logger, Name, MarkServerUnreachable, cancellationToken).ConfigureAwait(false);
        Sources = connected;
        return connected.Count > 0;
    }

    /// <summary>
    /// Runs once for each run after the servers connect and before any list is fetched, and does nothing by default.
    /// State kept for a run is reset here and not in <see cref="GetListAsync"/>, which runs once for each server.
    /// </summary>
    protected virtual void OnRunStarting()
    {
    }

    /// <summary>
    /// Fetches the items one source offers this run, called once for each connected server, highest ranked first.
    /// <paramref name="progress"/> runs from 0 to 100 for this server only.
    /// </summary>
    /// <param name="source">The server to list from.</param>
    /// <param name="progress">Receives this server's fetch progress.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The work items this server contributes.</returns>
    protected abstract Task<IList<TSource>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the key two servers collide on when they offer the same thing, such as a local path, a person name, or a local user, or null when the item never collides.
    /// When several servers produce the same key, the first in rank order keeps it.
    /// </summary>
    /// <param name="source">The work item to read the key from.</param>
    /// <returns>The collision key, or null.</returns>
    protected virtual string? PriorityKeyOf(TSource source) => null;

    /// <summary>
    /// Chooses between several servers' offers of the same thing, for each collision key more than one server offered.
    /// A key left out of the answer goes to the first server in rank order, which is what the default does for every key.
    /// </summary>
    /// <param name="contested">The offers of each contested key, highest ranked first.</param>
    /// <param name="cancellationToken">Cancels the choice.</param>
    /// <returns>The offer that speaks for a key, for the keys rank does not decide.</returns>
    protected virtual Task<IReadOnlyDictionary<string, TSource>> ContestAsync(IReadOnlyDictionary<string, IReadOnlyList<TSource>> contested, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<string, TSource>>(new Dictionary<string, TSource>());

    /// <summary>
    /// Decides what becomes of an offer that lost its key to another server this run, which is dropped by default.
    /// A sync type that must remember what a server held returns the offer marked to be recorded and not applied.
    /// </summary>
    /// <param name="passedOver">The offer that does not speak for its key this run.</param>
    /// <param name="speaking">The offer that does.</param>
    /// <returns>The offer to build a row from without applying it, or null to drop it.</returns>
    protected virtual TSource? ObservedOffer(TSource passedOver, TSource speaking) => default;

    /// <summary>
    /// Says whether a row from a lower ranked server may be weighed against this server's value on a run that lists only what changed, although a higher ranked server holds the item.
    /// The default is <c>false</c>, and the higher server keeps the item until a full scan.
    /// </summary>
    /// <param name="record">The row built from the lower ranked server's offer.</param>
    /// <returns><c>true</c> when the row goes on to be weighed.</returns>
    protected virtual bool MayOutrankOwner(TRecord record) => false;

    /// <summary>
    /// Runs once for each run after every server is listed and before the offers are weighed, and does nothing by default.
    /// </summary>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>A task that completes when the preparation is done.</returns>
    protected virtual Task PrepareContestAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private readonly HashSet<string> _claimedByHigherServers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Says whether a server listed earlier this run already offered the item with this collision key.
    /// A sync type can then skip fetching details for an item a lower ranked server would lose anyway.
    /// </summary>
    /// <param name="priorityKey">The collision key, as <see cref="PriorityKeyOf(TSource)"/> gives it.</param>
    /// <returns><c>true</c> when a higher ranked server already covers it.</returns>
    protected bool ClaimedByHigherServer(string priorityKey) => _claimedByHigherServers.Contains(priorityKey);

    /// <summary>
    /// Gets the same collision key from a stored row, so a row that lost to another server is retired and not treated as a removal.
    /// </summary>
    /// <param name="record">The stored row to read the key from.</param>
    /// <returns>The collision key, or null.</returns>
    protected virtual string? PriorityKeyOf(TRecord record) => null;

    /// <summary>
    /// The keys of the connected servers whose listing finished without a source error this run.
    /// A server left out counts as absent when a lower ranked server offers a row it holds.
    /// </summary>
    private readonly HashSet<string> _listedCleanly = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set by <see cref="MarkSourceUnavailable"/> while a server's listing is in progress, so the
    /// listing can be recorded as unclean. The listings run one server at a time.
    /// </summary>
    private volatile bool _currentListingFailed;

    /// <summary>
    /// Lists every connected server in rank order and keeps one offer for each collision key.
    /// A server whose listing throws contributes nothing this run, and the others are still listed.
    /// </summary>
    private async Task<IList<TSource>> CollectAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var all = new List<TSource>();
        var count = Math.Max(Sources.Count, 1);
        _claimedByHigherServers.Clear();
        _listedCleanly.Clear();
        for (var i = 0; i < Sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Sources[i];
            var offset = 100.0 * i / count;
            var sourceProgress = new Progress<double>(p => progress.Report(offset + (Math.Clamp(p, 0, 100) / count)));
            _currentListingFailed = false;
            IList<TSource> items;
            _listingServer = true;
            try
            {
                items = await GetListAsync(source, sourceProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A partial list cannot be trusted, so none of it is used.
                Logger.LogWarning(ex, "{Task}: listing '{Server}' failed. Its items are left out of this run", Name, source.Name);
                MarkSourceUnavailable($"server '{source.Name}' failed while listing: {ex.Message}");
                continue;
            }
            finally
            {
                _listingServer = false;
            }

            if (!_currentListingFailed)
            {
                _listedCleanly.Add(source.Key);
            }

            all.AddRange(items);
            foreach (var item in items)
            {
                if (PriorityKeyOf(item) is { } claimed)
                {
                    _claimedByHigherServers.Add(claimed);
                }
            }

            Logger.LogInformation("{Task}: '{Server}' offered {Count} item(s)", Name, source.Name, items.Count);
        }

        progress.Report(100);

        all = await DropOffersNotTheirsAsync(all, cancellationToken).ConfigureAwait(false);

        if (Sources.Count < 2)
        {
            return all;
        }

        await PrepareContestAsync(cancellationToken).ConfigureAwait(false);

        var offers = new Dictionary<string, List<TSource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in all)
        {
            if (PriorityKeyOf(item) is { } key)
            {
                if (!offers.TryGetValue(key, out var list))
                {
                    offers[key] = list = new List<TSource>(1);
                }

                list.Add(item);
            }
        }

        var contested = offers.Where(o => o.Value.Count > 1).ToDictionary(o => o.Key, o => (IReadOnlyList<TSource>)o.Value, StringComparer.OrdinalIgnoreCase);
        var winners = contested.Count > 0
            ? await ContestAsync(contested, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, TSource>();

        var kept = new List<TSource>(all.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = 0;
        var outranked = 0;
        foreach (var item in all)
        {
            var key = PriorityKeyOf(item);
            if (key is null)
            {
                kept.Add(item);
                continue;
            }

            if (!seen.Add(key))
            {
                dropped++;
                continue;
            }

            var speaking = item;
            if (winners.TryGetValue(key, out var winner) && !ReferenceEquals(winner, item))
            {
                speaking = winner;
                outranked++;
            }

            kept.Add(speaking);
            if (contested.TryGetValue(key, out var offered))
            {
                foreach (var other in offered)
                {
                    if (!ReferenceEquals(other, speaking) && ObservedOffer(other, speaking) is { } observed)
                    {
                        kept.Add(observed);
                    }
                }
            }
        }

        if (dropped > 0)
        {
            Logger.LogInformation("{Task}: {Dropped} item(s) offered by more than one server were left to one of them, {Outranked} to a lower priority server that holds the newer edit", Name, dropped, outranked);
        }

        return kept;
    }

    /// <summary>
    /// Settles the rows whose items a server says left its library since the last scan, on a run that
    /// listed only what changed. Default does nothing, and removals wait for the next full scan.
    /// </summary>
    /// <param name="existing">The stored rows, as the run started.</param>
    /// <param name="seenKeys">The keys this run saw on a source.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many rows were settled.</returns>
    protected virtual Task<int> ApplyReportedRemovalsAsync(IReadOnlyDictionary<TKey, TRecord> existing, HashSet<TKey> seenKeys, CancellationToken cancellationToken)
        => Task.FromResult(0);

    /// <summary>The most of one server's rows a single run may take for removed, as a fraction.</summary>
    protected static double PruneFractionLimit => MaxPruneFraction;

    /// <summary>The number of rows a server must hold before <see cref="PruneFractionLimit"/> applies.</summary>
    protected static int PruneGuardRows => PruneGuardMinRows;

    /// <summary>
    /// Leaves out offers that are not the offering server's to make, once every server has been listed.
    /// Default keeps them all.
    /// </summary>
    /// <param name="items">Every server's offers, in rank order.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The offers that count.</returns>
    protected virtual Task<List<TSource>> DropOffersNotTheirsAsync(List<TSource> items, CancellationToken cancellationToken)
        => Task.FromResult(items);

    /// <summary>Says whether a server's listing finished cleanly this run.</summary>
    /// <param name="serverKey">The server entry's key.</param>
    /// <returns><c>true</c> when it did.</returns>
    protected bool ListedCleanly(string serverKey) => _listedCleanly.Contains(serverKey);

    /// <summary>
    /// Applies user and library filters to <paramref name="items"/>, and keeps them all by default.
    /// </summary>
    /// <param name="items">The work items every source offered.</param>
    /// <param name="cancellationToken">Cancels the filtering.</param>
    /// <returns>The items to keep.</returns>
    protected virtual Task<IList<TSource>> FilterAsync(IList<TSource> items, CancellationToken cancellationToken)
        => Task.FromResult(items);

    /// <summary>
    /// Builds and stores the row for one source item outside a scheduled run, for a hint, under the sync type's mutex.
    /// Stored rows are read on demand, since loading the whole table for one item would defeat the point of a hint.
    /// </summary>
    /// <param name="work">The source item and the server it came from.</param>
    /// <param name="cancellationToken">Cancels the wait for the mutex and the build.</param>
    /// <returns>The stored row, or null when the item has no local counterpart.</returns>
    public async Task<TRecord?> RefreshOneAsync(TSource work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var moduleMutex = SyncModuleMutex.ForModule(ModuleMutexKey);
        if (!await moduleMutex.WaitAsync(SyncModuleMutex.SingleRowWait, cancellationToken).ConfigureAwait(false))
        {
            throw new ModuleBusyException($"{Name} is running");
        }

        try
        {
            PrepareForOne(work);
            var record = await BuildRecordAsync(work, new StoredRows(this), cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return null;
            }

            DecideStatus(record);
            Manager.Upsert(record);
            return record;
        }
        finally
        {
            moduleMutex.Release();
        }
    }

    /// <summary>
    /// Readies any state <see cref="BuildRecordAsync"/> relies on for a single item, since
    /// <see cref="RefreshOneAsync"/> never runs <see cref="GetListAsync"/>. The default does nothing.
    /// </summary>
    /// <param name="work">The single item about to be built.</param>
    protected virtual void PrepareForOne(TSource work)
    {
    }

    /// <summary>
    /// Runs once for each run with the rows this run queued, before the prune, so a sync type can settle conflicts and take back rows this server's own edit should win.
    /// The default does nothing, and an override stores the rows it changes.
    /// </summary>
    /// <param name="queued">The rows that ended the build queued.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the conflicts are settled.</returns>
    protected virtual Task ResolveConflictsAsync(IList<TRecord> queued, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Builds or updates the row for one source item, with its values and source hashes filled in for the settings that are on.
    /// Returning null skips the item, as when it has no local match.
    /// </summary>
    /// <param name="source">The work item to build from.</param>
    /// <param name="existing">The rows already stored, by key.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The row, or null to skip the item.</returns>
    protected abstract Task<TRecord?> BuildRecordAsync(
        TSource source,
        IReadOnlyDictionary<TKey, TRecord> existing,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the natural key of a row, which matches it to the stored rows and finds stale ones.
    /// </summary>
    /// <param name="record">The row to read the key from.</param>
    /// <returns>The row's key.</returns>
    protected abstract TKey ExtractKey(TRecord record);

    /// <summary>
    /// Decides a row's status after it is built: an Ignored row is left alone, a row with changes becomes Queued, and any other becomes Synced.
    /// Content overrides it for approvals, where new items wait as Pending.
    /// </summary>
    /// <param name="record">The row that was built.</param>
    protected virtual void DecideStatus(TRecord record)
    {
        if (record.Status == SyncStatus.Ignored)
        {
            return;
        }

        // A row that used its retry allowance stays Errored, or it would requeue forever.
        if (record.Status == SyncStatus.Errored
            && record.RetryCount >= Math.Max(1, ConfigManager.Configuration.MaxRetryCount))
        {
            return;
        }

        if (record.HasChanges)
        {
            record.Status = SyncStatus.Queued;
        }
        else
        {
            record.MarkSynced();
            record.Status = SyncStatus.Synced;
            record.LastSyncTime = DateTime.UtcNow;
        }

        record.StatusDate = DateTime.UtcNow;
        record.Reason = null;
    }

    /// <summary>
    /// Says whether a row belongs to a library or user mapping that is still on, which is every row by default.
    /// Rows out of scope are never pruned, so turning a mapping off does not delete its tracking or, for Content, its files.
    /// </summary>
    /// <param name="record">The row to check.</param>
    /// <returns><c>true</c> when the row belongs to a mapping that is still on.</returns>
    protected virtual bool IsInScope(TRecord record) => true;

    /// <summary>
    /// The source library ids of every enabled library mapping, computed once when a run starts and null outside a run.
    /// </summary>
    private HashSet<string>? _enabledSourceLibraryIdsForRun;

    /// <summary>
    /// Says whether a source library id belongs to an enabled library mapping on any source.
    /// Inside a run it reads the set computed when the run started, and outside one it reads the configuration.
    /// </summary>
    /// <param name="sourceLibraryId">The source library id carried on a row.</param>
    /// <returns><c>true</c> when an enabled mapping has that source library id.</returns>
    protected bool IsEnabledSourceLibrary(string? sourceLibraryId)
        => (_enabledSourceLibraryIdsForRun ?? BuildEnabledSourceLibraryIds()).Contains(sourceLibraryId ?? string.Empty);

    /// <summary>Collects the source library id of every enabled library mapping.</summary>
    private HashSet<string> BuildEnabledSourceLibraryIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in _configManager.Configuration.GetEnabledLibraryMappings())
        {
            ids.Add(mapping.SourceLibraryId ?? string.Empty);
        }

        return ids;
    }

    /// <summary>
    /// Copies the row state a scan must not reset from the stored row onto a freshly built one: the id, status, last sync time, reason, and retry count.
    /// Without the retry count, the retry ceiling in <see cref="DecideStatus"/> could never trip.
    /// </summary>
    /// <param name="fresh">The record built this run.</param>
    /// <param name="previous">The stored row for the same key.</param>
    protected static void CarryForwardRowState(TRecord fresh, TRecord previous)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(previous);
        fresh.Id = previous.Id;
        fresh.Status = previous.Status;
        fresh.LastSyncTime = previous.LastSyncTime;
        fresh.Reason = previous.Reason;
        fresh.RetryCount = previous.RetryCount;
    }

    /// <summary>
    /// Says whether pruning a row would change anything, which is <c>true</c> for every row by default.
    /// Rows the prune would skip must not count toward the prune safety limit, or real removals would add up until it refuses every prune.
    /// </summary>
    /// <param name="record">The row to check.</param>
    /// <returns><c>true</c> when pruning the row would change anything.</returns>
    protected virtual bool IsPruneCandidate(TRecord record) => true;

    /// <summary>
    /// Gets a value indicating whether the prune safety limit applies, which is <c>false</c> when the sync type's prune does nothing, so no blocked prune is recorded for it.
    /// </summary>
    protected virtual bool PruneGuardApplies => true;

    /// <summary>
    /// Set when a source returns anything but a clean response during this run's listing.
    /// It is volatile because listing can run in parallel.
    /// </summary>
    private volatile bool _sourceUnavailable;

    /// <summary>
    /// Whether a server is being listed right now.
    /// A failure during one server's listing holds back only that server's rows from the prune, and any other failure holds the whole prune.
    /// </summary>
    private volatile bool _listingServer;
    private volatile bool _unavailableBeyondOneServer;

    /// <summary>
    /// Set when a safety limit refused all or part of this run's prune.
    /// While set, the run keeps the recorded failure, so a blocked prune does not look like a clean run.
    /// </summary>
    private bool _pruneBlocked;

    /// <summary>
    /// Records that a prune safety limit refused to remove rows this run, with a reason the dashboard shows.
    /// </summary>
    /// <param name="reason">Why the prune was refused.</param>
    protected void MarkPruneBlocked(string reason)
    {
        _pruneBlocked = true;
        RecordRunFailure("Refresh", reason);
    }

    /// <summary>
    /// Records that a source returned an error during this run's listing, which makes the run skip pruning.
    /// A row missing from a partial fetch might still exist on the source, so removing it would lose data.
    /// </summary>
    /// <param name="reason">What failed, for the log and the dashboard.</param>
    protected void MarkSourceUnavailable(string reason) => MarkUnavailable(reason, oneServer: _listingServer);

    /// <summary>
    /// Records a server that could not be reached at all, so its rows are held back and the rest are cleaned up.
    /// </summary>
    private void MarkServerUnreachable(string reason) => MarkUnavailable(reason, oneServer: true);

    /// <summary>Marks the run as having an unclean source, logging and recording the first reason only.</summary>
    private void MarkUnavailable(string reason, bool oneServer)
    {
        _currentListingFailed = true;
        if (!oneServer)
        {
            // A failure outside any one server's listing cannot be blamed on one server.
            _unavailableBeyondOneServer = true;
        }

        if (_sourceUnavailable)
        {
            return;
        }

        _sourceUnavailable = true;
        Logger.LogWarning(
            "{Task}: {Reason}. Skipping prune this run so rows not re-confirmed on the source are left in place",
            Name, FormatUtilities.SanitizeForLog(reason, 300));
        RecordRunFailure("Refresh", reason);
    }

    /// <summary>
    /// The fraction of the table one run may prune.
    /// A clean looking listing that would delete more is far more likely a truncated answer than a real mass removal, so the run refuses.
    /// </summary>
    private const double MaxPruneFraction = 0.5;

    /// <summary>
    /// Minimum existing-row count before <see cref="MaxPruneFraction"/> kicks in.
    /// </summary>
    private const int PruneGuardMinRows = 50;

    /// <summary>
    /// Removes the rows the sources no longer hold, deleting them outright by default, and never rows out of scope.
    /// It runs only after a clean listing, and <paramref name="progress"/> runs from 0 to 100 for the prune alone.
    /// </summary>
    /// <param name="existing">The rows already stored, by key.</param>
    /// <param name="seenKeys">The keys of every item the sources still hold.</param>
    /// <param name="progress">Receives progress from 0 to 100.</param>
    /// <param name="cancellationToken">Cancels the prune between batches.</param>
    /// <returns>How many rows were pruned.</returns>
    protected virtual Task<int> PruneStaleAsync(
        IReadOnlyDictionary<TKey, TRecord> existing,
        HashSet<TKey> seenKeys,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var stale = existing
            .Where(kvp => !seenKeys.Contains(kvp.Key) && IsInScope(kvp.Value))
            .Select(kvp => kvp.Value)
            .ToList();

        var pruned = 0;
        for (var start = 0; start < stale.Count; start += WriteBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = stale.Skip(start).Take(WriteBatchSize).Select(r => r.Id).ToList();
            try
            {
                pruned += Manager.DeleteByIds(batch);
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                Logger.LogWarning(ex, "{Task}: failed to prune {Count} stale record(s) starting at id {Id}", Name, batch.Count, batch[0]);
            }

            progress.Report(100.0 * Math.Min(start + batch.Count, stale.Count) / stale.Count);
        }

        progress.Report(100);
        return Task.FromResult(pruned);
    }

    /// <summary>
    /// Does the sync type's work after the rows are stored, such as resolving local item ids, and does nothing by default.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the work is done.</returns>
    protected virtual Task FinalizeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Records the sync type's last scan time on the configuration, and does nothing by default.
    /// The base calls it after <see cref="FinalizeAsync"/> and then saves the configuration.
    /// </summary>
    /// <param name="config">The plugin settings to record the time on.</param>
    /// <param name="utcNow">The time the run finished, in UTC.</param>
    protected virtual void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
    }

    /// <summary>
    /// Returns no triggers. The per sync type tasks run only as steps of Sync Content and Sync Information,
    /// so a schedule of their own would run each twice as often as intended.
    /// </summary>
    /// <returns>No triggers.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!IsEnabled())
        {
            Logger.LogDebug("{Task} disabled, skipping", Name);
            return;
        }

        var moduleMutex = SyncModuleMutex.ForModule(ModuleMutexKey);
        await moduleMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            moduleMutex.Release();
        }
    }

    /// <summary>Runs the task and disposes every connected source on every way out, so no client outlives the run.</summary>
    private async Task ExecuteCoreAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting {Task}", Name);
        _sourceUnavailable = false;
        _unavailableBeyondOneServer = false;
        _pruneBlocked = false;

        try
        {
            await RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var source in Sources)
            {
                source.Dispose();
            }

            Sources = Array.Empty<ScanSource>();
            ExistingRows = new Dictionary<TKey, TRecord>();
            _enabledSourceLibraryIdsForRun = null;
        }
    }

    /// <summary>
    /// Where each phase ends on the progress bar: the snapshot at 3, the source fetch at 50, and the build and prune at 99.
    /// The build and the prune split their band by how many rows each will touch.
    /// </summary>
    private const double SnapshotEnd = 3.0;
    private const double FetchEnd = 50.0;
    private const double WorkEnd = 99.0;

    /// <summary>
    /// Deletes are weighted cheaper than builds when the band is split, since they make no HTTP calls
    /// and touch no blobs.
    /// </summary>
    private const double PruneRowWeight = 0.25;

    /// <summary>Runs one scan: connect, list, build, settle conflicts, prune, and record the outcome.</summary>
    private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await TestConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            // Recorded so the dashboard shows the scan failed.
            Logger.LogError("{Task}: connection check failed. Aborting refresh", Name);
            RecordRunFailure("Refresh", "Connection check failed: no scan server is reachable or every configured key is invalid");
            return;
        }

        _enabledSourceLibraryIdsForRun = BuildEnabledSourceLibraryIds();
        OnRunStarting();
        progress.Report(0);

        var existing = SnapshotExistingRows();
        ExistingRows = existing;
        DecideRunKind(existing.Count);
        progress.Report(SnapshotEnd);

        var fetchProgress = new Progress<double>(p =>
            progress.Report(SnapshotEnd + ((FetchEnd - SnapshotEnd) * Math.Clamp(p, 0, 100) / 100.0)));
        var sourceItems = await CollectAsync(fetchProgress, cancellationToken).ConfigureAwait(false);
        sourceItems = await FilterAsync(sourceItems, cancellationToken).ConfigureAwait(false);
        progress.Report(FetchEnd);

        var run = StartRun(existing, sourceItems.Count);
        await BuildAllAsync(run, sourceItems, progress, cancellationToken).ConfigureAwait(false);
        await ResolveRunConflictsAsync(run, cancellationToken).ConfigureAwait(false);
        RetireCoveredRows(run);
        var pruned = 0;
        if (IsIncrementalRun)
        {
            // A server that records what left its library settles removals on this run.
            if (!_sourceUnavailable && !run.HadFailures)
            {
                pruned = await ApplyReportedRemovalsAsync(run.Existing, run.SeenKeySet, cancellationToken).ConfigureAwait(false);
            }

            Logger.LogInformation("{Task}: listed only what changed, so stale rows are pruned on the next full scan", Name);
        }
        else
        {
            pruned = await PruneIfSafeAsync(run, progress, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(WorkEnd);
        await FinalizeAsync(cancellationToken).ConfigureAwait(false);
        RecordRunCompletedAndSave();
        RecordWatermarks(run);
        progress.Report(100);

        ReportRunOutcome(run, pruned);
    }

    /// <summary>Decides whether this run lists everything or only what changed since each source's last scan.</summary>
    private void DecideRunKind(int existingRows)
    {
        IsIncrementalRun = false;
        _changedSince.Clear();
        _runStartedAt = DateTime.UtcNow;
        if (!SupportsIncremental || _watermarks is null || Sources.Count == 0)
        {
            return;
        }

        var intervalHours = _configManager.Configuration.FullScanIntervalHours;
        string? why = intervalHours <= 0 ? "every scan is a full scan" : existingRows == 0 ? "the table is empty" : null;
        var since = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources)
        {
            if (why is not null)
            {
                break;
            }

            var mark = _watermarks.Get(Key, source.Key);
            why = mark is null ? $"'{source.Name}' has not been fully scanned yet"
                : !string.Equals(mark.ScopeHash, ScopeOf(source), StringComparison.Ordinal) ? $"the settings for '{source.Name}' changed"
                : _runStartedAt - Utilities.UtcTime.AsUtc(mark.FullAt) >= TimeSpan.FromHours(intervalHours) ? $"the last full scan of '{source.Name}' was over {intervalHours} hours ago"
                : null;
            if (why is null)
            {
                since[source.Key] = Utilities.UtcTime.AsUtc(mark!.ListedFrom) - IncrementalOverlap;
            }
        }

        if (why is not null)
        {
            Logger.LogInformation("{Task}: full scan, since {Why}", Name, why);
            return;
        }

        foreach (var pair in since)
        {
            _changedSince[pair.Key] = pair.Value;
        }

        IsIncrementalRun = true;
        Logger.LogInformation("{Task}: listing only what changed since the last scan. A full scan runs every {Hours} hours", Name, intervalHours);
    }

    /// <summary>Records when each cleanly listed source was scanned, unless the run was not clean.</summary>
    private void RecordWatermarks(RefreshRun run)
    {
        if (!SupportsIncremental || _watermarks is null)
        {
            return;
        }

        if (_sourceUnavailable || run.HadFailures || (!IsIncrementalRun && _pruneBlocked))
        {
            Logger.LogInformation("{Task}: the run was not clean, so the next scan lists from where the last clean one did", Name);
            return;
        }

        foreach (var source in Sources)
        {
            if (!_listedCleanly.Contains(source.Key))
            {
                continue;
            }

            var fullAt = IsIncrementalRun ? _watermarks.Get(Key, source.Key)?.FullAt ?? _runStartedAt : _runStartedAt;
            _watermarks.Set(Key, source.Key, new ScanWatermark(ScopeOf(source), _runStartedAt, fullAt));
        }
    }

    /// <summary>
    /// Gets anything else about one server that decides what a scan of it keeps, so a change to it makes the next scan of that server a full scan.
    /// It is empty by default.
    /// </summary>
    /// <param name="source">The server whose scope is described.</param>
    /// <returns>A short description, or empty.</returns>
    protected virtual string ScopeOfSource(ScanSource source) => string.Empty;

    /// <summary>Hashes everything that decides what a scan of one server keeps.</summary>
    private string ScopeOf(ScanSource source)
    {
        var server = source.Server;
        var extra = ScopeOfSource(source);
        var shape = extra.Length == 0
            ? JsonSerializer.Serialize(new
            {
                server.Url,
                Libraries = server.GetEnabledLibraryMappings(),
                Users = server.GetEnabledUserMappings(),
                Settings = ScopeSettings
            })
            : JsonSerializer.Serialize(new
            {
                server.Url,
                Libraries = server.GetEnabledLibraryMappings(),
                Users = server.GetEnabledUserMappings(),
                Settings = ScopeSettings,
                Extra = extra
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..16];
    }

    /// <summary>
    /// Reads every stored row, strictly.
    /// A lenient read would return nothing on a database error, every item would look new, and statuses the operator set would be overwritten.
    /// </summary>
    private Dictionary<TKey, TRecord> SnapshotExistingRows()
    {
        var existingList = Manager.GetAllStrict();
        var existing = new Dictionary<TKey, TRecord>(existingList.Count);
        foreach (var rec in existingList)
        {
            existing[ExtractKey(rec)] = rec;
        }

        return existing;
    }

    /// <summary>Builds the run's state, with the progress split and the server each stored row belonged to before the build.</summary>
    private RefreshRun StartRun(Dictionary<TKey, TRecord> existing, int sourceCount)
    {
        var buildWeight = (double)sourceCount;
        var pruneWeight = Math.Max(0, existing.Count - sourceCount) * PruneRowWeight;
        var buildEnd = buildWeight + pruneWeight <= 0
            ? WorkEnd
            : FetchEnd + ((WorkEnd - FetchEnd) * buildWeight / (buildWeight + pruneWeight));

        var pullServers = _configManager.Configuration.GetPullServers();
        var pullOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pullServers.Count; i++)
        {
            pullOrder.TryAdd(pullServers[i].Key, i);
        }

        // A build may update a stored row in place, so its server is snapshotted.
        var firstServerKey = pullServers.Count > 0 ? pullServers[0].Key : null;
        var heldServerKeys = new Dictionary<TKey, string?>(existing.Count);
        foreach (var kvp in existing)
        {
            heldServerKeys[kvp.Key] = string.IsNullOrEmpty(kvp.Value.ServerKey) ? firstServerKey : kvp.Value.ServerKey;
        }

        var run = new RefreshRun(existing, sourceCount, buildEnd, pullOrder, heldServerKeys, new HashSet<string>(_listedCleanly, StringComparer.OrdinalIgnoreCase));
        if (IsIncrementalRun)
        {
            var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in existing)
            {
                if (PriorityKeyOf(kvp.Value) is { } priorityKey)
                {
                    var order = run.OrderOf(heldServerKeys[kvp.Key]);
                    owners[priorityKey] = owners.TryGetValue(priorityKey, out var best) ? Math.Min(best, order) : order;
                }
            }

            run.IncrementalOwners = owners;
        }

        return run;
    }

    /// <summary>
    /// Builds and stores one row for each source item, in parallel when the sync type allows it.
    /// The rows are written in batches, one transaction each.
    /// </summary>
    private async Task BuildAllAsync(RefreshRun run, IList<TSource> sourceItems, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var parallelism = Math.Max(1, BuildRecordParallelism);
        if (parallelism == 1)
        {
            foreach (var src in sourceItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await BuildOneAsync(run, src, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await Parallel.ForEachAsync(
                sourceItems,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                (src, ct) => BuildOneAsync(run, src, progress, ct))
                .ConfigureAwait(false);
        }

        FlushWrites(run, all: true);
        run.SeenKeySet = new HashSet<TKey>(run.SeenKeys.Keys);
    }

    /// <summary>
    /// How many built rows are written in one transaction, since one commit for each row leaves a large scan waiting on the disk.
    /// </summary>
    protected const int WriteBatchSize = 250;

    /// <summary>
    /// Writes the rows built so far in one transaction. One writer at a time, so batches stay whole
    /// while the builders keep adding behind it.
    /// </summary>
    private void FlushWrites(RefreshRun run, bool all)
    {
        lock (run.FlushLock)
        {
            while (all ? !run.PendingWrites.IsEmpty : run.PendingWrites.Count >= WriteBatchSize)
            {
                var batch = new List<TRecord>(WriteBatchSize);
                while (batch.Count < WriteBatchSize && run.PendingWrites.TryDequeue(out var record))
                {
                    batch.Add(record);
                }

                Manager.UpsertMany(batch, (record, ex) =>
                {
                    run.CountPersistFailure();
                    Logger.LogError(ex, "{Task}: failed to upsert record for key {Key}", Name, FormatUtilities.SanitizeForLog(ExtractKey(record).ToString()));
                });
            }
        }
    }

    /// <summary>
    /// Builds one row and queues it for writing.
    /// A bad item never aborts the scan, and its failure is counted so the run skips the prune.
    /// </summary>
    private async ValueTask BuildOneAsync(RefreshRun run, TSource src, IProgress<double> progress, CancellationToken ct)
    {
        try
        {
            TRecord? record;
            try
            {
                record = await BuildRecordAsync(src, run.Existing, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A timeout cancels without the run's token, so it fails this item only.
                run.CountBuildFailure();
                Logger.LogError(ex, "{Task}: BuildRecordAsync threw for source {Source}. Record skipped", Name, FormatUtilities.SanitizeForLog(src?.ToString(), 300));
                return;
            }

            if (record == null)
            {
                return;
            }

            var key = ExtractKey(record);
            run.SeenKeys.TryAdd(key, 0);
            if (run.IsHeldByAbsentHigherServer(key, record))
            {
                return;
            }

            var priorityKey = PriorityKeyOf(record);
            if (priorityKey is not null
                && run.IncrementalOwners is { } owners
                && owners.TryGetValue(priorityKey, out var ownerOrder)
                && ownerOrder < run.OrderOf(record.ServerKey)
                && !MayOutrankOwner(record))
            {
                return;
            }

            if (priorityKey is not null)
            {
                run.SeenPriorityKeys.TryAdd(priorityKey, 0);
            }

            DecideStatus(record);
            if (record.Status == SyncStatus.Queued)
            {
                run.QueuedThisRun.Add(record);
            }

            run.PendingWrites.Enqueue(record);
            if (run.PendingWrites.Count >= WriteBatchSize)
            {
                FlushWrites(run, all: false);
            }
        }
        finally
        {
            var done = run.CountProcessed();
            progress.Report(FetchEnd + ((run.BuildEnd - FetchEnd) * done / run.Total));
        }
    }

    /// <summary>
    /// Runs the conflict pass.
    /// A failure leaves the queued rows as built and counts as a failure, so the run skips the prune and the dashboard shows why.
    /// </summary>
    private async Task ResolveRunConflictsAsync(RefreshRun run, CancellationToken cancellationToken)
    {
        if (run.QueuedThisRun.IsEmpty)
        {
            return;
        }

        try
        {
            await ResolveConflictsAsync(run.QueuedThisRun.ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            run.ConflictsFailed = true;
            Logger.LogError(ex, "{Task}: resolving conflicts with peers failed, queued rows are kept as they are", Name);
            RecordRunFailure("Refresh", $"Conflict resolution failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes rows whose item a higher ranked server now tracks, and counts them as seen so the prune does not treat them as stale.
    /// Nothing is retired while a server did not answer, or an outage would move its rows to a lower ranked server.
    /// </summary>
    private void RetireCoveredRows(RefreshRun run)
    {
        if (_sourceUnavailable)
        {
            if (!run.SeenPriorityKeys.IsEmpty)
            {
                Logger.LogInformation("{Task}: a server did not answer this run, so no rows are retired to another server", Name);
            }

            return;
        }

        var superseded = RetireSupersededRows(run.Existing, run.SeenKeySet, run.SeenPriorityKeys, run.HadFailures);
        if (superseded > 0)
        {
            Logger.LogInformation("{Task}: retired {Count} row(s) now covered by a higher priority server", Name, superseded);
        }
    }

    /// <summary>
    /// Prunes the rows the sources no longer hold, but only after a clean listing.
    /// A row missing after a source error or a failed build might still exist, so deleting it would lose data.
    /// </summary>
    private async Task<int> PruneIfSafeAsync(RefreshRun run, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_sourceUnavailable)
        {
            // A server that did not answer says nothing about its rows, so they stay.
            if (_unavailableBeyondOneServer || _listedCleanly.Count == 0)
            {
                Logger.LogWarning("{Task}: source was unavailable during discovery, skipping prune. No rows removed this run", Name);
                return 0;
            }

            var held = 0;
            foreach (var kvp in run.Existing)
            {
                if (!_listedCleanly.Contains(kvp.Value.ServerKey ?? string.Empty) && run.SeenKeySet.Add(kvp.Key))
                {
                    held++;
                }
            }

            Logger.LogWarning("{Task}: a server did not answer in full, so {Held} row(s) of it are left as they are. The other servers' rows are cleaned up as usual", Name, held);
        }

        if (run.HadFailures)
        {
            Logger.LogWarning(
                "{Task}: {BuildFailed} build / {PersistFailed} persist failure(s) this run, conflict resolution failed: {ConflictsFailed}. Skipping prune so partially processed items are not deleted",
                Name, run.BuildFailures, run.PersistFailures, run.ConflictsFailed);
            return 0;
        }

        if (PruneGuardApplies && PruneWouldExceedSafetyLimit(run, out var staleInScope, out var inScope))
        {
            Logger.LogWarning(
                "{Task}: refusing to prune {Stale} of {Existing} in-scope rows (>{Percent:P0}) in one run. This usually means the source answered with a truncated or empty catalog. If the removal is real, it will reconcile once the source returns a majority of the rows. To force it, reset this module's sync table from the dashboard",
                Name, staleInScope, inScope, MaxPruneFraction);
            MarkPruneBlocked($"The prune safety limit stopped a prune of {staleInScope} of {JPKribs.Jellyfin.Base.ActivityText.Count(inScope, "row")}. The source likely returned a truncated catalog.");
            return 0;
        }

        var pruneProgress = new Progress<double>(p =>
            progress.Report(run.BuildEnd + ((WorkEnd - run.BuildEnd) * Math.Clamp(p, 0, 100) / 100.0)));
        var pruned = await PruneStaleAsync(run.Existing, run.SeenKeySet, pruneProgress, cancellationToken).ConfigureAwait(false);
        if (pruned > 0)
        {
            Logger.LogInformation("{Task}: pruned {Count} stale records", Name, pruned);
        }

        return pruned;
    }

    /// <summary>
    /// The prune safety limit, which takes a clean looking listing that would delete most of the table as a truncated answer and not a real mass removal.
    /// It counts only rows in scope that the prune would act on, and judges each server as well as the whole table.
    /// </summary>
    /// <param name="run">The state of the current scan.</param>
    /// <param name="stale">The stale rows in the count that tripped, for the message.</param>
    /// <param name="inScope">The rows in scope in the count that tripped, for the message.</param>
    /// <returns><c>true</c> when the prune must not run.</returns>
    private bool PruneWouldExceedSafetyLimit(RefreshRun run, out int stale, out int inScope)
    {
        inScope = 0;
        stale = 0;
        var byServer = new Dictionary<string, (int InScope, int Stale)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in run.Existing)
        {
            if (!IsInScope(kvp.Value))
            {
                continue;
            }

            var isStale = !run.SeenKeySet.Contains(kvp.Key) && IsPruneCandidate(kvp.Value);
            inScope++;
            stale += isStale ? 1 : 0;
            var server = kvp.Value.ServerKey ?? string.Empty;
            byServer.TryGetValue(server, out var counts);
            byServer[server] = (counts.InScope + 1, counts.Stale + (isStale ? 1 : 0));
        }

        if (inScope >= PruneGuardMinRows && stale > inScope * MaxPruneFraction)
        {
            return true;
        }

        var worstServer = byServer.Values
            .Where(c => c.InScope >= PruneGuardMinRows && c.Stale > c.InScope * MaxPruneFraction)
            .OrderByDescending(c => c.Stale)
            .Take(1)
            .ToList();
        if (worstServer.Count == 0)
        {
            return false;
        }

        (inScope, stale) = worstServer[0];
        return true;
    }

    /// <summary>
    /// Logs the run's outcome and records a failure for the dashboard when rows did not build or store.
    /// A source outage or a blocked prune recorded its own failure earlier in the run, which is kept.
    /// </summary>
    private void ReportRunOutcome(RefreshRun run, int pruned)
    {
        if (_sourceUnavailable)
        {
            Logger.LogWarning("{Task} complete with source unavailable: {Processed} processed, prune skipped this run", Name, run.Processed);
        }
        else if (_pruneBlocked)
        {
            Logger.LogWarning("{Task} complete with prune blocked by safety limit: {Processed} processed, no rows removed this run", Name, run.Processed);
        }
        else if (run.HadFailures)
        {
            Logger.LogWarning(
                "{Task} complete: {Processed} processed, {Pruned} pruned, {BuildFailed} build-failed, {PersistFailed} persist-failed, conflict resolution failed: {ConflictsFailed} (see prior errors for details)",
                Name, run.Processed, pruned, run.BuildFailures, run.PersistFailures, run.ConflictsFailed);
            var reason = $"{run.BuildFailures} build failures, {run.PersistFailures} persist failures (check log)";
            if (run.ConflictsFailed)
            {
                reason += ", conflict resolution failed";
            }

            RecordRunFailure("Refresh", reason);
        }
        else
        {
            Logger.LogInformation("{Task} complete: {Processed} processed, {Pruned} pruned", Name, run.Processed, pruned);
            if (!IsIncrementalRun)
            {
                ClearRunFailure();
            }
        }
    }

    /// <summary>
    /// Says whether a row another server now covers may be deleted.
    /// An Ignored row is the operator's choice and is kept, and a sync type keeps rows holding state that belongs to the pair of servers.
    /// </summary>
    /// <param name="record">The row another server covers.</param>
    /// <returns><c>true</c> when it may be retired.</returns>
    protected virtual bool CanRetire(TRecord record) => record.Status != SyncStatus.Ignored;

    /// <summary>
    /// Deletes rows not seen this run whose collision key a seen row claimed, and marks the ones it must keep as seen.
    /// Retiring counts toward the same safety limit as the prune, for each server.
    /// </summary>
    private int RetireSupersededRows(
        IReadOnlyDictionary<TKey, TRecord> existing,
        HashSet<TKey> seenKeySet,
        ConcurrentDictionary<string, byte> seenPriorityKeys,
        bool hadFailures)
    {
        if (seenPriorityKeys.IsEmpty)
        {
            return 0;
        }

        var candidates = new List<KeyValuePair<TKey, TRecord>>();
        foreach (var kvp in existing)
        {
            if (seenKeySet.Contains(kvp.Key))
            {
                continue;
            }

            var priorityKey = PriorityKeyOf(kvp.Value);
            if (priorityKey is null || !seenPriorityKeys.ContainsKey(priorityKey))
            {
                continue;
            }

            if (!CanRetire(kvp.Value))
            {
                // Covered elsewhere but kept, so the prune must leave it alone.
                seenKeySet.Add(kvp.Key);
                continue;
            }

            candidates.Add(kvp);
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        if (hadFailures)
        {
            Logger.LogWarning("{Task}: items failed to build this run, so {Count} row(s) another server now covers are kept until a clean run", Name, candidates.Count);
            foreach (var kvp in candidates)
            {
                seenKeySet.Add(kvp.Key);
            }

            return 0;
        }

        var inScopeByServer = existing.Values.Where(IsInScope).GroupBy(r => r.ServerKey ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var tripped = candidates
            .GroupBy(c => c.Value.ServerKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => inScopeByServer.TryGetValue(g.Key, out var total) && total >= PruneGuardMinRows && g.Count() > total * MaxPruneFraction);
        if (PruneGuardApplies && tripped is not null)
        {
            Logger.LogWarning(
                "{Task}: refusing to retire {Count} of {Total} rows tracked from one server in one run. Another server claimed them, which usually means it answered without the paths that tell items apart",
                Name, tripped.Count(), inScopeByServer[tripped.Key]);
            MarkPruneBlocked($"The prune safety limit stopped {JPKribs.Jellyfin.Base.ActivityText.Count(tripped.Count(), "row")} from moving to another server.");
            foreach (var kvp in candidates)
            {
                seenKeySet.Add(kvp.Key);
            }

            return 0;
        }

        var retired = 0;
        foreach (var batch in candidates.Chunk(WriteBatchSize))
        {
            try
            {
                retired += Manager.DeleteByIds(batch.Select(kvp => kvp.Value.Id).ToList());
                foreach (var kvp in batch)
                {
                    seenKeySet.Add(kvp.Key);
                }
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                Logger.LogWarning(ex, "{Task}: failed to retire {Count} superseded row(s) starting at id {Id}", Name, batch.Length, batch[0].Value.Id);
            }
        }

        return retired;
    }

    /// <summary>Lets the subclass record its last run time, then saves the configuration.</summary>
    private void RecordRunCompletedAndSave() => ScanSources.RecordCompletedAndSave(_configManager, RecordRunCompleted, Logger, Name);

    /// <summary>Records this sync type's most recent run failure for the dashboard's status.</summary>
    private void RecordRunFailure(string phase, string reason)
        => RunFailureLog.Record(_configManager, ModuleMutexKey, phase, reason, Logger, Name);

    private void ClearRunFailure()
        => RunFailureLog.Clear(_configManager, ModuleMutexKey, "Refresh", Logger, Name);

    /// <summary>
    /// The state one scan carries from phase to phase: the stored rows, what the build saw and queued, its failure counts, and who held each row before it.
    /// </summary>
    private sealed class RefreshRun
    {
        private readonly Dictionary<string, int> _pullOrder;
        private readonly Dictionary<TKey, string?> _heldServerKeys;
        private readonly HashSet<string> _listedCleanly;
        private int _processed;
        private int _buildFailures;
        private int _persistFailures;

        /// <summary>Initializes a new instance of the <see cref="RefreshRun"/> class.</summary>
        /// <param name="existing">The rows stored when the run started, by key.</param>
        /// <param name="sourceCount">How many sources the run reads.</param>
        /// <param name="buildEnd">The progress value at which the build phase ends.</param>
        /// <param name="pullOrder">The rank of each source, by server key.</param>
        /// <param name="heldServerKeys">The server each stored row belonged to before the build, by row key.</param>
        /// <param name="listedCleanly">The servers whose listing finished without a failure.</param>
        public RefreshRun(
            Dictionary<TKey, TRecord> existing,
            int sourceCount,
            double buildEnd,
            Dictionary<string, int> pullOrder,
            Dictionary<TKey, string?> heldServerKeys,
            HashSet<string> listedCleanly)
        {
            Existing = existing;
            Total = Math.Max(sourceCount, 1);
            BuildEnd = buildEnd;
            _pullOrder = pullOrder;
            _heldServerKeys = heldServerKeys;
            _listedCleanly = listedCleanly;
        }

        /// <summary>Gets the rows stored when the run started, by key.</summary>
        public Dictionary<TKey, TRecord> Existing { get; }

        /// <summary>Gets or sets which server entry owns each source in an incremental run, by rank.</summary>
        public Dictionary<string, int>? IncrementalOwners { get; set; }

        /// <summary>Gets the rank of a source, with unknown servers last.</summary>
        /// <param name="serverKey">The server entry's key, or null.</param>
        /// <returns>The rank, where a lower number ranks higher.</returns>
        public int OrderOf(string? serverKey) => serverKey is not null && _pullOrder.TryGetValue(serverKey, out var order) ? order : int.MaxValue;

        /// <summary>Gets the number of sources, at least one, that progress is spread across.</summary>
        public int Total { get; }

        /// <summary>Gets the progress value at which the build phase ends.</summary>
        public double BuildEnd { get; }

        /// <summary>Gets the keys of every item the sources offered.</summary>
        public ConcurrentDictionary<TKey, byte> SeenKeys { get; } = new();

        /// <summary>Gets the collision keys the sources offered.</summary>
        public ConcurrentDictionary<string, byte> SeenPriorityKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets the rows this run set to Queued.</summary>
        public ConcurrentBag<TRecord> QueuedThisRun { get; } = new();

        /// <summary>Gets the rows built but not yet written to the table.</summary>
        public ConcurrentQueue<TRecord> PendingWrites { get; } = new();

        /// <summary>Gets the lock that lets one worker at a time write <see cref="PendingWrites"/>.</summary>
        public object FlushLock { get; } = new();

        /// <summary>Gets or sets the keys the build saw, fixed once the build is done.</summary>
        public HashSet<TKey> SeenKeySet { get; set; } = new();

        /// <summary>Gets or sets a value indicating whether settling conflicts failed this run.</summary>
        public bool ConflictsFailed { get; set; }

        /// <summary>Gets how many items have been built.</summary>
        public int Processed => Volatile.Read(ref _processed);

        /// <summary>Gets how many items could not be built.</summary>
        public int BuildFailures => Volatile.Read(ref _buildFailures);

        /// <summary>Gets how many rows could not be written.</summary>
        public int PersistFailures => Volatile.Read(ref _persistFailures);

        /// <summary>Gets a value indicating whether anything failed this run.</summary>
        public bool HadFailures => BuildFailures > 0 || PersistFailures > 0 || ConflictsFailed;

        /// <summary>Counts one built item.</summary>
        /// <returns>How many items have been built.</returns>
        public int CountProcessed() => Interlocked.Increment(ref _processed);

        /// <summary>Counts one item that could not be built.</summary>
        public void CountBuildFailure() => Interlocked.Increment(ref _buildFailures);

        /// <summary>Counts one row that could not be written.</summary>
        public void CountPersistFailure() => Interlocked.Increment(ref _persistFailures);

        /// <summary>
        /// Says whether a stored row belongs to a higher ranked server that did not answer this run, so a lower ranked one does not take it over for the outage.
        /// A server that connected but whose listing failed counts as absent too.
        /// </summary>
        /// <param name="key">The row's key.</param>
        /// <param name="built">The row this scan built.</param>
        /// <returns><c>true</c> when the row stays as it is this run.</returns>
        public bool IsHeldByAbsentHigherServer(TKey key, TRecord built)
        {
            if (!_heldServerKeys.TryGetValue(key, out var heldKey)
                || string.IsNullOrEmpty(heldKey)
                || string.Equals(heldKey, built.ServerKey, StringComparison.OrdinalIgnoreCase)
                || _listedCleanly.Contains(heldKey)
                || !_pullOrder.TryGetValue(heldKey, out var heldRank))
            {
                return false;
            }

            return !_pullOrder.TryGetValue(built.ServerKey ?? string.Empty, out var builtRank) || heldRank < builtRank;
        }
    }

    /// <summary>
    /// Reads stored rows on demand for <c>RefreshOneAsync</c>. The record builders use only <c>TryGetValue</c>.
    /// </summary>
    private sealed class StoredRows : IReadOnlyDictionary<TKey, TRecord>
    {
        private readonly RefreshSyncTaskBase<TRecord, TSource, TKey> _owner;

        /// <summary>Initializes a new instance of the <see cref="StoredRows"/> class.</summary>
        /// <param name="owner">The task whose table is read.</param>
        public StoredRows(RefreshSyncTaskBase<TRecord, TSource, TKey> owner) => _owner = owner;

        /// <inheritdoc/>
        public IEnumerable<TKey> Keys => Array.Empty<TKey>();

        /// <inheritdoc/>
        public IEnumerable<TRecord> Values => Array.Empty<TRecord>();

        /// <inheritdoc/>
        public int Count => 0;

        /// <inheritdoc/>
        public TRecord this[TKey key] => _owner.Manager.GetByKey(key) ?? throw new KeyNotFoundException();

        /// <inheritdoc/>
        public bool ContainsKey(TKey key) => _owner.Manager.GetByKey(key) is not null;

        /// <inheritdoc/>
        public bool TryGetValue(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TRecord value)
        {
            value = _owner.Manager.GetByKey(key);
            return value is not null;
        }

        /// <inheritdoc/>
        public IEnumerator<KeyValuePair<TKey, TRecord>> GetEnumerator() => Enumerable.Empty<KeyValuePair<TKey, TRecord>>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

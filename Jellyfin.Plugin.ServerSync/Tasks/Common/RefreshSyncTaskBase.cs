using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Refresh phase: scan source, fetch state for each item, populate the sync
/// table with snapshots and source hashes, and immediately decide each
/// row's status (<see cref="SyncStatus.Queued"/> if changes detected,
/// <see cref="SyncStatus.Synced"/> otherwise). Refresh + Compare run in
/// one pass. <see cref="SyncStatus.Pending"/> only appears for rows that
/// existed before this run and never got revisited (e.g. interrupted run).
/// <see cref="SyncStatus.Ignored"/> rows are preserved as user overrides
/// and never auto-transitioned.
/// </summary>
/// <typeparam name="TRecord">Record type.</typeparam>
/// <typeparam name="TSource">Type of items returned by the source list.</typeparam>
/// <typeparam name="TKey">Natural-key type used to correlate source and local.</typeparam>
public abstract class RefreshSyncTaskBase<TRecord, TSource, TKey> : IScheduledTask, IConfigurableScheduledTask
    where TRecord : SyncRecord
    where TKey : notnull
{
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IPluginConfigurationManager _configManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="manager">Table manager for upsert/prune operations.</param>
    /// <param name="clientFactory">Factory for the source-server HTTP client.</param>
    /// <param name="configManager">Plugin configuration accessor.</param>
    protected RefreshSyncTaskBase(
        ILogger logger,
        ISyncTableManager<TRecord, TKey> manager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(configManager);
        Logger = logger;
        Manager = manager;
        _clientFactory = clientFactory;
        _configManager = configManager;
    }

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
    /// Gets or sets the scan servers connected for the current run, in priority order. Populated by the
    /// default <see cref="TestConnectionAsync"/>, disposed at the end of <c>ExecuteAsync</c>, empty
    /// outside a run. The setter exists for subclasses and tests that provide their own sources.
    /// </summary>
    protected IReadOnlyList<ScanSource> Sources { get; set; } = Array.Empty<ScanSource>();

    /// <summary>
    /// Finds the connected source for a server entry key. A null key is a row written before servers
    /// became a list and belongs to the first configured scan server, which is where the single source
    /// migrated to, not to whichever server happened to connect first.
    /// </summary>
    /// <param name="serverKey">The entry key carried on a row, or null.</param>
    /// <returns>The source, or null when that server did not connect this run.</returns>
    protected ScanSource? SourceFor(string? serverKey)
    {
        if (string.IsNullOrEmpty(serverKey))
        {
            var pullServers = _configManager.Configuration.GetPullServers();
            if (pullServers.Count == 0)
            {
                return null;
            }

            serverKey = pullServers[0].Key;
        }

        foreach (var source in Sources)
        {
            if (string.Equals(source.Key, serverKey, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }

    // The per module tasks run inside Sync Content and Sync Information and from the dashboard, so
    // they stay registered but leave the scheduled task list.
    /// <inheritdoc />
    public bool IsHidden => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    // Explicit because the module tasks already have an IsEnabled method with a different meaning.
#pragma warning disable CA1033
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
    /// Returns true if this task should run given current configuration.
    /// </summary>
    protected abstract bool IsEnabled();

    /// <summary>
    /// Module key used to serialize this task against its module's Sync task.
    /// Both bases acquire <see cref="SyncModuleMutex"/> on the same key so a
    /// Refresh and a Sync within the same module never run simultaneously.
    /// </summary>
    protected abstract string ModuleMutexKey { get; }

    /// <summary>
    /// Maximum concurrent <see cref="BuildRecordAsync"/> calls per refresh.
    /// Default <c>1</c> (serial). Raise only when build is HTTP-bound , 
    /// Metadata fetches per-item image info and uses <c>8</c>. Parallelism
    /// doesn't help in-process Jellyfin reads or SQLite-write-bound work
    /// (the table manager's single write lock serializes the upsert).
    /// </summary>
    protected virtual int BuildRecordParallelism => 1;

    /// <summary>
    /// Connects every scan server and keeps the ones that answer, in priority order. A server that
    /// does not answer is logged and skipped for this run, and the run skips pruning, because rows
    /// that came from it cannot be re-confirmed. Returns false only when no server connects.
    /// Subclasses that override this should populate <see cref="Sources"/> themselves.
    /// </summary>
    protected virtual async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var connected = new List<ScanSource>();
        var servers = _configManager.Configuration.GetPullServers();
        for (var i = 0; i < servers.Count; i++)
        {
            var server = servers[i];
            SourceServerClient client;
            try
            {
                client = _clientFactory.Create(server);
            }
            catch (ArgumentException ex)
            {
                Logger.LogError("{Task}: server '{Server}' rejected: {Error}", Name, server.DisplayName, ex.Message);
                MarkSourceUnavailable($"server '{server.DisplayName}' has an invalid URL");
                continue;
            }

            Models.Configuration.ConnectionTestResult result;
            try
            {
                result = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Sources is assigned only after the loop, so the run's cleanup cannot see these
                // clients yet. Dispose them here before the failure or cancellation propagates.
                client.Dispose();
                foreach (var source in connected)
                {
                    source.Dispose();
                }

                throw;
            }

            if (!result.Success)
            {
                Logger.LogError("{Task}: connection to '{Server}' failed: {Error}", Name, server.DisplayName, result.ErrorMessage ?? "unknown");
                MarkSourceUnavailable($"server '{server.DisplayName}' is unreachable");
                client.Dispose();
                continue;
            }

            connected.Add(new ScanSource(server, client, i));
        }

        Sources = connected;
        return connected.Count > 0;
    }

    /// <summary>
    /// Runs once per run after the servers have connected and before any list is fetched. Default is a
    /// no-op. A module that keeps per run state should reset it here rather than in
    /// <see cref="GetListAsync"/>, which now runs once per server.
    /// </summary>
    protected virtual void OnRunStarting()
    {
    }

    /// <summary>
    /// Fetches the items one scan server offers this run. Called once per connected server, highest
    /// priority first. <paramref name="progress"/> reports 0 to 100 for this server only.
    /// </summary>
    /// <param name="source">The server to list from.</param>
    /// <param name="progress">Progress for this server's fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The work items this server contributes.</returns>
    protected abstract Task<IList<TSource>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// The key two servers collide on when they offer the same thing: a local path, a person name, a
    /// local user. Null means the item never collides. When several servers produce the same key the
    /// first in priority order keeps it and the rest are dropped before any record is built.
    /// </summary>
    /// <param name="source">A work item.</param>
    /// <returns>The collision key, or null.</returns>
    protected virtual string? PriorityKeyOf(TSource source) => null;

    private readonly HashSet<string> _claimedByHigherServers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a server listed earlier this run already offered the item with this collision key. A
    /// module can check it while listing a lower priority server and skip fetching the details of an
    /// item that server would lose anyway. The dedupe after listing still decides.
    /// </summary>
    /// <param name="priorityKey">The collision key, as <see cref="PriorityKeyOf(TSource)"/> gives it.</param>
    /// <returns>True when a higher priority server already covers it.</returns>
    protected bool ClaimedByHigherServer(string priorityKey) => _claimedByHigherServers.Contains(priorityKey);

    /// <summary>
    /// The same collision key read from a stored row, so a row that lost its priority to another
    /// server can be recognized and retired instead of treated as a removal.
    /// </summary>
    /// <param name="record">A stored row.</param>
    /// <returns>The collision key, or null.</returns>
    protected virtual string? PriorityKeyOf(TRecord record) => null;

    /// <summary>
    /// Keys of the connected servers whose listing this run finished without a source error. A server
    /// that connected but whose listing threw or reported a failure is left out, so it counts as absent
    /// when a row it holds is offered by a lower priority server.
    /// </summary>
    private readonly HashSet<string> _listedCleanly = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set by <see cref="MarkSourceUnavailable"/> while a server's listing is in progress, so the
    /// listing can be recorded as unclean. The listings run one server at a time.
    /// </summary>
    private volatile bool _currentListingFailed;

    /// <summary>
    /// Lists every connected server in priority order and keeps the first offer for each collision
    /// key. Progress is split evenly across the servers. A server whose listing throws is marked
    /// unavailable and contributes nothing this run, and the other servers are still listed.
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
            try
            {
                items = await GetListAsync(source, sourceProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A partial list from this server cannot be trusted, so none of it is used, and the
                // run skips pruning because rows from this server were not re-confirmed.
                Logger.LogWarning(ex, "{Task}: listing '{Server}' failed. Its items are left out of this run", Name, source.Name);
                MarkSourceUnavailable($"server '{source.Name}' failed while listing: {ex.Message}");
                continue;
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

        if (Sources.Count < 2)
        {
            return all;
        }

        var kept = new List<TSource>(all.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropped = 0;
        foreach (var item in all)
        {
            var key = PriorityKeyOf(item);
            if (key is not null && !seen.Add(key))
            {
                dropped++;
                continue;
            }

            kept.Add(item);
        }

        if (dropped > 0)
        {
            Logger.LogInformation("{Task}: {Dropped} item(s) offered by a lower priority server were already covered by a higher one", Name, dropped);
        }

        return kept;
    }

    /// <summary>
    /// Applies user/library filters to <paramref name="items"/>. Default
    /// implementation returns input unchanged.
    /// </summary>
    protected virtual Task<IList<TSource>> FilterAsync(IList<TSource> items, CancellationToken cancellationToken)
        => Task.FromResult(items);

    /// <summary>
    /// Builds and stores the row for one source item outside a scheduled run, for a change hint. The
    /// same build and status decision as the full refresh, under the module's mutex so a run in
    /// progress and a hint never write the same row at once. The stored rows are read on demand, since
    /// loading the whole table for one item would defeat the point of a hint.
    /// </summary>
    /// <param name="work">The source item and the server it came from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
    /// Readies any per run state <see cref="BuildRecordAsync"/> relies on for a single item, since
    /// <see cref="RefreshOneAsync"/> never runs <see cref="GetListAsync"/>. Default does nothing.
    /// </summary>
    /// <param name="work">The single item about to be built.</param>
    protected virtual void PrepareForOne(TSource work)
    {
    }

    /// <summary>
    /// Called once per run with the rows this run queued, before the prune, so a module can settle
    /// conflicts against peers that carry versions and un-queue rows this server's own edit should win.
    /// Default does nothing. Rows changed here must be stored again by the override.
    /// </summary>
    /// <param name="queued">The rows that ended the build queued.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual Task ResolveConflictsAsync(IList<TRecord> queued, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Builds (or updates) a record from a single source item. Implementations
    /// should:
    /// <list type="bullet">
    /// <item>Look up the local correlate (return null to skip when not found).</item>
    /// <item>Populate the record's <see cref="SyncableValue{T}"/> fields with
    /// current source/local values, scoped to enabled config flags.</item>
    /// <item>Recompute source hashes via <see cref="SyncableValue{T}.RecomputeSourceHash"/>.</item>
    /// </list>
    /// Returning null skips this source item entirely (no row written).
    /// </summary>
    protected abstract Task<TRecord?> BuildRecordAsync(
        TSource source,
        IReadOnlyDictionary<TKey, TRecord> existing,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extracts the natural key from a record (used for matching against
    /// the existing-rows dictionary and for stale-row detection).
    /// </summary>
    protected abstract TKey ExtractKey(TRecord record);

    /// <summary>
    /// Decides the post-Build status of a record. Default behavior:
    /// <list type="bullet">
    /// <item>If the record is <see cref="SyncStatus.Ignored"/>, leave alone.</item>
    /// <item>Otherwise, <see cref="SyncStatus.Queued"/> when <see cref="SyncRecord.HasChanges"/>
    /// is true, else <see cref="SyncStatus.Synced"/> (and call MarkSynced).</item>
    /// </list>
    /// Subclasses override to implement custom workflows, e.g. Content's
    /// approval gate where new items go to <see cref="SyncStatus.Pending"/>
    /// awaiting user approval rather than auto-Queued.
    /// </summary>
    protected virtual void DecideStatus(TRecord record)
    {
        if (record.Status == SyncStatus.Ignored)
        {
            return;
        }

        // A row that has burned its retry allowance stays Errored. Without
        // this the refresh re-queues it every run and the sync re-applies it
        // every run, so a row that can never converge churns forever , 
        // re-downloading images, rewriting user policies, with the failure
        // reason overwritten each cycle. The operator clears it with a Queue
        // or Retry action, which resets the count.
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
            // Source matches local, record the current source as the synced
            // baseline. This no longer suppresses future comparisons (see
            // SyncableValue.HasChanges). It keeps Synced/SyncedHash meaningful
            // for the modal and for per-module bookkeeping.
            record.MarkSynced();
            record.Status = SyncStatus.Synced;
            record.LastSyncTime = DateTime.UtcNow;
        }

        record.StatusDate = DateTime.UtcNow;
        record.Reason = null;
    }

    /// <summary>
    /// Returns true if the record falls within the current run's scope (i.e. it
    /// belongs to a still-enabled library/user mapping). Out-of-scope rows are
    /// skipped during pruning so disabling a mapping does not silently delete
    /// its tracking history (or, for Content, schedule its files for
    /// deletion). Default returns true (no scoping).
    /// </summary>
    protected virtual bool IsInScope(TRecord record) => true;

    /// <summary>
    /// Source library ids of every enabled library mapping, computed once when a run starts so
    /// <see cref="IsEnabledSourceLibrary"/> does not rebuild the mapping list for every row. Null
    /// outside a run.
    /// </summary>
    private HashSet<string>? _enabledSourceLibraryIdsForRun;

    /// <summary>
    /// Whether a source library id belongs to an enabled library mapping on any scan server. The
    /// shared library check behind the modules' <see cref="IsInScope"/> overrides. Inside a run it
    /// reads the set computed at the start of the run, outside one it reads the configuration.
    /// </summary>
    /// <param name="sourceLibraryId">The source library id carried on a row.</param>
    /// <returns>True when an enabled mapping has that source library id.</returns>
    protected bool IsEnabledSourceLibrary(string? sourceLibraryId)
        => (_enabledSourceLibraryIdsForRun ?? BuildEnabledSourceLibraryIds()).Contains(sourceLibraryId ?? string.Empty);

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
    /// Copies the row state a refresh must not reset from the stored row onto a freshly built one:
    /// the row id, status, last sync time, reason, and retry count. Without the retry count the
    /// retry ceiling in <see cref="DecideStatus"/> could never trip, since a fresh record starts at 0.
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
    /// Returns true if pruning this record would actually change anything.
    /// Rows the module's prune would no-op on (e.g. Content's already
    /// Ignored / pending-deletion rows) must not count toward the prune
    /// circuit breaker, or gradual legitimate removals accumulate until the
    /// breaker trips permanently. Default counts every row.
    /// </summary>
    protected virtual bool IsPruneCandidate(TRecord record) => true;

    /// <summary>
    /// Returns false when the module's prune is configured to be a complete
    /// no-op (e.g. Content with deletion disabled), so the circuit breaker
    /// doesn't record a scary "prune blocked" failure for a prune that would
    /// have done nothing anyway.
    /// </summary>
    protected virtual bool PruneGuardApplies => true;

    /// <summary>
    /// Set when the source returns anything other than a clean response during
    /// discovery this run. Volatile because discovery can run under
    /// <see cref="System.Threading.Tasks.Parallel"/>.
    /// </summary>
    private volatile bool _sourceUnavailable;

    /// <summary>
    /// Set when this run's prune was fully or partially refused by a safety
    /// guard (the base circuit breaker or a subclass guard such as Content's
    /// per-mapping empty-catalog check). While set, the end-of-run summary
    /// keeps the recorded failure instead of clearing it, otherwise a
    /// blocked prune would look like a fully successful run on the dashboard.
    /// </summary>
    private bool _pruneBlocked;

    /// <summary>
    /// Records that a prune safety guard refused to remove rows this run.
    /// The reason is surfaced on the dashboard and preserved through the
    /// end-of-run summary.
    /// </summary>
    protected void MarkPruneBlocked(string reason)
    {
        _pruneBlocked = true;
        RecordRunFailure("Refresh", reason);
    }

    /// <summary>
    /// Records that the source server returned an error, anything other than a
    /// clean response, during discovery this run. While set, the run skips
    /// pruning entirely: a partial or failed fetch means rows missing from the
    /// seen set might still exist on the source, so removing their tracking
    /// rows would be data loss. Only a run whose discovery completes cleanly
    /// prunes. The next clean run reconciles. Safe to call repeatedly and from
    /// concurrent discovery tasks.
    /// </summary>
    /// <param name="reason">Short description of what failed, for the log.</param>
    protected void MarkSourceUnavailable(string reason)
    {
        _currentListingFailed = true;
        if (_sourceUnavailable)
        {
            return;
        }

        _sourceUnavailable = true;
        Logger.LogWarning(
            "{Task}: {Reason}. Skipping prune this run so rows not re-confirmed on the source are left in place",
            Name, reason);
        RecordRunFailure("Refresh", reason);
    }

    /// <summary>
    /// Fraction of the existing table a single run is allowed to prune. A
    /// clean-looking discovery that would delete more than this is far more
    /// likely a source-side truncation (empty-but-200 answer, half a catalog)
    /// than a real mass removal, so the run refuses and leaves the rows for a
    /// later run to reconcile. Applies only when the table has at least
    /// <see cref="PruneGuardMinRows"/> rows, tiny tables can legitimately
    /// turn over completely.
    /// </summary>
    private const double MaxPruneFraction = 0.5;

    /// <summary>
    /// Minimum existing-row count before <see cref="MaxPruneFraction"/> kicks in.
    /// </summary>
    private const int PruneGuardMinRows = 50;

    /// <summary>
    /// Removes rows that no longer exist on the source. Default deletes them
    /// outright. Subclasses can override for soft-delete (Content marks them
    /// <see cref="SyncStatus.Pending"/> awaiting user approval). Out-of-scope
    /// rows (per <see cref="IsInScope"/>) are never pruned. Only called when
    /// discovery completed cleanly, a run that hit any source error skips
    /// pruning before reaching here (see <see cref="MarkSourceUnavailable"/>).
    /// <paramref name="progress"/> reports 0 to 100 for the prune phase only.
    /// the base scales it into the run's overall allocation, large prunes
    /// (100k+ rows) take real time and the bar must move through them.
    /// Returns the number of rows pruned.
    /// </summary>
    protected virtual Task<int> PruneStaleAsync(
        IReadOnlyDictionary<TKey, TRecord> existing,
        HashSet<TKey> seenKeys,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var stale = new List<TRecord>();
        foreach (var kvp in existing)
        {
            if (!seenKeys.Contains(kvp.Key) && IsInScope(kvp.Value))
            {
                stale.Add(kvp.Value);
            }
        }

        var pruned = 0;
        var reportEvery = Math.Max(1, stale.Count / 100);
        for (var i = 0; i < stale.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Manager.DeleteById(stale[i].Id);
                pruned++;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Task}: failed to prune stale record id {Id}", Name, stale[i].Id);
            }

            if ((i + 1) % reportEvery == 0)
            {
                progress.Report(100.0 * (i + 1) / stale.Count);
            }
        }

        progress.Report(100);
        return Task.FromResult(pruned);
    }

    /// <summary>
    /// Hook for post-run bookkeeping beyond timestamp updates (e.g. resolving
    /// LocalItemIds after the upsert pass). Default is a no-op. For the
    /// per-module "last refresh time" bump, override
    /// <see cref="RecordRunCompleted"/> instead, the base saves the config
    /// uniformly for all modules.
    /// </summary>
    protected virtual Task FinalizeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Per-module timestamp bump. Default is a no-op. Each module overrides
    /// to set its <c>LastXSyncTime</c> field on the configuration. The base
    /// calls this after <see cref="FinalizeAsync"/> and then persists the
    /// configuration with a single try/catch, modules don't repeat that
    /// save plumbing.
    /// </summary>
    protected virtual void RecordRunCompleted(Configuration.PluginConfiguration config, DateTime utcNow)
    {
        // Default: nothing to record.
    }

    /// <summary>
    /// None. The per module tasks are hidden and run only as steps of Sync Content and Sync Information,
    /// so a schedule of their own would run each module twice as often as intended.
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

        // Serialize against the same module's Sync task so concurrent runs
        // can't stomp each other's row writes.
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

    private async Task ExecuteCoreAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting {Task}", Name);
        _sourceUnavailable = false;
        _pruneBlocked = false;

        // TestConnectionAsync fills Sources with one connected client per
        // server. The finally disposes them on every exit path, including
        // exceptions from the build or prune and cancellation, so no client
        // outlives the run. A failure inside the connection loop itself
        // disposes its own clients, since Sources is not assigned yet.
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
            _enabledSourceLibraryIdsForRun = null;
        }
    }

    // Progress allocation. The snapshot takes 0 to 3 %, the source fetch 3 to 50 %, and 50 to 99 % is
    // split between the build and the prune by how many rows each will touch, so a run that builds
    // nothing but prunes 100k rows spends its band on the prune instead of freezing at the tail. The
    // finalize takes the last 1 %.
    private const double SnapshotEnd = 3.0;
    private const double FetchEnd = 50.0;
    private const double WorkEnd = 99.0;

    // Deletes are weighted cheaper than builds when the band is split, since they make no HTTP calls
    // and touch no blobs.
    private const double PruneRowWeight = 0.25;

    private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await TestConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            // Recorded so the dashboard shows the refresh failed instead of the operator assuming all is
            // well because the row count did not change.
            Logger.LogError("{Task}: connection check failed. Aborting refresh", Name);
            RecordRunFailure("Refresh", "Connection check failed: no scan server is reachable or every configured key is invalid");
            return;
        }

        _enabledSourceLibraryIdsForRun = BuildEnabledSourceLibraryIds();
        OnRunStarting();
        progress.Report(0);

        var existing = SnapshotExistingRows();
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
        var pruned = await PruneIfSafeAsync(run, progress, cancellationToken).ConfigureAwait(false);

        progress.Report(WorkEnd);
        await FinalizeAsync(cancellationToken).ConfigureAwait(false);
        RecordRunCompletedAndSave();
        progress.Report(100);

        ReportRunOutcome(run, pruned);
    }

    // Strict read. If this degraded to an empty list on a transient database error, every source item
    // would look new and the upserts would overwrite statuses the operator set, such as Ignored and
    // pending approvals. Failing the run is the safe outcome.
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

    private RefreshRun StartRun(Dictionary<TKey, TRecord> existing, int sourceCount)
    {
        // The build and the prune share the band by the rows each will touch. The prune count is not
        // exact until the build finishes, but existing minus source is a solid lower bound.
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

        // The server each stored row belonged to before the build. A module may build by updating the
        // stored row in place (People does), which stamps the new server onto the very object the guard
        // would read, so the guard reads this snapshot instead. A row with no key is a row written before
        // servers became a list and belongs to the first configured server.
        var firstServerKey = pullServers.Count > 0 ? pullServers[0].Key : null;
        var heldServerKeys = new Dictionary<TKey, string?>(existing.Count);
        foreach (var kvp in existing)
        {
            heldServerKeys[kvp.Key] = string.IsNullOrEmpty(kvp.Value.ServerKey) ? firstServerKey : kvp.Value.ServerKey;
        }

        return new RefreshRun(existing, sourceCount, buildEnd, pullOrder, heldServerKeys, new HashSet<string>(_listedCleanly, StringComparer.OrdinalIgnoreCase));
    }

    // Builds and stores one row per source item. When BuildRecordAsync spends most of its time on per
    // item HTTP calls, as Metadata does, parallelism turns hours into minutes. SQLite still serializes
    // the upserts through the manager's write lock.
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
                async (src, ct) => await BuildOneAsync(run, src, progress, ct).ConfigureAwait(false))
                .ConfigureAwait(false);
        }

        run.SeenKeySet = new HashSet<TKey>(run.SeenKeys.Keys);
    }

    // A single bad item never aborts the refresh. Its failure is counted so the run skips the prune and
    // the summary says how many rows did not make it.
    private async ValueTask BuildOneAsync(RefreshRun run, TSource src, IProgress<double> progress, CancellationToken ct)
    {
        try
        {
            TRecord? record;
            try
            {
                record = await BuildRecordAsync(src, run.Existing, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A bad source item, a path that does not translate, or a source HTTP failure. An HTTP
                // timeout surfaces as a cancellation while the run's token is not cancelled, so it lands
                // here as a per item failure too.
                run.CountBuildFailure();
                Logger.LogError(ex, "{Task}: BuildRecordAsync threw for source {Source}. Record skipped", Name, src);
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
            if (priorityKey is not null)
            {
                run.SeenPriorityKeys.TryAdd(priorityKey, 0);
            }

            // The snapshot is fresh, so the status is decided right away. Content overrides this for its
            // approval modes.
            DecideStatus(record);
            if (record.Status == SyncStatus.Queued)
            {
                run.QueuedThisRun.Add(record);
            }

            try
            {
                Manager.Upsert(record);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                run.CountPersistFailure();
                Logger.LogError(ex, "{Task}: failed to upsert record for key {Key}", Name, key);
            }
        }
        finally
        {
            var done = run.CountProcessed();
            progress.Report(FetchEnd + ((run.BuildEnd - FetchEnd) * done / run.Total));
        }
    }

    // A failed conflict pass leaves the queued rows as they were built. It counts as a failure so the
    // run skips the prune and the dashboard shows why.
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            run.ConflictsFailed = true;
            Logger.LogError(ex, "{Task}: resolving conflicts with peers failed, queued rows are kept as they are", Name);
            RecordRunFailure("Refresh", $"Conflict resolution failed: {ex.Message}");
        }
    }

    // When priorities change or a higher priority server gains an item, the row that tracked it under a
    // lower priority server is not a removal. The thing is still here, tracked by the new winner's row,
    // so the old row is deleted quietly and counted as seen, and neither the prune nor the circuit
    // breaker treats it as stale. Never while a server did not answer this run: its items were never
    // offered, so a lower server's rows would claim them, and the unanswered server's rows, negotiated
    // bases included, would be deleted over an outage rather than a real change of ownership.
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

    // Removes rows no longer present on the source, but only after a clean discovery. When a source
    // answered with anything but a real response, a row missing from the seen set might still exist
    // there, and when an item could not be built or stored it is missing from the seen set too. Either
    // way deleting would be data loss, so the next clean run reconciles instead.
    private async Task<int> PruneIfSafeAsync(RefreshRun run, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_sourceUnavailable)
        {
            Logger.LogWarning("{Task}: source was unavailable during discovery, skipping prune. No rows removed this run", Name);
            return 0;
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
            MarkPruneBlocked($"Prune of {staleInScope}/{inScope} rows blocked by safety limit, source likely returned a truncated catalog");
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
    /// The circuit breaker. A discovery that looked clean but would delete most of the table is taken as
    /// a truncated answer from the source, not a real mass removal, since an empty catalog answered with
    /// success passes every other guard. It is the last line of defense before the table, and for
    /// Content the local files, are destroyed. Counts cover the rows in scope only, so rows under a
    /// disabled mapping cannot dilute the fraction, and only rows the prune would act on count as stale,
    /// so pending and ignored rows cannot pile up into a breaker that never resets. It is judged per
    /// server as well as for the table, since one server answering with an empty catalog is only a
    /// share of the table and would slip under a table wide limit while every row it tracks is deleted.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="stale">The stale rows in the count that tripped, for the message.</param>
    /// <param name="inScope">The rows in scope in the count that tripped, for the message.</param>
    /// <returns>True when the prune must not run.</returns>
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

        // Reported against the server that tripped, which is the number that explains it.
        (inScope, stale) = worstServer[0];
        return true;
    }

    // Tells "no rows changed" apart from "rows failed to build or store", and records a failure the
    // dashboard shows. A source outage or a blocked prune recorded its own failure earlier in the run,
    // which is kept rather than cleared here.
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
            ClearRunFailure();
        }
    }

    /// <summary>
    /// Whether a row another server now covers may be deleted. An ignored row is the operator's choice
    /// and is kept. A module whose rows hold state that belongs to the pair of servers, such as a
    /// negotiated history base, keeps those too.
    /// </summary>
    /// <param name="record">The row.</param>
    /// <returns>True when it may be retired.</returns>
    protected virtual bool CanRetire(TRecord record) => record.Status != SyncStatus.Ignored;

    /// <summary>
    /// Deletes rows that were not seen this run but whose collision key was claimed by a row that
    /// was, and marks them seen. Rows that may not be retired are marked seen and kept. Retiring counts
    /// toward the same safety limit as the prune, per server, since a higher priority server that
    /// answers with a catalog it cannot fully describe would otherwise take a lower one's rows with it.
    /// Returns how many were retired.
    /// </summary>
    private int RetireSupersededRows(
        IReadOnlyDictionary<TKey, TRecord> existing,
        HashSet<TKey> seenKeySet,
        System.Collections.Concurrent.ConcurrentDictionary<string, byte> seenPriorityKeys,
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
                // Covered elsewhere but kept: it is no stale row either, so the prune leaves it alone.
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
            MarkPruneBlocked($"Retiring {tripped.Count()} rows to another server blocked by safety limit");
            foreach (var kvp in candidates)
            {
                seenKeySet.Add(kvp.Key);
            }

            return 0;
        }

        var retired = 0;
        foreach (var kvp in candidates)
        {
            try
            {
                Manager.DeleteById(kvp.Value.Id);
                seenKeySet.Add(kvp.Key);
                retired++;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Task}: failed to retire superseded row id {Id}", Name, kvp.Value.Id);
            }
        }

        return retired;
    }

    /// <summary>
    /// Lets the subclass stamp its module's last-run timestamp, then saves
    /// the configuration. Failures here are logged but never propagate, a
    /// failed save mustn't mask the actual run result.
    /// </summary>
    private void RecordRunCompletedAndSave()
    {
        try
        {
            RecordRunCompleted(_configManager.Configuration, DateTime.UtcNow);
            _configManager.SaveConfiguration();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Task}: failed to save run-completion timestamp", Name);
        }
    }

    /// <summary>
    /// Records the most-recent run failure for this module on the plugin
    /// configuration. Surfaced in the dashboard via the Status endpoint.
    /// </summary>
    private void RecordRunFailure(string phase, string reason)
        => RunFailureLog.Record(_configManager, ModuleMutexKey, phase, reason, Logger, Name);

    private void ClearRunFailure()
        => RunFailureLog.Clear(_configManager, ModuleMutexKey, "Refresh", Logger, Name);

    /// <summary>
    /// The state one refresh run carries from phase to phase: the stored rows, what the build saw and
    /// queued, its failure counts, and who held each row before it.
    /// </summary>
    private sealed class RefreshRun
    {
        private readonly Dictionary<string, int> _pullOrder;
        private readonly Dictionary<TKey, string?> _heldServerKeys;
        private readonly HashSet<string> _listedCleanly;
        private int _processed;
        private int _buildFailures;
        private int _persistFailures;

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

        public Dictionary<TKey, TRecord> Existing { get; }

        public int Total { get; }

        public double BuildEnd { get; }

        public System.Collections.Concurrent.ConcurrentDictionary<TKey, byte> SeenKeys { get; } = new();

        public System.Collections.Concurrent.ConcurrentDictionary<string, byte> SeenPriorityKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public System.Collections.Concurrent.ConcurrentBag<TRecord> QueuedThisRun { get; } = new();

        /// <summary>Gets or sets the keys the build saw, fixed once the build is done.</summary>
        public HashSet<TKey> SeenKeySet { get; set; } = new();

        public bool ConflictsFailed { get; set; }

        public int Processed => Volatile.Read(ref _processed);

        public int BuildFailures => Volatile.Read(ref _buildFailures);

        public int PersistFailures => Volatile.Read(ref _persistFailures);

        public bool HadFailures => BuildFailures > 0 || PersistFailures > 0 || ConflictsFailed;

        public int CountProcessed() => Interlocked.Increment(ref _processed);

        public void CountBuildFailure() => Interlocked.Increment(ref _buildFailures);

        public void CountPersistFailure() => Interlocked.Increment(ref _persistFailures);

        /// <summary>
        /// Whether a stored row belongs to a higher priority server that did not answer this run. A
        /// lower one would otherwise take it over for the outage and hand it back afterwards, writing
        /// its values and then the first server's values over the same row twice. A server that
        /// connected but whose listing failed counts as absent too, since it offered nothing trustworthy.
        /// </summary>
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

    // Reads stored rows on demand for RefreshOneAsync. Only TryGetValue is used by the record builders.
    private sealed class StoredRows : IReadOnlyDictionary<TKey, TRecord>
    {
        private readonly RefreshSyncTaskBase<TRecord, TSource, TKey> _owner;

        public StoredRows(RefreshSyncTaskBase<TRecord, TSource, TKey> owner) => _owner = owner;

        public IEnumerable<TKey> Keys => Array.Empty<TKey>();

        public IEnumerable<TRecord> Values => Array.Empty<TRecord>();

        public int Count => 0;

        public TRecord this[TKey key] => _owner.Manager.GetByKey(key) ?? throw new KeyNotFoundException();

        public bool ContainsKey(TKey key) => _owner.Manager.GetByKey(key) is not null;

        public bool TryGetValue(TKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TRecord value)
        {
            value = _owner.Manager.GetByKey(key);
            return value is not null;
        }

        public IEnumerator<KeyValuePair<TKey, TRecord>> GetEnumerator() => System.Linq.Enumerable.Empty<KeyValuePair<TKey, TRecord>>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

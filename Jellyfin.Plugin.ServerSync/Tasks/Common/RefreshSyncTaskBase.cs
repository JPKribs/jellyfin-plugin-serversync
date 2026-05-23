using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Refresh phase: scan source, fetch state for each item, populate the sync
/// table with snapshots and source hashes — and immediately decide each
/// row's status (<see cref="SyncStatus.Queued"/> if changes detected,
/// <see cref="SyncStatus.Synced"/> otherwise). Refresh + Compare run in
/// one pass; <see cref="SyncStatus.Pending"/> only appears for rows that
/// existed before this run and never got revisited (e.g. interrupted run).
/// <see cref="SyncStatus.Ignored"/> rows are preserved as user overrides
/// and never auto-transitioned.
/// </summary>
/// <typeparam name="TRecord">Record type.</typeparam>
/// <typeparam name="TSource">Type of items returned by the source list.</typeparam>
/// <typeparam name="TKey">Natural-key type used to correlate source and local.</typeparam>
public abstract class RefreshSyncTaskBase<TRecord, TSource, TKey> : IScheduledTask
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
    /// Gets or sets the source-server client for the current run. Created
    /// by the default <see cref="TestConnectionAsync"/>; disposed at the
    /// end of <c>ExecuteAsync</c>; null outside a run. Setter is exposed
    /// for subclasses that override <see cref="TestConnectionAsync"/>.
    /// </summary>
    protected SourceServerClient? Client { get; set; }

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
    /// Default <c>1</c> (serial). Raise only when build is HTTP-bound —
    /// Metadata fetches per-item image info and uses <c>8</c>. Parallelism
    /// doesn't help in-process Jellyfin reads or SQLite-write-bound work
    /// (the table manager's single write lock serializes the upsert).
    /// </summary>
    protected virtual int BuildRecordParallelism => 1;

    /// <summary>
    /// Verifies the source server is reachable. Default implementation
    /// creates <see cref="Client"/> from the configured source URL/API key
    /// and runs the connection test; subclasses can override to add custom
    /// pre-flight checks but should call <c>base.TestConnectionAsync</c>
    /// to populate <see cref="Client"/>.
    /// </summary>
    protected virtual async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;
        Client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);
        var result = await Client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            Logger.LogError("{Task}: source connection failed — {Error}", Name, result.ErrorMessage ?? "unknown");
        }

        return result.Success;
    }

    /// <summary>
    /// Fetches the list of source items to consider this run.
    /// <paramref name="progress"/> reports 0–100 for the fetch phase only;
    /// the base class scales it into the run's overall allocation.
    /// </summary>
    protected abstract Task<IList<TSource>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Applies user/library filters to <paramref name="items"/>. Default
    /// implementation returns input unchanged.
    /// </summary>
    protected virtual Task<IList<TSource>> FilterAsync(IList<TSource> items, CancellationToken cancellationToken)
        => Task.FromResult(items);

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
    /// Subclasses override to implement custom workflows — e.g. Content's
    /// approval gate where new items go to <see cref="SyncStatus.Pending"/>
    /// awaiting user approval rather than auto-Queued.
    /// </summary>
    protected virtual void DecideStatus(TRecord record)
    {
        if (record.Status == SyncStatus.Ignored)
        {
            return;
        }

        if (record.HasChanges)
        {
            record.Status = SyncStatus.Queued;
        }
        else
        {
            // Source matches local — record the current source as the synced
            // baseline so future runs can short-circuit via SyncedHash.
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
    /// its tracking history (or — for Content — schedule its files for
    /// deletion). Default returns true (no scoping).
    /// </summary>
    protected virtual bool IsInScope(TRecord record) => true;

    /// <summary>
    /// Removes rows that no longer exist on the source. Default deletes them
    /// outright; subclasses can override for soft-delete (Content marks them
    /// <see cref="SyncStatus.Pending"/> awaiting user approval). Out-of-scope
    /// rows (per <see cref="IsInScope"/>) are never pruned. Returns the
    /// number of rows pruned.
    /// </summary>
    protected virtual Task<int> PruneStaleAsync(
        IReadOnlyDictionary<TKey, TRecord> existing,
        HashSet<TKey> seenKeys,
        CancellationToken cancellationToken)
    {
        var pruned = 0;
        foreach (var kvp in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seenKeys.Contains(kvp.Key))
            {
                continue;
            }

            if (!IsInScope(kvp.Value))
            {
                continue;
            }

            try
            {
                Manager.DeleteById(kvp.Value.Id);
                pruned++;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Task}: failed to prune stale record id {Id}", Name, kvp.Value.Id);
            }
        }

        return Task.FromResult(pruned);
    }

    /// <summary>
    /// Hook for post-run bookkeeping beyond timestamp updates (e.g. resolving
    /// LocalItemIds after the upsert pass). Default is a no-op. For the
    /// per-module "last refresh time" bump, override
    /// <see cref="RecordRunCompleted"/> instead — the base saves the config
    /// uniformly for all modules.
    /// </summary>
    protected virtual Task FinalizeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Per-module timestamp bump. Default is a no-op; each module overrides
    /// to set its <c>LastXSyncTime</c> field on the configuration. The base
    /// calls this after <see cref="FinalizeAsync"/> and then persists the
    /// configuration with a single try/catch — modules don't repeat that
    /// save plumbing.
    /// </summary>
    protected virtual void RecordRunCompleted(Configuration.PluginConfiguration config, DateTime utcNow)
    {
        // Default: nothing to record.
    }

    /// <inheritdoc />
    public abstract IEnumerable<TaskTriggerInfo> GetDefaultTriggers();

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

        if (!await TestConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            // Bumped to LogError + recorded to config so the dashboard can
            // surface "last refresh failed: connection check" instead of the
            // user assuming everything's fine because the row count didn't
            // change.
            Logger.LogError("{Task}: connection check failed; aborting refresh", Name);
            RecordRunFailure("Refresh", "Connection check failed — source server unreachable or credentials invalid");
            return;
        }

        // Progress allocation:
        //   0–  3 %  existing-row snapshot
        //   3– 50 %  source-side fetch (sub-reported by GetListAsync)
        //  50– 95 %  per-item BuildRecordAsync + Upsert
        //  95–100 %  prune + finalize
        const double SnapshotEnd = 3.0;
        const double FetchEnd = 50.0;
        const double BuildEnd = 95.0;
        progress.Report(0);

        // 1. Snapshot existing rows so we can detect stale ones at the end.
        var existingList = Manager.GetAll();
        var existing = new Dictionary<TKey, TRecord>(existingList.Count);
        foreach (var rec in existingList)
        {
            existing[ExtractKey(rec)] = rec;
        }

        progress.Report(SnapshotEnd);

        // 2. Fetch + filter source items. Sub-progress (0–100 within the
        // fetch phase) is mapped into the SnapshotEnd–FetchEnd band.
        var fetchProgress = new Progress<double>(p =>
            progress.Report(SnapshotEnd + ((FetchEnd - SnapshotEnd) * Math.Clamp(p, 0, 100) / 100.0)));
        var sourceItems = await GetListAsync(fetchProgress, cancellationToken).ConfigureAwait(false);
        sourceItems = await FilterAsync(sourceItems, cancellationToken).ConfigureAwait(false);

        progress.Report(FetchEnd);

        // 3. Build/update one record per source item. When BuildRecordAsync
        // spends most of its time on per-item HTTP calls (Metadata fetches
        // per-item image info from the source), parallelism here turns
        // hours into minutes. SQLite still serializes the actual Upsert via
        // the manager's WriteLock, so concurrent builds queue cleanly at
        // persist time.
        var seenKeys = new System.Collections.Concurrent.ConcurrentDictionary<TKey, byte>();
        var processed = 0;
        var total = Math.Max(sourceItems.Count, 1);
        var parallelism = Math.Max(1, BuildRecordParallelism);

        // Per-item failures during build/upsert get counted so the run
        // summary tells the user how many records didn't make it. A single
        // bad item must not abort the whole refresh — that's the audit fix.
        var buildFailures = 0;
        var persistFailures = 0;

        async ValueTask BuildOneAsync(TSource src, CancellationToken ct)
        {
            try
            {
                TRecord? record;
                try
                {
                    record = await BuildRecordAsync(src, existing, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Bad source item (path translate fault, source HTTP
                    // hiccup, etc.). Log loudly with item context where
                    // possible, count the failure, and let other items
                    // continue. Without this catch, one bad record aborts
                    // the entire refresh and the row vanishes silently from
                    // the user's table.
                    Interlocked.Increment(ref buildFailures);
                    Logger.LogError(ex, "{Task}: BuildRecordAsync threw for source {Source}; record skipped", Name, src);
                    return;
                }

                if (record == null)
                {
                    return;
                }

                var key = ExtractKey(record);
                seenKeys.TryAdd(key, 0);

                // Refresh + Compare in one pass: snapshot data is fresh, so
                // decide status immediately. Subclasses can override
                // DecideStatus to implement workflows like Content's approval
                // gate.
                DecideStatus(record);

                try
                {
                    Manager.Upsert(record);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref persistFailures);
                    Logger.LogError(ex, "{Task}: failed to upsert record for key {Key}", Name, key);
                }
            }
            finally
            {
                var p = Interlocked.Increment(ref processed);
                progress.Report(FetchEnd + ((BuildEnd - FetchEnd) * p / total));
            }
        }

        if (parallelism == 1)
        {
            foreach (var src in sourceItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await BuildOneAsync(src, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await Parallel.ForEachAsync(
                sourceItems,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken
                },
                async (src, ct) => await BuildOneAsync(src, ct).ConfigureAwait(false))
                .ConfigureAwait(false);
        }

        var seenKeySet = new HashSet<TKey>(seenKeys.Keys);

        // 4. Prune rows that are no longer present on the source.
        var pruned = await PruneStaleAsync(existing, seenKeySet, cancellationToken).ConfigureAwait(false);

        if (pruned > 0)
        {
            Logger.LogInformation("{Task}: pruned {Count} stale records", Name, pruned);
        }

        try
        {
            await FinalizeAsync(cancellationToken).ConfigureAwait(false);
            RecordRunCompletedAndSave();
        }
        finally
        {
            Client?.Dispose();
            Client = null;
        }

        progress.Report(100);

        // Surface per-item failure counts so the user / log-reader can
        // tell the difference between "no rows changed" (normal) and "lots
        // of rows silently failed to build or persist" (problem). Logs at
        // Warning when any failure occurred and records a config-side
        // failure entry so the dashboard can surface it.
        if (buildFailures > 0 || persistFailures > 0)
        {
            Logger.LogWarning(
                "{Task} complete: {Processed} processed, {Pruned} pruned, {BuildFailed} build-failed, {PersistFailed} persist-failed (see prior errors for details)",
                Name, processed, pruned, buildFailures, persistFailures);
            RecordRunFailure("Refresh", $"{buildFailures} build failures, {persistFailures} persist failures (check log)");
        }
        else
        {
            Logger.LogInformation("{Task} complete: {Processed} processed, {Pruned} pruned", Name, processed, pruned);
            ClearRunFailure();
        }
    }

    /// <summary>
    /// Lets the subclass stamp its module's last-run timestamp, then saves
    /// the configuration. Failures here are logged but never propagate — a
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
    /// Best-effort — config save failure is logged but doesn't propagate.
    /// </summary>
    private void RecordRunFailure(string phase, string reason)
    {
        try
        {
            var failures = _configManager.Configuration.LastRunFailures;
            failures.RemoveAll(f => string.Equals(f.ModuleKey, ModuleMutexKey, StringComparison.OrdinalIgnoreCase));
            failures.Add(new SyncRunFailure
            {
                ModuleKey = ModuleMutexKey,
                Phase = phase,
                Reason = reason,
                Timestamp = DateTime.UtcNow
            });
            _configManager.SaveConfiguration();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Task}: failed to record run-failure outcome", Name);
        }
    }

    private void ClearRunFailure()
    {
        try
        {
            var failures = _configManager.Configuration.LastRunFailures;
            var removed = failures.RemoveAll(f => string.Equals(f.ModuleKey, ModuleMutexKey, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                _configManager.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Task}: failed to clear run-failure outcome", Name);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Sync phase: read <see cref="SyncStatus.Queued"/> rows and apply each one
/// to the local server. Successful applies transition to
/// <see cref="SyncStatus.Synced"/> and update the synced hashes. Failures
/// transition to <see cref="SyncStatus.Errored"/> with the exception message
/// captured in <see cref="SyncRecord.Reason"/>.
/// </summary>
/// <typeparam name="TRecord">Record type.</typeparam>
/// <typeparam name="TKey">Natural-key type.</typeparam>
public abstract class SyncQueueTaskBase<TRecord, TKey> : IScheduledTask, IConfigurableScheduledTask
    where TRecord : SyncRecord
    where TKey : notnull
{
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IPluginConfigurationManager _configManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    protected SyncQueueTaskBase(
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
    /// default <see cref="BeforeRunAsync"/>, disposed at the end of <c>ExecuteAsync</c>, empty outside a
    /// run. Rows carry the key of the server they came from, and <see cref="SourceFor"/> turns that
    /// back into the connected source to talk to.
    /// </summary>
    protected IReadOnlyList<ScanSource> Sources { get; set; } = Array.Empty<ScanSource>();

    /// <summary>
    /// Finds the connected source a row belongs to. A row with no key was written before servers
    /// became a list and belongs to the first configured scan server, which is where the single source
    /// migrated to, not to whichever server happened to connect first.
    /// </summary>
    /// <param name="record">The row.</param>
    /// <returns>The source, or null when that server did not connect this run.</returns>
    protected ScanSource? SourceFor(TRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var serverKey = record.ServerKey;
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

    /// <summary>
    /// The connected source a row belongs to, or an exception naming the server when it did not
    /// connect this run, so the row errors with a reason an operator can act on.
    /// </summary>
    /// <param name="record">The row.</param>
    /// <returns>The source.</returns>
    protected ScanSource RequireSource(TRecord record)
    {
        var source = SourceFor(record);
        if (source is not null)
        {
            return source;
        }

        var config = _configManager.Configuration;
        var server = string.IsNullOrEmpty(record.ServerKey)
            ? config.GetPullServers().FirstOrDefault()
            : config.FindServer(record.ServerKey);
        var name = server?.DisplayName ?? (string.IsNullOrEmpty(record.ServerKey) ? "the first scan server" : record.ServerKey);
        throw new InvalidOperationException($"Source server '{name}' is not available this run");
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
    /// Module key used to serialize this task against its module's Refresh
    /// task. Both bases acquire <see cref="SyncModuleMutex"/> on the same key
    /// so a Refresh and a Sync within the same module never run
    /// simultaneously.
    /// </summary>
    protected abstract string ModuleMutexKey { get; }

    /// <summary>
    /// Maximum parallelism for <see cref="ApplyAsync"/>. Default <c>1</c>
    /// (serial). Override to enable concurrent applies, Content uses this
    /// to download multiple items at once while still benefiting from the
    /// base's status-transition + persistence boilerplate.
    /// </summary>
    protected virtual int MaxDegreeOfParallelism => 1;

    /// <summary>
    /// Pre-flight hook run after <see cref="IsEnabled"/> and before the
    /// queued items are processed. Default creates a client for every scan
    /// server, keeps the ones whose connection test passes in
    /// <see cref="Sources"/>, and returns false (aborting the run) when none
    /// connects. Subclasses with custom pre-flight (disk space, circuit
    /// breaker, etc.) should override and call <c>base.BeforeRunAsync</c>.
    /// </summary>
    protected virtual async Task<bool> BeforeRunAsync(CancellationToken cancellationToken)
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
                client.Dispose();
                continue;
            }

            connected.Add(new ScanSource(server, client, i));
        }

        Sources = connected;
        if (connected.Count == 0)
        {
            FailPreflight("no scan server is reachable or every configured key is invalid");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records why the pre-flight is aborting, so the dashboard shows the real cause instead of the
    /// generic abort message. Call before returning false from <see cref="BeforeRunAsync"/>.
    /// </summary>
    /// <param name="reason">The cause, in words an operator can act on.</param>
    protected void FailPreflight(string reason)
    {
        Logger.LogError("{Task}: {Reason}", Name, reason);
        _preflightFailureReason = reason;
    }

    /// <summary>
    /// Runs once per apply group before any of its records are applied. Default is a no-op. A
    /// module that can settle a whole group with the source in one call does that work here and
    /// lets <see cref="ApplyAsync(TRecord, CancellationToken)"/> consume the result per record.
    /// </summary>
    /// <param name="group">The records about to be applied.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual Task PrepareGroupAsync(IList<TRecord> group, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Applies the queued change to the local server. Throwing transitions
    /// the record to <see cref="SyncStatus.Errored"/>.
    /// <see cref="OperationCanceledException"/> always propagates.
    /// </summary>
    protected abstract Task ApplyAsync(TRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Progress-aware apply. The default forwards to
    /// <see cref="ApplyAsync(TRecord, CancellationToken)"/> and ignores
    /// <paramref name="itemProgress"/>, right for modules whose per-item work
    /// is near-instant. Content overrides this to stream download progress
    /// (fraction 0 to 1 of the item) so a multi-hour file moves the bar instead
    /// of freezing it.
    /// </summary>
    protected virtual Task ApplyAsync(TRecord record, IProgress<double>? itemProgress, CancellationToken cancellationToken)
        => ApplyAsync(record, cancellationToken);

    /// <summary>
    /// Relative progress weight of one record. Default 1, every item counts
    /// equally, which is honest when per-item cost is uniform (a history
    /// write, a policy update). Content overrides with the file size in
    /// bytes: a 50 GB movie is not the same amount of work as a 5 MB episode,
    /// and counting them equally made the bar sprint through small files and
    /// crawl through large ones.
    /// </summary>
    protected virtual long GetApplyWeight(TRecord record) => 1;

    /// <summary>
    /// Called after a successful <see cref="ApplyAsync"/> to confirm the write
    /// landed. Default is a no-op. Modules that mutate live Jellyfin state
    /// override to re-read and compare. Throwing transitions the record to
    /// <see cref="SyncStatus.Errored"/> exactly like a failed
    /// <see cref="ApplyAsync"/>. Records with per-category state may call
    /// <c>record.Foo.MarkSynced()</c> on individual categories before throwing
    /// so partial success persists alongside the Errored row.
    /// </summary>
    protected virtual Task VerifyAfterApplyAsync(TRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Applies one stored row outside a scheduled run, for a change hint, against the server it came
    /// from. The same apply, verify, and bookkeeping as the full run, under the module's mutex.
    /// </summary>
    /// <param name="record">The queued row.</param>
    /// <param name="source">The connected server the row came from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the row is now synced. On failure the row holds the reason.</returns>
    public async Task<bool> ApplyRowAsync(TRecord record, ScanSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(source);

        var moduleMutex = SyncModuleMutex.ForModule(ModuleMutexKey);
        if (!await moduleMutex.WaitAsync(SyncModuleMutex.SingleRowWait, cancellationToken).ConfigureAwait(false))
        {
            throw new ModuleBusyException($"{Name} is running");
        }

        try
        {
            Sources = new[] { source };
            if (!await BeforeRowAsync(cancellationToken).ConfigureAwait(false))
            {
                record.Status = SyncStatus.Errored;
                record.StatusDate = DateTime.UtcNow;
                record.Reason = _preflightFailureReason ?? "pre-flight failed";
                Manager.Upsert(record);
                return false;
            }

            await PrepareGroupAsync(new List<TRecord> { record }, cancellationToken).ConfigureAwait(false);
            var ok = await ApplyOneAsync(record, null, cancellationToken).ConfigureAwait(false);
            await AfterRowAsync(ok, cancellationToken).ConfigureAwait(false);
            return ok;
        }
        finally
        {
            Sources = Array.Empty<ScanSource>();
            moduleMutex.Release();
        }
    }

    /// <summary>
    /// Pre-flight for a single row apply, without connecting servers, since the caller provides the
    /// source. Modules with checks beyond the connection, such as disk space, override. Call
    /// <see cref="FailPreflight"/> with the reason before returning false.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> to go ahead.</returns>
    protected virtual Task<bool> BeforeRowAsync(CancellationToken cancellationToken)
    {
        _preflightFailureReason = null;
        return Task.FromResult(true);
    }

    /// <summary>Follow up after a single row apply, such as asking the library to notice a new file. Default does nothing.</summary>
    /// <param name="applied">Whether the row applied.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual Task AfterRowAsync(bool applied, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Registers the write about to happen with the apply guard, so the change observer treats the
    /// events it raises as the echo of a sync rather than a local edit. Default registers nothing.
    /// </summary>
    /// <param name="record">The row about to be applied.</param>
    /// <returns>A handle to dispose when the write is done, or null.</returns>
    protected virtual IDisposable? EnterApplyGuard(TRecord record) => null;

    /// <summary>
    /// Called after <see cref="OnApplySucceeded"/> and before the row is stored, for work that needs
    /// the source, such as recording the version the applied value carries. Default does nothing.
    /// </summary>
    /// <param name="record">The applied row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    protected virtual Task AfterApplySucceededAsync(TRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Called after a successful apply, before the record is marked Synced.
    /// Default implementation calls <see cref="SyncRecord.MarkSynced"/>, which
    /// copies <c>SourceHash → SyncedHash</c> on each constituent
    /// <see cref="SyncableValue{T}"/>. Override only if some fields are
    /// applied conditionally and shouldn't all be marked at once.
    /// </summary>
    protected virtual void OnApplySucceeded(TRecord record) => record.MarkSynced();

    /// <summary>
    /// Called after an apply throws, before the record is persisted as Errored.
    /// Default increments <see cref="SyncRecord.RetryCount"/>, which the
    /// refresh's retry ceiling reads: an Errored row that has used its
    /// allowance stays Errored instead of being queued again every run.
    /// Content overrides to also feed its <c>GetErroredItemsForRetry</c>
    /// query, and to leave the count alone for a row its circuit breaker
    /// skipped without trying.
    /// </summary>
    protected virtual void OnApplyFailed(TRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.RetryCount++;
    }

    /// <summary>
    /// Hook for post-run bookkeeping beyond timestamp updates, Content uses
    /// this to process pending deletions and trigger a library refresh on
    /// every Sync run. Default is a no-op. For the per-module "last sync
    /// time" bump, override <see cref="RecordRunCompleted"/> instead.
    /// <paramref name="progress"/> reports 0 to 100 for the finalize phase only.
    /// the base scales it into the run's overall 90 to 100% band.
    /// </summary>
    protected virtual Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;

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

    /// <summary>
    /// Returns the items to apply this run. Default is rows in
    /// <see cref="SyncStatus.Queued"/>. Content overrides to also include
    /// errored-with-retries-left rows. Strict read: the lenient
    /// <c>GetByStatus</c> returns an empty list on transient DB errors,
    /// which would make this run stamp a clean completion while the queue
    /// silently went unprocessed.
    /// </summary>
    protected virtual IList<TRecord> GetItemsToApply() => Manager.GetByStatusStrict(SyncStatus.Queued);

    /// <summary>
    /// Splits the items into sequentially-applied groups: groups run in
    /// order with a full barrier between them, while items inside a group
    /// may apply in parallel (per <see cref="MaxDegreeOfParallelism"/>).
    /// Default is a single group. Metadata overrides so parent folders
    /// (Series/Season/…) are fully applied before their leaves, a sorted
    /// flat list alone doesn't guarantee that once the loop is parallel.
    /// </summary>
    protected virtual IEnumerable<IList<TRecord>> GetApplyGroups(IList<TRecord> items)
    {
        yield return items;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!IsEnabled())
        {
            Logger.LogDebug("{Task} disabled, skipping", Name);
            return;
        }

        // Serialize against the same module's Refresh task so concurrent runs
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
        progress.Report(0);

        // BeforeRunAsync fills Sources with one connected client per server.
        // The finally disposes them on every exit path, including a pre-flight
        // abort after some servers connected, exceptions from the apply loop,
        // and cancellation, so no client outlives the run. A failure inside the
        // connection loop itself disposes its own clients, since Sources is
        // not assigned yet.
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
        }
    }

    private string? _preflightFailureReason;

    private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _preflightFailureReason = null;
        if (!await BeforeRunAsync(cancellationToken).ConfigureAwait(false))
        {
            // Bumped to LogError + recorded to config so the dashboard can
            // surface "last sync aborted: pre-flight failure" instead of
            // the user wondering why queued rows aren't draining. Subclass
            // BeforeRunAsync overrides log specific reasons (disk space,
            // circuit breaker, connection). This captures the high-level
            // fact of the abort.
            Logger.LogError("{Task}: pre-flight aborted run, see prior log entries for the specific check that failed", Name);
            RecordRunFailure("Sync", _preflightFailureReason ?? "Pre-flight aborted (see log for cause: connection / disk space / circuit breaker)");
            return;
        }

        // Progress allocation:
        //   0 to 90 %  per-item ApplyAsync loop, weighted by GetApplyWeight and
        //            fed by per-item in-flight fractions, a run downloading
        //            one huge file and nine small ones reports bytes moved,
        //            not "1 of 10 items"
        //  90 to 100 %  FinalizeAsync (Content's library-refresh phase fits here
        //            so the bar moves while ValidateMediaLibrary runs)
        const double ApplyEnd = 90.0;

        // Rows from a server that is no longer configured, or that did not connect this run, have
        // nothing to pull from. They are left as they are rather than errored on every run.
        // A row with no key belongs to the first configured server and is left the same way when
        // that server did not connect. With no configured servers at all (a subclass that supplies its
        // own sources) such a row has no server to wait for and is applied.
        var all = GetItemsToApply();
        var hasPullServers = _configManager.Configuration.GetPullServers().Count > 0;
        var queued = all.Where(r => SourceFor(r) is not null || (string.IsNullOrEmpty(r.ServerKey) && !hasPullServers)).ToList();
        if (queued.Count < all.Count)
        {
            Logger.LogInformation("{Task}: leaving {Count} queued row(s) whose server is not configured or did not connect this run", Name, all.Count - queued.Count);
        }

        var successes = 0;
        var failures = 0;

        long totalWeight = 0;
        foreach (var record in queued)
        {
            totalWeight += Math.Max(1, GetApplyWeight(record));
        }

        totalWeight = Math.Max(1, totalWeight);

        // completedWeight counts finished items (success or failure, either
        // way their share of the run is spent). Inflight carries each running
        // item's partial contribution. Guarded by one gate because byte
        // callbacks arrive from parallel download workers.
        var progressGate = new object();
        double completedWeight = 0;
        var inflight = new Dictionary<TRecord, double>(ReferenceEqualityComparer.Instance);

        void ReportOverall()
        {
            double fraction;
            lock (progressGate)
            {
                var sum = completedWeight;
                foreach (var kvp in inflight)
                {
                    sum += kvp.Value;
                }

                fraction = sum / totalWeight;
            }

            progress.Report(ApplyEnd * Math.Clamp(fraction, 0, 1));
        }

        void OnItemProgress(TRecord record, long weight, double fraction)
        {
            var contribution = Math.Clamp(fraction, 0, 1) * weight;
            lock (progressGate)
            {
                // Monotone per item: a download retry restarts the file's
                // byte count, and a bar that visibly rewinds reads as a bug.
                if (inflight.TryGetValue(record, out var previous) && contribution <= previous)
                {
                    return;
                }

                inflight[record] = contribution;
            }

            ReportOverall();
        }

        void OnItemDone(TRecord record, long weight)
        {
            lock (progressGate)
            {
                inflight.Remove(record);
                completedWeight += weight;
            }

            ReportOverall();
        }

        var maxParallel = Math.Max(1, MaxDegreeOfParallelism);
        foreach (var group in GetApplyGroups(queued))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await PrepareGroupAsync(group, cancellationToken).ConfigureAwait(false);

            if (maxParallel == 1 || group.Count <= 1)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Logger.LogInformation("{Task}: cancellation requested, stopping queue processing", Name);
                        break;
                    }

                    var record = group[i];
                    var weight = Math.Max(1, GetApplyWeight(record));
                    var itemProgress = new DelegateProgress(f => OnItemProgress(record, weight, f));
                    if (await ApplyOneAsync(record, itemProgress, cancellationToken).ConfigureAwait(false))
                    {
                        successes++;
                    }
                    else
                    {
                        failures++;
                    }

                    OnItemDone(record, weight);
                }
            }
            else
            {
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxParallel,
                    CancellationToken = cancellationToken
                };

                try
                {
                    await Parallel.ForEachAsync(group, options, async (record, ct) =>
                    {
                        var weight = Math.Max(1, GetApplyWeight(record));
                        var itemProgress = new DelegateProgress(f => OnItemProgress(record, weight, f));
                        if (await ApplyOneAsync(record, itemProgress, ct).ConfigureAwait(false))
                        {
                            Interlocked.Increment(ref successes);
                        }
                        else
                        {
                            Interlocked.Increment(ref failures);
                        }

                        OnItemDone(record, weight);
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Logger.LogInformation("{Task}: cancellation requested, stopping queue processing", Name);
                }
            }
        }

        // A cancelled run must not fall through to the completion path below:
        // stamping the last-sync timestamp and clearing the failure record
        // would make an aborted run (with items still queued) look like a
        // clean finished one on the dashboard. Propagate like the refresh base.
        cancellationToken.ThrowIfCancellationRequested();

        progress.Report(ApplyEnd);

        var finalizeProgress = new Progress<double>(p =>
            progress.Report(ApplyEnd + ((100.0 - ApplyEnd) * Math.Clamp(p, 0, 100) / 100.0)));
        await FinalizeAsync(finalizeProgress, cancellationToken).ConfigureAwait(false);
        RecordRunCompletedAndSave();

        progress.Report(100);

        Logger.LogInformation("{Task} complete: {Success} synced, {Failure} errored out of {Total}", Name, successes, failures, queued.Count);

        // Mirror the refresh base: record/clear the run outcome so the
        // dashboard can surface "last sync had errors" without log-diving.
        // We treat any errored item as a non-clean run.
        if (failures > 0)
        {
            RecordRunFailure("Sync", $"{failures} of {queued.Count} item(s) errored, open the table to see per-row reasons");
        }
        else
        {
            ClearRunFailure();
        }
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
        => RunFailureLog.Clear(_configManager, ModuleMutexKey, "Sync", Logger, Name);

    /// <summary>
    /// Plain-lambda IProgress. <see cref="Progress{T}"/> posts through the
    /// captured SynchronizationContext, which reorders and defers reports.
    /// progress math here is already thread-safe, so report inline.
    /// </summary>
    private sealed class DelegateProgress : IProgress<double>
    {
        private readonly Action<double> _handler;

        public DelegateProgress(Action<double> handler) => _handler = handler;

        public void Report(double value) => _handler(value);
    }

    private async Task<bool> ApplyOneAsync(TRecord record, IProgress<double>? itemProgress, CancellationToken cancellationToken)
    {
        try
        {
            using (EnterApplyGuard(record))
            {
                await ApplyAsync(record, itemProgress, cancellationToken).ConfigureAwait(false);
                await VerifyAfterApplyAsync(record, cancellationToken).ConfigureAwait(false);
            }

            OnApplySucceeded(record);
            await AfterApplySucceededAsync(record, cancellationToken).ConfigureAwait(false);
            record.Status = SyncStatus.Synced;
            record.StatusDate = DateTime.UtcNow;
            record.LastSyncTime = DateTime.UtcNow;
            record.Reason = null;
            Manager.Upsert(record);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An HTTP timeout surfaces as a cancellation while the run's token is not cancelled, so it
            // lands here and errors this row instead of aborting the whole run.
            Logger.LogError(ex, "{Task}: apply failed for record id {Id}", Name, record.Id);
            OnApplyFailed(record);
            record.Status = SyncStatus.Errored;
            record.StatusDate = DateTime.UtcNow;
            record.Reason = ex.Message;
            try
            {
                Manager.Upsert(record);
            }
            catch (Exception persistEx)
            {
                Logger.LogError(persistEx, "{Task}: failed to persist Errored status for record id {Id}", Name, record.Id);
            }

            return false;
        }
    }
}

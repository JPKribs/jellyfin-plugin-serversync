using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Database;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Applies each <see cref="SyncStatus.Queued"/> row to this server.
/// A row that applies becomes <see cref="SyncStatus.Synced"/>, and one that fails becomes <see cref="SyncStatus.Errored"/> with the reason in <see cref="SyncRecord.Reason"/>.
/// </summary>
/// <typeparam name="TRecord">The sync table's row type.</typeparam>
/// <typeparam name="TKey">The type of a row's natural key.</typeparam>
public abstract class SyncQueueTaskBase<TRecord, TKey> : IScheduledTask, IConfigurableScheduledTask
    where TRecord : SyncRecord
    where TKey : notnull
{
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IPluginConfigurationManager _configManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncQueueTaskBase{TRecord, TKey}"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
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
    /// Gets or sets the sources connected for the current run, in rank order.
    /// It is filled by <see cref="BeforeRunAsync"/>, disposed when the run ends, and empty outside a run.
    /// </summary>
    protected IReadOnlyList<ScanSource> Sources { get; set; } = Array.Empty<ScanSource>();

    /// <summary>
    /// Finds the connected source a row belongs to. A row with no key belongs to the first
    /// configured source, not to whichever connected first.
    /// </summary>
    /// <param name="record">The row whose server key is looked up.</param>
    /// <returns>The source, or null when that server did not connect this run.</returns>
    protected ScanSource? SourceFor(TRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ScanSources.Find(Sources, _configManager, record.ServerKey);
    }

    /// <summary>
    /// Returns the connected source a row belongs to, or throws naming the server when it did not connect this run.
    /// </summary>
    /// <param name="record">The row whose server key is looked up.</param>
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
    /// Gets the key that serializes this task against its sync type's scan task through <see cref="SyncModuleMutex"/>.
    /// </summary>
    protected abstract string ModuleMutexKey { get; }

    /// <summary>
    /// Gets how many rows are applied at once, which is one unless overridden.
    /// </summary>
    protected virtual int MaxDegreeOfParallelism => 1;

    /// <summary>
    /// Gets how many rows of one apply group are applied at once, which is <see cref="MaxDegreeOfParallelism"/> unless overridden.
    /// </summary>
    /// <param name="group">The group about to be applied.</param>
    /// <returns>The number of rows applied at once.</returns>
    protected virtual int ParallelismFor(IList<TRecord> group) => MaxDegreeOfParallelism;

    /// <summary>
    /// Runs before any row is applied. The default connects every source into <see cref="Sources"/> and
    /// returns <c>false</c> when none connects, and an override with its own checks calls the base.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connection tests.</param>
    /// <returns><c>false</c> to stop the run.</returns>
    protected virtual async Task<bool> BeforeRunAsync(CancellationToken cancellationToken)
    {
        var connected = await ScanSources.ConnectAsync(_configManager, _clientFactory, Logger, Name, null, cancellationToken).ConfigureAwait(false);
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
    /// generic abort message. Call before returning <c>false</c> from <see cref="BeforeRunAsync"/>.
    /// </summary>
    /// <param name="reason">The cause, in words an operator can act on.</param>
    protected void FailPreflight(string reason)
    {
        Logger.LogError("{Task}: {Reason}", Name, FormatUtilities.SanitizeForLog(reason, 300));
        _preflightFailureReason = reason;
    }

    /// <summary>
    /// Runs once for each apply group before its rows are applied, and does nothing by default.
    /// A sync type that can settle a whole group with the source in one call does it here.
    /// </summary>
    /// <param name="group">The rows about to be applied.</param>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>A task that completes when the group is ready.</returns>
    protected virtual Task PrepareGroupAsync(IList<TRecord> group, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Applies the queued change to this server. Throwing makes the row <see cref="SyncStatus.Errored"/>,
    /// and <see cref="OperationCanceledException"/> always propagates.
    /// </summary>
    /// <param name="record">The row to apply.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>A task that completes when the change is applied.</returns>
    protected abstract Task ApplyAsync(TRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a row while reporting how far along it is, from 0 to 1.
    /// The default ignores <paramref name="itemProgress"/> and forwards to <see cref="ApplyAsync(TRecord, CancellationToken)"/>.
    /// </summary>
    /// <param name="record">The row to apply.</param>
    /// <param name="itemProgress">Receives the row's progress, or null.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <returns>A task that completes when the change is applied.</returns>
    protected virtual Task ApplyAsync(TRecord record, IProgress<double>? itemProgress, CancellationToken cancellationToken)
        => ApplyAsync(record, cancellationToken);

    /// <summary>
    /// Gets the share of the run's progress one row takes, which is 1 by default.
    /// Content returns the file size, so the bar follows bytes moved and not files finished.
    /// </summary>
    /// <param name="record">The row to weigh.</param>
    /// <returns>The weight, which sets how much of the run's progress the row takes.</returns>
    protected virtual long GetApplyWeight(TRecord record) => 1;

    /// <summary>
    /// Confirms the write landed after a successful apply, and does nothing by default.
    /// Throwing makes the row <see cref="SyncStatus.Errored"/>, and categories marked synced before the throw are kept.
    /// </summary>
    /// <param name="record">The row that was applied.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the write is confirmed.</returns>
    protected virtual Task VerifyAfterApplyAsync(TRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Applies one stored row outside a scheduled run, for a hint, against the server it came from.
    /// It runs the same apply, verify, and bookkeeping as the full run, under the sync type's mutex.
    /// </summary>
    /// <param name="record">The queued row.</param>
    /// <param name="source">The connected server the row came from.</param>
    /// <param name="cancellationToken">Cancels the wait for the mutex and the apply.</param>
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
            if (!await BeforeRowAsync(record, cancellationToken).ConfigureAwait(false))
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
    /// Checks a single row before it is applied, without connecting servers, since the caller provides the source.
    /// An override calls <see cref="FailPreflight"/> with the reason before returning <c>false</c>.
    /// </summary>
    /// <param name="record">The row about to be applied.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><c>true</c> to go ahead.</returns>
    protected virtual Task<bool> BeforeRowAsync(TRecord record, CancellationToken cancellationToken)
    {
        _preflightFailureReason = null;
        return Task.FromResult(true);
    }

    /// <summary>Follows up after a single row apply, such as asking the library to notice a new file, and does nothing by default.</summary>
    /// <param name="applied">Whether the row applied.</param>
    /// <param name="cancellationToken">Cancels the follow up.</param>
    /// <returns>A task that completes when the follow up is done.</returns>
    protected virtual Task AfterRowAsync(bool applied, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Registers the write about to happen with the apply guard, so the change observer treats the
    /// events it raises as the echo of a sync and not a local edit. The default registers nothing.
    /// </summary>
    /// <param name="record">The row about to be applied.</param>
    /// <returns>A handle to dispose when the write is done, or null.</returns>
    protected virtual IDisposable? EnterApplyGuard(TRecord record) => null;

    /// <summary>
    /// Runs after <see cref="OnApplySucceeded"/> and before the row is stored, for work that needs
    /// the source, such as recording the stamp the applied value carries. The default does nothing.
    /// </summary>
    /// <param name="record">The applied row.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the work is done.</returns>
    protected virtual Task AfterApplySucceededAsync(TRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Marks the row's values synced after a successful apply, before the row becomes <see cref="SyncStatus.Synced"/>.
    /// Override only when some fields are applied conditionally.
    /// </summary>
    /// <param name="record">The applied row.</param>
    protected virtual void OnApplySucceeded(TRecord record) => record.MarkSynced();

    /// <summary>
    /// Counts a failed apply on <see cref="SyncRecord.RetryCount"/> before the row is stored as <see cref="SyncStatus.Errored"/>.
    /// The scan's retry ceiling reads the count, so a row that has used its allowance stays Errored.
    /// </summary>
    /// <param name="record">The row that failed.</param>
    protected virtual void OnApplyFailed(TRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.RetryCount++;
    }

    /// <summary>
    /// Does the sync type's work after every row is applied, such as Content's pending deletions, and does nothing by default.
    /// <paramref name="progress"/> runs from 0 to 100, and the base scales it into the last tenth of the run.
    /// </summary>
    /// <param name="progress">Receives progress from 0 to 100.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the work is done.</returns>
    protected virtual Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Records the sync type's last sync time on the configuration, and does nothing by default.
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

    /// <summary>
    /// Returns the rows to apply this run, which are the <see cref="SyncStatus.Queued"/> rows by default.
    /// The read is strict, because a lenient read returns nothing on a database error and the run would record a clean finish.
    /// </summary>
    /// <returns>The rows to apply.</returns>
    protected virtual IList<TRecord> GetItemsToApply() => Manager.GetByStatusStrict(SyncStatus.Queued);

    /// <summary>
    /// Says whether a row is applied even when its server did not connect this run, because applying it
    /// reads nothing from that server. The default is <c>false</c>.
    /// </summary>
    /// <param name="record">The row to check.</param>
    /// <returns><c>true</c> to apply it anyway.</returns>
    protected virtual bool AppliesWithoutSource(TRecord record) => false;

    /// <summary>
    /// Splits the rows into groups that are applied one after another, with the rows of each group in parallel.
    /// The default is one group, and Metadata overrides it so parent folders are applied before their leaves.
    /// </summary>
    /// <param name="items">The rows to sync.</param>
    /// <returns>The groups, which are applied one after another with the rows of each group in parallel.</returns>
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
        progress.Report(0);

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

    /// <summary>Applies the queued rows, then finalizes and records the outcome.</summary>
    private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _preflightFailureReason = null;
        if (!await BeforeRunAsync(cancellationToken).ConfigureAwait(false))
        {
            // Recorded so the dashboard explains why queued rows did not drain.
            Logger.LogError("{Task}: pre-flight aborted run, see prior log entries for the specific check that failed", Name);
            RecordRunFailure("Sync", _preflightFailureReason ?? "Pre-flight aborted (see log for cause: connection / disk space / circuit breaker)");
            return;
        }

        const double ApplyEnd = 90.0;

        // Rows whose server did not connect are left queued, not errored on every run.
        var all = GetItemsToApply();
        var hasPullServers = _configManager.Configuration.GetPullServers().Count > 0;
        var queued = all.Where(r => SourceFor(r) is not null || AppliesWithoutSource(r) || (string.IsNullOrEmpty(r.ServerKey) && !hasPullServers)).ToList();
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
                // A download retry restarts the byte count, and the bar must not rewind.
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

        foreach (var group in GetApplyGroups(queued))
        {
            var maxParallel = Math.Max(1, ParallelismFor(group));
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

        // A cancelled run must not record a clean finish while rows are still queued.
        cancellationToken.ThrowIfCancellationRequested();

        progress.Report(ApplyEnd);

        var finalizeProgress = new Progress<double>(p =>
            progress.Report(ApplyEnd + ((100.0 - ApplyEnd) * Math.Clamp(p, 0, 100) / 100.0)));
        await FinalizeAsync(finalizeProgress, cancellationToken).ConfigureAwait(false);
        RecordRunCompletedAndSave();

        progress.Report(100);

        Logger.LogInformation("{Task} complete: {Success} synced, {Failure} errored out of {Total}", Name, successes, failures, queued.Count);

        // Any errored row makes the run unclean, which the dashboard shows.
        if (failures > 0)
        {
            RecordRunFailure("Sync", $"{failures} of {JPKribs.Jellyfin.Base.ActivityText.Count(queued.Count, "item")} errored, open the table to see per-row reasons");
        }
        else
        {
            ClearRunFailure();
        }
    }

    /// <summary>Lets the subclass record its last run time, then saves the configuration.</summary>
    private void RecordRunCompletedAndSave() => ScanSources.RecordCompletedAndSave(_configManager, RecordRunCompleted, Logger, Name);

    /// <summary>Records this sync type's most recent run failure for the dashboard's status.</summary>
    private void RecordRunFailure(string phase, string reason)
        => RunFailureLog.Record(_configManager, ModuleMutexKey, phase, reason, Logger, Name);

    private void ClearRunFailure()
        => RunFailureLog.Clear(_configManager, ModuleMutexKey, "Sync", Logger, Name);

    /// <summary>
    /// Reports progress inline. <see cref="Progress{T}"/> posts through the captured
    /// SynchronizationContext, which reorders and defers reports, and the progress math here is
    /// already thread-safe.
    /// </summary>
    private sealed class DelegateProgress : IProgress<double>
    {
        private readonly Action<double> _handler;

        /// <summary>Initializes a new instance of the <see cref="DelegateProgress"/> class.</summary>
        /// <param name="handler">Called with each report.</param>
        public DelegateProgress(Action<double> handler) => _handler = handler;

        /// <inheritdoc/>
        public void Report(double value) => _handler(value);
    }

    /// <summary>Applies and verifies one row, then stores it as Synced or Errored.</summary>
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // An HTTP timeout cancels without the run's token, so it errors this row only.
            Logger.LogError(ex, "{Task}: apply failed for record id {Id}", Name, record.Id);
            OnApplyFailed(record);
            record.Status = SyncStatus.Errored;
            record.StatusDate = DateTime.UtcNow;
            record.Reason = ex.Message;
            try
            {
                Manager.Upsert(record);
            }
            // A failed write must not stop the rows that remain.
            catch (Exception persistEx)
            {
                Logger.LogError(persistEx, "{Task}: failed to persist Errored status for record id {Id}", Name, record.Id);
            }

            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks.Common;

/// <summary>
/// Sync phase: read <see cref="SyncStatus.Queued"/> rows and apply each one
/// to the local server. Successful applies transition to
/// <see cref="SyncStatus.Synced"/> and update the synced hashes; failures
/// transition to <see cref="SyncStatus.Errored"/> with the exception message
/// captured in <see cref="SyncRecord.Reason"/>.
/// </summary>
/// <typeparam name="TRecord">Record type.</typeparam>
/// <typeparam name="TKey">Natural-key type.</typeparam>
public abstract class SyncQueueTaskBase<TRecord, TKey> : IScheduledTask
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
    /// Gets or sets the source-server client for the current run. Created
    /// by the default <see cref="BeforeRunAsync"/> from the configured
    /// source URL/API key; disposed automatically at the end of
    /// <c>ExecuteAsync</c>; null outside a run. Subclasses that override
    /// <see cref="BeforeRunAsync"/> entirely (e.g. Content needs a
    /// circuit-breaker wrapped connection test) can assign this directly —
    /// the base's finally-disposal still kicks in.
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
    /// Module key used to serialize this task against its module's Refresh
    /// task. Both bases acquire <see cref="SyncModuleMutex"/> on the same key
    /// so a Refresh and a Sync within the same module never run
    /// simultaneously.
    /// </summary>
    protected abstract string ModuleMutexKey { get; }

    /// <summary>
    /// Maximum parallelism for <see cref="ApplyAsync"/>. Default <c>1</c>
    /// (serial). Override to enable concurrent applies — Content uses this
    /// to download multiple items at once while still benefiting from the
    /// base's status-transition + persistence boilerplate.
    /// </summary>
    protected virtual int MaxDegreeOfParallelism => 1;

    /// <summary>
    /// Pre-flight hook run after <see cref="IsEnabled"/> and before the
    /// queued items are processed. Default creates <see cref="Client"/>
    /// from the configured source URL/API key and runs a connection test;
    /// returns false (aborting the run) if the connection check fails.
    /// Subclasses with custom pre-flight (disk space, circuit breaker, etc.)
    /// should override and call <c>base.BeforeRunAsync</c> first.
    /// </summary>
    protected virtual async Task<bool> BeforeRunAsync(CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;
        Client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);
        var result = await Client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            Logger.LogError("{Task}: source connection failed — {Error}", Name, result.ErrorMessage ?? "unknown");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Applies the queued change to the local server. Throwing transitions
    /// the record to <see cref="SyncStatus.Errored"/>;
    /// <see cref="OperationCanceledException"/> always propagates.
    /// </summary>
    protected abstract Task ApplyAsync(TRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Called after a successful <see cref="ApplyAsync"/> to confirm the write
    /// landed. Default is a no-op; modules that mutate live Jellyfin state
    /// override to re-read and compare. Throwing transitions the record to
    /// <see cref="SyncStatus.Errored"/> exactly like a failed
    /// <see cref="ApplyAsync"/>. Records with per-category state may call
    /// <c>record.Foo.MarkSynced()</c> on individual categories before throwing
    /// so partial success persists alongside the Errored row.
    /// </summary>
    protected virtual Task VerifyAfterApplyAsync(TRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

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
    /// Retry-extension point — Content overrides to increment
    /// <c>RetryCount</c>, which feeds its <c>GetErroredItemsForRetry</c>
    /// query so failed downloads get another attempt on the next run.
    /// History / Metadata / User / People inherit the no-op because their
    /// apply failures are deterministic (mismatched IDs, refused writes,
    /// verification mismatches) and a retry without operator intervention
    /// would just rerun the same failure.
    /// </summary>
    protected virtual void OnApplyFailed(TRecord record)
    {
        // Default: nothing to do.
    }

    /// <summary>
    /// Hook for post-run bookkeeping beyond timestamp updates — Content uses
    /// this to process pending deletions and trigger a library refresh on
    /// every Sync run. Default is a no-op. For the per-module "last sync
    /// time" bump, override <see cref="RecordRunCompleted"/> instead.
    /// <paramref name="progress"/> reports 0–100 for the finalize phase only;
    /// the base scales it into the run's overall 90–100% band.
    /// </summary>
    protected virtual Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;

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
    /// (Series/Season/…) are fully applied before their leaves — a sorted
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

        // Client is created inside BeforeRunAsync; the finally covers every
        // exit path — pre-flight abort (Client may already exist when the
        // connection test fails), exceptions from the apply loop, and
        // cancellation — so the API client wrapper never leaks past the run.
        try
        {
            await RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Client?.Dispose();
            Client = null;
        }
    }

    private async Task RunAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await BeforeRunAsync(cancellationToken).ConfigureAwait(false))
        {
            // Bumped to LogError + recorded to config so the dashboard can
            // surface "last sync aborted: pre-flight failure" instead of
            // the user wondering why queued rows aren't draining. Subclass
            // BeforeRunAsync overrides log specific reasons (disk space,
            // circuit breaker, connection); this captures the high-level
            // fact of the abort.
            Logger.LogError("{Task}: pre-flight aborted run — see prior log entries for the specific check that failed", Name);
            RecordRunFailure("Sync", "Pre-flight aborted (see log for cause: connection / disk space / circuit breaker)");
            return;
        }

        // Progress allocation:
        //   0– 90 %  per-item ApplyAsync loop
        //  90–100 %  FinalizeAsync (Content's library-refresh phase fits here
        //            so the bar moves while ValidateMediaLibrary runs)
        const double ApplyEnd = 90.0;
        var queued = GetItemsToApply();
        var total = Math.Max(queued.Count, 1);
        var successes = 0;
        var failures = 0;
        var processed = 0;

        var maxParallel = Math.Max(1, MaxDegreeOfParallelism);
        foreach (var group in GetApplyGroups(queued))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

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
                    if (await ApplyOneAsync(record, cancellationToken).ConfigureAwait(false))
                    {
                        successes++;
                    }
                    else
                    {
                        failures++;
                    }

                    processed++;
                    progress.Report(ApplyEnd * processed / total);
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
                        if (await ApplyOneAsync(record, ct).ConfigureAwait(false))
                        {
                            Interlocked.Increment(ref successes);
                        }
                        else
                        {
                            Interlocked.Increment(ref failures);
                        }

                        var done = Interlocked.Increment(ref processed);
                        progress.Report(ApplyEnd * done / total);
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
            RecordRunFailure("Sync", $"{failures} of {queued.Count} item(s) errored — open the table to see per-row reasons");
        }
        else
        {
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
    /// </summary>
    private void RecordRunFailure(string phase, string reason)
        => RunFailureLog.Record(_configManager, ModuleMutexKey, phase, reason, Logger, Name);

    private void ClearRunFailure()
        => RunFailureLog.Clear(_configManager, ModuleMutexKey, Logger, Name);

    private async Task<bool> ApplyOneAsync(TRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyAsync(record, cancellationToken).ConfigureAwait(false);
            await VerifyAfterApplyAsync(record, cancellationToken).ConfigureAwait(false);

            OnApplySucceeded(record);
            record.Status = SyncStatus.Synced;
            record.StatusDate = DateTime.UtcNow;
            record.LastSyncTime = DateTime.UtcNow;
            record.Reason = null;
            Manager.Upsert(record);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
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

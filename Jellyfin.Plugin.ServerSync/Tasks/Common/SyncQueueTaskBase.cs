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
    /// Called after a successful apply, before the record is marked Synced.
    /// Default implementation calls <see cref="SyncRecord.MarkSynced"/>, which
    /// copies <c>SourceHash → SyncedHash</c> on each constituent
    /// <see cref="SyncableValue{T}"/>. Override only if some fields are
    /// applied conditionally and shouldn't all be marked at once.
    /// </summary>
    protected virtual void OnApplySucceeded(TRecord record) => record.MarkSynced();

    /// <summary>
    /// Called after an apply throws, before the record is persisted as Errored.
    /// Subclasses override to update retry-related fields on the in-memory
    /// record (Content increments <c>RetryCount</c>). Default is a no-op.
    /// </summary>
    protected virtual void OnApplyFailed(TRecord record)
    {
        // Default: nothing to do.
    }

    /// <summary>
    /// Hook for post-run bookkeeping. Runs even when no items were queued —
    /// Content uses this to process pending deletions and trigger a library
    /// refresh on every Sync run, regardless of how many downloads happened.
    /// Default is a no-op.
    /// <para>
    /// <paramref name="progress"/> reports 0–100 for the finalize phase only;
    /// the base scales it into the run's overall 90–100% band so a long-
    /// running library refresh doesn't sit at 100% while still working.
    /// </para>
    /// </summary>
    protected virtual Task FinalizeAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public abstract IEnumerable<TaskTriggerInfo> GetDefaultTriggers();

    /// <summary>
    /// Returns the items to apply this run. Default is rows in
    /// <see cref="SyncStatus.Queued"/>. Content overrides to also include
    /// errored-with-retries-left rows.
    /// </summary>
    protected virtual IList<TRecord> GetItemsToApply() => Manager.GetByStatus(SyncStatus.Queued);

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

        if (!await BeforeRunAsync(cancellationToken).ConfigureAwait(false))
        {
            Logger.LogInformation("{Task}: pre-flight aborted run", Name);
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
        if (maxParallel == 1 || queued.Count <= 1)
        {
            for (int i = 0; i < queued.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Logger.LogInformation("{Task}: cancellation requested, stopping queue processing", Name);
                    break;
                }

                var record = queued[i];
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
                await Parallel.ForEachAsync(queued, options, async (record, ct) =>
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

        progress.Report(ApplyEnd);

        var finalizeProgress = new Progress<double>(p =>
            progress.Report(ApplyEnd + ((100.0 - ApplyEnd) * Math.Clamp(p, 0, 100) / 100.0)));
        try
        {
            await FinalizeAsync(finalizeProgress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Client?.Dispose();
            Client = null;
        }

        progress.Report(100);

        Logger.LogInformation("{Task} complete: {Success} synced, {Failure} errored out of {Total}", Name, successes, failures, queued.Count);
    }

    private async Task<bool> ApplyOneAsync(TRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyAsync(record, cancellationToken).ConfigureAwait(false);

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

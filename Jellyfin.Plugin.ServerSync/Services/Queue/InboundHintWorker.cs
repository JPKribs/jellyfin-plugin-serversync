using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies inbound hints. Runs for the life of the server, in two lanes: content, whose applies
/// download files and can take a long time, and everything else, so a slow download never delays a
/// history or metadata change. Each due row is handed to the handler for its kind, then removed and
/// reported complete to its origin. A failure keeps the row and tries again with backoff, keeping the
/// error for the dashboard, until about a day of attempts has passed. A row for an origin that is not a
/// scan source here cannot be applied, since there is nothing to pull from, and waits with that reason.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class InboundHintWorker : IHostedService, IDisposable
{
    private const int BatchSize = 50;
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(30);

    private readonly InboundHintStore _inbound;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly HistoryHintHandler _history;
    private readonly ItemHintHandler _items;
    private readonly ContentHintHandler _content;
    private readonly HintActivityLog _activity;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<InboundHintWorker> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _wakeContent = new(0, 1);
    private readonly SemaphoreSlim _quickPass = new(1, 1);
    private readonly SemaphoreSlim _contentPass = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, DateTime Version)> _completed = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private Task? _contentLoop;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboundHintWorker"/> class.
    /// </summary>
    /// <param name="inbound">The inbound store.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="clientFactory">Client factory for origins.</param>
    /// <param name="history">The history handler.</param>
    /// <param name="items">The metadata and people handler.</param>
    /// <param name="content">The content handler.</param>
    /// <param name="activity">Writes each conclusion to Jellyfin's activity log.</param>
    /// <param name="applicationHost">The server host, for this server's id.</param>
    /// <param name="logger">Logger.</param>
    public InboundHintWorker(
        InboundHintStore inbound,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        HistoryHintHandler history,
        ItemHintHandler items,
        ContentHintHandler content,
        HintActivityLog activity,
        IServerApplicationHost applicationHost,
        ILogger<InboundHintWorker> logger)
    {
        _activity = activity;
        _inbound = inbound;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _history = history;
        _items = items;
        _content = content;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>
    /// Gets the hints finished in the last day, each with the version it applied, for the status a peer
    /// reads. Kept in memory only: after a restart an origin that never heard back sends the hint again
    /// and it applies as unchanged.
    /// </summary>
    public IReadOnlyList<CompletedHint> RecentlyCompleted
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var pair in _completed)
            {
                if (pair.Value.At < cutoff)
                {
                    _completed.TryRemove(pair.Key, out _);
                }
            }

            return _completed.Select(pair => new CompletedHint { HintId = pair.Key, VersionTimestamp = pair.Value.Version }).ToList();
        }
    }

    // Finished ids are kept for a day and never more than the cap. Past it the oldest go first, and an
    // origin that then asks sends the hint again, which applies as unchanged.
    private void Remember(string hintId, DateTime version)
    {
        _completed[hintId] = (DateTime.UtcNow, version);

        // Trimmed back to the cap only once it runs a tenth over, so the sort runs once per thousand
        // completions rather than on every one.
        if (_completed.Count <= HintProtocol.MaxRememberedCompletions + (HintProtocol.MaxRememberedCompletions / 10))
        {
            return;
        }

        foreach (var pair in _completed.OrderBy(p => p.Value.At).Take(_completed.Count - HintProtocol.MaxRememberedCompletions))
        {
            _completed.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>Asks the worker to apply now rather than at its next idle tick.</summary>
    public void Wake()
    {
        foreach (var gate in new[] { _wake, _wakeContent })
        {
            try
            {
                gate.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already awake.
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_wake, content: false, _stopping.Token), CancellationToken.None);
        _contentLoop = Task.Run(() => RunAsync(_wakeContent, content: true, _stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        foreach (var loop in new[] { _loop, _contentLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on stop.
            }
        }
    }

    /// <summary>One apply pass over every due row in both lanes. Public so the dashboard and tests can run it on demand.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were finished.</returns>
    public async Task<int> ApplyAsync(CancellationToken cancellationToken)
        => (await ApplyLaneAsync(content: false, cancellationToken).ConfigureAwait(false)).Finished
           + (await ApplyLaneAsync(content: true, cancellationToken).ConfigureAwait(false)).Finished;

    /// <summary>
    /// What the dashboard's Run does: applies what is due in the lane of quick changes now, and wakes the
    /// lane that downloads files, so a download never runs inside the request that asked for it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows of the quick lane finished.</returns>
    public async Task<int> ApplyNowAsync(CancellationToken cancellationToken)
    {
        var finished = (await ApplyLaneAsync(content: false, cancellationToken).ConfigureAwait(false)).Finished;
        Wake();
        return finished;
    }

    // Returns how many rows finished and whether the lane read a full batch, so its loop goes again at
    // once rather than after the idle wait.
    private async Task<(int Finished, bool Full)> ApplyLaneAsync(bool content, CancellationToken cancellationToken)
    {
        // One pass per lane at a time, whether the lane's loop or the dashboard starts it.
        var laneLock = content ? _contentPass : _quickPass;
        await laneLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyLanePassAsync(content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            laneLock.Release();
        }
    }

    private async Task<(int Finished, bool Full)> ApplyLanePassAsync(bool content, CancellationToken cancellationToken)
    {
        var finished = 0;
        var full = false;
        var config = _configManager.Configuration;

        // One client per origin for the pass, and the completions for each origin gathered into one
        // report at the end, rather than a client and a request per row.
        var clients = new Dictionary<string, SourceServerClient>(StringComparer.OrdinalIgnoreCase);
        var completions = new Dictionary<string, (SourceServer Origin, List<CompletedHint> Done)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var due = _inbound.GetDue(DateTime.UtcNow, BatchSize, content);
            full = due.Count >= BatchSize;
            var prefetched = content ? new Dictionary<string, Jellyfin.Sdk.Generated.Models.BaseItemDto>(StringComparer.Ordinal) : await PrefetchItemsAsync(due, config, clients, cancellationToken).ConfigureAwait(false);
            foreach (var row in due)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var origin = config.Servers.FirstOrDefault(s => s.Pulls && string.Equals(s.ServerId, row.OriginServerId, StringComparison.OrdinalIgnoreCase));
                if (origin is null)
                {
                    _inbound.Defer(row, DateTime.UtcNow + HintProtocol.NextDelay(row.Attempts + 1), $"server {row.OriginServerId} is not configured as a Pull or Sync server here, so its change cannot be pulled");
                    continue;
                }

                if (!TryGetClient(origin, clients, out var client, out var clientError))
                {
                    _inbound.Defer(row, DateTime.UtcNow + HintProtocol.NextDelay(row.Attempts + 1), $"server '{origin.DisplayName}' has an invalid URL: {clientError}");
                    continue;
                }

                prefetched.TryGetValue(PrefetchKey(row.OriginServerId, row.Key), out var item);
                var result = await TryApplyAsync(row, origin, client, item, cancellationToken).ConfigureAwait(false);
                if (result is null || !await ConcludeAsync(row, origin, result.Value).ConfigureAwait(false))
                {
                    continue;
                }

                // A row refreshed with a newer version during the apply stays and is applied again.
                if (_inbound.Remove(row.Id, row.VersionTimestamp))
                {
                    finished++;
                    Remember(row.HintId, row.VersionTimestamp);
                    if (!completions.TryGetValue(origin.Key, out var batch))
                    {
                        batch = (origin, new List<CompletedHint>());
                        completions[origin.Key] = batch;
                    }

                    batch.Done.Add(new CompletedHint { HintId = row.HintId, VersionTimestamp = row.VersionTimestamp });
                }
            }

            foreach (var (origin, done) in completions.Values)
            {
                await ReportCompleteAsync(done, origin, clients[origin.Key], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var client in clients.Values)
            {
                client.Dispose();
            }
        }

        return (finished, full);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _wake.Dispose();
        _wakeContent.Dispose();
        _quickPass.Dispose();
        _contentPass.Dispose();
        _stopping?.Dispose();
    }

    private async Task RunAsync(SemaphoreSlim gate, bool content, CancellationToken cancellationToken)
    {
        var more = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!more)
                {
                    await gate.WaitAsync(IdleWait, cancellationToken).ConfigureAwait(false);
                }

                more = (await ApplyLaneAsync(content, cancellationToken).ConfigureAwait(false)).Full;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inbound hint worker pass failed");
            }
        }
    }

    // One client per origin for a pass. An address the factory refuses yields the reason instead.
    private bool TryGetClient(SourceServer origin, Dictionary<string, SourceServerClient> clients, out SourceServerClient client, out string? error)
    {
        error = null;
        if (clients.TryGetValue(origin.Key, out client!))
        {
            return true;
        }

        try
        {
            client = _clientFactory.Create(origin);
            clients[origin.Key] = client;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // Applies one row. Null means the module's scheduled run holds it: nothing failed, so the row is put
    // off for a minute without counting an attempt.
    private async Task<HintApplyResult?> TryApplyAsync(InboundHint row, SourceServer origin, SourceServerClient client, Jellyfin.Sdk.Generated.Models.BaseItemDto? prefetched, CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyOneAsync(row, origin, client, prefetched, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Tasks.Common.ModuleBusyException ex)
        {
            _inbound.Postpone(row, DateTime.UtcNow + TimeSpan.FromMinutes(1), ex.Message);
            _logger.LogDebug("Hint {Hint} from '{Origin}' waits: {Reason}", row.HintId, origin.DisplayName, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            return HintApplyResult.RetryLater(ex.Message);
        }
    }

    // Records how a row ended and says whether it is finished. A retry keeps the row with its backoff
    // until about a day of attempts has passed. A hint dropped on its first try is routine, a mapping that
    // does not cover it or a file that has not arrived yet, and goes to the server log only, while one
    // dropped after failing is worth a line on the Activity page. An applied hand made edit is worth a
    // line too. Provider work is not, since a scan on the peer would otherwise write one entry here per
    // item it refreshed.
    private async Task<bool> ConcludeAsync(InboundHint row, SourceServer origin, HintApplyResult result)
    {
        if (result.Outcome == HintApplyOutcome.Retry && row.Attempts + 1 >= HintProtocol.MaxInboundAttempts)
        {
            result = HintApplyResult.Dropped($"{row.Attempts + 1} attempts failed, the last with {result.Reason ?? "no reason given"}");
        }

        switch (result.Outcome)
        {
            case HintApplyOutcome.Retry:
                _inbound.Defer(row, DateTime.UtcNow + HintProtocol.NextDelay(row.Attempts + 1), result.Reason ?? "failed");
                _logger.LogWarning("Hint {Hint} from '{Origin}' will be retried: {Reason}", row.HintId, origin.DisplayName, result.Reason);
                await _activity.RetryingAsync(row, origin.DisplayName, result.Reason).ConfigureAwait(false);
                return false;

            case HintApplyOutcome.Dropped:
                _logger.LogInformation("Hint {Hint} from '{Origin}' dropped: {Reason}", row.HintId, origin.DisplayName, result.Reason);
                if (row.Attempts > 0)
                {
                    await _activity.DroppedAsync(row, origin.DisplayName, result.Reason).ConfigureAwait(false);
                }

                return true;

            case HintApplyOutcome.Applied when row.Recorded:
                await _activity.AppliedAsync(row, origin.DisplayName, result).ConfigureAwait(false);
                return true;

            default:
                return true;
        }
    }

    private static string PrefetchKey(string originServerId, string key) => originServerId.ToUpperInvariant() + ":" + key.ToUpperInvariant();

    // The scheduled scan reads items in pages. A pass of hints reads them the same way, one request per
    // page per origin, instead of one request per hint. A bulk provider refresh on a peer then costs the
    // receiver no more fetches than its own scan would. A page that cannot be read falls back to the
    // per hint fetch, which says why.
    private async Task<Dictionary<string, Jellyfin.Sdk.Generated.Models.BaseItemDto>> PrefetchItemsAsync(
        IList<InboundHint> due,
        Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config,
        Dictionary<string, SourceServerClient> clients,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, Jellyfin.Sdk.Generated.Models.BaseItemDto>(StringComparer.Ordinal);
        var byOrigin = due
            .Where(r => r.Kind == HintKind.Metadata && Guid.TryParse(r.Key, out _))
            .GroupBy(r => r.OriginServerId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in byOrigin)
        {
            var origin = config.Servers.FirstOrDefault(s => s.Pulls && string.Equals(s.ServerId, group.Key, StringComparison.OrdinalIgnoreCase));
            if (origin is null)
            {
                continue;
            }

            if (!TryGetClient(origin, clients, out var client, out _))
            {
                continue;
            }

            var ids = group.Select(r => Guid.Parse(r.Key)).Distinct().ToList();
            try
            {
                var fields = Tasks.RefreshMetadataSyncTableTask.BuildRequestedFields(config);
                foreach (var dto in await client.GetItemsByIdsAsync(ids, fields, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (dto.Id.HasValue)
                    {
                        items[PrefetchKey(origin.ServerId, dto.Id.Value.ToString("N"))] = dto;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read {Count} item(s) from '{Origin}' in one page, each hint will read its own", ids.Count, origin.DisplayName);
            }
        }

        return items;
    }

    private async Task<HintApplyResult> ApplyOneAsync(InboundHint row, SourceServer origin, SourceServerClient client, Jellyfin.Sdk.Generated.Models.BaseItemDto? prefetched, CancellationToken cancellationToken)
    {
        return row.Kind switch
        {
            HintKind.History => await _history.ApplyAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Metadata => await _items.ApplyMetadataAsync(row, origin, client, cancellationToken, prefetched).ConfigureAwait(false),
            HintKind.People => await _items.ApplyPeopleAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Content => await _content.ApplyAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Users => HintApplyResult.Dropped("user settings are not announced live, the scheduled Sync Information task carries them"),
            _ => HintApplyResult.Dropped($"this version does not apply {row.Kind} hints")
        };
    }

    private async Task ReportCompleteAsync(List<CompletedHint> done, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        try
        {
            var request = new CompleteRequest { SenderServerId = _applicationHost.SystemId, Items = done };
            if (!await client.CompleteHintsAsync(request, cancellationToken).ConfigureAwait(false))
            {
                // A standard user's key cannot reach the origin's endpoint. The origin reads the
                // completions from this server's status with its own key instead.
                _logger.LogInformation("Could not tell '{Origin}' that {Count} hint(s) are complete. It will read them from this server's status", origin.DisplayName, done.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell '{Origin}' that {Count} hint(s) are complete. It will check back later", origin.DisplayName, done.Count);
        }
    }
}

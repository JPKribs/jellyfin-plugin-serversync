using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Tasks.MetadataSync;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies received hints in two lanes, so a slow content download never delays a history or metadata change.
/// Each due row goes to the handler for its kind, then is removed and reported complete to its origin, or retried with backoff.
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
    private readonly PlaylistHintHandler _playlists;
    private readonly ContentHintHandler _content;
    private readonly HintActivityLog _activity;
    private readonly HintTriage _triage;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<InboundHintWorker> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _wakeContent = new(0, 1);
    private readonly SemaphoreSlim _quickPass = new(1, 1);
    private readonly SemaphoreSlim _contentPass = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTime At, DateTime Version, HintCode Code, string? Reason)> _completed = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private Task? _contentLoop;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboundHintWorker"/> class.
    /// </summary>
    /// <param name="inbound">Holds the received hints that are due.</param>
    /// <param name="configManager">Finds the server entry for each origin.</param>
    /// <param name="clientFactory">Builds one client for each origin in a pass.</param>
    /// <param name="history">Applies history hints.</param>
    /// <param name="items">Applies metadata and people hints.</param>
    /// <param name="content">Applies content hints.</param>
    /// <param name="playlists">Applies playlist hints.</param>
    /// <param name="activity">Writes how hints ended to the Activity page.</param>
    /// <param name="triage">Finds the local object a parked hint waits for.</param>
    /// <param name="applicationHost">Gives this server's id.</param>
    /// <param name="logger">Writes how each hint ended.</param>
    public InboundHintWorker(
        InboundHintStore inbound,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        HistoryHintHandler history,
        ItemHintHandler items,
        ContentHintHandler content,
        PlaylistHintHandler playlists,
        HintActivityLog activity,
        HintTriage triage,
        IServerApplicationHost applicationHost,
        ILogger<InboundHintWorker> logger)
    {
        _triage = triage;
        _activity = activity;
        _inbound = inbound;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _history = history;
        _items = items;
        _content = content;
        _playlists = playlists;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>
    /// Gets the hints finished in the last day, each with the stamp it applied, for the status a peer reads.
    /// They are kept in memory only, so after a restart an origin sends the hint again and it applies as unchanged.
    /// </summary>
    public IReadOnlyList<CompletedHint> RecentlyCompleted
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var pair in _completed.Where(entry => entry.Value.At < cutoff))
            {
                _completed.TryRemove(pair.Key, out _);
            }

            return _completed.Select(pair => new CompletedHint { HintId = pair.Key, VersionTimestamp = pair.Value.Version, Code = pair.Value.Code, Reason = pair.Value.Reason }).ToList();
        }
    }

    /// <summary>Remembers a finished hint, dropping the oldest once the list runs past its cap.</summary>
    private void Remember(CompletedHint done)
    {
        _completed[done.HintId] = (DateTime.UtcNow, done.VersionTimestamp, done.Code, done.Reason);

        // Trimming only once a tenth over the cap keeps the sort off most completions.
        if (_completed.Count <= HintProtocol.MaxRememberedCompletions + (HintProtocol.MaxRememberedCompletions / 10))
        {
            return;
        }

        foreach (var pair in _completed.OrderBy(p => p.Value.At).Take(_completed.Count - HintProtocol.MaxRememberedCompletions))
        {
            _completed.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>
    /// Discards one row for the operator, remembering it as finished so its origin does not send it again.
    /// </summary>
    /// <param name="id">The id of the row to discard.</param>
    /// <returns><c>true</c> when the row was there and is gone.</returns>
    public bool Dismiss(long id) => _inbound.Get(id) is { } row && Dismiss(row);

    /// <summary>Discards every row whose apply has failed at least once, as <see cref="Dismiss(long)"/> does one.</summary>
    /// <returns>How many rows were discarded.</returns>
    public int DismissFailing() => _inbound.GetFailing().Count(Dismiss);

    /// <summary>Discards a row while it still carries the stamp that was read, so a newer hint stays and is applied.</summary>
    private bool Dismiss(InboundHint row)
    {
        if (!_inbound.Remove(row.Id, row.VersionTimestamp))
        {
            return false;
        }

        Remember(new CompletedHint { HintId = row.HintId, VersionTimestamp = row.VersionTimestamp, Code = HintCode.GaveUp, Reason = "dismissed on the receiving server" });
        return true;
    }

    /// <summary>Asks the worker to apply at once and not at its next idle tick.</summary>
    public void Wake()
    {
        foreach (var gate in new[] { _wake, _wakeContent })
        {
            try
            {
                gate.Release();
            }
            // A full gate means the lane is already awake.
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <summary>Makes the rows parked under a key due and wakes the worker to apply them.</summary>
    /// <param name="parkKey">The key the rows wait under.</param>
    /// <returns>How many rows were made due.</returns>
    public int WakeParked(string parkKey)
    {
        ArgumentNullException.ThrowIfNull(parkKey);
        var woken = _inbound.WakeParked(parkKey, DateTime.UtcNow);
        if (woken > 0)
        {
            _logger.LogDebug("{Count} parked hint(s) for {Key} are due now", woken, FormatUtilities.SanitizeForLog(parkKey));
            Wake();
        }

        return woken;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        try
        {
            var shortened = _inbound.ShortenParked(DateTime.UtcNow, HintProtocol.ParkWindow);
            if (shortened > 0)
            {
                _logger.LogDebug("{Count} parked hint(s) moved into the {Hours}-hour wait", shortened, HintProtocol.ParkWindow.TotalHours);
            }
        }
        // The worker must start even when the parked hints cannot be shortened.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not shorten the wait of parked hints");
        }

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
            // The loops end by cancellation when the service stops.
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Runs one apply pass over every due row in both lanes.</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many rows were finished.</returns>
    public async Task<int> ApplyAsync(CancellationToken cancellationToken)
        => (await ApplyLaneAsync(content: false, cancellationToken).ConfigureAwait(false)).Finished
           + (await ApplyLaneAsync(content: true, cancellationToken).ConfigureAwait(false)).Finished;

    /// <summary>
    /// Applies what is due in the lane of quick changes and wakes the lane that downloads files.
    /// A download therefore never runs inside the request that asked for it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>How many rows of the quick lane finished.</returns>
    public async Task<int> ApplyNowAsync(CancellationToken cancellationToken)
    {
        var finished = (await ApplyLaneAsync(content: false, cancellationToken).ConfigureAwait(false)).Finished;
        Wake();
        return finished;
    }

    /// <summary>Runs one pass of a lane, one at a time, and says how many rows finished and whether it read a full batch.</summary>
    private async Task<(int Finished, bool Full)> ApplyLaneAsync(bool content, CancellationToken cancellationToken)
    {
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

    /// <summary>Applies every due row of a lane with one client for each origin, then reports the completions to each origin in one request.</summary>
    private async Task<(int Finished, bool Full)> ApplyLanePassAsync(bool content, CancellationToken cancellationToken)
    {
        var finished = 0;
        var full = false;
        var config = _configManager.Configuration;

        var clients = new Dictionary<string, SourceServerClient>(StringComparer.OrdinalIgnoreCase);
        var completions = new Dictionary<string, (SourceServer Origin, List<CompletedHint> Done)>(StringComparer.OrdinalIgnoreCase);
        var neverArrived = new Dictionary<string, (SourceServer Origin, int Count)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var due = _inbound.GetDue(DateTime.UtcNow, BatchSize, content);
            full = due.Count >= BatchSize;
            var prefetched = content ? new Dictionary<string, BaseItemDto>(StringComparer.Ordinal) : await PrefetchItemsAsync(due, config, clients, cancellationToken).ConfigureAwait(false);
            foreach (var row in due)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var origin = config.Servers.FirstOrDefault(s => s.Pulls && string.Equals(s.ServerId, row.OriginServerId, StringComparison.OrdinalIgnoreCase));
                if (origin is null)
                {
                    // The server may be added back, so the hint is retried for a day.
                    if (row.Attempts + 1 >= HintProtocol.MaxInboundAttempts)
                    {
                        _inbound.Remove(row.Id, row.VersionTimestamp);
                        _logger.LogInformation("Dropped a change from server {Origin}, which is not set up as a Pull or Sync server here", FormatUtilities.SanitizeForLog(row.OriginServerId));
                        continue;
                    }

                    _inbound.Defer(row, DateTime.UtcNow + HintProtocol.NextDelay(row.Attempts + 1), $"server {row.OriginServerId} is not configured as a Pull or Sync server here, so its change cannot be pulled");
                    continue;
                }

                if (!TryGetClient(origin, clients, out var client, out var clientError))
                {
                    if (row.Attempts + 1 >= HintProtocol.MaxInboundAttempts)
                    {
                        _inbound.Remove(row.Id, row.VersionTimestamp);
                        _logger.LogInformation("Dropped a change from '{Origin}', whose address here is not valid: {Error}", FormatUtilities.SanitizeForLog(origin.DisplayName), FormatUtilities.SanitizeForLog(clientError, 300));
                        continue;
                    }

                    _inbound.Defer(row, DateTime.UtcNow + HintProtocol.NextDelay(row.Attempts + 1), $"server '{origin.DisplayName}' has an invalid URL: {clientError}");
                    continue;
                }

                prefetched.TryGetValue(PrefetchKey(row.OriginServerId, row.Key), out var item);
                var result = await TryApplyAsync(row, origin, client, item, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    continue;
                }

                var concluded = await ConcludeAsync(row, origin, result.Value, neverArrived).ConfigureAwait(false);
                if (concluded is null)
                {
                    continue;
                }

                // A row refreshed with a newer stamp during the apply stays to apply again.
                if (_inbound.Remove(row.Id, row.VersionTimestamp))
                {
                    finished++;
                    var done = new CompletedHint { HintId = row.HintId, VersionTimestamp = row.VersionTimestamp, Code = concluded.Value.Code, Reason = concluded.Value.Reason };
                    Remember(done);
                    if (!completions.TryGetValue(origin.Key, out var batch))
                    {
                        batch = (origin, new List<CompletedHint>());
                        completions[origin.Key] = batch;
                    }

                    batch.Done.Add(done);
                }
            }

            foreach (var (origin, count) in neverArrived.Values)
            {
                _logger.LogInformation("Item never arrived; dropped {Count} hint(s) from '{Origin}'", count, FormatUtilities.SanitizeForLog(origin.DisplayName));
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

    /// <summary>Runs a lane until stopped, going again at once after a full batch and waiting otherwise.</summary>
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
            // The loop must outlive a failed pass, which then waits like an idle one.
            catch (Exception ex)
            {
                more = false;
                _logger.LogError(ex, "Inbound hint worker pass failed");
            }
        }
    }

    /// <summary>Gets the pass's client for an origin, or the reason when the factory refuses its address.</summary>
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

    /// <summary>
    /// Applies one row, turning a failure into a retry so one hint never stops the pass.
    /// Null means the scheduled task holds the sync type, so the row is put off without counting an attempt.
    /// </summary>
    private async Task<HintApplyResult?> TryApplyAsync(InboundHint row, SourceServer origin, SourceServerClient client, BaseItemDto? prefetched, CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyOneAsync(row, origin, client, prefetched, cancellationToken).ConfigureAwait(false);
        }
        catch (Tasks.Common.ModuleBusyException ex)
        {
            _inbound.Postpone(row, DateTime.UtcNow + TimeSpan.FromMinutes(1), ex.Message);
            _logger.LogDebug("Hint {Hint} from '{Origin}' waits: {Reason}", FormatUtilities.SanitizeForLog(row.HintId), FormatUtilities.SanitizeForLog(origin.DisplayName), FormatUtilities.SanitizeForLog(ex.Message, 300));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HintApplyResult.RetryLater(ex.Message);
        }
    }

    /// <summary>
    /// Records how a row ended and returns the result when it is finished, or null when it stays to be retried or parked.
    /// Only a hint dropped after failing and an applied hand made edit reach the Activity page, since the rest is routine.
    /// </summary>
    private async Task<HintApplyResult?> ConcludeAsync(InboundHint row, SourceServer origin, HintApplyResult result, Dictionary<string, (SourceServer Origin, int Count)> neverArrived)
    {
        if (result.Outcome == HintApplyOutcome.Retry && row.Attempts + 1 >= HintProtocol.MaxInboundAttempts)
        {
            result = HintApplyResult.Dropped(HintCode.GaveUp, $"{row.Attempts + 1} attempts failed, the last with {result.Reason ?? "no reason given"}");
        }

        if (result.Outcome == HintApplyOutcome.Parked)
        {
            var until = row.ReceivedAt + HintProtocol.ParkWindow;
            if (DateTime.UtcNow < until && result.ParkKey is not null)
            {
                _inbound.Park(row, result.ParkKey, until, result.Reason ?? "waiting for the local item");
                if (_triage.Present(result.ParkKey, row.Kind == HintKind.People ? row.Key : null))
                {
                    WakeParked(result.ParkKey);
                }
                else
                {
                    _logger.LogDebug("Hint {Hint} from '{Origin}' waits for {Key}", FormatUtilities.SanitizeForLog(row.HintId), FormatUtilities.SanitizeForLog(origin.DisplayName), FormatUtilities.SanitizeForLog(result.ParkKey));
                }

                return null;
            }

            _logger.LogDebug("Hint {Hint} from '{Origin}' dropped: its item never arrived", FormatUtilities.SanitizeForLog(row.HintId), FormatUtilities.SanitizeForLog(origin.DisplayName));
            neverArrived[origin.Key] = (origin, neverArrived.GetValueOrDefault(origin.Key).Count + 1);
            return HintApplyResult.Dropped(HintCode.NoLocalItem, "the item never arrived");
        }

        switch (result.Outcome)
        {
            case HintApplyOutcome.Retry:
                _inbound.Defer(row, DateTime.UtcNow + HintProtocol.NextDelay(row.Attempts + 1), result.Reason ?? "failed");
                _logger.LogWarning("Hint {Hint} from '{Origin}' will be retried: {Reason}", FormatUtilities.SanitizeForLog(row.HintId), FormatUtilities.SanitizeForLog(origin.DisplayName), FormatUtilities.SanitizeForLog(result.Reason, 300));
                await _activity.RetryingAsync(row, origin.DisplayName, result.Reason).ConfigureAwait(false);
                return null;

            case HintApplyOutcome.Dropped:
                _logger.LogInformation("Hint {Hint} from '{Origin}' dropped as {Code}: {Reason}", FormatUtilities.SanitizeForLog(row.HintId), FormatUtilities.SanitizeForLog(origin.DisplayName), result.Code, FormatUtilities.SanitizeForLog(result.Reason, 300));
                if (row.Attempts > 0)
                {
                    await _activity.DroppedAsync(row, origin.DisplayName, result.Code).ConfigureAwait(false);
                }

                return result;

            case HintApplyOutcome.Applied when row.Recorded:
                await _activity.AppliedAsync(row, origin.DisplayName, result).ConfigureAwait(false);
                return result;

            default:
                return result;
        }
    }

    /// <summary>Builds the key a prefetched item is found under.</summary>
    private static string PrefetchKey(string originServerId, string key) => originServerId.ToUpperInvariant() + ":" + key.ToUpperInvariant();

    /// <summary>
    /// Reads the items a pass's metadata hints name in one page for each origin, as the scan does.
    /// A page that cannot be read leaves each hint to read its own item, which says why.
    /// </summary>
    private async Task<Dictionary<string, BaseItemDto>> PrefetchItemsAsync(
        IList<InboundHint> due,
        PluginConfiguration config,
        Dictionary<string, SourceServerClient> clients,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, BaseItemDto>(StringComparer.Ordinal);
        var byOrigin = due
            .Where(r => r.Kind == HintKind.Metadata && Guid.TryParse(r.Key, out _))
            .GroupBy(r => r.OriginServerId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var originGroup in byOrigin)
        {
            var origin = config.Servers.FirstOrDefault(s => s.Pulls && string.Equals(s.ServerId, originGroup.Key, StringComparison.OrdinalIgnoreCase));
            if (origin is null)
            {
                continue;
            }

            var group = originGroup.Where(r => _triage.MissingLocal(origin, r.Kind, r.ItemPath, r.Key) is null).ToList();
            if (group.Count < 2)
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
                var fields = RefreshMetadataSyncTableTask.BuildRequestedFields(config);
                var fetched = await client.GetItemsByIdsAsync(ids, fields, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var dto in fetched.Where(d => d.Id.HasValue))
                {
                    items[PrefetchKey(origin.ServerId, dto.Id!.Value.ToString("N"))] = dto;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not read {Count} item(s) from '{Origin}' in one page, each hint will read its own", ids.Count, FormatUtilities.SanitizeForLog(origin.DisplayName));
            }
        }

        return items;
    }

    /// <summary>Hands a row to the handler for its kind.</summary>
    private async Task<HintApplyResult> ApplyOneAsync(InboundHint row, SourceServer origin, SourceServerClient client, BaseItemDto? prefetched, CancellationToken cancellationToken)
    {
        return row.Kind switch
        {
            HintKind.History => await _history.ApplyAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Metadata => await _items.ApplyMetadataAsync(row, origin, client, cancellationToken, prefetched).ConfigureAwait(false),
            HintKind.People => await _items.ApplyPeopleAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Content => await _content.ApplyAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Playlists => await _playlists.ApplyAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Users => HintApplyResult.Dropped(HintCode.Unsupported, "user settings are not announced live, the scheduled Sync Information task carries them"),
            _ => HintApplyResult.Dropped(HintCode.Unsupported, $"this server does not apply {row.Kind} hints")
        };
    }

    /// <summary>Tells an origin which of its hints finished, without failing the pass when it cannot be told.</summary>
    private async Task ReportCompleteAsync(List<CompletedHint> done, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        try
        {
            var request = new CompleteRequest { SenderServerId = _applicationHost.SystemId, Items = done };
            // A standard user's API Key cannot reach the route, so the origin reads status.
            if (!await client.CompleteHintsAsync(request, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Could not tell '{Origin}' that {Count} hint(s) are complete. It will read them from this server's status", FormatUtilities.SanitizeForLog(origin.DisplayName), done.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not tell '{Origin}' that {Count} hint(s) are complete. It will check back later", FormatUtilities.SanitizeForLog(origin.DisplayName), done.Count);
        }
    }
}

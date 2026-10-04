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
/// reported complete to its origin. A failure keeps the row and tries again
/// with backoff, forever, and the error is kept for the dashboard. A row for an origin that is not a
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
    private readonly UserHintHandler _users;
    private readonly HintActivityLog _activity;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<InboundHintWorker> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _wakeContent = new(0, 1);
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
    /// <param name="users">The users handler.</param>
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
        UserHintHandler users,
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
        _users = users;
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
        => await ApplyLaneAsync(content: false, cancellationToken).ConfigureAwait(false)
           + await ApplyLaneAsync(content: true, cancellationToken).ConfigureAwait(false);

    private async Task<int> ApplyLaneAsync(bool content, CancellationToken cancellationToken)
    {
        var finished = 0;
        var config = _configManager.Configuration;

        // One client per origin for the pass, and the completions for each origin gathered into one
        // report at the end, rather than a client and a request per row.
        var clients = new Dictionary<string, SourceServerClient>(StringComparer.OrdinalIgnoreCase);
        var completions = new Dictionary<string, (SourceServer Origin, List<CompletedHint> Done)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var row in _inbound.GetDue(DateTime.UtcNow, BatchSize, content))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var origin = config.Servers.FirstOrDefault(s => s.Pulls && string.Equals(s.ServerId, row.OriginServerId, StringComparison.OrdinalIgnoreCase));
                if (origin is null)
                {
                    _inbound.Defer(row.Id, DateTime.UtcNow + HintProtocol.NextDelay(row.Attempts + 1), $"server {row.OriginServerId} is not configured as a Pull or Sync server here, so its change cannot be pulled");
                    continue;
                }

                if (!clients.TryGetValue(origin.Key, out var client))
                {
                    try
                    {
                        client = _clientFactory.Create(origin);
                    }
                    catch (ArgumentException ex)
                    {
                        _inbound.Defer(row.Id, DateTime.UtcNow + HintProtocol.NextDelay(row.Attempts + 1), $"server '{origin.DisplayName}' has an invalid URL: {ex.Message}");
                        continue;
                    }

                    clients[origin.Key] = client;
                }

                HintApplyResult result;
                try
                {
                    result = await ApplyOneAsync(row, origin, client, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = HintApplyResult.RetryLater(ex.Message);
                }

                if (result.Outcome == HintApplyOutcome.Retry)
                {
                    _inbound.Defer(row.Id, DateTime.UtcNow + HintProtocol.NextDelay(row.Attempts + 1), result.Reason ?? "failed");
                    _logger.LogWarning("Hint {Hint} from '{Origin}' will be retried: {Reason}", row.HintId, origin.DisplayName, result.Reason);
                    await _activity.RetryingAsync(row, origin.DisplayName, result.Reason).ConfigureAwait(false);
                    continue;
                }

                if (result.Outcome == HintApplyOutcome.Dropped)
                {
                    _logger.LogInformation("Hint {Hint} from '{Origin}' dropped: {Reason}", row.HintId, origin.DisplayName, result.Reason);
                    await _activity.DroppedAsync(row, origin.DisplayName, result.Reason).ConfigureAwait(false);
                }
                else if (result.Outcome == HintApplyOutcome.Applied)
                {
                    await _activity.AppliedAsync(row, origin.DisplayName).ConfigureAwait(false);
                }

                // A row refreshed with a newer version during the apply stays and is applied again.
                if (_inbound.Remove(row.Id, row.VersionTimestamp))
                {
                    finished++;
                    _completed[row.HintId] = (DateTime.UtcNow, row.VersionTimestamp);
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

        return finished;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _wake.Dispose();
        _wakeContent.Dispose();
        _stopping?.Dispose();
    }

    private async Task RunAsync(SemaphoreSlim gate, bool content, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await gate.WaitAsync(IdleWait, cancellationToken).ConfigureAwait(false);
                await ApplyLaneAsync(content, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inbound hint worker pass failed");
            }
        }
    }

    private async Task<HintApplyResult> ApplyOneAsync(InboundHint row, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        return row.Kind switch
        {
            HintKind.History => await _history.ApplyAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Metadata => await _items.ApplyMetadataAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.People => await _items.ApplyPeopleAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Content => await _content.ApplyAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
            HintKind.Users => await _users.ApplyAsync(row, origin, client, cancellationToken).ConfigureAwait(false),
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
                _logger.LogInformation("Could not tell '{Origin}' that {Count} hint(s) are complete; it will read them from this server's status", origin.DisplayName, done.Count);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell '{Origin}' that {Count} hint(s) are complete; it will check back later", origin.DisplayName, done.Count);
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Once a minute, ends the link sessions on this server, the source, whose server stopped reporting on
/// them, so a viewer whose stop report was lost does not stay listed on the dashboard.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkSessionSweeper : BackgroundService
{
    /// <summary>How often stale link sessions are looked for.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly LinkPlaybackSessions _sessions;
    private readonly ILogger<LinkSessionSweeper> _logger;

    public LinkSessionSweeper(LinkPlaybackSessions sessions, ILogger<LinkSessionSweeper> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _sessions.SweepAsync(DateTime.UtcNow).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not sweep stale link sessions");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

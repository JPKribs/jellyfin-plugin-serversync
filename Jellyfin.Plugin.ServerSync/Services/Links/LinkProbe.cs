using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Gives a new link the media info its source holds, since Jellyfin never probes a .strm during a library
/// scan, and queues Jellyfin's own probe through the link when the source cannot give it. The probe waits
/// until the item has settled, since a scan still refreshing it can save over the result, and is tried a few times.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkProbe : IHostedService, IDisposable
{
    /// <summary>How long after the library adds a link before it is probed.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>How long after a probe is queued before its result is checked.</summary>
    public static readonly TimeSpan CheckAfter = TimeSpan.FromMinutes(2);

    /// <summary>How many times a link is probed before it is left until someone plays it.</summary>
    public const int MaxAttempts = 3;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILinkRegistry _links;
    private readonly LinkGrants _grants;
    private readonly ILogger<LinkProbe> _logger;
    private readonly Func<DateTime> _now;
    private readonly LinkMediaInfo? _mediaInfo;
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly SemaphoreSlim _shipping = new(2, 2);
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private Timer? _timer;
    private int _sweeping;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkProbe"/> class.
    /// </summary>
    /// <param name="libraryManager">Raises the item added event and finds the items to probe.</param>
    /// <param name="providerManager">Queues the refresh that probes a link.</param>
    /// <param name="fileSystem">Backs the directory service of each refresh.</param>
    /// <param name="links">Tells which paths are this server's links.</param>
    /// <param name="grants">Grants the probe its read of a link.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="mediaInfo">Takes the source's media info before a probe is tried.</param>
    public LinkProbe(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILinkRegistry links, LinkGrants grants, ILogger<LinkProbe> logger, LinkMediaInfo mediaInfo)
        : this(libraryManager, providerManager, fileSystem, links, grants, logger, () => DateTime.UtcNow, mediaInfo)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkProbe"/> class.
    /// </summary>
    /// <param name="libraryManager">Raises the item added event and finds the items to probe.</param>
    /// <param name="providerManager">Queues the refresh that probes a link.</param>
    /// <param name="fileSystem">Backs the directory service of each refresh.</param>
    /// <param name="links">Tells which paths are this server's links.</param>
    /// <param name="grants">Grants the probe its read of a link.</param>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="now">Reads the current time, so a test can move it.</param>
    /// <param name="mediaInfo">Takes the source's media info before a probe is tried, or null to always probe.</param>
    public LinkProbe(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILinkRegistry links, LinkGrants grants, ILogger<LinkProbe> logger, Func<DateTime> now, LinkMediaInfo? mediaInfo = null)
    {
        _mediaInfo = mediaInfo;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _links = links;
        _grants = grants;
        _logger = logger;
        _now = now;
    }

    /// <summary>Gets how many links wait for a probe or its check.</summary>
    public int Waiting => _pending.Count;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _timer = new Timer(_ => Sweep(), null, Tick, Tick);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _shipping.Dispose();
    }

    /// <summary>Starts watching a newly added item, if it is one of this server's links.</summary>
    /// <param name="item">The item the library added, or null.</param>
    public void Track(BaseItem? item)
    {
        // This runs in Jellyfin's item added event, which must never see a throw.
        try
        {
            var path = item?.Path;
            if (path is null || !path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_links.Find(path) is not { } link)
            {
                return;
            }

            _pending[item!.Id] = new Pending(link.LinkId, _now() + Settle, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue the media probe for a new link");
        }
    }

    /// <summary>Probes the links that have settled, and checks the ones probed a while ago.</summary>
    public void Sweep()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
        {
            return;
        }

        // A throw from a timer callback would take the server down.
        try
        {
            var now = _now();
            foreach (var (itemId, pending) in _pending.Where(p => p.Value.DueAt <= now).ToList())
            {
                // Still waiting its turn for media info, which is not a failed try.
                if (_inFlight.ContainsKey(itemId))
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(itemId);
                if (item is null || (pending.Attempts > 0 && item.RunTimeTicks is > 0))
                {
                    _pending.TryRemove(itemId, out _);
                    continue;
                }

                if (pending.Attempts >= MaxAttempts)
                {
                    _logger.LogDebug("Gave up probing the link {Path} after {Attempts} tries. It is probed when someone plays it", FormatUtilities.SanitizeForLog(item.Path), pending.Attempts);
                    _pending.TryRemove(itemId, out _);
                    continue;
                }

                _pending[itemId] = pending with { DueAt = now + CheckAfter, Attempts = pending.Attempts + 1 };

                // The source already holds the media info, so it is asked before probing.
                if (_mediaInfo is not null && item.Path is { } path && _links.Find(path) is { } link && _inFlight.TryAdd(itemId, 0))
                {
                    _ = ShipOrProbeAsync(item, link, pending.LinkId);
                }
                else
                {
                    Probe(itemId, pending.LinkId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not probe new links");
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }

    /// <summary>
    /// Takes a link's media info from its source, or probes it when the source cannot give it. Only a few
    /// run at once, since a large sync adds many links and each may take images too.
    /// </summary>
    private async Task ShipOrProbeAsync(BaseItem item, LinkEntry link, string linkId)
    {
        await _shipping.WaitAsync().ConfigureAwait(false);

        // Any failure to take the media info falls back to the probe.
        try
        {
            if (await _mediaInfo!.ShipAsync(item, link, CancellationToken.None).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not take the media info for the link {Path} from its source, so it is probed", FormatUtilities.SanitizeForLog(link.LinkPath));
        }
        finally
        {
            _shipping.Release();

            // The check counts from now, not from when it joined the queue.
            if (_pending.TryGetValue(item.Id, out var waiting))
            {
                _pending[item.Id] = waiting with { DueAt = _now() + CheckAfter };
            }

            _inFlight.TryRemove(item.Id, out _);
        }

        // Nothing awaits this task, so a throw would go unobserved.
        try
        {
            Probe(item.Id, linkId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not probe the link {Path}", FormatUtilities.SanitizeForLog(link.LinkPath));
        }
    }

    /// <summary>Queues Jellyfin's probe of a link under a probe grant, since nobody is playing it.</summary>
    private void Probe(Guid itemId, string linkId)
    {
        _grants.GrantProbe(linkId);
        _providerManager.QueueRefresh(
            itemId,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh
            },
            RefreshPriority.Normal);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => Track(e.Item);

    /// <summary>A link waiting for its probe or its check, with when that is due and the tries made.</summary>
    private sealed record Pending(string LinkId, DateTime DueAt, int Attempts);
}

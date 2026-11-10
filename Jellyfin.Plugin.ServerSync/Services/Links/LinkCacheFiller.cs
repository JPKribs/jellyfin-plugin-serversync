using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// Decides what goes into the link cache and downloads it, one file at a time, in the background.
/// Three things put a link's file in the cache: someone plays the link, it is an episode ahead of the
/// one a user is watching, or it is one of the items chosen to always keep. Each download reads the
/// file from the link's own server through the route links stream from, so that server's speed limits
/// apply, and this server's own download speed limit applies as well.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LinkCacheFiller : BackgroundService
{
    private readonly LinkCache _cache;
    private readonly ILinkRegistry _links;
    private readonly IServiceProvider _services;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LinkCacheFiller> _logger;
    private readonly Channel<Fill> _queue = Channel.CreateUnbounded<Fill>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _playing = new(StringComparer.OrdinalIgnoreCase);

    public LinkCacheFiller(
        LinkCache cache,
        ILinkRegistry links,
        IServiceProvider services,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        IServerApplicationHost applicationHost,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<LinkCacheFiller> logger)
    {
        _cache = cache;
        _links = links;
        _services = services;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _applicationHost = applicationHost;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Gets how many files wait to be cached.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>
    /// The episodes that follow one in its show, in order, up to a count. Specials, which have no place
    /// in the order, are passed over.
    /// </summary>
    /// <param name="episodes">Every episode of the show.</param>
    /// <param name="current">The episode being watched.</param>
    /// <param name="count">How many to take.</param>
    /// <returns>The episodes ahead.</returns>
    public static IReadOnlyList<Episode> Ahead(IEnumerable<Episode> episodes, Episode current, int count)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(current);
        if (count <= 0 || current.ParentIndexNumber is not { } season || current.IndexNumber is not { } number)
        {
            return Array.Empty<Episode>();
        }

        return episodes
            .Where(e => e.ParentIndexNumber is > 0 && e.IndexNumber is not null && e.Id != current.Id)
            .Where(e => e.ParentIndexNumber > season || (e.ParentIndexNumber == season && e.IndexNumber > number))
            .OrderBy(e => e.ParentIndexNumber)
            .ThenBy(e => e.IndexNumber)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// The links chosen to always keep: every link whose source file sits under one of the items
    /// picked on its library mapping.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="links">Every link row.</param>
    /// <returns>The link ids.</returns>
    public static HashSet<string> PermanentLinks(PluginConfiguration config, IEnumerable<SyncItem> links)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(links);
        var permanent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in links)
        {
            if (MappingFor(config, row) is { ExcludeFromLinkCache: false, PermanentItems.Count: > 0 } mapping
                && PathUtilities.IsItemFiltered(row.SourcePath, mapping.SourceRootPath ?? string.Empty, LibraryFilterMode.Blacklist, mapping.PermanentItems))
            {
                permanent.Add(row.SourceItemId);
            }
        }

        return permanent;
    }

    /// <summary>The library mapping a link row came through, or null when it is no longer set up.</summary>
    public static LibraryMapping? MappingFor(PluginConfiguration config, SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(row);
        return config.FindServer(row.ServerKey) is { IsEnabled: true } server
            ? server.GetEnabledLibraryMappings().FirstOrDefault(m => string.Equals(m.SourceLibraryId, row.SourceLibraryId, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Downloads a stopped server cut short are cleared. Whole cached files are kept.
        try
        {
            _cache.Tidy();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tidy the link cache at startup");
        }

        _sessionManager.PlaybackStart += OnStart;
        _sessionManager.PlaybackStopped += OnStopped;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnStart;
        _sessionManager.PlaybackStopped -= OnStopped;
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Brings the cache in line with the settings: marks and queues the items to always keep, forgets
    /// files whose link is gone, and applies retention and the size limit. Run by every content sync.
    /// </summary>
    /// <returns>How many files were queued to cache.</returns>
    public int Reconcile()
    {
        if (!_cache.Enabled)
        {
            return 0;
        }

        var config = _configManager.Configuration;
        var rows = Table().GetLinks().Where(r => r.StorageMode == StorageMode.Link).ToList();
        var byId = rows.ToDictionary(r => r.SourceItemId, StringComparer.OrdinalIgnoreCase);
        var permanent = PermanentLinks(config, rows);
        foreach (var entry in _cache.List())
        {
            // The link was removed, became a copy, or its library left the cache.
            if (!byId.TryGetValue(entry.LinkId, out var row) || MappingFor(config, row) is not { ExcludeFromLinkCache: false })
            {
                _cache.Remove(entry.LinkId);
            }
        }

        _cache.MarkPermanent(permanent);
        if (!_cache.MakeRoom(0, Playing()))
        {
            _logger.LogWarning(
                "The items chosen to always keep fill the link cache ({Used} of {Limit}), so nothing else is cached until its size is raised or fewer are chosen",
                FormatUtilities.FormatBytes(_cache.UsedBytes()),
                FormatUtilities.FormatBytes(config.GetLinkCacheSizeBytes()));
        }

        var queued = 0;
        foreach (var linkId in permanent)
        {
            if (Enqueue(linkId, CacheReason.Permanent, null))
            {
                queued++;
            }
        }

        return queued;
    }

    /// <summary>Queues a link's file to be cached. False when it is cached or waiting already.</summary>
    public bool Enqueue(string linkId, CacheReason reason, Guid? userId)
    {
        if (!_cache.Enabled || _cache.Holds(linkId) || !_waiting.TryAdd(linkId, 0))
        {
            return false;
        }

        if (_queue.Writer.TryWrite(new Fill(linkId, reason, userId)))
        {
            return true;
        }

        _waiting.TryRemove(linkId, out _);
        return false;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var fill in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await FillAsync(fill, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not cache the link {LinkId}", fill.LinkId);
                }
                finally
                {
                    _waiting.TryRemove(fill.LinkId, out _);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private ContentSyncTableManager Table() => _services.GetRequiredService<ContentSyncTableManager>();

    private HashSet<string> Playing() => _playing.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void OnStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (!_cache.Enabled || e.Item?.Path is not { } path || !path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) || _links.Find(path) is not { } link)
            {
                return;
            }

            _playing.AddOrUpdate(link.LinkId, 1, (_, viewers) => viewers + 1);
            _cache.Played(link.LinkId);
            var user = e.Users?.FirstOrDefault();
            Enqueue(link.LinkId, CacheReason.Played, user?.Id);

            // The episodes after this one are cached ahead, so the next press of play is local.
            var ahead = _configManager.Configuration.LinkCachePreCacheEpisodes;
            if (ahead > 0 && e.Item is Episode episode && episode.SeriesId != Guid.Empty)
            {
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    AncestorIds = new[] { episode.SeriesId },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                    IsVirtualItem = false
                }).OfType<Episode>();
                foreach (var next in Ahead(episodes, episode, ahead))
                {
                    if (next.Path is { } nextPath && _links.Find(nextPath) is { } nextLink)
                    {
                        Enqueue(nextLink.LinkId, CacheReason.Ahead, user?.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue a played link for the cache");
        }
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            if (e.Item?.Path is { } path && path.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) && _links.Find(path) is { } link)
            {
                _cache.Played(link.LinkId);
                if (_playing.AddOrUpdate(link.LinkId, 0, (_, viewers) => viewers - 1) <= 0)
                {
                    _playing.TryRemove(link.LinkId, out _);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not note that a link stopped playing");
        }
    }

    private async Task FillAsync(Fill fill, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;
        if (!_cache.Enabled || _cache.Holds(fill.LinkId)
            || Table().GetBySourceItemId(fill.LinkId) is not { StorageMode: StorageMode.Link, Status: SyncStatus.Synced } row
            || MappingFor(config, row) is not { ExcludeFromLinkCache: false }
            || config.FindServer(row.ServerKey) is not { IsEnabled: true, IsConfigured: true } server)
        {
            return;
        }

        var permanent = PermanentLinks(config, new[] { row }).Count > 0;
        var name = Path.GetFileName(row.SourcePath);
        if (row.SourceSize > config.GetLinkCacheSizeBytes() || !_cache.MakeRoom(row.SourceSize, Playing()))
        {
            _logger.LogInformation("Did not cache {File} ({Size}): the link cache has no room for it", name, FormatUtilities.FormatBytes(row.SourceSize));
            return;
        }

        var extension = Path.GetExtension(row.SourcePath);
        var part = _cache.PartPathFor(fill.LinkId, extension);
        using var client = _clientFactory.Create(server);
        try
        {
            // Read through the route links stream from, marked as a cache download: the source applies
            // its speed limits, and does not count it as someone watching.
            using var response = await client.OpenPeerStreamAsync(
                fill.LinkId,
                _applicationHost.SystemId,
                new LinkGrant(fill.UserId, fill.UserId is null),
                LinkAccess.SourceUserFor(server, fill.UserId),
                null,
                false,
                true,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Did not cache {File}: '{Server}' answered {Status}", name, server.DisplayName, (int)response.StatusCode);
                return;
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    var speed = config.GetEffectiveDownloadSpeedBytes();
                    await StallWatchdogStream.CopyAsync(
                        body,
                        file,
                        StallWatchdogStream.DefaultIdleTimeout,
                        (from, to, token) => StreamUtilities.CopyWithSpeedLimitAsync(from, to, speed, token),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var expected = row.SourceSize > 0 ? row.SourceSize : response.Content.Headers.ContentLength ?? 0;
            if (_cache.Complete(fill.LinkId, extension, expected, fill.Reason, permanent))
            {
                _logger.LogInformation("Cached {File} ({Size}) from '{Server}'", name, FormatUtilities.FormatBytes(expected), server.DisplayName);
            }
            else
            {
                _logger.LogWarning("Did not cache {File}: what '{Server}' sent was not the size of the file", name, server.DisplayName);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Did not cache {File}: {Reason}", name, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cleared by the next tidy.
            }
        }
    }

    private sealed record Fill(string LinkId, CacheReason Reason, Guid? UserId);
}

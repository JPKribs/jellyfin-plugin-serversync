using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Listens for changes made on this server and raises one hint for each object once it has gone untouched for Send Changes After.
/// A hand made edit is stamped the moment it is made, and a save that is the sync's own write raises nothing.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LocalChangeObserver : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpContextAccessor _httpContext;
    private readonly IPluginConfigurationManager _configManager;
    private readonly LocalHintPublisher _publisher;
    private readonly OutboundHintWorker _worker;
    private readonly VersionStore _versions;
    private readonly ApplyGuard _guard;
    private readonly HintSuppression _suppression;
    private readonly InboundHintWorker _inboundWorker;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LocalChangeObserver> _logger;
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stopping;
    private Task? _flushLoop;

    /// <summary>The most unmatched changes kept to check again, so mappings that miss a whole library do not hold every change made there.</summary>
    private const int MaxUnmatched = 1000;

    private readonly ConcurrentDictionary<string, UnmatchedChange> _unmatched = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalChangeObserver"/> class.
    /// </summary>
    /// <param name="userDataManager">Raises the event for each history save.</param>
    /// <param name="userManager">Names the user a history hint is for.</param>
    /// <param name="libraryManager">Raises the events for items added, updated, and removed.</param>
    /// <param name="httpContext">Tells an edit someone made in a request from Jellyfin's own background work.</param>
    /// <param name="configManager">Reads Send Changes After and the server entries.</param>
    /// <param name="publisher">Queues a hint for each peer once a change is raised.</param>
    /// <param name="worker">Tells what each peer accepts.</param>
    /// <param name="guard">Tells a save that is the echo of an apply.</param>
    /// <param name="versions">Records the stamp of each hand made edit, even one no peer is told about.</param>
    /// <param name="suppression">Holds the objects peers declined, which are forgotten when the configuration changes.</param>
    /// <param name="inboundWorker">Wakes the hints parked for an item or person that arrives.</param>
    /// <param name="applicationHost">Gives this server's id.</param>
    /// <param name="playlistCopies">Ensures the list of playlist copies is loaded before a playlist is removed.</param>
    /// <param name="logger">Writes a change that could not be recorded or raised.</param>
    /// <param name="removed">Writes down each media file that leaves the library, or null.</param>
    public LocalChangeObserver(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IHttpContextAccessor httpContext,
        IPluginConfigurationManager configManager,
        LocalHintPublisher publisher,
        OutboundHintWorker worker,
        ApplyGuard guard,
        VersionStore versions,
        HintSuppression suppression,
        InboundHintWorker inboundWorker,
        IServerApplicationHost applicationHost,
        PlaylistCopyStore playlistCopies,
        ILogger<LocalChangeObserver> logger,
        RemovedItemsStore? removed = null)
    {
        ArgumentNullException.ThrowIfNull(userDataManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(suppression);
        ArgumentNullException.ThrowIfNull(inboundWorker);
        ArgumentNullException.ThrowIfNull(applicationHost);
        ArgumentNullException.ThrowIfNull(playlistCopies);
        ArgumentNullException.ThrowIfNull(logger);

        _removed = removed;
        _suppression = suppression;
        _inboundWorker = inboundWorker;
        _worker = worker;
        _versions = versions;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _httpContext = httpContext;
        _configManager = configManager;
        _publisher = publisher;
        _guard = guard;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>Gets how many objects are waiting out Send Changes After.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Gets how many objects changed here since start that no Push or Sync server mapped, so nothing was sent.
    /// Saving the configuration checks them again.
    /// </summary>
    public int UnmatchedCount => _unmatched.Count;

    /// <summary>Gets a description of the last change that matched no mapping, or null.</summary>
    public string? LastUnmatched => _unmatched.Values.OrderByDescending(u => u.At).FirstOrDefault()?.Description;

    /// <summary>Forgets the unmatched changes once the operator has seen them and accepts them.</summary>
    public void DismissUnmatched() => _unmatched.Clear();

    /// <summary>Lists the changes still gathering, newest edit first, for the dashboard.</summary>
    /// <returns>A snapshot of the gathering changes.</returns>
    public IReadOnlyList<GatheringChange> Gathering()
        => _pending.Values.Select(c => new GatheringChange(c.Kind, c.UserId, c.ItemId, c.EditedAt, c.Due, c.Recorded)).OrderByDescending(c => c.EditedAt).ToList();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemRemoved += OnItemRemoved;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _flushLoop = Task.Run(() => FlushLoopAsync(_stopping.Token), CancellationToken.None);
        _logger.LogInformation("Server Sync is watching for local changes to watch history, metadata, people, files, and playlists. User settings are not announced live, since Jellyfin raises no event for policy and configuration changes. The scheduled Sync Information task carries them");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        if (_flushLoop is not null)
        {
            try
            {
                await _flushLoop.ConfigureAwait(false);
            }
            // The loop ends by cancellation when the service stops.
            catch (OperationCanceledException)
            {
            }
        }

        Flush(DateTime.MaxValue);
    }

    /// <summary>Raises every gathered change whose Send Changes After wait has ended.</summary>
    /// <param name="utcNow">Changes due at or before this time are raised, so <see cref="DateTime.MaxValue"/> raises everything.</param>
    /// <returns>How many changes were raised.</returns>
    public int Flush(DateTime utcNow)
    {
        var raised = 0;
        foreach (var entry in _pending.ToArray())
        {
            // Due moves with every edit, so a change still being edited keeps gathering.
            if (entry.Value.Due > utcNow)
            {
                continue;
            }

            if (!_pending.TryRemove(entry))
            {
                continue;
            }

            try
            {
                Raise(entry.Value);
                raised++;
            }
            // One change that cannot be raised must not stop the rest of the flush.
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not raise a {Kind} hint for {Key}", entry.Value.Kind, FormatUtilities.SanitizeForLog(entry.Key));
            }
        }

        return raised;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping?.Dispose();
    }

    /// <summary>Notes a history change when a user's data for a media file is saved.</summary>
    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (e?.Item is null || e.Item is Folder || string.IsNullOrEmpty(e.Item.Path))
            {
                return;
            }

            Note(HintKind.History, HintProtocol.HistoryKey(e.UserId, e.Item.Id), e.UserId, e.Item.Id, itemPath: e.Item.Path, parts: PartsOf(e.SaveReason));
        }
        // A Jellyfin event handler must never throw into the save that raised it.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record a local history change");
        }
    }

    /// <summary>
    /// Tells which parts of a user's history of an item a save changed.
    /// A save that could have changed either part counts as both.
    /// </summary>
    /// <param name="reason">Why Jellyfin saved the user's data.</param>
    /// <returns>The parts.</returns>
    public static HistoryParts PartsOf(MediaBrowser.Model.Entities.UserDataSaveReason reason) => reason switch
    {
        MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserRating => HistoryParts.Favorite,
        MediaBrowser.Model.Entities.UserDataSaveReason.TogglePlayed or MediaBrowser.Model.Entities.UserDataSaveReason.PlaybackStart or MediaBrowser.Model.Entities.UserDataSaveReason.PlaybackProgress or MediaBrowser.Model.Entities.UserDataSaveReason.PlaybackFinished => HistoryParts.Played,
        _ => HistoryParts.All
    };

    /// <summary>
    /// Sorts an item update into a hand made edit, a provider's work, or nothing to announce.
    /// Only a save with the metadata edit or image update flag alone, made inside a request, is someone's edit.
    /// </summary>
    /// <param name="reason">The flags Jellyfin saved the item with.</param>
    /// <param name="inRequest">Whether the save was made while handling an HTTP request.</param>
    /// <returns>Where the change came from.</returns>
    public static ChangeOrigin Classify(ItemUpdateType reason, bool inRequest)
    {
        if (reason is ItemUpdateType.MetadataEdit or ItemUpdateType.ImageUpdate)
        {
            return inRequest ? ChangeOrigin.Edit : ChangeOrigin.Provider;
        }

        return (reason & (ItemUpdateType.MetadataEdit | ItemUpdateType.ImageUpdate | ItemUpdateType.MetadataDownload)) != 0
            ? ChangeOrigin.Provider
            : ChangeOrigin.None;
    }

    /// <summary>Notes a playlist, people, or metadata change when an item is updated.</summary>
    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e?.Item is null)
            {
                return;
            }

            var origin = Classify(e.UpdateReason, _httpContext.HttpContext is not null);
            if (origin == ChangeOrigin.None)
            {
                return;
            }

            var recorded = origin == ChangeOrigin.Edit;

            // A value this server's provider copied from a source is not announced back.
            if (!recorded && ProviderFills.WasFilled(e.Item.Path, e.Item is Person ? e.Item.Name : null, DateTime.UtcNow))
            {
                return;
            }

            if (e.Item is Playlist playlist)
            {
                if (recorded && _configManager.Configuration.EnablePlaylistSync)
                {
                    Note(HintKind.Playlists, PlaylistMirror.RootKey(playlist), playlist.OwnerUserId, playlist.Id);
                }

                return;
            }

            if (e.Item is Person person)
            {
                if (!string.IsNullOrWhiteSpace(person.Name))
                {
                    Note(HintKind.People, HintProtocol.PeopleKey(person.Name), Guid.Empty, person.Id, recorded);
                }

                return;
            }

            if (!string.IsNullOrEmpty(e.Item.Path))
            {
                Note(HintKind.Metadata, HintProtocol.MetadataKey(e.Item.Id), Guid.Empty, e.Item.Id, recorded, e.Item.Path);
            }
        }
        // A Jellyfin event handler must never throw into the save that raised it.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record a local item change");
        }
    }

    /// <summary>Wakes the hints parked for an item that arrived, and notes a new media file or playlist.</summary>
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e?.Item is null)
            {
                return;
            }

            if (e.Item is Person added && !string.IsNullOrWhiteSpace(added.Name))
            {
                _inboundWorker.WakeParked(HintTriage.PersonParkKey(added.Name));
            }
            else if (!string.IsNullOrEmpty(e.Item.Path))
            {
                _inboundWorker.WakeParked(HintTriage.PathParkKey(e.Item.Path));
            }

            if (e.Item is Playlist created)
            {
                if (_httpContext.HttpContext is not null && _configManager.Configuration.EnablePlaylistSync)
                {
                    Note(HintKind.Playlists, PlaylistMirror.RootKey(created), created.OwnerUserId, created.Id);
                }

                return;
            }

            if (e.Item is Folder || e.Item is Person || string.IsNullOrEmpty(e.Item.Path) || e.Item.IsVirtualItem)
            {
                return;
            }

            // A file this server downloaded while syncing is the sync's own write.
            if (WrittenFiles.WasWritten(e.Item.Path))
            {
                return;
            }

            if (e.Item is not (MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Episode or MediaBrowser.Controller.Entities.Audio.Audio or MediaBrowser.Controller.Entities.Video))
            {
                return;
            }

            Note(HintKind.Content, HintProtocol.MetadataKey(e.Item.Id), Guid.Empty, e.Item.Id, itemPath: e.Item.Path);
        }
        // A Jellyfin event handler must never throw into the save that raised it.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record a local item addition");
        }
    }

    private readonly RemovedItemsStore? _removed;

    /// <summary>
    /// Writes down a media file that left the library and announces it, so links to it move or go without waiting for a scan.
    /// It is announced even when Content is off here, since a server that only gives still has servers linking to its files.
    /// </summary>
    private void NoteMediaRemoved(BaseItem item)
    {
        if (item is Folder || item.IsVirtualItem || string.IsNullOrEmpty(item.Path)
            || item.Path.EndsWith(Links.LinkPaths.Extension, StringComparison.OrdinalIgnoreCase)
            || item is not (MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Episode or MediaBrowser.Controller.Entities.Audio.Audio or MediaBrowser.Controller.Entities.Video))
        {
            return;
        }

        var now = DateTime.UtcNow;
        _removed?.Note(item.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture), now);
        var version = new ObjectVersion { ServerId = _applicationHost.SystemId, Timestamp = now };
        _publisher.PublishContent(item.Id, item.Path, version, excludePeerKey: null, itemType: item.GetType().Name, removed: true);
    }

    /// <summary>Notes a media file leaving the library, and announces a playlist someone deleted.</summary>
    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e?.Item is not null and not Playlist)
            {
                NoteMediaRemoved(e.Item);
            }
        }
        // A Jellyfin event handler must never throw into the save that raised it.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record a media file leaving the library");
        }

        try
        {
            if (e?.Item is not Playlist playlist)
            {
                return;
            }

            var rootKey = PlaylistMirror.RootKey(playlist);
            var copy = PlaylistMirror.IsMirror(playlist);
            PlaylistCopyStore.Remove(playlist.Id);
            if (_httpContext.HttpContext is null || !_configManager.Configuration.EnablePlaylistSync)
            {
                return;
            }

            if (_guard.IsApplying(HintProtocol.GuardKey(HintKind.Playlists, rootKey)))
            {
                return;
            }

            _pending.TryRemove(HintProtocol.GuardKey(HintKind.Playlists, rootKey), out _);
            var version = new ObjectVersion { ServerId = _applicationHost.SystemId, Timestamp = DateTime.UtcNow };
            _publisher.PublishPlaylist(playlist.Id, rootKey, playlist.Name ?? string.Empty, playlist.OwnerUserId, version, excludePeerKey: null, recorded: true, deleted: true, copy: copy);
        }
        // A Jellyfin event handler must never throw into the save that raised it.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record a local playlist deletion");
        }
    }

    /// <summary>
    /// Adds a change to those gathering for the same object, where one hand made edit makes the whole hint a recorded edit.
    /// A hand made edit is stamped at once, so a peer's older edit arriving during the wait loses to it.
    /// </summary>
    private void Note(HintKind kind, string localKey, Guid userId, Guid itemId, bool recorded = true, string? itemPath = null, HistoryParts parts = HistoryParts.None)
    {
        if (_guard.IsApplying(HintProtocol.GuardKey(kind, localKey)))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (recorded)
        {
            _versions.Set(new ObjectVersion { Kind = kind, Key = localKey, ServerId = _applicationHost.SystemId, Timestamp = now });

            foreach (var part in new[] { HistoryParts.Played, HistoryParts.Favorite })
            {
                // Each part of history carries its own stamp, which later changes are weighed against.
                if (kind == HintKind.History && parts.HasFlag(part))
                {
                    _versions.Set(new ObjectVersion { Kind = kind, Key = HintProtocol.HistoryPartKey(localKey, part), ServerId = _applicationHost.SystemId, Timestamp = now });
                }
            }
        }

        // Nothing gathers for a change no peer will take.
        if (!AnyPeerAccepts(kind, itemPath, userId, localKey))
        {
            return;
        }

        var due = now + HintProtocol.Debounce(_configManager.Configuration);
        _pending.AddOrUpdate(
            HintProtocol.GuardKey(kind, localKey),
            _ => new PendingChange(kind, localKey, userId, itemId, now, due, recorded, now, parts),
            (_, existing) => existing with
            {
                EditedAt = now,
                Due = due,
                Parts = existing.Parts | parts,
                Recorded = existing.Recorded || recorded,
                VersionAt = recorded || !existing.Recorded ? now : existing.VersionAt
            });
    }

    /// <summary>Raises the changes that are due once every second until stopped.</summary>
    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_pending.IsEmpty)
            {
                Flush(DateTime.UtcNow);
            }
        }
    }

    /// <summary>Forgets what peers declined and raises each unmatched change again, since a saved mapping may cover it.</summary>
    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        _suppression.Clear();

        foreach (var entry in _unmatched.Values.ToList())
        {
            try
            {
                Raise(entry.Change);
            }
            // A configuration event handler must never throw into the save that raised it.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not check an unmatched change again: {Change}", FormatUtilities.SanitizeForLog(entry.Description));
            }
        }
    }

    /// <summary>Publishes one gathered change as a hint, with the time of its last hand made edit as its stamp.</summary>
    private void Raise(PendingChange change)
    {
        _unmatched.TryRemove(HintProtocol.GuardKey(change.Kind, change.LocalKey), out _);
        var version = new ObjectVersion { ServerId = _applicationHost.SystemId, Timestamp = change.VersionAt };

        // A peer's newer edit applied during the wait, so this one is not announced.
        if (change.Recorded
            && _versions.Get(change.Kind, change.LocalKey) is { } current
            && !string.Equals(current.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase)
            && VersionDecider.IsNewer(current, new ObjectVersion { Kind = change.Kind, Key = change.LocalKey, ServerId = version.ServerId, Timestamp = version.Timestamp }))
        {
            return;
        }

        var item = _libraryManager.GetItemById(change.ItemId);
        if (item is null)
        {
            return;
        }

        switch (change.Kind)
        {
            case HintKind.History when !string.IsNullOrEmpty(item.Path):
                var user = _userManager.GetUserById(change.UserId);
                NoteUnmatched(_publisher.PublishHistory(change.UserId, user?.Username, change.ItemId, item.Path, version, excludePeerKey: null, itemType: HintProtocol.HistoryItemType(item.GetType().Name, change.Parts)), change, HintKind.History, item.Path, user?.Username);
                break;

            case HintKind.Metadata when !string.IsNullOrEmpty(item.Path):
                NoteUnmatched(_publisher.PublishMetadata(change.ItemId, item.Path, version, excludePeerKey: null, recorded: change.Recorded, itemType: item.GetType().Name), change, HintKind.Metadata, item.Path, null);
                break;

            case HintKind.People when !string.IsNullOrWhiteSpace(item.Name):
                NoteUnmatched(_publisher.PublishPeople(item.Name, change.ItemId, version, excludePeerKey: null, recorded: change.Recorded), change, HintKind.People, null, item.Name);
                break;

            case HintKind.Playlists when item is Playlist playlist:
                NoteUnmatched(_publisher.PublishPlaylist(playlist.Id, change.LocalKey, playlist.Name ?? string.Empty, playlist.OwnerUserId, version, excludePeerKey: null, recorded: change.Recorded, deleted: false, copy: PlaylistMirror.IsMirror(playlist)), change, HintKind.Playlists, null, playlist.Name);
                break;

            case HintKind.Content when !string.IsNullOrEmpty(item.Path):
                NoteUnmatched(_publisher.PublishContent(change.ItemId, item.Path, version, excludePeerKey: null, itemType: item.GetType().Name), change, HintKind.Content, item.Path, null);
                break;
        }
    }

    /// <summary>Whether any Push or Sync server maps the object and takes its kind.</summary>
    private bool AnyPeerAccepts(HintKind kind, string? itemPath = null, Guid userId = default, string? localKey = null)
    {
        foreach (var server in _configManager.Configuration.Servers)
        {
            if (server.Pushes && (itemPath is null || HintMapping.FindByLocalPath(server, itemPath) is not null)
                && (kind is not (HintKind.History or HintKind.Playlists) || userId == Guid.Empty || HintMapping.FindByLocalUser(server, userId) is not null)
                && (localKey is null ? _worker.PeerAccepts(server.Key, kind) : _worker.PeerWants(server.Key, kind, itemPath, userId, localKey)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Remembers a change that was queued for no peer although a peer takes its kind.</summary>
    private void NoteUnmatched(int queued, PendingChange change, HintKind kind, string? itemPath, string? userName)
    {
        if (queued != 0 || !AnyPeerAccepts(kind, itemPath, change.UserId) || _unmatched.Count >= MaxUnmatched)
        {
            return;
        }

        var description = HintActivityLog.Describe(kind, itemPath, userName, itemPath ?? userName ?? string.Empty);
        _unmatched[HintProtocol.GuardKey(change.Kind, change.LocalKey)] = new UnmatchedChange(change, description, DateTime.UtcNow);
        _logger.LogDebug("A local change matched no mapping on any Push or Sync server: {Change}", FormatUtilities.SanitizeForLog(description));
    }

    /// <summary>A change that matched no mapping, with its description and when it was raised.</summary>
    private sealed record UnmatchedChange(PendingChange Change, string Description, DateTime At);

    /// <summary>One object's gathering changes, with when it is due and the time its stamp carries.</summary>
    private sealed record PendingChange(HintKind Kind, string LocalKey, Guid UserId, Guid ItemId, DateTime EditedAt, DateTime Due, bool Recorded, DateTime VersionAt, HistoryParts Parts = HistoryParts.None);
}

/// <summary>What an item update is, as the observer sorts it.</summary>
public enum ChangeOrigin
{
    /// <summary>Nothing hints carry.</summary>
    None,

    /// <summary>A change made by hand, which travels as a recorded edit.</summary>
    Edit,

    /// <summary>A provider's work during a scan or a refresh, which travels marked and never replaces a recorded edit.</summary>
    Provider
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Listens for changes made on this server and raises hints for them: user data saves for history,
/// item updates for metadata and people, and items added for content. User settings are not
/// announced: Jellyfin raises no event for policy and configuration changes, so the scheduled Sync
/// Information task carries them. The module switches are the receiver's business: a server raises a hint for
/// anything a peer it sends to has mapped, and the peer decides. A metadata edit or an image change
/// raises an item hint. Provider work during a scan or a refresh is marked as such, since a server
/// that fetches its own metadata after receiving a file would otherwise push that over the other
/// server's curated values
/// the moment it arrived; it still travels, marked as a provider's, and never replaces a recorded edit. Each object's edits are
/// gathered until it has gone untouched for the configured wait, so a poster changed twice or a
/// playback that reports progress every few seconds becomes one hint, not one per edit. An event raised while a peer's hint is being applied to the same
/// object is the echo of that apply and raises nothing. See <see cref="ApplyGuard"/>.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LocalChangeObserver : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IPluginConfigurationManager _configManager;
    private readonly LocalHintPublisher _publisher;
    private readonly OutboundHintWorker _worker;
    private readonly VersionStore _versions;
    private readonly ApplyGuard _guard;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LocalChangeObserver> _logger;
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, byte> _refreshing = new();
    private CancellationTokenSource? _stopping;
    private Task? _flushLoop;
    private int _unmatched;
    private string? _lastUnmatched;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalChangeObserver"/> class.
    /// </summary>
    /// <param name="userDataManager">User data manager, for the save event.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Provider manager, for the refresh events that tell a provider's image from a hand picked one.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="publisher">The publisher that writes the outbound rows.</param>
    /// <param name="worker">The delivery worker, which knows what each peer accepts.</param>
    /// <param name="guard">The apply guard.</param>
    /// <param name="versions">The version store, for edits no peer is told about.</param>
    /// <param name="applicationHost">The server host, for this server's id.</param>
    /// <param name="logger">Logger.</param>
    public LocalChangeObserver(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IPluginConfigurationManager configManager,
        LocalHintPublisher publisher,
        OutboundHintWorker worker,
        ApplyGuard guard,
        VersionStore versions,
        IServerApplicationHost applicationHost,
        ILogger<LocalChangeObserver> logger)
    {
        _worker = worker;
        _versions = versions;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _configManager = configManager;
        _publisher = publisher;
        _guard = guard;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>Gets how many objects are waiting for their debounce to end.</summary>
    public int PendingCount => _pending.Values.Count(c => c.Publish);

    /// <summary>
    /// Gets how many local changes were raised since start that no Push or Sync server mapped, so
    /// nothing was sent. The usual reason is a server entry whose Libraries or Users step does not
    /// cover the changed item or user.
    /// </summary>
    public int UnmatchedCount => _unmatched;

    /// <summary>Gets a description of the last change that matched no mapping, or null.</summary>
    public string? LastUnmatched => _lastUnmatched;

    /// <summary>Gets the changes still gathering, newest edit first, for the dashboard.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<GatheringChange> Gathering()
        => _pending.Values.Where(c => c.Publish).Select(c => new GatheringChange(c.Kind, c.UserId, c.ItemId, c.EditedAt, c.Due, c.Recorded)).OrderByDescending(c => c.EditedAt).ToList();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemAdded += OnItemAdded;
        _providerManager.RefreshStarted += OnRefreshStarted;
        _providerManager.RefreshCompleted += OnRefreshCompleted;
        _flushLoop = Task.Run(() => FlushLoopAsync(_stopping.Token), CancellationToken.None);
        _logger.LogInformation("Server Sync is watching for local changes to watch history, metadata, people, and files. User settings are not announced live, since Jellyfin raises no event for policy and configuration changes; the scheduled Sync Information task carries them");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemAdded -= OnItemAdded;
        _providerManager.RefreshStarted -= OnRefreshStarted;
        _providerManager.RefreshCompleted -= OnRefreshCompleted;
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
            catch (OperationCanceledException)
            {
                // Expected on stop.
            }
        }

        // Whatever was still gathering is raised now so a restart does not lose it.
        Flush(DateTime.MaxValue);
    }

    /// <summary>Raises every gathered change whose debounce has ended. Public so tests and the dashboard can force it.</summary>
    /// <param name="utcNow">Now. Pass <see cref="DateTime.MaxValue"/> to raise everything.</param>
    /// <returns>How many changes were raised.</returns>
    public int Flush(DateTime utcNow)
    {
        var raised = 0;
        foreach (var entry in _pending.ToArray())
        {
            // Due moves with every edit, so the change is raised once the object has gone untouched for
            // the configured wait, and only then.
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not raise a history hint for {Key}", entry.Key);
            }
        }

        return raised;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping?.Dispose();
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (e?.Item is null || e.Item is Folder || string.IsNullOrEmpty(e.Item.Path))
            {
                return;
            }

            // Versions are recorded whether or not any peer is sent to, so a server that only receives
            // still knows when its own values were edited when a first contact has to be decided.
            Note(HintKind.History, HintProtocol.HistoryKey(e.UserId, e.Item.Id), e.UserId, e.Item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record a local history change");
        }
    }

    /// <summary>Whether an item update is a metadata edit, which hints always carry. Public for tests.</summary>
    /// <param name="reason">Why Jellyfin saved the item.</param>
    /// <returns><c>true</c> for a metadata edit.</returns>
    public static bool IsHintedUpdate(ItemUpdateType reason) => (reason & ItemUpdateType.MetadataEdit) != 0;

    /// <summary>
    /// Sorts an item update into what travels and how. A metadata edit is a hand made change. An image
    /// change is a hand made change too, unless a library scan or a refresh of the item is running, when
    /// it is a provider's; Jellyfin reports both with the same reason. A metadata download is always a
    /// provider's. Provider work travels as well, marked so a hand made edit on the other side still
    /// wins and so it only fills in where the other side has recorded nothing. Public for tests.
    /// </summary>
    /// <param name="reason">Why Jellyfin saved the item.</param>
    /// <param name="beingRefreshed">Whether the item, an ancestor, or for a person any item, is in a metadata refresh.</param>
    /// <param name="scanRunning">Whether a library scan is running.</param>
    /// <returns>What the update is.</returns>
    public static ChangeOrigin Classify(ItemUpdateType reason, bool beingRefreshed, bool scanRunning)
    {
        if (IsHintedUpdate(reason))
        {
            return ChangeOrigin.Edit;
        }

        if ((reason & ItemUpdateType.ImageUpdate) != 0)
        {
            return beingRefreshed || scanRunning ? ChangeOrigin.Provider : ChangeOrigin.Edit;
        }

        return (reason & ItemUpdateType.MetadataDownload) != 0 ? ChangeOrigin.Provider : ChangeOrigin.None;
    }

    private void OnRefreshStarted(object? sender, Jellyfin.Data.Events.GenericEventArgs<BaseItem> e)
    {
        if (e?.Argument is not null)
        {
            _refreshing[e.Argument.Id] = 0;
        }
    }

    private void OnRefreshCompleted(object? sender, Jellyfin.Data.Events.GenericEventArgs<BaseItem> e)
    {
        if (e?.Argument is not null)
        {
            _refreshing.TryRemove(e.Argument.Id, out _);
        }
    }

    // A refresh of a series or a movie also refreshes what belongs to it, children and cast alike, so
    // an item counts as being refreshed when it or an ancestor is, and a person whenever any item is.
    private bool IsBeingRefreshed(BaseItem item)
    {
        if (_refreshing.IsEmpty)
        {
            return false;
        }

        if (item is Person)
        {
            return true;
        }

        for (var current = item; current is not null; current = current.GetParent())
        {
            if (_refreshing.ContainsKey(current.Id))
            {
                return true;
            }
        }

        return false;
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e?.Item is null)
            {
                return;
            }

            // The ancestor walk only decides an image change outside a scan, so it runs only then. A
            // library scan raises this event for every item it touches, and each walk is a lookup per
            // level of the tree.
            var scanRunning = _libraryManager.IsScanRunning;
            var needsRefreshCheck = !IsHintedUpdate(e.UpdateReason) && (e.UpdateReason & ItemUpdateType.ImageUpdate) != 0 && !scanRunning;
            var origin = Classify(e.UpdateReason, needsRefreshCheck && IsBeingRefreshed(e.Item), scanRunning);
            if (origin == ChangeOrigin.None)
            {
                return;
            }

            var recorded = origin == ChangeOrigin.Edit;
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
                Note(HintKind.Metadata, HintProtocol.MetadataKey(e.Item.Id), Guid.Empty, e.Item.Id, recorded);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record a local item change");
        }
    }

    // A file the library just picked up. Folders, people, and virtual items are not files.
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e?.Item is null || e.Item is Folder || e.Item is Person || string.IsNullOrEmpty(e.Item.Path) || e.Item.IsVirtualItem)
            {
                return;
            }

            if (e.Item is not (MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Episode or MediaBrowser.Controller.Entities.Audio.Audio or MediaBrowser.Controller.Entities.Video))
            {
                return;
            }

            Note(HintKind.Content, HintProtocol.MetadataKey(e.Item.Id), Guid.Empty, e.Item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record a local item addition");
        }
    }

    // A change gathers with the others to the same object. One hand made edit in the window makes the
    // whole hint a recorded edit; a window of nothing but provider work travels as provider work.
    private void Note(HintKind kind, string localKey, Guid userId, Guid itemId, bool recorded = true)
    {
        if (_guard.IsApplying(HintProtocol.GuardKey(kind, localKey)))
        {
            return;
        }

        // Nothing is sent for a kind no peer will take, and the queue does not show it. A hand made edit
        // still has its version recorded when the wait ends, so a server that only receives knows when
        // its own values were edited the day a conflict has to be decided. The server entries are few, so
        // this is a short scan on every event rather than a row that waits for nothing.
        var publish = AnyPeerAccepts(kind);
        if (!publish && !recorded)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var due = now + HintProtocol.Debounce(_configManager.Configuration);
        _pending.AddOrUpdate(
            HintProtocol.GuardKey(kind, localKey),
            _ => new PendingChange(kind, localKey, userId, itemId, now, due, recorded, publish),
            (_, existing) => existing with { EditedAt = now, Due = due, Recorded = existing.Recorded || recorded, Publish = existing.Publish || publish });
    }

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

    private void Raise(PendingChange change)
    {
        var version = new ObjectVersion { ServerId = _applicationHost.SystemId, Timestamp = change.EditedAt };
        if (!change.Publish)
        {
            version.Kind = change.Kind;
            version.Key = change.LocalKey;
            _versions.Set(version);
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
                NoteUnmatched(_publisher.PublishHistory(change.UserId, user?.Username, change.ItemId, item.Path, version, excludePeerKey: null, itemType: item.GetType().Name), HintKind.History, item.Path, user?.Username);
                break;

            case HintKind.Metadata when !string.IsNullOrEmpty(item.Path):
                NoteUnmatched(_publisher.PublishMetadata(change.ItemId, item.Path, version, excludePeerKey: null, recorded: change.Recorded, itemType: item.GetType().Name), HintKind.Metadata, item.Path, null);
                break;

            case HintKind.People when !string.IsNullOrWhiteSpace(item.Name):
                NoteUnmatched(_publisher.PublishPeople(item.Name, change.ItemId, version, excludePeerKey: null, recorded: change.Recorded), HintKind.People, null, item.Name);
                break;

            case HintKind.Content when !string.IsNullOrEmpty(item.Path):
                NoteUnmatched(_publisher.PublishContent(change.ItemId, item.Path, version, excludePeerKey: null, itemType: item.GetType().Name), HintKind.Content, item.Path, null);
                break;

            default:
                break;
        }
    }

    // A change raised to no peer is only worth counting when there is a peer to send to; a server
    // with no Push or Sync entry is not misconfigured, it just does not send.
    private bool AnyPeerAccepts(HintKind kind)
    {
        foreach (var server in _configManager.Configuration.Servers)
        {
            if (server.Pushes && _worker.PeerAccepts(server.Key, kind))
            {
                return true;
            }
        }

        return false;
    }

    private void NoteUnmatched(int queued, HintKind kind, string? itemPath, string? userName)
    {
        if (queued > 0 || !AnyPeerAccepts(kind))
        {
            return;
        }

        Interlocked.Increment(ref _unmatched);
        _lastUnmatched = HintActivityLog.Describe(kind, itemPath, userName, itemPath ?? userName ?? string.Empty);
        _logger.LogDebug("A local change matched no mapping on any Push or Sync server: {Change}", _lastUnmatched);
    }

    private sealed record PendingChange(HintKind Kind, string LocalKey, Guid UserId, Guid ItemId, DateTime EditedAt, DateTime Due, bool Recorded, bool Publish);
}

/// <summary>One change still gathering before it becomes a hint.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="UserId">The local user, for history.</param>
/// <param name="ItemId">The local item.</param>
/// <param name="EditedAt">When it was last edited, in UTC.</param>
/// <param name="Due">When it is sent unless edited again, in UTC.</param>
/// <param name="Recorded">Whether a hand made edit is among the gathered changes.</param>
public sealed record GatheringChange(HintKind Kind, Guid UserId, Guid ItemId, DateTime EditedAt, DateTime Due, bool Recorded);

/// <summary>What an item update is, as the observer sorts it.</summary>
public enum ChangeOrigin
{
    /// <summary>Nothing hints carry.</summary>
    None,

    /// <summary>A change made by hand. Travels as a recorded edit.</summary>
    Edit,

    /// <summary>A provider's work during a scan or a refresh. Travels marked, and never replaces a recorded edit.</summary>
    Provider
}

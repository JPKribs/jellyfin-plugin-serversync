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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Listens for changes made on this server and raises hints for them: user data saves for history,
/// item updates for metadata and people, items added for content, and user updates for users. Policy
/// and configuration changes raise no Jellyfin event, so mapped users are also checked on a timer
/// against a snapshot. The module switches are the receiver's business: a server raises a hint for
/// anything a peer it sends to has mapped, and the peer decides. Only a metadata edit raises an item hint. Provider
/// downloads and image refreshes are left to the scheduled scan, since a server that fetches its own
/// metadata after receiving a file would otherwise push that over the other server's curated values
/// the moment it arrived. Each object's edits are
/// gathered for a few seconds so a playback that reports progress every few seconds becomes one hint
/// per pause, not one per tick. An event raised while a peer's hint is being applied to the same
/// object is the echo of that apply and raises nothing. See <see cref="ApplyGuard"/>.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LocalChangeObserver : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginConfigurationManager _configManager;
    private readonly LocalHintPublisher _publisher;
    private readonly ApplyGuard _guard;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LocalChangeObserver> _logger;
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _userPrints = new();
    private CancellationTokenSource? _stopping;
    private Task? _flushLoop;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalChangeObserver"/> class.
    /// </summary>
    /// <param name="userDataManager">User data manager, for the save event.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="publisher">The publisher that writes the outbound rows.</param>
    /// <param name="guard">The apply guard.</param>
    /// <param name="applicationHost">The server host, for this server's id.</param>
    /// <param name="logger">Logger.</param>
    public LocalChangeObserver(
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IPluginConfigurationManager configManager,
        LocalHintPublisher publisher,
        ApplyGuard guard,
        IServerApplicationHost applicationHost,
        ILogger<LocalChangeObserver> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _configManager = configManager;
        _publisher = publisher;
        _guard = guard;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>Gets how many objects are waiting for their debounce to end.</summary>
    public int PendingCount => _pending.Count;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemAdded += OnItemAdded;
        _userManager.OnUserUpdated += OnUserUpdated;
        _flushLoop = Task.Run(() => FlushLoopAsync(_stopping.Token), CancellationToken.None);
        _logger.LogInformation("Server Sync is watching for local history changes");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemAdded -= OnItemAdded;
        _userManager.OnUserUpdated -= OnUserUpdated;
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

    /// <summary>Whether an item update is the kind of change hints carry. Public for tests.</summary>
    /// <param name="reason">Why Jellyfin saved the item.</param>
    /// <returns><c>true</c> for a metadata edit.</returns>
    public static bool IsHintedUpdate(ItemUpdateType reason) => (reason & ItemUpdateType.MetadataEdit) != 0;

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e?.Item is null || !IsHintedUpdate(e.UpdateReason))
            {
                return;
            }

            if (e.Item is Person person)
            {
                if (!string.IsNullOrWhiteSpace(person.Name))
                {
                    Note(HintKind.People, HintProtocol.PeopleKey(person.Name), Guid.Empty, person.Id);
                }

                return;
            }

            if (!string.IsNullOrEmpty(e.Item.Path))
            {
                Note(HintKind.Metadata, HintProtocol.MetadataKey(e.Item.Id), Guid.Empty, e.Item.Id);
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

    private void OnUserUpdated(object? sender, Jellyfin.Data.Events.GenericEventArgs<Jellyfin.Database.Implementations.Entities.User> e)
    {
        try
        {
            if (e?.Argument is null)
            {
                return;
            }

            NoteUser(e.Argument.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record a local user change");
        }
    }

    /// <summary>
    /// Takes a fresh picture of a user after a peer's change was applied here, so the write is not
    /// reported back as a local edit when the timer next looks.
    /// </summary>
    /// <param name="userId">The local user.</param>
    public void ResetUserSnapshot(Guid userId)
    {
        var print = Fingerprint(userId);
        if (print is null)
        {
            _userPrints.TryRemove(userId, out _);
        }
        else
        {
            _userPrints[userId] = print;
        }
    }

    private void NoteUser(Guid userId)
    {
        var key = HintProtocol.UsersKey(userId);
        if (_guard.IsApplying(HintProtocol.GuardKey(HintKind.Users, key)))
        {
            ResetUserSnapshot(userId);
            return;
        }

        ResetUserSnapshot(userId);
        Note(HintKind.Users, key, userId, Guid.Empty);
    }

    // Policy and configuration saves raise no event, so every user mapped to a peer this server
    // sends to is compared against a snapshot on a timer. The first look only takes the snapshot.
    private void PollUsers()
    {
        var config = _configManager.Configuration;
        var mapped = new HashSet<Guid>();
        foreach (var peer in config.Servers.Where(s => s.Pushes))
        {
            foreach (var mapping in peer.GetEnabledUserMappings())
            {
                if (Guid.TryParse(mapping.LocalUserId, out var id))
                {
                    mapped.Add(id);
                }
            }
        }

        foreach (var stale in _userPrints.Keys.Where(k => !mapped.Contains(k)).ToList())
        {
            _userPrints.TryRemove(stale, out _);
        }

        foreach (var userId in mapped)
        {
            var print = Fingerprint(userId);
            if (print is null)
            {
                continue;
            }

            if (!_userPrints.TryGetValue(userId, out var previous))
            {
                _userPrints[userId] = print;
                continue;
            }

            if (!string.Equals(previous, print, StringComparison.Ordinal))
            {
                NoteUser(userId);
            }
        }
    }

    private string? Fingerprint(Guid userId)
    {
        try
        {
            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                return null;
            }

            var dto = _userManager.GetUserDto(user);
            return System.Text.Json.JsonSerializer.Serialize(new { dto.Name, dto.PrimaryImageTag, dto.Policy, dto.Configuration });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read user {User} for change detection", userId);
            return null;
        }
    }

    private void Note(HintKind kind, string localKey, Guid userId, Guid itemId)
    {
        if (_guard.IsApplying(HintProtocol.GuardKey(kind, localKey)))
        {
            return;
        }

        var now = DateTime.UtcNow;
        _pending.AddOrUpdate(
            HintProtocol.GuardKey(kind, localKey),
            _ => new PendingChange(kind, userId, itemId, now, now + HintProtocol.Debounce),
            (_, existing) => existing with { EditedAt = now, Due = now + HintProtocol.Debounce });
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var nextUserPoll = DateTime.UtcNow + HintProtocol.UserPoll;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_pending.IsEmpty)
            {
                Flush(DateTime.UtcNow);
            }

            if (DateTime.UtcNow >= nextUserPoll)
            {
                nextUserPoll = DateTime.UtcNow + HintProtocol.UserPoll;
                try
                {
                    PollUsers();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "User change check failed");
                }
            }
        }
    }

    private void Raise(PendingChange change)
    {
        var version = new ObjectVersion { ServerId = _applicationHost.SystemId, Timestamp = change.EditedAt };
        if (change.Kind == HintKind.Users)
        {
            var changedUser = _userManager.GetUserById(change.UserId);
            if (changedUser is not null)
            {
                _publisher.PublishUsers(change.UserId, changedUser.Username, version, excludePeerKey: null);
            }

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
                _publisher.PublishHistory(change.UserId, user?.Username, change.ItemId, item.Path, version, excludePeerKey: null);
                break;

            case HintKind.Metadata when !string.IsNullOrEmpty(item.Path):
                _publisher.PublishMetadata(change.ItemId, item.Path, version, excludePeerKey: null);
                break;

            case HintKind.People when !string.IsNullOrWhiteSpace(item.Name):
                _publisher.PublishPeople(item.Name, change.ItemId, version, excludePeerKey: null);
                break;

            case HintKind.Content when !string.IsNullOrEmpty(item.Path):
                _publisher.PublishContent(change.ItemId, item.Path, version, excludePeerKey: null);
                break;

            default:
                break;
        }
    }

    private sealed record PendingChange(HintKind Kind, Guid UserId, Guid ItemId, DateTime EditedAt, DateTime Due);
}

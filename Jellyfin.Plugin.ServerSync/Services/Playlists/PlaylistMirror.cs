using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

public enum PlaylistMirrorOutcome
{
    Created,
    Updated,
    Unchanged,
    Waiting,
    Skipped
}

public sealed record SourcePlaylist(
    string Id,
    string RootId,
    string Name,
    string? MediaType,
    string OwnerUserId,
    bool OpenAccess,
    List<(string UserId, bool CanEdit)> Shares,
    List<string?> EntryPaths,
    bool OwnerKnown,
    bool IsCopy)
{
    public bool IsOriginal => !IsCopy;
}

[PluginService(ServiceLifetime.Singleton)]
public sealed class PlaylistMirror
{
    public const string SourcePlaylistProviderKey = "ServerSyncSourcePlaylist";

    public const string SourceServerProviderKey = "ServerSyncSourceServer";

    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly MediaBrowser.Controller.Providers.IProviderManager _providerManager;
    private readonly ApplyGuard _guard;
    private readonly ILogger<PlaylistMirror> _logger;

    public PlaylistMirror(ILibraryManager libraryManager, IPlaylistManager playlistManager, IUserManager userManager, IServerApplicationHost applicationHost, MediaBrowser.Controller.Providers.IProviderManager providerManager, ApplyGuard guard, PlaylistCopyStore copies, ILogger<PlaylistMirror> logger)
    {
        ArgumentNullException.ThrowIfNull(copies);
        _providerManager = providerManager;
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _userManager = userManager;
        _applicationHost = applicationHost;
        _guard = guard;
        _logger = logger;
    }

    public static string RootKey(BaseItem playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return TaggedRoot(playlist) ?? IdKey(playlist.Id);
    }

    public static string IdKey(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    public static bool IsMirror(BaseItem playlist) => TaggedRoot(playlist) is not null;

    public List<Playlist> LocalPlaylists()
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true
        }).OfType<Playlist>().ToList();

    public Playlist? FindByRoot(string rootKey, bool sourceIsOriginal, IReadOnlyList<Playlist>? local = null)
    {
        foreach (var playlist in local ?? LocalPlaylists())
        {
            if (string.Equals(TaggedRoot(playlist), rootKey, StringComparison.OrdinalIgnoreCase))
            {
                return playlist;
            }

            if (!sourceIsOriginal && !IsMirror(playlist) && string.Equals(IdKey(playlist.Id), rootKey, StringComparison.OrdinalIgnoreCase))
            {
                return playlist;
            }
        }

        return null;
    }

    public int RebuildCopiesFromTags()
    {
        if (PlaylistCopyStore.Count() > 0)
        {
            return 0;
        }

        var rebuilt = 0;
        foreach (var playlist in LocalPlaylists())
        {
            if (playlist.ProviderIds.TryGetValue(SourcePlaylistProviderKey, out var root)
                && Guid.TryParse(root, out var rootId)
                && playlist.ProviderIds.TryGetValue(SourceServerProviderKey, out var server)
                && !string.IsNullOrWhiteSpace(server))
            {
                PlaylistCopyStore.Set(playlist.Id, IdKey(rootId), server);
                rebuilt++;
            }
        }

        if (rebuilt > 0)
        {
            _logger.LogInformation("Playlists: rebuilt {Count} playlist copies from their tags", rebuilt);
        }

        return rebuilt;
    }

    public Dictionary<string, Guid> UserMap(SourceServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var users = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in server.GetEnabledUserMappings())
        {
            if (Guid.TryParse(mapping.SourceUserId, out var sourceId) && Guid.TryParse(mapping.LocalUserId, out var localId) && _userManager.GetUserById(localId) is not null)
            {
                users.TryAdd(IdKey(sourceId), localId);
            }
        }

        return users;
    }

    public async Task<List<SourcePlaylist>> ReadAsync(SourceServer server, SourceServerClient client, Dictionary<string, Guid> users, Guid? onlyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(users);
        var self = server.IsStandardUser && Guid.TryParse(server.AuthenticatedUserId, out var selfId) && users.ContainsKey(IdKey(selfId)) ? IdKey(selfId) : null;
        var peer = await client.GetPeerPlaylistsAsync(_applicationHost.SystemId, onlyId, cancellationToken).ConfigureAwait(false);
        if (peer is not null)
        {
            return peer.Playlists
                .Where(p => Guid.TryParse(p.Id, out _) && Guid.TryParse(p.OwnerUserId, out var owner) && (self is not null || users.ContainsKey(IdKey(owner))))
                .Select(p =>
                {
                    var exact = self is null || string.Equals(IdKey(Guid.Parse(p.OwnerUserId)), self, StringComparison.OrdinalIgnoreCase);
                    return new SourcePlaylist(
                        IdKey(Guid.Parse(p.Id)),
                        Guid.TryParse(p.RootId, out var root) ? IdKey(root) : IdKey(Guid.Parse(p.Id)),
                        p.Name,
                        p.MediaType,
                        self ?? IdKey(Guid.Parse(p.OwnerUserId)),
                        p.OpenAccess,
                        p.Shares.Where(s => Guid.TryParse(s.UserId, out _)).Select(s => (IdKey(Guid.Parse(s.UserId)), s.CanEdit)).ToList(),
                        p.Entries.Select(e => e.Path).ToList(),
                        exact,
                        p.IsCopy);
                })
                .ToList();
        }

        if (self is null)
        {
            throw new InvalidOperationException("Server Sync there did not list its playlists, and without it playlist owners cannot be read. Check the whitelist there, or use a standard user's key to copy only that user's playlists");
        }

        var found = new List<SourcePlaylist>();
        var userId = Guid.Parse(self);
        var visible = await client.GetVisiblePlaylistsAsync(userId, cancellationToken).ConfigureAwait(false);
        foreach (var playlist in visible.Where(p => p.Id.HasValue && (onlyId is null || p.Id.Value.Equals(onlyId.Value))))
        {
            var id = IdKey(playlist.Id!.Value);
            var root = playlist.ProviderIds?.AdditionalData is { } extra
                && extra.TryGetValue(SourcePlaylistProviderKey, out var tagged)
                && Guid.TryParse(tagged?.ToString(), out var tag)
                    ? IdKey(tag)
                    : id;
            var entries = await client.GetPlaylistEntriesAsync(playlist.Id.Value, userId, cancellationToken).ConfigureAwait(false);
            found.Add(new SourcePlaylist(
                id,
                root,
                playlist.Name ?? string.Empty,
                playlist.MediaType?.ToString(),
                self,
                false,
                new List<(string UserId, bool CanEdit)>(),
                entries.Select(e => e.Path).ToList(),
                false,
                !string.Equals(root, id, StringComparison.OrdinalIgnoreCase)));
        }

        return found;
    }

    public async Task<PlaylistMirrorOutcome> ApplyAsync(SourceServer server, SourcePlaylist source, Dictionary<string, Guid> users, bool syncShares, CancellationToken cancellationToken, IReadOnlyList<Playlist>? local = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(users);
        var ownerId = users[source.OwnerUserId];
        var owner = _userManager.GetUserById(ownerId) ?? throw new InvalidOperationException("the local owner no longer exists");
        var mappings = server.GetEnabledLibraryMappings();

        var desired = new List<Guid>();
        foreach (var path in source.EntryPaths)
        {
            var item = Resolve(path, mappings);
            if (item is not null && item.SupportsAddingToPlaylist && item.IsVisible(owner))
            {
                desired.Add(item.Id);
            }
        }

        var shares = syncShares && source.OwnerKnown
            ? source.Shares
                .Where(s => users.ContainsKey(s.UserId) && users[s.UserId] != ownerId)
                .GroupBy(s => users[s.UserId])
                .Select(g => new PlaylistUserPermissions(g.Key, g.Any(s => s.CanEdit)))
                .ToList()
            : null;
        var openAccess = syncShares && source.OwnerKnown ? source.OpenAccess : (bool?)null;

        var existing = FindByRoot(source.RootId, source.IsOriginal, local);
        if (existing is null)
        {
            if (desired.Count == 0 && source.EntryPaths.Count > 0)
            {
                _logger.LogInformation("Playlists: none of the items in '{Name}' are here yet. The playlist is created once some arrive", source.Name);
                return PlaylistMirrorOutcome.Waiting;
            }

            using (_guard.Enter(HintProtocol.GuardAllKey(HintKind.Playlists)))
            {
                var created = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = source.Name,
                    ItemIdList = desired,
                    MediaType = ParseMediaType(source.MediaType),
                    UserId = ownerId,
                    Users = shares ?? new List<PlaylistUserPermissions>(),
                    Public = openAccess ?? false
                }).ConfigureAwait(false);

                PlaylistCopyStore.Set(Guid.Parse(created.Id), source.RootId, server.Key);
                if (_libraryManager.GetItemById(Guid.Parse(created.Id)) is Playlist made)
                {
                    made.ProviderIds[SourcePlaylistProviderKey] = source.RootId;
                    made.ProviderIds[SourceServerProviderKey] = server.Key;
                    await _libraryManager.UpdateItemAsync(made, made.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    await _providerManager.SaveMetadataAsync(made, ItemUpdateType.MetadataEdit).ConfigureAwait(false);
                }
            }

            _logger.LogInformation("Playlists: created '{Name}' for {User} with {Count} item(s), {Dropped} not here", source.Name, owner.Username, desired.Count, source.EntryPaths.Count - desired.Count);
            return PlaylistMirrorOutcome.Created;
        }

        var ownerChanged = !existing.OwnerUserId.Equals(ownerId);
        if (ownerChanged && !(source.OwnerKnown && IsMirror(existing)))
        {
            _logger.LogWarning("Playlists: '{Name}' here belongs to another user than the one it maps to, so it is left alone", existing.Name);
            return PlaylistMirrorOutcome.Skipped;
        }

        var current = existing.GetManageableItems().Select(e => e.Item2.Id).ToList();
        if (desired.Count == 0 && source.EntryPaths.Count > 0 && current.Count > 0)
        {
            _logger.LogWarning("Playlists: none of the {Count} item(s) in '{Name}' resolved here. Leaving the local playlist unchanged this run", source.EntryPaths.Count, source.Name);
            return PlaylistMirrorOutcome.Waiting;
        }

        var request = new PlaylistUpdateRequest { Id = existing.Id, UserId = ownerId };
        var changed = false;
        if (!current.SequenceEqual(desired))
        {
            request.Ids = desired;
            changed = true;
        }

        if (!string.Equals(existing.Name, source.Name, StringComparison.Ordinal))
        {
            request.Name = source.Name;
            changed = true;
        }

        if (shares is not null && !SameShares(existing.Shares, shares))
        {
            request.Users = shares;
            changed = true;
        }

        if (openAccess.HasValue && existing.OpenAccess != openAccess.Value)
        {
            request.Public = openAccess.Value;
            changed = true;
        }

        if (!changed && !ownerChanged)
        {
            return PlaylistMirrorOutcome.Unchanged;
        }

        using (_guard.Enter(HintProtocol.GuardKey(HintKind.Playlists, source.RootId)))
        {
            if (ownerChanged)
            {
                existing.OwnerUserId = ownerId;
                await _libraryManager.UpdateItemAsync(existing, existing.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                _playlistManager.SavePlaylistFile(existing);
            }

            if (changed)
            {
                await _playlistManager.UpdatePlaylist(request).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Playlists: updated '{Name}' for {User}, {Count} item(s), {Dropped} not here", source.Name, owner.Username, desired.Count, source.EntryPaths.Count - desired.Count);
        return PlaylistMirrorOutcome.Updated;
    }

    public void Delete(Playlist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        using (_guard.Enter(HintProtocol.GuardKey(HintKind.Playlists, RootKey(playlist))))
        {
            _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true }, playlist.GetParent(), false);
        }

        PlaylistCopyStore.Remove(playlist.Id);

        _logger.LogInformation("Playlists: deleted '{Name}'", playlist.Name);
    }

    public static string? CopyServer(BaseItem playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return PlaylistCopyStore.Get(playlist.Id)?.Server
            ?? (playlist.ProviderIds.TryGetValue(SourceServerProviderKey, out var server) ? server : null);
    }

    private static string? TaggedRoot(BaseItem playlist)
    {
        if (PlaylistCopyStore.Get(playlist.Id) is { } copy && Guid.TryParse(copy.Root, out var registered))
        {
            return IdKey(registered);
        }

        return playlist.ProviderIds.TryGetValue(SourcePlaylistProviderKey, out var tagged) && Guid.TryParse(tagged, out var root) ? IdKey(root) : null;
    }

    private BaseItem? Resolve(string? sourcePath, List<LibraryMapping> mappings)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var normalized = sourcePath.Replace('\\', '/');
        foreach (var mapping in mappings)
        {
            var root = (mapping.SourceRootPath ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (root.Length == 0 || !normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var localPath = PathUtilities.TranslatePath(sourcePath, mapping.SourceRootPath!, mapping.LocalRootPath);
            return _libraryManager.FindByPath(localPath, isFolder: false);
        }

        return null;
    }

    private static bool SameShares(IReadOnlyList<PlaylistUserPermissions> current, List<PlaylistUserPermissions> wanted)
        => current.Count == wanted.Count
            && wanted.All(w => current.Any(c => c.UserId.Equals(w.UserId) && c.CanEdit == w.CanEdit));

    private static MediaType? ParseMediaType(string? value)
        => Enum.TryParse<MediaType>(value, true, out var parsed) && parsed != MediaType.Unknown ? parsed : null;
}

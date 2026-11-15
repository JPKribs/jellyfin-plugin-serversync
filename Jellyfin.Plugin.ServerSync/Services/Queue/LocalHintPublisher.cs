using System;
using System.Globalization;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Turns one local change into a hint for each peer this server sends to that maps the object.
/// It also records the stamp the change carries and wakes the delivery worker.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class LocalHintPublisher
{
    private readonly IPluginConfigurationManager _configManager;
    private readonly OutboundHintStore _outbound;
    private readonly VersionStore _versions;
    private readonly OutboundHintWorker _worker;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LocalHintPublisher> _logger;
    private readonly ILinkRegistry? _links;
    private readonly ContentSyncTableManager? _content;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalHintPublisher"/> class.
    /// </summary>
    /// <param name="configManager">Lists the server entries to send to.</param>
    /// <param name="outbound">Holds the hints waiting to be sent.</param>
    /// <param name="versions">Records the stamp of each hand made edit.</param>
    /// <param name="worker">The delivery worker to wake.</param>
    /// <param name="applicationHost">Gives this server's id.</param>
    /// <param name="logger">Writes a hint that could not be queued or was held.</param>
    /// <param name="links">Gives a link the path its copy would have, or null.</param>
    /// <param name="content">Tells a file synced from a peer from a new one, or null.</param>
    public LocalHintPublisher(
        IPluginConfigurationManager configManager,
        OutboundHintStore outbound,
        VersionStore versions,
        OutboundHintWorker worker,
        IServerApplicationHost applicationHost,
        ILogger<LocalHintPublisher> logger,
        ILinkRegistry? links = null,
        ContentSyncTableManager? content = null)
    {
        _links = links;
        _content = content;
        _configManager = configManager;
        _outbound = outbound;
        _versions = versions;
        _worker = worker;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>Publishes a metadata change for a library item.</summary>
    /// <param name="localItemId">The local item that changed.</param>
    /// <param name="itemPath">The local path of the item.</param>
    /// <param name="version">The stamp the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the change, or null.</param>
    /// <param name="recorded">Whether the change was made by hand. Provider work records no stamp here.</param>
    /// <param name="itemType">The item's Jellyfin type, for the queue view.</param>
    /// <returns>How many peers were queued a hint, or -1 when the change is held.</returns>
    public int PublishMetadata(Guid localItemId, string itemPath, ObjectVersion version, string? excludePeerKey, bool recorded = true, string? itemType = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        itemPath = SentPath(itemPath);
        var key = HintProtocol.MetadataKey(localItemId);
        return Publish(
            HintKind.Metadata,
            key,
            version,
            excludePeerKey,
            peer => HintMapping.FindByLocalPath(peer, itemPath) is not null,
            () => new OutboundHint { Kind = HintKind.Metadata, Key = key, ItemPath = itemPath, ItemId = key, ItemType = itemType, Recorded = recorded },
            recorded,
            itemPath);
    }

    /// <summary>Publishes a change to a person's own metadata or images.</summary>
    /// <param name="name">The person's name.</param>
    /// <param name="localPersonId">The local person item.</param>
    /// <param name="version">The stamp the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the change, or null.</param>
    /// <param name="recorded">Whether the change was made by hand. Provider work records no stamp here.</param>
    /// <returns>How many peers were queued a hint, or -1 when the change is held.</returns>
    public int PublishPeople(string name, Guid localPersonId, ObjectVersion version, string? excludePeerKey, bool recorded = true)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var localKey = HintProtocol.PeopleKey(name);
        return Publish(
            HintKind.People,
            localKey,
            version,
            excludePeerKey,
            _ => true,
            () => new OutboundHint { Kind = HintKind.People, Key = name, ItemId = localPersonId.ToString("N", CultureInfo.InvariantCulture), UserName = name, Recorded = recorded },
            recorded);
    }

    /// <summary>Gives a link the path its copy would have, so a peer matches the file it stands in for.</summary>
    private string SentPath(string itemPath)
        => _links is null || string.IsNullOrEmpty(itemPath) ? itemPath : LinkPaths.RowPathFor(itemPath, _links);

    /// <summary>Publishes a media file that appeared here.</summary>
    /// <param name="localItemId">The local item that changed.</param>
    /// <param name="itemPath">The local path of the file.</param>
    /// <param name="version">The stamp the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the file, or null.</param>
    /// <param name="itemType">The item's Jellyfin type, for the queue view.</param>
    /// <returns>How many peers were queued a hint, or -1 when the change is held.</returns>
    public int PublishContent(Guid localItemId, string itemPath, ObjectVersion version, string? excludePeerKey, string? itemType = null)
        => PublishContent(localItemId, itemPath, version, excludePeerKey, itemType, removed: false);

    /// <summary>
    /// Publishes a media file that appeared here, or one that left, under the same key.
    /// A file a peer gave this server is announced when it leaves but not when it arrives, since a linking server hears it from nobody else.
    /// </summary>
    /// <param name="localItemId">The local item that changed.</param>
    /// <param name="itemPath">The local path of the file.</param>
    /// <param name="version">The stamp the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the change, or null.</param>
    /// <param name="itemType">The item's Jellyfin type, for the queue view.</param>
    /// <param name="removed">Whether the file left.</param>
    /// <returns>How many peers were queued a hint, or -1 when the change is held.</returns>
    public int PublishContent(Guid localItemId, string itemPath, ObjectVersion version, string? excludePeerKey, string? itemType, bool removed)
    {
        ArgumentNullException.ThrowIfNull(version);

        // A link or any other .strm is not a file a peer can copy.
        if (itemPath?.EndsWith(LinkPaths.Extension, StringComparison.OrdinalIgnoreCase) == true || _links?.Find(itemPath!) is not null)
        {
            return 0;
        }

        // A file synced from a peer is that peer's to announce.
        if (!removed && !string.IsNullOrEmpty(itemPath) && _content?.GetByLocalPath(itemPath) is not null)
        {
            return 0;
        }

        var key = HintProtocol.MetadataKey(localItemId);
        return Publish(
            HintKind.Content,
            key,
            version,
            excludePeerKey,
            peer => HintMapping.FindByLocalPath(peer, itemPath) is not null,
            () => new OutboundHint { Kind = HintKind.Content, Key = key, ItemPath = itemPath, ItemId = key, ItemType = removed ? HintProtocol.ContentRemoved : itemType },
            itemPath: itemPath,
            deletion: removed);
    }

    /// <summary>Publishes a change to a playlist, or its deletion.</summary>
    /// <param name="localPlaylistId">The local playlist.</param>
    /// <param name="rootKey">The playlist key, shared by the original and its copies.</param>
    /// <param name="name">The playlist's name.</param>
    /// <param name="ownerId">The local owner.</param>
    /// <param name="version">The stamp the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the change, or null.</param>
    /// <param name="recorded">Whether the change was made by hand.</param>
    /// <param name="deleted">Whether the playlist was deleted.</param>
    /// <param name="copy">Whether the playlist is a copy of another server's.</param>
    /// <returns>How many peers were queued a hint, or -1 when the change is held.</returns>
    public int PublishPlaylist(Guid localPlaylistId, string rootKey, string name, Guid ownerId, ObjectVersion version, string? excludePeerKey, bool recorded, bool deleted, bool copy)
    {
        ArgumentNullException.ThrowIfNull(version);
        var owner = ownerId.ToString("N", CultureInfo.InvariantCulture);
        return Publish(
            HintKind.Playlists,
            rootKey,
            version,
            excludePeerKey,
            peer => HintMapping.FindByLocalUser(peer, ownerId) is not null,
            () => new OutboundHint
            {
                Kind = HintKind.Playlists,
                Key = rootKey,
                ItemId = localPlaylistId.ToString("N", CultureInfo.InvariantCulture),
                ItemType = HintProtocol.PlaylistItemType(copy, deleted),
                UserId = owner,
                UserName = name,
                Recorded = recorded
            },
            recorded,
            userId: ownerId,
            deletion: deleted);
    }

    /// <summary>Records the stamp, checks whether the change is held, and queues a hint for each peer that wants it.</summary>
    private int Publish(HintKind kind, string localKey, ObjectVersion version, string? excludePeerKey, Func<SourceServer, bool> mapped, Func<OutboundHint> rowFor, bool recorded = true, string? itemPath = null, Guid userId = default, bool deletion = false)
    {
        version.Kind = kind;
        version.Key = localKey;
        // Provider work leaves no stamp, so any hand made edit beats it.
        if (recorded)
        {
            _versions.Set(version);
        }

        if (!deletion && ApplyGuard.AppliedRecently(HintProtocol.GuardKey(kind, localKey), LoopBreaker.Window) && LoopBreaker.Trips(kind, localKey, DateTime.UtcNow))
        {
            _logger.LogWarning("{Kind} {Key} was announced more than {Limit} times in {Minutes} minutes, which looks like two servers echoing one change. It is not announced again until things settle, and the scheduled task carries it", kind, FormatUtilities.SanitizeForLog(localKey), LoopBreaker.Limit, LoopBreaker.Window.TotalMinutes);
            return -1;
        }

        var queued = 0;
        foreach (var peer in _configManager.Configuration.Servers)
        {
            if (!peer.Pushes || string.Equals(peer.Key, excludePeerKey, StringComparison.OrdinalIgnoreCase) || !mapped(peer) || !_worker.PeerWants(peer.Key, kind, itemPath, userId, localKey))
            {
                continue;
            }

            var row = rowFor();
            row.PeerKey = peer.Key;
            row.VersionServerId = version.ServerId;
            row.VersionTimestamp = version.Timestamp;
            try
            {
                _outbound.Enqueue(row, _applicationHost.SystemId);
                queued++;
            }
            // One peer's row failing must not stop the others or reach Jellyfin's event.
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not queue a {Kind} hint for '{Peer}'", kind, FormatUtilities.SanitizeForLog(peer.DisplayName));
            }
        }

        if (queued > 0)
        {
            _logger.LogDebug("Queued a {Kind} hint for {Count} peer(s): {Key}", kind, queued, FormatUtilities.SanitizeForLog(localKey));
            _worker.Wake();
        }

        return queued;
    }

    /// <summary>Publishes a history change, recording its stamp whether or not any peer is told.</summary>
    /// <param name="localUserId">The local user.</param>
    /// <param name="userName">The local username.</param>
    /// <param name="localItemId">The local item that changed.</param>
    /// <param name="itemPath">The local path of the item.</param>
    /// <param name="version">The stamp the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the change and needs no hint, or null.</param>
    /// <param name="itemType">The item's Jellyfin type, for the queue view.</param>
    /// <returns>How many peers were queued a hint, or -1 when the change is held.</returns>
    public int PublishHistory(Guid localUserId, string? userName, Guid localItemId, string itemPath, ObjectVersion version, string? excludePeerKey, string? itemType = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        itemPath = SentPath(itemPath);

        var key = HintProtocol.HistoryKey(localUserId, localItemId);
        var itemKey = localItemId.ToString("N", CultureInfo.InvariantCulture);
        var userKey = localUserId.ToString("N", CultureInfo.InvariantCulture);
        return Publish(
            HintKind.History,
            key,
            version,
            excludePeerKey,
            peer => HintMapping.FindByLocalUser(peer, localUserId) is not null && HintMapping.FindByLocalPath(peer, itemPath) is not null,
            () => new OutboundHint { Kind = HintKind.History, Key = key, ItemPath = itemPath, ItemId = itemKey, ItemType = itemType, UserId = userKey, UserName = userName },
            itemPath: itemPath,
            userId: localUserId);
    }
}

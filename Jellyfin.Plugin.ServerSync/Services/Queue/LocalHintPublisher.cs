using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Turns one local change into outbound rows, one per peer this server sends to that maps the object,
/// records the version the change carries, and wakes the delivery worker. Used by the change observer
/// for edits made here and by the inbound handlers for merges that moved past what the origin had.
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

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalHintPublisher"/> class.
    /// </summary>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="outbound">The outbound store.</param>
    /// <param name="versions">The version store.</param>
    /// <param name="worker">The delivery worker to wake.</param>
    /// <param name="applicationHost">The server host, for this server's id.</param>
    /// <param name="logger">Logger.</param>
    public LocalHintPublisher(
        IPluginConfigurationManager configManager,
        OutboundHintStore outbound,
        VersionStore versions,
        OutboundHintWorker worker,
        IServerApplicationHost applicationHost,
        ILogger<LocalHintPublisher> logger)
    {
        _configManager = configManager;
        _outbound = outbound;
        _versions = versions;
        _worker = worker;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>Publishes a metadata change for a library item.</summary>
    /// <param name="localItemId">The local item.</param>
    /// <param name="itemPath">The local path of the item.</param>
    /// <param name="version">The version the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the change, or null.</param>
    /// <param name="recorded">Whether the change was made by hand. Provider work is sent marked and records no version here.</param>
    /// <returns>How many peers were queued a hint.</returns>
    public int PublishMetadata(Guid localItemId, string itemPath, ObjectVersion version, string? excludePeerKey, bool recorded = true, string? itemType = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        var key = HintProtocol.MetadataKey(localItemId);
        return Publish(
            HintKind.Metadata,
            key,
            version,
            excludePeerKey,
            peer => HintMapping.FindByLocalPath(peer, itemPath) is not null,
            () => new OutboundHint { Kind = HintKind.Metadata, Key = key, ItemPath = itemPath, ItemId = key, ItemType = itemType, Recorded = recorded },
            recorded);
    }

    /// <summary>Publishes a change to a person's own metadata or images.</summary>
    /// <param name="name">The person's name.</param>
    /// <param name="localPersonId">The local person item.</param>
    /// <param name="version">The version the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the change, or null.</param>
    /// <param name="recorded">Whether the change was made by hand. Provider work is sent marked and records no version here.</param>
    /// <returns>How many peers were queued a hint.</returns>
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
            () => new OutboundHint { Kind = HintKind.People, Key = name, ItemId = localPersonId.ToString("N", System.Globalization.CultureInfo.InvariantCulture), UserName = name, Recorded = recorded },
            recorded);
    }

    /// <summary>Publishes a media file that appeared here.</summary>
    /// <param name="localItemId">The local item.</param>
    /// <param name="itemPath">The local path of the file.</param>
    /// <param name="version">The version the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the file, or null.</param>
    /// <returns>How many peers were queued a hint.</returns>
    public int PublishContent(Guid localItemId, string itemPath, ObjectVersion version, string? excludePeerKey, string? itemType = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        var key = HintProtocol.MetadataKey(localItemId);
        return Publish(
            HintKind.Content,
            key,
            version,
            excludePeerKey,
            peer => HintMapping.FindByLocalPath(peer, itemPath) is not null,
            () => new OutboundHint { Kind = HintKind.Content, Key = key, ItemPath = itemPath, ItemId = key, ItemType = itemType });
    }

    private int Publish(HintKind kind, string localKey, ObjectVersion version, string? excludePeerKey, Func<Models.Configuration.SourceServer, bool> mapped, Func<OutboundHint> rowFor, bool recorded = true)
    {
        version.Kind = kind;
        version.Key = localKey;
        if (recorded)
        {
            // Only a hand made edit is a version of this server's own. Provider work leaves none, so a
            // recorded edit anywhere in the pool still beats it.
            _versions.Set(version);
        }

        var queued = 0;
        foreach (var peer in _configManager.Configuration.Servers)
        {
            if (!peer.Pushes || string.Equals(peer.Key, excludePeerKey, StringComparison.OrdinalIgnoreCase) || !_worker.PeerAccepts(peer.Key, kind) || !mapped(peer))
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not queue a {Kind} hint for '{Peer}'", kind, peer.DisplayName);
            }
        }

        if (queued > 0)
        {
            _logger.LogDebug("Queued a {Kind} hint for {Count} peer(s): {Key}", kind, queued, localKey);
            _worker.Wake();
        }

        return queued;
    }

    /// <summary>
    /// Publishes a history change. The version is recorded whether or not any peer is told.
    /// </summary>
    /// <param name="localUserId">The local user.</param>
    /// <param name="userName">The local username.</param>
    /// <param name="localItemId">The local item.</param>
    /// <param name="itemPath">The local path of the item.</param>
    /// <param name="version">The version the change carries.</param>
    /// <param name="excludePeerKey">A peer that already holds the change and needs no hint, or null.</param>
    /// <param name="itemType">The item's Jellyfin type, for the queue view.</param>
    /// <returns>How many peers were queued a hint.</returns>
    public int PublishHistory(Guid localUserId, string? userName, Guid localItemId, string itemPath, ObjectVersion version, string? excludePeerKey, string? itemType = null)
    {
        ArgumentNullException.ThrowIfNull(version);

        var key = HintProtocol.HistoryKey(localUserId, localItemId);
        var itemKey = localItemId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        var userKey = localUserId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        return Publish(
            HintKind.History,
            key,
            version,
            excludePeerKey,
            peer => HintMapping.FindByLocalUser(peer, localUserId) is not null && HintMapping.FindByLocalPath(peer, itemPath) is not null,
            () => new OutboundHint { Kind = HintKind.History, Key = key, ItemPath = itemPath, ItemId = itemKey, ItemType = itemType, UserId = userKey, UserName = userName });
    }
}

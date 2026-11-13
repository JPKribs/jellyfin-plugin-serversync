using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies a playlist hint: reads the playlist from the origin and copies it here the way the scheduled
/// playlist sync would, or deletes the local copy when the origin deleted its own. Unless the origin
/// is a Pull server, a newer edit made here is kept and announced instead.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class PlaylistHintHandler
{
    private readonly IPluginConfigurationManager _configManager;
    private readonly PlaylistMirror _mirror;
    private readonly VersionConflictResolver _resolver;
    private readonly LocalHintPublisher _publisher;
    private readonly ILogger<PlaylistHintHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistHintHandler"/> class.
    /// </summary>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="mirror">Reads, copies, and deletes playlists.</param>
    /// <param name="resolver">The version resolver.</param>
    /// <param name="publisher">Publishes this server's winning values.</param>
    /// <param name="logger">Logger.</param>
    public PlaylistHintHandler(IPluginConfigurationManager configManager, PlaylistMirror mirror, VersionConflictResolver resolver, LocalHintPublisher publisher, ILogger<PlaylistHintHandler> logger)
    {
        _configManager = configManager;
        _mirror = mirror;
        _resolver = resolver;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>Applies one playlist hint, waiting briefly for the scheduled playlist sync to finish.</summary>
    /// <param name="hint">The inbound row.</param>
    /// <param name="origin">The configured entry for the origin.</param>
    /// <param name="client">A client bound to the origin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<HintApplyResult> ApplyAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);
        var config = _configManager.Configuration;
        if (!config.EnablePlaylistSync)
        {
            return HintApplyResult.Dropped(HintCode.ModuleOff, "playlist sync is off on this server");
        }

        if (!Guid.TryParse(hint.Key, out var root) || !Guid.TryParse(hint.ItemId, out var senderId))
        {
            return HintApplyResult.Dropped(HintCode.Invalid, "the key is not a playlist id");
        }

        var rootKey = HintProtocol.PlaylistKey(root);
        var senderIsOriginal = !HintProtocol.IsPlaylistCopy(hint.ItemType);
        var mutex = SyncModuleMutex.ForModule("Playlists");
        if (!await mutex.WaitAsync(SyncModuleMutex.SingleRowWait, cancellationToken).ConfigureAwait(false))
        {
            return HintApplyResult.RetryLater("the scheduled playlist sync is running");
        }

        try
        {
            return await ApplyLockedAsync(hint, origin, client, rootKey, senderIsOriginal, senderId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutex.Release();
        }
    }

    private async Task<HintApplyResult> ApplyLockedAsync(InboundHint hint, SourceServer origin, SourceServerClient client, string rootKey, bool senderIsOriginal, Guid senderId, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;
        var incoming = HintProtocol.IncomingVersion(hint, HintKind.Playlists, rootKey);
        if (origin.Mode != ServerMode.Pull
            && _resolver.Recorded(HintKind.Playlists, rootKey) is { } local
            && VersionDecider.Decide(local, incoming, valuesEqual: false) == VersionDecision.Keep)
        {
            if (_resolver.IsThisServer(local) && _mirror.FindByRoot(rootKey, senderIsOriginal) is { } mine)
            {
                _publisher.PublishPlaylist(mine.Id, rootKey, mine.Name ?? string.Empty, mine.OwnerUserId, local, excludePeerKey: null, recorded: true, deleted: false, copy: PlaylistMirror.IsMirror(mine));
            }

            return HintApplyResult.Kept(HintCode.Superseded, "this server holds a newer edit");
        }

        if (HintProtocol.IsPlaylistDeletion(hint.ItemType))
        {
            var existing = _mirror.FindByRoot(rootKey, senderIsOriginal);
            if (existing is null)
            {
                _resolver.Record(incoming);
                return HintApplyResult.Unchanged;
            }

            if (!Guid.TryParse(hint.UserId, out var senderOwner)
                || HintMapping.FindBySourceUser(origin, senderOwner) is not { } mapping
                || !Guid.TryParse(mapping.LocalUserId, out var localOwner)
                || !existing.OwnerUserId.Equals(localOwner))
            {
                return HintApplyResult.Kept(HintCode.NotMapped, "the local copy belongs to another user, so it is kept");
            }

            _mirror.Delete(existing);
            _resolver.Record(incoming);
            return HintApplyResult.Applied;
        }

        var users = _mirror.UserMap(origin);
        SourcePlaylist? source;
        try
        {
            source = (await _mirror.ReadAsync(origin, client, users, senderId, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read playlist {Id} from '{Origin}'", senderId, origin.DisplayName);
            return HintApplyResult.RetryLater($"could not read the playlist: {ex.Message}");
        }

        if (source is null)
        {
            return HintApplyResult.Dropped(HintCode.SourceGone, "the playlist is gone on the sender, or its owner is not mapped here");
        }

        var outcome = await _mirror.ApplyAsync(origin, source, users, config.PlaylistSyncShares, cancellationToken).ConfigureAwait(false);
        if (outcome == PlaylistMirrorOutcome.Waiting)
        {
            return HintApplyResult.Kept(HintCode.NoLocalItem, "none of its items are on this server yet. The scheduled task copies it once they arrive");
        }

        if (outcome == PlaylistMirrorOutcome.Skipped)
        {
            return HintApplyResult.Kept(HintCode.NotMapped, "the local copy belongs to another user, so it is kept");
        }

        if (hint.Recorded)
        {
            _resolver.Record(incoming);
        }

        return outcome == PlaylistMirrorOutcome.Unchanged
            ? HintApplyResult.Unchanged
            : HintApplyResult.AppliedTo(_mirror.FindByRoot(rootKey, source.IsOriginal));
    }
}

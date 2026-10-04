using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// Keeps this server's own history row for a peer in step with what the two servers just agreed on
/// through the negotiation endpoint. Without this only the server that proposed the state would
/// remember the agreement, and the next change coming the other way would be merged against a base
/// that is out of date, which lets an older play outrank a newer unmark. A row that does not exist yet
/// is created from the sender's ids and this server's mappings for it, so a first contact from either
/// side leaves both with a base.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class HistoryAgreementRecorder
{
    private readonly IPluginConfigurationManager _configManager;
    private readonly HistorySyncTableManager _table;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<HistoryAgreementRecorder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryAgreementRecorder"/> class.
    /// </summary>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="table">The history table.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public HistoryAgreementRecorder(
        IPluginConfigurationManager configManager,
        HistorySyncTableManager table,
        ILibraryManager libraryManager,
        ILogger<HistoryAgreementRecorder> logger)
    {
        _configManager = configManager;
        _table = table;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Records an agreement with a peer on one of this server's user and item pairs.</summary>
    /// <param name="senderServerId">The peer's Jellyfin server id.</param>
    /// <param name="userId">This server's user id.</param>
    /// <param name="itemId">This server's item id.</param>
    /// <param name="senderUserId">The peer's own user id, needed to create a row that does not exist yet.</param>
    /// <param name="senderItemId">The peer's own item id, needed to create a row that does not exist yet.</param>
    /// <param name="agreed">The state both servers now hold.</param>
    public void Record(string? senderServerId, Guid userId, Guid itemId, string? senderUserId, string? senderItemId, PeerHistoryState agreed)
    {
        ArgumentNullException.ThrowIfNull(agreed);

        try
        {
            if (string.IsNullOrWhiteSpace(senderServerId))
            {
                return;
            }

            var peer = _configManager.Configuration.FindServerById(senderServerId);
            if (peer is null)
            {
                return;
            }

            var localItemId = itemId.ToString("N", CultureInfo.InvariantCulture);
            var row = _table.GetByLocalItem(peer.Key, localItemId).FirstOrDefault(r => Guid.TryParse(r.LocalUserId, out var u) && u == userId);
            if (row is null)
            {
                row = Build(peer, userId, itemId, localItemId, senderUserId, senderItemId);
                if (row is null)
                {
                    return;
                }
            }

            if (row.Status == SyncStatus.Ignored)
            {
                return;
            }

            var now = DateTime.UtcNow;
            PeerHistoryNegotiator.ApplySourceState(row, agreed);
            PeerHistoryNegotiator.ApplyLocalState(row, agreed);
            PeerHistoryNegotiator.ApplyMergedState(row, agreed);
            row.NegotiateWithSource = true;
            row.UpdateSourceStateBundle();
            row.RecordNegotiatedBase(now);
            row.MarkApplied(now);
            _table.Upsert(row);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the history agreement with {Sender} for user {User}, item {Item}", senderServerId, userId, itemId);
        }
    }

    private HistorySyncItem? Build(Models.Configuration.SourceServer peer, Guid userId, Guid itemId, string localItemId, string? senderUserId, string? senderItemId)
    {
        if (string.IsNullOrWhiteSpace(senderUserId) || string.IsNullOrWhiteSpace(senderItemId))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        var library = HintMapping.FindByLocalPath(peer, item.Path);
        var user = HintMapping.FindByLocalUser(peer, userId);
        if (library is null || user is null)
        {
            return null;
        }

        return new HistorySyncItem
        {
            SourceUserId = senderUserId,
            LocalUserId = user.LocalUserId,
            SourceLibraryId = library.SourceLibraryId,
            LocalLibraryId = library.LocalLibraryId ?? string.Empty,
            SourceItemId = senderItemId,
            LocalItemId = localItemId,
            ItemName = item.Name,
            SourcePath = PathUtilities.TranslatePath(item.Path, library.LocalRootPath, library.SourceRootPath),
            LocalPath = item.Path,
            ServerKey = peer.Key
        };
    }
}

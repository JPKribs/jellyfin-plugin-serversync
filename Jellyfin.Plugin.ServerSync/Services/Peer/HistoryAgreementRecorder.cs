using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// Keeps this server's history row for a peer in step with what the two servers agreed on, creating the row when it is missing.
/// Without it the next change the other way would merge against a stale base, and an older play could outrank a newer unmark.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class HistoryAgreementRecorder
{
    private readonly IPluginConfigurationManager _configManager;
    private readonly HistorySyncTableManager _table;
    private readonly ILibraryManager _libraryManager;
    private readonly ILinkRegistry _links;
    private readonly ILogger<HistoryAgreementRecorder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryAgreementRecorder"/> class.
    /// </summary>
    /// <param name="configManager">Finds the server entry for the sender.</param>
    /// <param name="table">Holds the history rows the agreement is written to.</param>
    /// <param name="libraryManager">Finds the local item when a row must be created.</param>
    /// <param name="links">Gives a row for a link the path its copy would have.</param>
    /// <param name="logger">Writes a warning when an agreement cannot be recorded.</param>
    public HistoryAgreementRecorder(
        IPluginConfigurationManager configManager,
        HistorySyncTableManager table,
        ILibraryManager libraryManager,
        ILinkRegistry links,
        ILogger<HistoryAgreementRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(logger);

        _configManager = configManager;
        _table = table;
        _libraryManager = libraryManager;
        _links = links;
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
        // The peer already holds the agreed state, so a failed record must not fail its request.
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the history agreement with {Sender} for user {User}, item {Item}", FormatUtilities.SanitizeForLog(senderServerId), userId, itemId);
        }
    }

    /// <summary>Builds a new row from the sender's ids and this server's mappings, or null when either is missing.</summary>
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
            SourcePath = LinkPaths.SourcePathFor(item.Path, library.LocalRootPath, library.SourceRootPath, _links),
            LocalPath = LinkPaths.RowPathFor(item.Path, _links),
            ServerKey = peer.Key
        };
    }
}

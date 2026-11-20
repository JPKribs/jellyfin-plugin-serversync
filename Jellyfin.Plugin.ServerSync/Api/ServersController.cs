using System;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// The operator's actions on a server entry beyond saving the settings.
/// Removing an entry removes the rows that carry its key, and never touches files already downloaded.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Sync/Servers")]
[Produces(MediaTypeNames.Application.Json)]
public class ServersController : ControllerBase
{
    private readonly ContentSyncTableManager _content;
    private readonly HistorySyncTableManager _history;
    private readonly MetadataSyncTableManager _metadata;
    private readonly PeopleSyncTableManager _people;
    private readonly UserSyncTableManager _users;
    private readonly OutboundHintStore _outbound;
    private readonly InboundHintStore _inbound;
    private readonly Services.Peer.PeerPairingStore _pairings;
    private readonly ILogger<ServersController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServersController"/> class.
    /// </summary>
    /// <param name="content">Removes the entry's Content rows and finds its links.</param>
    /// <param name="history">Removes the entry's History rows.</param>
    /// <param name="metadata">Removes the entry's Metadata rows.</param>
    /// <param name="people">Removes the entry's People rows.</param>
    /// <param name="users">Removes the entry's Users rows.</param>
    /// <param name="outbound">Removes the hints owed to the entry.</param>
    /// <param name="inbound">Removes the hints the entry sent.</param>
    /// <param name="pairings">Forgets the secrets shared with the entry.</param>
    /// <param name="logger">Writes log entries.</param>
    public ServersController(
        ContentSyncTableManager content,
        HistorySyncTableManager history,
        MetadataSyncTableManager metadata,
        PeopleSyncTableManager people,
        UserSyncTableManager users,
        OutboundHintStore outbound,
        InboundHintStore inbound,
        Services.Peer.PeerPairingStore pairings,
        ILogger<ServersController> logger)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(pairings);
        ArgumentNullException.ThrowIfNull(logger);

        _pairings = pairings;
        _inbound = inbound;
        _content = content;
        _history = history;
        _metadata = metadata;
        _people = people;
        _users = users;
        _outbound = outbound;
        _logger = logger;
    }

    /// <summary>Removes every sync row, queued hint, and link that came from one server entry. Its copies are real files and stay.</summary>
    /// <param name="key">The key of the removed server entry.</param>
    /// <param name="serverId">The removed server's Jellyfin ID, so the hints it sent go too.</param>
    /// <param name="libraryManager">Lists the library folders links are removed under, or null to leave links alone.</param>
    /// <returns>How many rows were removed from each table.</returns>
    [HttpDelete("{key}/Rows")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ServerRowsRemoved> ForgetRows([FromRoute] string key, [FromQuery] string? serverId, [FromServices] MediaBrowser.Controller.Library.ILibraryManager? libraryManager = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return BadRequest(BaseTranslations.Get(TranslationKeys.ErrorServerKeyRequired, BaseTranslations.RequestLanguage(Request)));
        }

        var links = 0;
        var roots = libraryManager?.GetVirtualFolders().SelectMany(folder => folder.Locations ?? System.Array.Empty<string>()).ToList() ?? new System.Collections.Generic.List<string>();
        foreach (var row in (_content.TryGetLinks() ?? new System.Collections.Generic.List<Models.ContentSync.SyncItem>())
            .Where(row => string.Equals(row.ServerKey, key, System.StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrEmpty(row.LocalPath) || !Services.Links.LinkFiles.IsOwnLink(row.LocalPath))
            {
                continue;
            }

            var root = roots.FirstOrDefault(location => row.LocalPath.StartsWith(location.TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase));
            if (root is not null && FileDeletionService.DeleteLinkWithFolders(row.LocalPath, _logger, root))
            {
                links++;
            }
        }

        if (links > 0)
        {
            _logger.LogInformation("Removed {Count} link(s) to the forgotten server {Key}", links, FormatUtilities.SanitizeForLog(key));
        }

        var removed = new ServerRowsRemoved
        {
            Content = _content.DeleteByServerKey(key),
            History = _history.DeleteByServerKey(key),
            Metadata = _metadata.DeleteByServerKey(key),
            People = _people.DeleteByServerKey(key),
            Users = _users.DeleteByServerKey(key),
            Hints = _outbound.DeleteForPeer(key) + (string.IsNullOrWhiteSpace(serverId) ? 0 : _inbound.DeleteForOrigin(serverId))
        };
        _pairings.Remove(key);
        _logger.LogInformation(
            "Forgot server {Key}: {Content} content, {History} history, {Metadata} metadata, {People} people, {Users} user row(s), {Hints} queued hint(s)",
            FormatUtilities.SanitizeForLog(key), removed.Content, removed.History, removed.Metadata, removed.People, removed.Users, removed.Hints);
        return Ok(removed);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Api;

/// <summary>
/// The operator routes of the dashboard, which act only on this server and never change a source.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Sync")]
[Produces(MediaTypeNames.Application.Json)]
public partial class ConfigurationController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISyncDatabaseProvider _databaseProvider;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly ILogger<ConfigurationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationController"/> class.
    /// </summary>
    /// <param name="libraryManager">Reads Jellyfin's library.</param>
    /// <param name="taskManager">Runs Jellyfin's scheduled tasks.</param>
    /// <param name="httpClientFactory">Builds the named HTTP clients that carry the SSRF guard.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="databaseProvider">Opens the sync database.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="logger">Writes log entries.</param>
    public ConfigurationController(
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        IHttpClientFactory httpClientFactory,
        IPluginConfigurationManager configManager,
        ISyncDatabaseProvider databaseProvider,
        ISourceServerClientFactory clientFactory,
        ILogger<ConfigurationController> logger)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(databaseProvider);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _httpClientFactory = httpClientFactory;
        _configManager = configManager;
        _databaseProvider = databaseProvider;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves an API Key posted from a settings page, where the kept sentinel stands for the stored key.
    /// </summary>
    private string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl)
        => _configManager.ResolveRequestApiKey(requestApiKey, serverKey, requestUrl);

    /// <summary>
    /// Finds the URL the browser loads images from for a row of the given server entry. A row with no key belongs to the first source.
    /// </summary>
    private string? BrowserUrlFor(string? serverKey)
        => _configManager.Configuration.ResolveServer(serverKey)?.BrowserUrl;

    /// <summary>
    /// Builds a client for the server a row came from, or null when that entry is gone or not set up. The caller disposes it.
    /// </summary>
    private SourceServerClient? ClientForRow(string? serverKey)
    {
        var server = _configManager.Configuration.ResolveServer(serverKey);
        if (server is null || !server.IsConfigured)
        {
            return null;
        }

        try
        {
            return _clientFactory.Create(server);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Server '{Server}' rejected: {Error}", FormatUtilities.SanitizeForLog(server.DisplayName), FormatUtilities.SanitizeForLog(ex.Message, 300));
            return null;
        }
    }

    /// <summary>
    /// Builds the result of a bulk status change, and logs a warning when some rows were not found.
    /// </summary>
    private BulkOperationResult BuildBulkResult(
        int updated,
        int requested,
        IReadOnlyList<long> notFoundIds,
        string operation)
    {
        if (notFoundIds.Count > 0)
        {
            _logger.LogWarning(
                "{Operation}: {Updated}/{Requested} rows updated. {NotFound} ID(s) not found in table",
                operation, updated, requested, notFoundIds.Count);
        }

        return new BulkOperationResult
        {
            Updated = updated,
            Requested = requested,
            Failed = notFoundIds
                .Select(id => new BulkOperationFailure { Id = id, Reason = "Item not found in sync table (already removed?)" })
                .ToList()
        };
    }

    /// <summary>
    /// Queues every row of a sync type that is in the posted status, for the retry actions.
    /// One statement reaches every matching row, not only the loaded page, and resets each row's retry count.
    /// </summary>
    private ActionResult QueueAllWithStatus<TRecord, TKey>(SyncTableManagerBase<TRecord, TKey> manager, string status, string operation)
        where TRecord : SyncRecord
        where TKey : notnull
    {
        if (!Enum.TryParse<SyncStatus>(status, ignoreCase: true, out var fromStatus) || !Enum.IsDefined(fromStatus))
        {
            return BadRequest("The status is not valid.");
        }

        try
        {
            var updated = manager.QueueAllWithStatus(fromStatus);
            _logger.LogInformation("{Operation}: queued {Count} row(s) that were {Status}", operation, updated, fromStatus);
            return Ok(new BulkOperationResult { Updated = updated, Requested = updated });
        }
        // Any failure becomes a 500 the page can show.
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation}: failed to queue rows that were {Status}", operation, fromStatus);
            return StatusCode(500, new { Error = "Could not queue the items. See the server log." });
        }
    }

    /// <summary>
    /// Fills a status response with the sync type's last run failure, when it has one.
    /// </summary>
    private void PopulateLastFailure(BaseSyncStatusResponse response, string moduleKey)
    {
        ArgumentNullException.ThrowIfNull(response);
        var failures = _configManager.Configuration.LastRunFailures;
        if (failures == null) return;

        var failure = failures
            .Where(f => string.Equals(f.ModuleKey, moduleKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Timestamp)
            .FirstOrDefault();
        if (failure != null)
        {
            response.LastFailurePhase = failure.Phase;
            response.LastFailureReason = failure.Reason;
            response.LastFailureTime = failure.Timestamp;
        }
    }

    /// <summary>
    /// Reports whether Server Sync can run here, which it cannot while its database is one an older release wrote.
    /// </summary>
    /// <returns>Ready, or the reason it is held.</returns>
    [HttpGet("Health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetHealth()
    {
        var held = Plugin.Instance?.HeldReason;
        return Ok(new { Ready = held is null, Message = held });
    }

    /// <summary>
    /// The scheduled tasks the Sync tab may start, by key.
    /// </summary>
    private static readonly HashSet<string> RunnableTasks = new(StringComparer.Ordinal)
    {
        "ServerSyncUpdateTables", "ServerSyncDownloadContent",
        "ServerSyncRefreshHistoryTable", "ServerSyncMissingHistory",
        "ServerSyncRefreshMetadataTable", "ServerSyncMissingMetadata",
        "ServerSyncRefreshPeopleTable", "ServerSyncMissingPeople",
        "ServerSyncRefreshUserTable", "ServerSyncMissingUserData"
    };

    /// <summary>
    /// Starts one of the plugin's scheduled tasks right away.
    /// </summary>
    /// <param name="taskKey">The scheduled task's key, such as <c>ServerSyncUpdateTables</c>.</param>
    /// <returns>A message that the task started, or 404 for an unknown task.</returns>
    [HttpPost("Tasks/{taskKey}/Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RunTask([FromRoute] string taskKey)
    {
        var task = RunnableTasks.Contains(taskKey ?? string.Empty)
            ? _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == taskKey)
            : null;
        if (task == null)
        {
            return NotFound("Task not found.");
        }

        _taskManager.Execute(task, new TaskOptions());
        return Ok(new { Message = "Task started." });
    }

    /// <summary>
    /// Empties one sync table, or with <c>All</c> deletes the whole sync database and creates it again.
    /// </summary>
    /// <param name="table">Content, History, Metadata, People, Playlists, Users, or All.</param>
    /// <returns>Whether the reset worked, or 404 for an unknown table.</returns>
    [HttpPost("Reset/{table}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Reset([FromRoute] string table)
    {
        var services = HttpContext.RequestServices;
        Func<int>? reset = (table ?? string.Empty).ToUpperInvariant() switch
        {
            "CONTENT" => () => services.GetRequiredService<ContentSyncTableManager>().ResetTable(),
            "HISTORY" => () => services.GetRequiredService<HistorySyncTableManager>().ResetTable(),
            "METADATA" => () => services.GetRequiredService<MetadataSyncTableManager>().ResetTable(),
            "PEOPLE" => () => services.GetRequiredService<PeopleSyncTableManager>().ResetTable(),
            "PLAYLISTS" => () => services.GetRequiredService<Services.Playlists.PlaylistCopyStore>().Clear(),
            "USERS" => () => services.GetRequiredService<UserSyncTableManager>().ResetTable(),
            "ALL" => () =>
            {
                _databaseProvider.Database.ResetDatabase();
                return 0;
            },
            _ => null
        };
        if (reset is null)
        {
            return NotFound("Unknown table.");
        }

        try
        {
            var deleted = reset();
            _logger.LogInformation("Sync reset of {Table} finished, {Count} rows deleted", SanitizeForLog(table), deleted);
            return Ok(new { Success = true, Message = "Table reset." });
        }
        // Any failure becomes a 500 the page can show.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset {Table}", SanitizeForLog(table));
            return StatusCode(500, new { Success = false, Error = "Could not reset the table. See the server log." });
        }
    }

    /// <summary>
    /// Makes text from a request safe to log.
    /// </summary>
    private static string SanitizeForLog(string? input) => FormatUtilities.SanitizeForLog(input, 100);

    /// <summary>
    /// Reduces several statuses to one, where Errored outranks Queued, then Pending, then Ignored, then Synced.
    /// </summary>
    private static string ComputeOverallStatus(params SyncStatus?[] statusValues)
    {
        var statuses = statusValues.OfType<SyncStatus>().ToList();

        if (statuses.Contains(SyncStatus.Errored))
        {
            return "Errored";
        }

        if (statuses.Contains(SyncStatus.Queued))
        {
            return "Queued";
        }

        if (statuses.Contains(SyncStatus.Pending))
        {
            return "Pending";
        }

        if (statuses.Count > 0 && statuses.All(s => s == SyncStatus.Ignored))
        {
            return "Ignored";
        }

        return "Synced";
    }
}

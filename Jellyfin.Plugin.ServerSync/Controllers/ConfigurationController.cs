using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// API controller for Server Sync plugin operations. Operates only on the
/// local server, never modifies the source server.
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
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="configManager">The plugin configuration manager.</param>
    /// <param name="databaseProvider">The sync database provider.</param>
    /// <param name="clientFactory">The source server client factory.</param>
    /// <param name="logger">The logger.</param>
    public ConfigurationController(
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        IHttpClientFactory httpClientFactory,
        IPluginConfigurationManager configManager,
        ISyncDatabaseProvider databaseProvider,
        ISourceServerClientFactory clientFactory,
        ILogger<ConfigurationController> logger)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _httpClientFactory = httpClientFactory;
        _configManager = configManager;
        _databaseProvider = databaseProvider;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves an API key posted from a settings page. The page shows the
    /// kept-sentinel instead of the stored secret, so a request carrying the
    /// sentinel means "use the configured key".
    /// </summary>
    private string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl)
        => _configManager.ResolveRequestApiKey(requestApiKey, serverKey, requestUrl);

    /// <summary>
    /// The URL the browser should load images from for a row that came from the given server entry.
    /// A row with no key belongs to the first scan server.
    /// </summary>
    private string? BrowserUrlFor(string? serverKey)
        => _configManager.Configuration.ResolveServer(serverKey)?.BrowserUrl;

    /// <summary>
    /// A connected client for the server a row came from, or null when that entry is gone or not
    /// configured. Callers dispose it.
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
            _logger.LogWarning("Server '{Server}' rejected: {Error}", server.DisplayName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Builds a <see cref="BulkOperationResult"/> from a manager's
    /// <c>BulkUpdateStatusWithDetails</c> tuple. Logs at Warning if any
    /// items were not found so log readers can correlate UI alerts to
    /// server-side details.
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
    /// Queues every row of a module that is in the posted status, for the "Retry errors" actions,
    /// which post an empty id list and a status. The update is one statement with no row cap, so it
    /// reaches every matching row and not only those on the loaded page, and the Queued transition
    /// resets each row's RetryCount.
    /// </summary>
    private ActionResult QueueAllWithStatus<TRecord, TKey>(SyncTableManagerBase<TRecord, TKey> manager, string status, string operation)
        where TRecord : SyncRecord
        where TKey : notnull
    {
        if (!Enum.TryParse<SyncStatus>(status, ignoreCase: true, out var fromStatus) || !Enum.IsDefined(fromStatus))
        {
            return BadRequest("Invalid status value");
        }

        try
        {
            var updated = manager.QueueAllWithStatus(fromStatus);
            _logger.LogInformation("{Operation}: queued {Count} row(s) that were {Status}", operation, updated, fromStatus);
            return Ok(new BulkOperationResult { Updated = updated, Requested = updated });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation}: failed to queue rows that were {Status}", operation, fromStatus);
            return StatusCode(500, new { Error = "Bulk queue failed, see the server log" });
        }
    }

    /// <summary>
    /// Populates the LastFailure* fields on a status response from the
    /// per-module run-failure list on the plugin config. No-op when the
    /// module's last run completed cleanly (no entry for this module).
    /// </summary>
    private void PopulateLastFailure(BaseSyncStatusResponse response, string moduleKey)
    {
        ArgumentNullException.ThrowIfNull(response);
        var failures = _configManager.Configuration.LastRunFailures;
        if (failures == null) return;

        // A module can hold a Refresh and a Sync failure at once. The latest is the one worth showing.
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
    /// Whether Server Sync can run here. It cannot while its database is one an older release wrote,
    /// and every page shows the reason.
    /// </summary>
    /// <returns>Ready, or the reason it is held.</returns>
    [HttpGet("Health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetHealth()
    {
        var held = Plugin.Instance?.HeldReason;
        return Ok(new { Ready = held is null, Message = held });
    }

    // The scheduled tasks the Sync tab may start, by key.
    private static readonly HashSet<string> RunnableTasks = new(StringComparer.Ordinal)
    {
        "ServerSyncUpdateTables", "ServerSyncDownloadContent",
        "ServerSyncRefreshHistoryTable", "ServerSyncMissingHistory",
        "ServerSyncRefreshMetadataTable", "ServerSyncMissingMetadata",
        "ServerSyncRefreshPeopleTable", "ServerSyncMissingPeople",
        "ServerSyncRefreshUserTable", "ServerSyncMissingUserData"
    };

    /// <summary>
    /// Starts one of the plugin's refresh or sync tasks now.
    /// </summary>
    /// <param name="taskKey">The scheduled task's key, such as <c>ServerSyncUpdateTables</c>.</param>
    /// <returns>Action result with status message.</returns>
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
            return NotFound("Task not found");
        }

        _taskManager.Execute(task, new TaskOptions());
        return Ok(new { Message = "Task started" });
    }

    /// <summary>
    /// Empties one sync table, or with <c>All</c> deletes the whole sync database and creates it again.
    /// </summary>
    /// <param name="table">Content, History, Metadata, People, Playlists, Users, or All.</param>
    /// <returns>Action result with success status.</returns>
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
            return NotFound("Unknown table");
        }

        try
        {
            var deleted = reset();
            _logger.LogInformation("Sync reset of {Table} finished, {Count} rows deleted", SanitizeForLog(table), deleted);
            return Ok(new { Success = true, Message = "Reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset {Table}", SanitizeForLog(table));
            return StatusCode(500, new { Success = false, Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Sanitizes user input to prevent log injection attacks.
    /// </summary>
    /// <param name="input">User input to sanitize.</param>
    /// <returns>Sanitized string safe for logging.</returns>
    private static string SanitizeForLog(string? input) => FormatUtilities.SanitizeForLog(input, 100);

    /// <summary>
    /// Computes the overall sync status from a set of individual status values using priority ordering:
    /// Errored > Queued > Pending > Ignored > Synced.
    /// </summary>
    /// <param name="statusValues">Individual sync status values to aggregate.</param>
    /// <returns>The computed overall status string.</returns>
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

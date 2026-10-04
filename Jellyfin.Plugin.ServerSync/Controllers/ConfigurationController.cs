using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Controllers;

/// <summary>
/// API controller for Server Sync plugin operations. Operates only on the
/// local server, never modifies the source server.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ServerSync")]
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
    /// Looks up a scheduled task by its <see cref="IScheduledTask.Key"/>
    /// and triggers it. Returns 200 OK with the supplied success message
    /// or 404 with <paramref name="notFoundMessage"/> if the key isn't
    /// registered. Centralizes the "trigger X task" pattern that every
    /// per-module controller exposes.
    /// </summary>
    private ActionResult ExecuteScheduledTaskByKey(string taskKey, string successMessage, string notFoundMessage)
    {
        var task = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask.Key == taskKey);

        if (task == null)
        {
            return NotFound(notFoundMessage);
        }

        _taskManager.Execute(task, new TaskOptions());
        return Ok(new { Message = successMessage });
    }

    /// <summary>
    /// Sanitizes user input to prevent log injection attacks.
    /// </summary>
    /// <param name="input">User input to sanitize.</param>
    /// <returns>Sanitized string safe for logging.</returns>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "[empty]";
        }

        // Remove control characters (newlines, tabs, etc.) that could forge log entries
        var sanitized = new string(input.Where(c => !char.IsControl(c)).ToArray());

        // Truncate to prevent log flooding
        const int maxLength = 100;
        if (sanitized.Length > maxLength)
        {
            sanitized = string.Concat(sanitized.AsSpan(0, maxLength), "...");
        }

        return sanitized;
    }

    /// <summary>
    /// Computes the overall sync status from a set of individual status values using priority ordering:
    /// Errored > Queued > Pending > Ignored > Synced.
    /// </summary>
    /// <param name="statusValues">Individual sync status values to aggregate.</param>
    /// <returns>The computed overall status string.</returns>
    private static string ComputeOverallStatus(params SyncStatus?[] statusValues)
    {
        var statuses = statusValues
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToList();

        if (statuses.Any(s => s == SyncStatus.Errored))
        {
            return "Errored";
        }

        if (statuses.Any(s => s == SyncStatus.Queued))
        {
            return "Queued";
        }

        if (statuses.Any(s => s == SyncStatus.Pending))
        {
            return "Pending";
        }

        if (statuses.Count > 0 && statuses.All(s => s == SyncStatus.Ignored))
        {
            return "Ignored";
        }

        return "Synced";
    }

    /// <summary>
    /// Returns the plugin capabilities including whether deletion is supported.
    /// </summary>
    /// <returns>Capabilities response.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CapabilitiesResponse> GetCapabilities()
    {
        // The library manager is injected and never null, so deletion is always available.
        return Ok(new CapabilitiesResponse
        {
            CanDeleteItems = true,
            SupportsCompanionFiles = true,
            SupportsBandwidthScheduling = true
        });
    }

    /// <summary>
    /// Returns metadata for all status types including display names and colors.
    /// Used by the frontend to render status badges consistently.
    /// </summary>
    /// <returns>Dictionary of status metadata.</returns>
    [HttpGet("StatusMetadata")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, StatusMetadata>> GetStatusMetadata()
    {
        return Ok(StatusAppearanceHelper.GetStatusMetadata());
    }

    /// <summary>
    /// Attempts to find and store the local Jellyfin item IDs for synced items.
    /// </summary>
    /// <returns>Action result with resolved count.</returns>
    [HttpPost("ResolveLocalItemIds")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResolveLocalItemIds([FromServices] ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        try
        {
            var syncedItems = manager.GetByStatus(SyncStatus.Synced);
            var resolvedCount = 0;
            var alreadyResolvedCount = 0;

            foreach (var item in syncedItems)
            {
                // Skip if already has LocalItemId
                if (!string.IsNullOrEmpty(item.LocalItemId))
                {
                    alreadyResolvedCount++;
                    continue;
                }

                if (string.IsNullOrEmpty(item.LocalPath))
                {
                    continue;
                }

                var sanitizedFileName = SanitizeForLog(Path.GetFileName(item.LocalPath));
                try
                {
                    // Try to find the item in Jellyfin by path
                    var localItem = _libraryManager.FindByPath(item.LocalPath, isFolder: false);
                    if (localItem != null)
                    {
                        // Only the id changes. A status update would stamp LastSyncTime with now and
                        // make every resolved row look freshly synced.
                        manager.UpdateLocalItemId(item.SourceItemId, localItem.Id.ToString());
                        resolvedCount++;
                        _logger.LogDebug("Resolved LocalItemId for {FileName}", sanitizedFileName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve LocalItemId for {FileName}", sanitizedFileName);
                }
            }

            _logger.LogInformation("Resolved {Count} local item IDs, {AlreadyResolved} already resolved", resolvedCount, alreadyResolvedCount);
            return Ok(new { Resolved = resolvedCount, AlreadyResolved = alreadyResolvedCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve local item IDs");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Deletes all items from the sync database and recreates it with the latest schema.
    /// </summary>
    /// <returns>Action result with success status.</returns>
    [HttpPost("ResetSyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetSyncDatabase()
    {
        try
        {
            _databaseProvider.Database.ResetDatabase();
            _logger.LogInformation("Sync database has been reset");
            return Ok(new { Success = true, Message = "Database reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset sync database");
            return StatusCode(500, new { Success = false, Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Resets the content sync table only, removing all tracked content items.
    /// Other tables (History, Metadata, User) are not affected.
    /// </summary>
    /// <returns>Action result with success status.</returns>
    [HttpPost("ResetContentSyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetContentSyncDatabase([FromServices] ContentSyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        try
        {
            var deleted = manager.ResetTable();
            _logger.LogInformation("Content sync table has been reset, {Count} rows deleted", deleted);
            return Ok(new { Success = true, Message = "Content sync table reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset content sync table");
            return StatusCode(500, new { Success = false, Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Resets the history sync database, removing all tracked history items.
    /// </summary>
    /// <returns>Action result with success status.</returns>
    [HttpPost("ResetHistorySyncDatabase")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetHistorySyncDatabase([FromServices] HistorySyncTableManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        try
        {
            var deleted = manager.ResetTable();
            _logger.LogInformation("History sync database has been reset, {Count} rows deleted", deleted);
            return Ok(new { Success = true, Message = "History database reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset history sync database");
            return StatusCode(500, new { Success = false, Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Validates the current plugin configuration.
    /// </summary>
    /// <returns>Validation response with errors.</returns>
    [HttpGet("ValidateConfiguration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ConfigurationValidationResponse> ValidateConfiguration()
    {
        var errors = _configManager.Configuration.ValidateConfiguration();

        return Ok(new ConfigurationValidationResponse
        {
            IsValid = errors.Count == 0,
            Errors = errors
        });
    }

    /// <summary>
    /// Sanitizes configuration values to valid ranges.
    /// </summary>
    /// <returns>Action result with status message.</returns>
    [HttpPost("SanitizeConfiguration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult SanitizeConfiguration()
    {
        _configManager.Configuration.SanitizeValues();
        _configManager.SaveConfiguration();

        return Ok(new { Message = "Configuration sanitized" });
    }
}

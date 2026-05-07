using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
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
/// API controller for Server Sync plugin operations.
/// NOTE: This controller ONLY operates on the LOCAL server. It NEVER modifies the source server.
/// All delete operations use the local Jellyfin API to delete items from this server only.
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
                "{Operation}: {Updated}/{Requested} rows updated; {NotFound} ID(s) not found in table",
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
    /// Populates the LastFailure* fields on a status response from the
    /// per-module run-failure list on the plugin config. No-op when the
    /// module's last run completed cleanly (no entry for this module).
    /// </summary>
    private void PopulateLastFailure(BaseSyncStatusResponse response, string moduleKey)
    {
        ArgumentNullException.ThrowIfNull(response);
        var failures = _configManager.Configuration.LastRunFailures;
        if (failures == null) return;

        var failure = failures.FirstOrDefault(f => string.Equals(f.ModuleKey, moduleKey, StringComparison.OrdinalIgnoreCase));
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
    /// SanitizeForLog
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
    /// GetCapabilities
    /// Returns the plugin capabilities including whether deletion is supported.
    /// </summary>
    /// <returns>Capabilities response.</returns>
    [HttpGet("Capabilities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CapabilitiesResponse> GetCapabilities()
    {
        try
        {
            // Deletion is supported if we have access to the library manager
            // which is injected via DI - if we got here, we have it
            var canDelete = _libraryManager != null;

            return Ok(new CapabilitiesResponse
            {
                CanDeleteItems = canDelete,
                SupportsCompanionFiles = true,
                SupportsBandwidthScheduling = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error retrieving capabilities, returning safe defaults");
            return Ok(new CapabilitiesResponse
            {
                CanDeleteItems = false,
                SupportsCompanionFiles = true,
                SupportsBandwidthScheduling = true
            });
        }
    }

    /// <summary>
    /// GetStatusMetadata
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
    /// ResolveLocalItemIds
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
                        manager.UpdateStatus(
                            item.SourceItemId,
                            item.Status,
                            localPath: item.LocalPath,
                            localItemId: localItem.Id.ToString());
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
    /// ResetSyncDatabase
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
    /// ResetContentSyncDatabase
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
    /// ResetHistorySyncDatabase
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
    /// ValidateConfiguration
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
    /// SanitizeConfiguration
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

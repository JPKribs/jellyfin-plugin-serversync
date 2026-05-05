using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to update sync tables from the source server.
/// </summary>
public class UpdateSyncTablesTask : IScheduledTask
{
    private readonly ILogger<UpdateSyncTablesTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISyncDatabaseProvider _databaseProvider;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly SyncTableService _syncTableService;

    public UpdateSyncTablesTask(
        ILogger<UpdateSyncTablesTask> logger,
        IPluginConfigurationManager configManager,
        ISyncDatabaseProvider databaseProvider,
        ISourceServerClientFactory clientFactory,
        SyncTableService syncTableService)
    {
        _logger = logger;
        _configManager = configManager;
        _databaseProvider = databaseProvider;
        _clientFactory = clientFactory;
        _syncTableService = syncTableService;
    }

    public string Name => "Refresh Content Sync Table";

    public string Key => "ServerSyncUpdateTables";

    public string Description => "Fetches item list from source server and updates the sync tracking table.";

    public string Category => "Content Sync";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        if (!config.EnableContentSync)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerUrl))
        {
            _logger.LogWarning("Sync table update skipped: source server URL not configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("Sync table update skipped: API key not configured");
            return;
        }

        var enabledMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();
        if (enabledMappings.Count == 0)
        {
            _logger.LogDebug("Sync table update skipped: no enabled library mappings");
            return;
        }

        _logger.LogInformation("Starting sync table update from {SourceUrl}", config.SourceServerUrl);

        using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);

        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server at {SourceUrl}: {Error}",
                config.SourceServerUrl, connectionResult.ErrorMessage ?? "Unknown error");
            return;
        }

        var database = _databaseProvider.Database;

        // Progress tracking: 1% for init, 98% for processing items, 1% for finalization
        const double InitProgress = 1.0;
        const double ProcessingProgress = 98.0;

        progress.Report(0);

        // Get total item counts for all libraries to enable granular progress
        var totalItems = 0;

        foreach (var mapping in enabledMappings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!Guid.TryParse(mapping.SourceLibraryId, out var sourceLibraryGuid))
            {
                _logger.LogWarning("Invalid source library ID '{LibraryId}' in mapping for {LibraryName}, skipping",
                    mapping.SourceLibraryId, mapping.SourceLibraryName);
                continue;
            }

            var count = await client.GetLibraryItemCountAsync(
                sourceLibraryGuid,
                cancellationToken).ConfigureAwait(false);

            totalItems += count;
        }

        progress.Report(InitProgress);

        // Process each library with item-level progress tracking
        var processedItems = 0;

        foreach (var mapping in enabledMappings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await _syncTableService.ProcessLibraryAsync(
                client,
                database,
                mapping,
                config.DownloadNewContentMode,
                config.ReplaceExistingContentMode,
                config.DeleteMissingContentMode,
                config.DetectUpdatedFiles,
                config.ChangeDetectionPolicy,
                config.SizeMatchToleranceBytes,
                config.SkipWatchedByAllUsers,
                config.WatchedFilterUserIds,
                cancellationToken,
                onItemProcessed: () =>
                {
                    processedItems++;
                    if (totalItems > 0)
                    {
                        var itemProgress = (double)processedItems / totalItems * ProcessingProgress;
                        progress.Report(InitProgress + itemProgress);
                    }
                }).ConfigureAwait(false);
        }

        progress.Report(InitProgress + ProcessingProgress);

        // Resolve LocalItemIds for synced items that don't have them yet
        _syncTableService.ResolveLocalItemIds(database);

        progress.Report(100);
        _logger.LogInformation("Sync table update completed");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(10).Ticks
            }
        };
    }
}

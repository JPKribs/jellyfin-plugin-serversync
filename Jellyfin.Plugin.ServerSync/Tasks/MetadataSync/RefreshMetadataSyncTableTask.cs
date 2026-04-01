using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to refresh the metadata sync table from source and local servers.
/// </summary>
public class RefreshMetadataSyncTableTask : IScheduledTask
{
    private readonly ILogger<RefreshMetadataSyncTableTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISyncDatabaseProvider _databaseProvider;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly MetadataSyncTableService _metadataService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshMetadataSyncTableTask"/> class.
    /// </summary>
    public RefreshMetadataSyncTableTask(
        ILogger<RefreshMetadataSyncTableTask> logger,
        IPluginConfigurationManager configManager,
        ISyncDatabaseProvider databaseProvider,
        ISourceServerClientFactory clientFactory,
        MetadataSyncTableService metadataService)
    {
        _logger = logger;
        _configManager = configManager;
        _databaseProvider = databaseProvider;
        _clientFactory = clientFactory;
        _metadataService = metadataService;
    }

    /// <inheritdoc />
    public string Name => "Refresh Metadata Sync Table";

    /// <inheritdoc />
    public string Key => "ServerSyncRefreshMetadataTable";

    /// <inheritdoc />
    public string Description => "Scans source and local servers for metadata differences and updates the metadata sync table.";

    /// <inheritdoc />
    public string Category => "Metadata Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        // Check if metadata sync is enabled
        if (!config.EnableMetadataSync)
        {
            return;
        }

        // Validate source server configuration
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl))
        {
            _logger.LogWarning("Metadata sync skipped: source server URL not configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("Metadata sync skipped: API key not configured");
            return;
        }

        // Get enabled library mappings
        var enabledLibraryMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

        if (enabledLibraryMappings.Count == 0)
        {
            _logger.LogDebug("Metadata sync skipped: no enabled library mappings");
            return;
        }

        // Get enabled category flags
        var syncMetadata = config.MetadataSyncMetadata;
        var syncImages = config.MetadataSyncImages;
        var syncPeople = config.MetadataSyncPeople;
        var syncStudios = config.MetadataSyncStudios;
        var syncFolderItems = config.MetadataSyncFolderItems;
        var refreshMode = config.MetadataRefreshMode;

        if (!syncMetadata && !syncImages && !syncPeople && !syncStudios)
        {
            _logger.LogDebug("Metadata sync skipped: no categories enabled");
            return;
        }

        _logger.LogInformation("Starting metadata sync table refresh from {SourceUrl}", config.SourceServerUrl);

        using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);

        // Test connection
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

        // Get total item count for progress tracking
        var totalItems = await _metadataService.GetTotalItemCountAsync(
            client,
            enabledLibraryMappings,
            cancellationToken,
            includeFolderItems: syncFolderItems).ConfigureAwait(false);

        progress.Report(InitProgress);

        // Process each library mapping
        var processedItems = 0;

        foreach (var libraryMapping in enabledLibraryMappings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await _metadataService.ProcessLibraryAsync(
                client,
                database,
                libraryMapping,
                syncMetadata,
                syncImages,
                syncPeople,
                syncStudios,
                refreshMode,
                cancellationToken,
                onItemProcessed: () =>
                {
                    processedItems++;
                    if (totalItems > 0)
                    {
                        var itemProgress = (double)processedItems / totalItems * ProcessingProgress;
                        progress.Report(InitProgress + itemProgress);
                    }
                },
                syncFolderItems: syncFolderItems).ConfigureAwait(false);
        }

        progress.Report(100);

        // Update last sync time
        config.LastMetadataSyncTime = DateTime.UtcNow;
        _configManager.SaveConfiguration();

        _logger.LogInformation("Metadata sync table refresh completed");
    }

    /// <inheritdoc />
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

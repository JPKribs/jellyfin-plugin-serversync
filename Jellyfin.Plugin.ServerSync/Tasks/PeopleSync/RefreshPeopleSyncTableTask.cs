using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to refresh the people sync table from source and local servers.
/// </summary>
public class RefreshPeopleSyncTableTask : IScheduledTask
{
    private readonly ILogger<RefreshPeopleSyncTableTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISyncDatabaseProvider _databaseProvider;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly PeopleSyncTableService _peopleService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshPeopleSyncTableTask"/> class.
    /// </summary>
    public RefreshPeopleSyncTableTask(
        ILogger<RefreshPeopleSyncTableTask> logger,
        IPluginConfigurationManager configManager,
        ISyncDatabaseProvider databaseProvider,
        ISourceServerClientFactory clientFactory,
        PeopleSyncTableService peopleService)
    {
        _logger = logger;
        _configManager = configManager;
        _databaseProvider = databaseProvider;
        _clientFactory = clientFactory;
        _peopleService = peopleService;
    }

    /// <inheritdoc />
    public string Name => "Refresh People Sync Table";

    /// <inheritdoc />
    public string Key => "ServerSyncRefreshPeopleTable";

    /// <inheritdoc />
    public string Description => "Scans source and local servers for people (person entity) differences and updates the people sync table.";

    /// <inheritdoc />
    public string Category => "People Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        if (!config.EnablePeopleSync)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerUrl))
        {
            _logger.LogWarning("People sync skipped: source server URL not configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("People sync skipped: API key not configured");
            return;
        }

        var syncImages = config.PeopleSyncImages;

        _logger.LogInformation("Starting people sync table refresh from {SourceUrl}", config.SourceServerUrl);

        using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);

        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server at {SourceUrl}: {Error}",
                config.SourceServerUrl, connectionResult.ErrorMessage ?? "Unknown error");
            return;
        }

        var database = _databaseProvider.Database;

        const double InitProgress = 1.0;
        const double ProcessingProgress = 98.0;

        progress.Report(0);

        var totalItems = await _peopleService.GetTotalPersonCountAsync(client, cancellationToken).ConfigureAwait(false);

        progress.Report(InitProgress);

        var processedItems = 0;

        await _peopleService.RefreshPeopleSyncTableAsync(
            client,
            database,
            syncImages,
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

        progress.Report(100);

        config.LastPeopleSyncTime = DateTime.UtcNow;
        _configManager.SaveConfiguration();

        _logger.LogInformation("People sync table refresh completed");
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(12).Ticks
            }
        };
    }
}

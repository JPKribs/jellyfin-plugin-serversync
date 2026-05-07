using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Refresh phase for People sync. Fetches all source persons, looks up each
/// by name on the local server, and writes a snapshot row for the matched
/// ones. Persons without a local match are skipped (no row written).
/// </summary>
public class RefreshPeopleSyncTableTask : RefreshSyncTaskBase<PeopleSyncItem, BaseItemDto, string>
{
    private readonly ILibraryManager _libraryManager;
    private readonly PeopleSyncTableService _peopleService;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public RefreshPeopleSyncTableTask(
        ILogger<RefreshPeopleSyncTableTask> logger,
        ILibraryManager libraryManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        PeopleSyncTableService peopleService,
        PeopleSyncTableManager manager)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _peopleService = peopleService;
    }

    /// <inheritdoc />
    public override string Name => "Refresh People Sync Table";

    /// <inheritdoc />
    public override string Key => "ServerSyncRefreshPeopleTable";

    /// <inheritdoc />
    public override string Description => "Scans source persons, matches against local persons, and stores snapshots in the people sync table.";

    /// <inheritdoc />
    public override string Category => "People Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "People";

    /// <inheritdoc />
    /// <remarks>
    /// People's <c>BuildRecordAsync</c> issues a per-person
    /// <c>GetItemImageInfoAsync</c> HTTP call for every locally-matched
    /// person when image sync is enabled. Most persons get filtered earlier
    /// (no local match), but on a library with many matched persons this
    /// adds up; parallelism keeps the build phase from being HTTP-bound.
    /// </remarks>
    protected override int BuildRecordParallelism => 8;

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        return config.EnablePeopleSync
            && !string.IsNullOrWhiteSpace(config.SourceServerUrl)
            && !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }

    /// <inheritdoc />
    protected override async Task<IList<BaseItemDto>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Client == null)
        {
            return Array.Empty<BaseItemDto>();
        }

        // /Persons is a single non-paginated call — report midway during
        // the fetch and 100 when complete so the UI moves rather than
        // sitting still.
        progress.Report(20);
        var result = await Client.GetAllPersonsAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
        return result;
    }

    /// <inheritdoc />
    protected override Task<PeopleSyncItem?> BuildRecordAsync(
        BaseItemDto source,
        IReadOnlyDictionary<string, PeopleSyncItem> existing,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (string.IsNullOrEmpty(source.Name))
        {
            return Task.FromResult<PeopleSyncItem?>(null);
        }

        // Look up local person — skip entirely if no local match.
        var personStub = _libraryManager.GetPerson(source.Name);
        if (personStub == null)
        {
            return Task.FromResult<PeopleSyncItem?>(null);
        }

        var localPerson = _libraryManager.GetItemById(personStub.Id);
        if (localPerson == null)
        {
            return Task.FromResult<PeopleSyncItem?>(null);
        }

        // Reuse existing record (preserves Status if Ignored) or create new.
        var record = existing.TryGetValue(source.Name, out var prev)
            ? prev
            : new PeopleSyncItem { PersonName = source.Name };

        record.SourcePersonId = source.Id?.ToString("N", CultureInfo.InvariantCulture);
        record.LocalPersonId = localPerson.Id.ToString("N", CultureInfo.InvariantCulture);

        // Build metadata blobs for both sides; SyncableValue.RecomputeSourceHash
        // ensures the SourceHash field is populated for the Compare fast-path.
        record.Metadata.Source = PeopleSyncTableService.BuildSourceMetadata(source);
        record.Metadata.Local = PeopleSyncTableService.BuildLocalMetadata(localPerson);
        record.Metadata.RecomputeSourceHash();

        // Image manifests — image data comes from the bulk /Persons response
        // (no per-person HTTP call). UpdateSource produces a comparator-built
        // hash so the SourceHash short-circuit works correctly across
        // refreshes.
        var config = ConfigManager.Configuration;
        if (config.PeopleSyncImages)
        {
            var (sourceImg, localImg) = _peopleService.PopulateImageData(source, localPerson);
            record.Images.UpdateSource(sourceImg);
            record.Images.Local = localImg;
        }
        else
        {
            // Clear image fields if image sync is disabled — the Compare phase
            // then sees no Image-side changes regardless of what was stored.
            record.Images.Source = null;
            record.Images.Local = null;
            record.Images.SourceHash = null;
        }

        return Task.FromResult<PeopleSyncItem?>(record);
    }

    /// <inheritdoc />
    protected override string ExtractKey(PeopleSyncItem record) => record.PersonName;

    /// <inheritdoc />
    protected override Task FinalizeAsync(CancellationToken cancellationToken)
    {
        ConfigManager.Configuration.LastPeopleSyncTime = DateTime.UtcNow;
        ConfigManager.SaveConfiguration();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = MediaBrowser.Model.Tasks.TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks
        }
    };
}

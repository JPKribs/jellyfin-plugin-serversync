using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using SdkBaseItemDto = Jellyfin.Sdk.Generated.Models.BaseItemDto;

namespace Jellyfin.Plugin.ServerSync.Tasks.PeopleSync;

/// <summary>
/// Scans People: fetches every person a source holds, matches each by name on this server, and stores a snapshot row for the ones that match.
/// A person with no local match gets no row.
/// </summary>
public class RefreshPeopleSyncTableTask : RefreshSyncTaskBase<PeopleSyncItem, PersonWork, string>
{
    private readonly ILibraryManager _libraryManager;
    private readonly ScanConflictSettler _conflicts;

    /// <summary>
    /// The local Person items by name, for one run.
    /// <see cref="GetListAsync"/> builds it and <see cref="BuildRecordAsync"/> reads it, so the parallel build does not query Jellyfin's library database for every item and lock it.
    /// </summary>
    private Dictionary<string, BaseItem>? _localPersonsByName;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshPeopleSyncTableTask"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="libraryManager">Reads and changes Jellyfin's library.</param>
    /// <param name="clientFactory">Builds the client for each source.</param>
    /// <param name="configManager">Reads and saves the plugin settings.</param>
    /// <param name="manager">Reads and writes the sync table.</param>
    /// <param name="conflicts">Settles which server speaks for an item both sides changed.</param>
    /// <param name="statistics">Keeps what each scan found for the Statistics tab, or null to keep nothing.</param>
    public RefreshPeopleSyncTableTask(
        ILogger<RefreshPeopleSyncTableTask> logger,
        ILibraryManager libraryManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        PeopleSyncTableManager manager,
        ScanConflictSettler conflicts,
        Services.Database.SyncStatisticsStore? statistics = null)
        : base(logger, manager, clientFactory, configManager, statistics: statistics)
    {
        _libraryManager = libraryManager;
        _conflicts = conflicts;
    }

    /// <summary>
    /// Readies the cache for a single person from a hint, holding just that one local person, found by name.
    /// </summary>
    /// <inheritdoc />
    protected override void PrepareForOne(PersonWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // A query is used because GetPerson creates a person that does not exist yet.
        var byName = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(work.Person.Name))
        {
            var found = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Person },
                Name = work.Person.Name,
                Limit = 1
            });
            if (found is { Count: > 0 } && string.Equals(found[0].Name, work.Person.Name, StringComparison.OrdinalIgnoreCase))
            {
                byName[work.Person.Name] = found[0];
            }
        }

        _localPersonsByName = byName;
    }

    /// <inheritdoc />
    protected override Task ResolveConflictsAsync(IList<PeopleSyncItem> queued, CancellationToken cancellationToken)
        => _conflicts.SettleAsync(
            queued,
            Sources,
            Models.Queue.HintKind.People,
            record => HintProtocol.PeopleKey(record.PersonName),
            record => HintProtocol.PeopleKey(record.PersonName),
            record => (record.LocalPersonId, record.PersonName),
            record => Manager.Upsert(record),
            Logger,
            cancellationToken);

    private HashSet<string> _versionedServers = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override async Task PrepareContestAsync(CancellationToken cancellationToken)
        => _versionedServers = ConfigManager.Configuration.PeopleSyncPrecedence == Models.Configuration.SyncPrecedence.Rank
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await _conflicts.ServersWithVersionsAsync(Sources, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Among the servers offering one person, the one that holds the newest recorded edit speaks.
    /// </summary>
    /// <inheritdoc />
    protected override Task<IReadOnlyDictionary<string, PersonWork>> ContestAsync(IReadOnlyDictionary<string, IReadOnlyList<PersonWork>> contested, CancellationToken cancellationToken)
        => _conflicts.NewestOffersAsync(
            contested,
            Models.Queue.HintKind.People,
            _versionedServers,
            work => work.Source,
            work => string.IsNullOrEmpty(work.Person.Name) ? null : HintProtocol.PeopleKey(work.Person.Name),
            cancellationToken);

    /// <inheritdoc />
    protected override bool MayOutrankOwner(PeopleSyncItem record)
        => record?.ServerKey is { } key && _versionedServers.Contains(key);

    /// <inheritdoc />
    public override string Name => "Scan People";

    /// <inheritdoc />
    public override string Key => "ServerSyncRefreshPeopleTable";

    /// <inheritdoc />
    public override string Description => "Scans source persons, matches against local persons, and stores snapshots in the people sync table.";

    /// <inheritdoc />
    public override string Category => "People Sync";

    /// <inheritdoc />
    protected override string ModuleMutexKey => "People";

    /// <summary>
    /// Gets how many rows are built at once, a setting shared with the Metadata scan.
    /// The build makes no HTTP calls in steady state, so this is in effect how much CPU a scan may use.
    /// </summary>
    /// <inheritdoc />
    protected override int BuildRecordParallelism => Math.Clamp(ConfigManager.Configuration.RefreshParallelism, 1, 16);

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        return config.EnablePeopleSync && config.GetPullServers().Count > 0;
    }

    /// <summary>
    /// Clears the local person cache, which the first server's listing then builds once for the run.
    /// </summary>
    /// <inheritdoc />
    protected override void OnRunStarting()
    {
        _localPersonsByName = null;
    }

    /// <summary>
    /// Lists the local Person items, reads the source's whole person catalog a page at a time, and matches the two by name in memory.
    /// </summary>
    /// <inheritdoc />
    protected override async Task<IList<PersonWork>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);

        progress.Report(2);

        if (_localPersonsByName == null)
        {
            var localPersons = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Person }
            }) ?? new List<BaseItem>();

            var byName = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in localPersons)
            {
                if (!string.IsNullOrEmpty(p.Name) && !byName.ContainsKey(p.Name))
                {
                    byName[p.Name] = p;
                }
            }

            _localPersonsByName = byName;
            Logger.LogInformation(
                "{Task}: enumerated {Count} local persons. Bulk-fetching source persons",
                Name,
                _localPersonsByName.Count);
        }

        progress.Report(35);

        if (_localPersonsByName.Count == 0)
        {
            // Jellyfin empties the people table during a rescan, so this is not a removal.
            if (Manager.Count() > 0)
            {
                MarkSourceUnavailable(
                    "no Person items found on this server while tracking rows exist, local catalog is likely mid rescan, so pruning is skipped");
            }

            return Array.Empty<PersonWork>();
        }

        IReadOnlyList<SdkBaseItemDto> sourcePersons;
        try
        {
            sourcePersons = await source.Client.GetAllPersonsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A source error skips the prune, so persons that still exist are kept.
            MarkSourceUnavailable($"server '{source.Name}' unavailable bulk-fetching persons");
            Logger.LogWarning(ex, "Failed to bulk-fetch persons from {Server}. Skipping prune this run", source.Name);
            return Array.Empty<PersonWork>();
        }

        if (sourcePersons.Count == 0)
        {
            // An overlapping server always has persons, so an empty list is no answer.
            MarkSourceUnavailable(
                $"server '{source.Name}' returned 0 persons while {_localPersonsByName.Count} exist locally, treating as an unreliable answer");
            return Array.Empty<PersonWork>();
        }

        progress.Report(85);

        var matched = new List<PersonWork>(_localPersonsByName.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in sourcePersons)
        {
            if (string.IsNullOrEmpty(person.Name)) continue;
            if (!_localPersonsByName.ContainsKey(person.Name)) continue;
            if (!seen.Add(person.Name)) continue;
            matched.Add(new PersonWork(source, person));
        }

        Logger.LogInformation(
            "{Task}: matched {Matched}/{Local} local persons against {SourceTotal} source persons",
            Name,
            matched.Count,
            _localPersonsByName.Count,
            sourcePersons.Count);

        progress.Report(100);
        return matched;
    }

    /// <inheritdoc />
    protected override async Task<PeopleSyncItem?> BuildRecordAsync(
        PersonWork work,
        IReadOnlyDictionary<string, PeopleSyncItem> existing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var source = work.Person;
        if (string.IsNullOrEmpty(source.Name))
        {
            return null;
        }

        if (_localPersonsByName == null
            || !_localPersonsByName.TryGetValue(source.Name, out var localPerson))
        {
            return null;
        }

        var record = existing.TryGetValue(source.Name, out var prev)
            ? prev
            : new PeopleSyncItem { PersonName = source.Name };

        record.SourcePersonId = source.Id?.ToString("N", CultureInfo.InvariantCulture);
        record.LocalPersonId = localPerson.Id.ToString("N", CultureInfo.InvariantCulture);
        record.ServerKey = work.Source.Key;

        record.Metadata.Source = PeopleSyncMergeService.BuildSourceMetadata(source);
        record.Metadata.Local = PeopleSyncMergeService.BuildLocalMetadata(localPerson);
        record.Metadata.RecomputeSourceHash();

        var config = ConfigManager.Configuration;
        if (config.PeopleSyncImages)
        {
            var (sourceImg, localImg) = await ImageUtilities.ReadManifestsAsync(
                source,
                localPerson,
                PeopleSyncMergeService.ImageTypes,
                record.Images.Source,
                record.Images.Local,
                Guid.TryParse(record.SourcePersonId, out var sourcePersonGuid) ? sourcePersonGuid : null,
                work.Source.Client,
                config.DeepImageVerification,
                null,
                Logger,
                source.Name,
                cancellationToken).ConfigureAwait(false);

            record.Images.UpdateSource(sourceImg);
            record.Images.Local = localImg;
        }
        else
        {
            // With image sync off, stored image values must not show as changes.
            record.Images.Source = null;
            record.Images.Local = null;
            record.Images.SourceHash = null;
        }

        return record;
    }

    /// <inheritdoc />
    protected override string ExtractKey(PeopleSyncItem record) => record.PersonName;

    /// <summary>
    /// Two servers collide on the person's name, which is also the row key, so the first server in
    /// rank order supplies the person and the rest are dropped before any row is built.
    /// </summary>
    /// <inheritdoc />
    protected override string? PriorityKeyOf(PersonWork source) => source?.Person.Name;

    /// <inheritdoc />
    protected override string? PriorityKeyOf(PeopleSyncItem record) => record?.PersonName;

    /// <inheritdoc />
    protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastPeopleSyncTime = utcNow;
    }
}

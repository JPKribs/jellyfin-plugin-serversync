using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using SdkBaseItemDto = Jellyfin.Sdk.Generated.Models.BaseItemDto;
using TaskTriggerInfo = MediaBrowser.Model.Tasks.TaskTriggerInfo;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Source side work item for the People refresh task: one source person and the server it came from.
/// </summary>
public sealed record PersonWork(ScanSource Source, SdkBaseItemDto Person);

/// <summary>
/// Refresh phase for People sync. Fetches all source persons, looks up each
/// by name on the local server, and writes a snapshot row for the matched
/// ones. Persons without a local match are skipped (no row written).
/// </summary>
public class RefreshPeopleSyncTableTask : RefreshSyncTaskBase<PeopleSyncItem, PersonWork, string>
{
    private readonly ILibraryManager _libraryManager;
    private readonly ScanConflictSettler _conflicts;

    /// <summary>
    /// Per-run cache of local Person items keyed by Name. Built by
    /// <see cref="GetListAsync"/>, consumed by <see cref="BuildRecordAsync"/>
    /// so the build phase doesn't re-hit Jellyfin's library SQLite for every
    /// item — that contention was producing
    /// <c>SQLite Error 5: 'database is locked'</c> under parallelism.
    /// </summary>
    private Dictionary<string, BaseItem>? _localPersonsByName;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public RefreshPeopleSyncTableTask(
        ILogger<RefreshPeopleSyncTableTask> logger,
        ILibraryManager libraryManager,
        ISourceServerClientFactory clientFactory,
        IPluginConfigurationManager configManager,
        PeopleSyncTableManager manager,
        ScanConflictSettler conflicts)
        : base(logger, manager, clientFactory, configManager)
    {
        _libraryManager = libraryManager;
        _conflicts = conflicts;
    }

    // A single person from a hint: the cache holds just that one local person, found by name.
    /// <inheritdoc />
    protected override void PrepareForOne(PersonWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // Looked up by query rather than GetPerson, which creates a person that does not exist yet.
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
            record => Services.Queue.HintProtocol.PeopleKey(record.PersonName),
            record => Services.Queue.HintProtocol.PeopleKey(record.PersonName),
            record => (record.LocalPersonId, record.PersonName),
            record => Manager.Upsert(record),
            Logger,
            cancellationToken);

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

    // User-configurable (Configuration > Processing) — shared with the
    // Metadata refresh. <c>BuildRecordAsync</c> is HTTP-free in steady state
    // (source data comes from the bulk <c>/Persons</c> fetch, image data
    // from <c>BaseItemDto.ImageTags</c>), so this dial is effectively "how
    // much CPU may a refresh use".
    /// <inheritdoc />
    protected override int BuildRecordParallelism => Math.Clamp(ConfigManager.Configuration.RefreshParallelism, 1, 16);

    /// <inheritdoc />
    protected override bool IsEnabled()
    {
        var config = ConfigManager.Configuration;
        return config.EnablePeopleSync && config.GetPullServers().Count > 0;
    }

    // The local person cache is built once per run, by the first server's list pass.
    /// <inheritdoc />
    protected override void OnRunStarting()
    {
        _localPersonsByName = null;
    }

    // Bulk fetch + in-memory join: enumerate local Person items, then pull
    // the full source Person catalog in one /Persons call and intersect by
    // name in memory. /Persons is the only route that works for every token
    // type — /Items?recursive=true scopes to the requesting user's libraries
    // and returns an empty 200 for non-admin tokens because Person items
    // live outside library folders. The per-name fan-out this replaced cost
    // one HTTP round-trip per local person (130k+ requests on a real
    // library, 2.5 hours wall-clock).
    /// <inheritdoc />
    protected override async Task<IList<PersonWork>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);

        progress.Report(2);

        // Step 1: enumerate local Person items once per run and cache them for
        // BuildRecordAsync. Doing this once up front (rather than per-item)
        // avoids hammering Jellyfin's library SQLite during the parallel
        // build phase — that was producing "database is locked" errors.
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
                "{Task}: enumerated {Count} local persons; bulk-fetching source persons",
                Name,
                _localPersonsByName.Count);
        }

        // Local enumeration is the long pole of this phase on large
        // libraries (single blocking GetItemList call), so it owns the
        // front of the fetch band; the /Persons download owns the rest.
        progress.Report(35);

        if (_localPersonsByName.Count == 0)
        {
            // Mirror of the zero-source guard below. Jellyfin rebuilds the
            // people table during a library rescan, so a momentary empty read
            // is normal — and with an empty work list every tracked row falls
            // out of the seen set and looks removed. The table-wide circuit
            // breaker only catches this above 50 rows; below that the whole
            // table would go. Refuse to prune instead.
            if (Manager.Count() > 0)
            {
                MarkSourceUnavailable(
                    "no Person items found on this server while tracking rows exist — local catalog is likely mid rescan, so pruning is skipped");
            }

            return Array.Empty<PersonWork>();
        }

        // Step 2: bulk-fetch the entire source Person catalog via /Persons.
        // Returns BaseItemDto with the field set the metadata blobs need
        // (Overview, ProviderIds, Tags, Settings, etc.).
        IReadOnlyList<SdkBaseItemDto> sourcePersons;
        try
        {
            sourcePersons = await source.Client.GetAllPersonsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Source returned an error (or no real answer). Skip pruning this run
            // so persons that still exist aren't deleted, and enumerate nothing.
            MarkSourceUnavailable($"server '{source.Name}' unavailable bulk-fetching persons");
            Logger.LogWarning(ex, "Failed to bulk-fetch persons from {Server}; skipping prune this run", source.Name);
            return Array.Empty<PersonWork>();
        }

        if (sourcePersons.Count == 0)
        {
            // A server whose library overlaps ours never has zero persons —
            // an empty 200 means the source didn't give a real answer (mid-
            // startup, migrating, or an endpoint/auth regression). Treating
            // it as truth is what wiped the tracking table on 10.11.64.0.
            MarkSourceUnavailable(
                $"server '{source.Name}' returned 0 persons while {_localPersonsByName.Count} exist locally — treating as an unreliable answer");
            return Array.Empty<PersonWork>();
        }

        progress.Report(85);

        // Step 3: intersect by name in memory. Source duplicates (same name
        // appearing twice on source) keep the first occurrence — the local
        // dictionary uses the same first-wins rule above.
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

        // Look up local person from the cache built in GetListAsync. Going
        // back to ILibraryManager here under parallel load was producing
        // SQLite "database is locked" errors against Jellyfin's main DB.
        if (_localPersonsByName == null
            || !_localPersonsByName.TryGetValue(source.Name, out var localPerson))
        {
            return null;
        }

        // Reuse existing record (preserves Status if Ignored) or create new.
        var record = existing.TryGetValue(source.Name, out var prev)
            ? prev
            : new PeopleSyncItem { PersonName = source.Name };

        record.SourcePersonId = source.Id?.ToString("N", CultureInfo.InvariantCulture);
        record.LocalPersonId = localPerson.Id.ToString("N", CultureInfo.InvariantCulture);
        record.ServerKey = work.Source.Key;

        // Build metadata blobs for both sides; SyncableValue.RecomputeSourceHash
        // ensures the SourceHash field is populated for the Compare fast-path.
        record.Metadata.Source = PeopleSyncMergeService.BuildSourceMetadata(source);
        record.Metadata.Local = PeopleSyncMergeService.BuildLocalMetadata(localPerson);
        record.Metadata.RecomputeSourceHash();

        var config = ConfigManager.Configuration;
        if (config.PeopleSyncImages)
        {
            var (sourceImg, localImg) = PeopleSyncMergeService.PopulateImageData(source, localPerson);

            // Size the source-side manifest: carry sizes forward from the
            // prior manifest for unchanged tags (no HTTP — the steady-state
            // path), falling back to live /Items/{id}/Images enrichment only
            // when a tag changed or a size is missing. PopulateImageData
            // builds the source side tag-only (Size=0) from
            // BaseItemDto.ImageTags; without sizing, the comparator's
            // tag-only-vs-sized fallback fires on every row, every refresh —
            // every row queues and re-downloads images that already match.
            if (!string.IsNullOrEmpty(record.SourcePersonId)
                && Guid.TryParse(record.SourcePersonId, out var sourcePersonGuid))
            {
                try
                {
                    sourceImg = await ImageManifestEnricher.EnrichWithCarryForwardAsync(
                        sourceImg,
                        record.Images.Source,
                        sourcePersonGuid,
                        work.Source.Client,
                        Logger,
                        source.Name,
                        config.DeepImageVerification,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Source image enrichment failed for {PersonName}; comparator will fall back to tag-only", source.Name);
                    sourceImg = ImageManifestEnricher.CarryForwardSizes(sourceImg, record.Images.Source);
                }
            }

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

        return record;
    }

    /// <inheritdoc />
    protected override string ExtractKey(PeopleSyncItem record) => record.PersonName;

    // Two servers collide on the person's name, which is also the row key, so the first server in
    // priority order supplies the person and the rest are dropped before any row is built.
    /// <inheritdoc />
    protected override string? PriorityKeyOf(PersonWork source) => source?.Person.Name;

    /// <inheritdoc />
    protected override string? PriorityKeyOf(PeopleSyncItem record) => record?.PersonName;

    /// <inheritdoc />
    protected override void RecordRunCompleted(Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration config, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.LastPeopleSyncTime = utcNow;
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

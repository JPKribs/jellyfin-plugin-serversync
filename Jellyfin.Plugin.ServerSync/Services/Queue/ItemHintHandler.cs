using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tasks.MetadataSync;
using Jellyfin.Plugin.ServerSync.Tasks.PeopleSync;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies metadata and people hints through the scheduled tasks' own refresh and apply, so a hint and a scan never disagree.
/// Between the two the stamps decide, and when this server's edit is newer nothing is written and the peers are told to pull it.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class ItemHintHandler
{
    private readonly IServiceProvider _services;
    private readonly HintTriage _triage;
    private readonly IPluginConfigurationManager _configManager;
    private readonly VersionConflictResolver _resolver;
    private readonly ILibraryManager _libraryManager;
    private readonly LocalHintPublisher _publisher;
    private readonly ILogger<ItemHintHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemHintHandler"/> class.
    /// </summary>
    /// <param name="services">Builds the refresh and apply tasks for one row.</param>
    /// <param name="configManager">Tells which sync types are on and how the sources rank.</param>
    /// <param name="triage">Finds the local object a hint waits for.</param>
    /// <param name="resolver">Reads and records the stamps the conflict is decided on.</param>
    /// <param name="publisher">Publishes this server's winning values.</param>
    /// <param name="libraryManager">Finds the local item, for its lock and its title.</param>
    /// <param name="logger">Writes what was applied or kept.</param>
    public ItemHintHandler(
        IServiceProvider services,
        IPluginConfigurationManager configManager,
        HintTriage triage,
        VersionConflictResolver resolver,
        LocalHintPublisher publisher,
        ILibraryManager libraryManager,
        ILogger<ItemHintHandler> logger)
    {
        _services = services;
        _configManager = configManager;
        _triage = triage;
        _resolver = resolver;
        _libraryManager = libraryManager;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>Applies a metadata hint.</summary>
    /// <param name="hint">The hint that names the item on the origin.</param>
    /// <param name="origin">The server entry of the origin, whose library mappings apply.</param>
    /// <param name="client">Reads the item from the origin.</param>
    /// <param name="cancellationToken">Cancels the read and the apply.</param>
    /// <param name="prefetched">The origin's item when the worker already read it in a page, or null.</param>
    /// <returns>How the hint ended.</returns>
    public async Task<HintApplyResult> ApplyMetadataAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken, BaseItemDto? prefetched = null)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);

        var config = _configManager.Configuration;
        if (!config.EnableMetadataSync)
        {
            return HintApplyResult.Dropped(HintCode.ModuleOff, "metadata sync is off on this server");
        }

        if (!Guid.TryParse(hint.Key, out var originItemId))
        {
            return HintApplyResult.Dropped(HintCode.Invalid, "the hint's key is not an item id");
        }

        var mapping = HintMapping.FindBySourcePath(origin, hint.ItemPath);
        if (mapping is null)
        {
            return HintApplyResult.Dropped(HintCode.NotMapped, $"path '{hint.ItemPath}' on '{origin.DisplayName}' is not in a mapped library here");
        }

        if (_triage.MissingLocal(origin, HintKind.Metadata, hint.ItemPath, hint.Key) is { } waitFor)
        {
            return HintApplyResult.Park(waitFor, "no local item at the mapped path yet. It applies when the item arrives");
        }

        // An item the worker's page read without an id is gone, and the origin is not asked again.
        if (prefetched is { Id: null })
        {
            return HintApplyResult.Dropped(HintCode.SourceGone, $"the item no longer exists on '{origin.DisplayName}'");
        }

        var source = Connect(origin, client);

        // The origin's stamp is read before and after its item, so the value is recorded under the stamp it carries, never an older one.
        var before = await OriginStampAsync(source, HintKind.Metadata, HintProtocol.MetadataKey(originItemId), hint.Recorded, cancellationToken).ConfigureAwait(false);
        if (!before.Read)
        {
            return HintApplyResult.RetryLater($"could not read the item's version from '{origin.DisplayName}'");
        }

        // An item read in a page before a newer edit landed on the origin is read again.
        var dto = before.Stamp is { } newer && hint.Recorded && VersionDecider.IsNewer(newer, HintProtocol.IncomingVersion(hint, HintKind.Metadata, hint.Key)) ? null : prefetched;

        // A row the worker's pages did not cover reads its own item.
        if (dto is null)
        {
            List<BaseItemDto> fetched;
            try
            {
                fetched = await client.GetItemsByIdsAsync(new[] { originItemId }, RefreshMetadataSyncTableTask.BuildRequestedFields(config), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return HintApplyResult.RetryLater($"could not read the item from '{origin.DisplayName}': {ex.Message}");
            }

            dto = fetched.FirstOrDefault();
        }

        if (dto is null || !dto.Id.HasValue)
        {
            return HintApplyResult.Dropped(HintCode.SourceGone, $"the item no longer exists on '{origin.DisplayName}'");
        }

        mapping = HintMapping.FindBySourcePath(origin, dto.Path);
        if (mapping is null)
        {
            return HintApplyResult.Dropped(HintCode.NotMapped, $"'{origin.DisplayName}' has the item at '{dto.Path}', which is not in a library mapped here");
        }

        if (HintTriage.IsFiltered(mapping, dto.Path))
        {
            return HintApplyResult.Dropped(HintCode.Filtered, $"'{dto.Path}' is left out by the library mapping's filter here");
        }

        var after = await OriginStampAsync(source, HintKind.Metadata, HintProtocol.MetadataKey(originItemId), hint.Recorded, cancellationToken).ConfigureAwait(false);
        if (!after.Read || !SameStamp(before.Stamp, after.Stamp))
        {
            return HintApplyResult.RetryLater($"the item changed on '{origin.DisplayName}' while it was read");
        }

        var refresh = ActivatorUtilities.CreateInstance<RefreshMetadataSyncTableTask>(_services);
        var record = await refresh.RefreshOneAsync(new MetadataWork(source, mapping, dto, RefreshMetadataSyncTableTask.IsFolderType(dto.Type)), cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return _triage.MissingLocal(origin, HintKind.Metadata, dto.Path, hint.Key) is { } missing
                ? HintApplyResult.Park(missing, "no local item at the mapped path yet. It applies when the item arrives")
                : HintApplyResult.Dropped(HintCode.NotMapped, $"the local item at the path mapped from '{dto.Path}' could not be matched here");
        }

        var localItemId = Guid.Parse(record.LocalItemId!);
        return await SettleAndApplyAsync(
            record,
            HintKind.Metadata,
            HintProtocol.MetadataKey(localItemId),
            hint,
            after.Stamp,
            origin,
            version => _publisher.PublishMetadata(localItemId, record.LocalPath ?? string.Empty, version, excludePeerKey: null, itemType: hint.ItemType),
            () => ActivatorUtilities.CreateInstance<SyncMissingMetadataTask>(_services).ApplyRowAsync(record, source, cancellationToken),
            record.ItemName,
            () => _libraryManager.GetItemById(localItemId)).ConfigureAwait(false);
    }

    /// <summary>Applies a people hint.</summary>
    /// <param name="hint">The hint that names the person.</param>
    /// <param name="origin">The server entry of the origin.</param>
    /// <param name="client">Reads the person from the origin.</param>
    /// <param name="cancellationToken">Cancels the read and the apply.</param>
    /// <returns>How the hint ended.</returns>
    public async Task<HintApplyResult> ApplyPeopleAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);

        if (!_configManager.Configuration.EnablePeopleSync)
        {
            return HintApplyResult.Dropped(HintCode.ModuleOff, "people sync is off on this server");
        }

        if (string.IsNullOrWhiteSpace(hint.Key))
        {
            return HintApplyResult.Dropped(HintCode.Invalid, "the hint carries no person name");
        }

        if (_triage.MissingLocal(origin, HintKind.People, null, hint.Key) is { } waitFor)
        {
            return HintApplyResult.Park(waitFor, "the person does not exist on this server yet. It applies when the person arrives");
        }

        var source = Connect(origin, client);
        var peopleKey = HintProtocol.PeopleKey(hint.Key);
        var before = await OriginStampAsync(source, HintKind.People, peopleKey, hint.Recorded, cancellationToken).ConfigureAwait(false);
        if (!before.Read)
        {
            return HintApplyResult.RetryLater($"could not read the person's version from '{origin.DisplayName}'");
        }

        BaseItemDto? dto;
        try
        {
            dto = await client.GetPersonByNameAsync(hint.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HintApplyResult.RetryLater($"could not read the person from '{origin.DisplayName}': {ex.Message}");
        }

        if (dto is null || string.IsNullOrEmpty(dto.Name))
        {
            return HintApplyResult.Dropped(HintCode.SourceGone, $"it no longer exists on '{origin.DisplayName}'");
        }

        var after = await OriginStampAsync(source, HintKind.People, peopleKey, hint.Recorded, cancellationToken).ConfigureAwait(false);
        if (!after.Read || !SameStamp(before.Stamp, after.Stamp))
        {
            return HintApplyResult.RetryLater($"Person {hint.Key} changed on '{origin.DisplayName}' while it was read");
        }

        var refresh = ActivatorUtilities.CreateInstance<RefreshPeopleSyncTableTask>(_services);
        var record = await refresh.RefreshOneAsync(new PersonWork(source, dto), cancellationToken).ConfigureAwait(false);
        if (record is null || string.IsNullOrEmpty(record.LocalPersonId))
        {
            return _triage.MissingLocal(origin, HintKind.People, null, hint.Key) is { } missing
                ? HintApplyResult.Park(missing, $"Person {hint.Key} does not exist on this server yet. It applies when the person arrives")
                : HintApplyResult.Dropped(HintCode.NotMapped, $"Person {hint.Key} could not be matched on this server");
        }

        return await SettleAndApplyAsync(
            record,
            HintKind.People,
            HintProtocol.PeopleKey(record.PersonName),
            hint,
            after.Stamp,
            origin,
            version => _publisher.PublishPeople(record.PersonName, Guid.Parse(record.LocalPersonId), version, excludePeerKey: null),
            () => ActivatorUtilities.CreateInstance<SyncMissingPeopleTask>(_services).ApplyRowAsync(record, source, cancellationToken),
            record.PersonName,
            () => _libraryManager.GetItemById(Guid.Parse(record.LocalPersonId))).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides between the refreshed row and this server's value by their stamps, then applies the row or keeps the local value.
    /// A row whose retries ran out is queued again first, since its value still differs.
    /// </summary>
    private async Task<HintApplyResult> SettleAndApplyAsync<TRecord>(
        TRecord record,
        HintKind kind,
        string localKey,
        InboundHint hint,
        ObjectVersion? originNow,
        SourceServer origin,
        Action<ObjectVersion> publishLocal,
        Func<Task<bool>> apply,
        string? name,
        Func<BaseItem?> localItem)
        where TRecord : SyncRecord
    {
        var incoming = CurrentVersion(HintProtocol.IncomingVersion(hint, kind, localKey), originNow, hint.Recorded);

        if (record.Status == SyncStatus.Ignored)
        {
            return HintApplyResult.Dropped(HintCode.Ignored, "the row is ignored on this server");
        }

        record.RetryIfErrored();

        if (record.Status == SyncStatus.Pending)
        {
            return HintApplyResult.Kept(HintCode.AwaitingApproval, "waiting for approval on this server");
        }

        // A row that is not queued already matches, so the hint's stamp applies here.
        if (record.Status != SyncStatus.Queued)
        {
            if (hint.Recorded)
            {
                _resolver.Record(incoming);
            }

            return HintApplyResult.Unchanged;
        }

        // A lock protects edits no stamp records, so provider work never replaces them.
        if (!hint.Recorded && localItem() is { IsLocked: true })
        {
            record.MarkKept($"kept: the item is locked here, so a provider's work on '{origin.DisplayName}' does not replace it");
            StoreKept(record, kind);
            _logger.LogInformation("Kept {Kind} for {Name}: the item is locked here and the change from '{Origin}' is a provider's work", kind, FormatUtilities.SanitizeForLog(name), FormatUtilities.SanitizeForLog(origin.DisplayName));
            return HintApplyResult.Kept(HintCode.Locked, "the item is locked here");
        }

        // A stamped edit here beats provider work there, and beats an older edit there.
        var local = _resolver.Recorded(kind, localKey);
        if (local is not null && (!hint.Recorded || VersionDecider.Decide(local, incoming, valuesEqual: false) == VersionDecision.Keep))
        {
            record.MarkKept(hint.Recorded
                ? $"kept: this server's edit is newer than the one on '{origin.DisplayName}'"
                : $"kept: this server's edit beats a provider's work on '{origin.DisplayName}'");
            StoreKept(record, kind);

            // A value taken from a third server is that server's to announce.
            if (_resolver.IsThisServer(local))
            {
                publishLocal(local);
            }

            _logger.LogInformation("Kept this server's {Kind} for {Name}, newer than '{Origin}'", kind, FormatUtilities.SanitizeForLog(name), FormatUtilities.SanitizeForLog(origin.DisplayName));
            return HintApplyResult.Kept(HintCode.Superseded, "this server's edit is newer");
        }

        if (!await apply().ConfigureAwait(false))
        {
            return HintApplyResult.RetryLater(record.Reason ?? "the apply failed");
        }

        // Provider work leaves no stamp, so a later edit anywhere still wins over it.
        if (hint.Recorded)
        {
            _resolver.Record(incoming);
        }

        _logger.LogInformation("Applied a {Kind} hint from '{Origin}' for {Name}{Provider}", kind, FormatUtilities.SanitizeForLog(origin.DisplayName), FormatUtilities.SanitizeForLog(name), hint.Recorded ? string.Empty : " (provider work)");
        return HintApplyResult.AppliedTo(localItem());
    }

    /// <summary>
    /// Gives the stamp a value read live from the origin carries: the hint's, or the origin's stamp at the read when that is newer,
    /// since the origin may have taken a newer edit from a third server after it sent the hint. A newer value is never recorded under an older stamp,
    /// so an edit made between the two cannot overwrite it.
    /// </summary>
    /// <param name="incoming">The hint's stamp.</param>
    /// <param name="originNow">The origin's stamp for the object, read before and after the value, or null when it holds none or keeps no stamps.</param>
    /// <param name="recorded">Whether the hint is a stamped edit, not provider work.</param>
    /// <returns>The stamp to decide and record by.</returns>
    public static ObjectVersion CurrentVersion(ObjectVersion incoming, ObjectVersion? originNow, bool recorded)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        return recorded && originNow is not null && VersionDecider.IsNewer(originNow, incoming)
            ? new ObjectVersion { Kind = incoming.Kind, Key = incoming.Key, ServerId = originNow.ServerId, Timestamp = originNow.Timestamp }
            : incoming;
    }

    /// <summary>Whether two reads of the origin's stamp agree, so its object did not change between them.</summary>
    private static bool SameStamp(ObjectVersion? first, ObjectVersion? second)
        => first is null ? second is null : second is not null && VersionDecider.SameVersion(first, second);

    /// <summary>Reads the origin's stamp for one object, or no stamp when the origin keeps none or the hint is provider work.</summary>
    /// <returns>Whether it could be read, and the stamp.</returns>
    private async Task<(bool Read, ObjectVersion? Stamp)> OriginStampAsync(ScanSource source, HintKind kind, string peerKey, bool recorded, CancellationToken cancellationToken)
    {
        // Provider work records no stamp, so it needs none.
        if (!recorded || await _resolver.PeerCarriesVersionsAsync(source, cancellationToken).ConfigureAwait(false) != true)
        {
            return (true, null);
        }

        var stamps = await _resolver.PeerVersionsAsync(source, kind, new[] { peerKey }, cancellationToken).ConfigureAwait(false);
        if (stamps is null)
        {
            return (false, null);
        }

        stamps.TryGetValue(peerKey, out var stamp);
        return (true, stamp);
    }

    /// <summary>Stores a row marked kept in the table of its sync type.</summary>
    private void StoreKept(SyncRecord record, HintKind kind)
    {
        switch (kind)
        {
            case HintKind.Metadata:
                _services.GetRequiredService<MetadataSyncTableManager>().Upsert((MetadataSyncItem)record);
                break;
            case HintKind.People:
                _services.GetRequiredService<PeopleSyncTableManager>().Upsert((PeopleSyncItem)record);
                break;
        }
    }

    /// <summary>Wraps the origin and its client as a scan source with the origin's rank.</summary>
    private ScanSource Connect(SourceServer origin, SourceServerClient client)
    {
        var priority = Math.Max(0, _configManager.Configuration.GetPullServers().FindIndex(s => string.Equals(s.Key, origin.Key, StringComparison.OrdinalIgnoreCase)));
        return new ScanSource(origin, client, priority);
    }
}

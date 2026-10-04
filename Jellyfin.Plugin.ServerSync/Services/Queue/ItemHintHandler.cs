using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Tasks;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies metadata and people hints through the module tasks themselves: the refresh task builds
/// and stores the one row the way the full scan would, and the apply task writes it the way the full
/// run would, so a hint and a scan can never disagree. Between the two, the conflict is decided on
/// origin versions. The peer's edit wins when it is newer. This server's wins when it is newer, in
/// which case nothing is written here and the peers, the origin included, are told to pull it.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class ItemHintHandler
{
    private readonly IServiceProvider _services;
    private readonly IPluginConfigurationManager _configManager;
    private readonly VersionConflictResolver _resolver;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly LocalHintPublisher _publisher;
    private readonly ILogger<ItemHintHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemHintHandler"/> class.
    /// </summary>
    /// <param name="services">The service provider the module tasks are built from.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="resolver">The version resolver.</param>
    /// <param name="publisher">Publishes this server's winning values.</param>
    /// <param name="libraryManager">The library, which names the item the activity log mentions.</param>
    /// <param name="logger">Logger.</param>
    public ItemHintHandler(
        IServiceProvider services,
        IPluginConfigurationManager configManager,
        VersionConflictResolver resolver,
        LocalHintPublisher publisher,
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        ILogger<ItemHintHandler> logger)
    {
        _services = services;
        _configManager = configManager;
        _resolver = resolver;
        _libraryManager = libraryManager;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>Applies a metadata hint.</summary>
    /// <param name="hint">The inbound row.</param>
    /// <param name="origin">The configured entry for the origin.</param>
    /// <param name="client">A client bound to the origin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="prefetched">The origin's item when the worker already read it in a page, else null.</param>
    /// <returns>The outcome.</returns>
    public async Task<HintApplyResult> ApplyMetadataAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken, Jellyfin.Sdk.Generated.Models.BaseItemDto? prefetched = null)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);

        var config = _configManager.Configuration;
        if (!config.EnableMetadataSync)
        {
            return HintApplyResult.Dropped("metadata sync is off on this server");
        }

        if (!Guid.TryParse(hint.Key, out var originItemId))
        {
            return HintApplyResult.Dropped("the hint's key is not an item id");
        }

        var mapping = HintMapping.FindBySourcePath(origin, hint.ItemPath);
        if (mapping is null)
        {
            return HintApplyResult.Dropped($"path '{hint.ItemPath}' on '{origin.DisplayName}' is not in a mapped library here");
        }

        // The worker reads a pass's items in pages. A row it did not cover reads its own.
        var dto = prefetched;
        if (dto is null)
        {
            List<Jellyfin.Sdk.Generated.Models.BaseItemDto> fetched;
            try
            {
                fetched = await client.GetItemsByIdsAsync(new[] { originItemId }, RefreshMetadataSyncTableTask.BuildRequestedFields(config), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return HintApplyResult.RetryLater($"could not read the item from '{origin.DisplayName}': {ex.Message}");
            }

            dto = fetched.FirstOrDefault();
        }

        if (dto is null || !dto.Id.HasValue)
        {
            return HintApplyResult.Dropped($"the item no longer exists on '{origin.DisplayName}'");
        }

        var source = Connect(origin, client);
        var refresh = ActivatorUtilities.CreateInstance<RefreshMetadataSyncTableTask>(_services);
        var record = await refresh.RefreshOneAsync(new MetadataWork(source, mapping, dto, RefreshMetadataSyncTableTask.IsFolderType(dto.Type)), cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return HintApplyResult.Dropped("no local item at the mapped path yet. The next content sync or scan will pick it up");
        }

        return await SettleAndApplyAsync(
            record,
            HintKind.Metadata,
            HintProtocol.MetadataKey(Guid.Parse(record.LocalItemId!)),
            hint,
            origin,
            source,
            version => _publisher.PublishMetadata(Guid.Parse(record.LocalItemId!), record.LocalPath ?? string.Empty, version, excludePeerKey: null, itemType: hint.ItemType),
            () => ActivatorUtilities.CreateInstance<SyncMissingMetadataTask>(_services).ApplyRowAsync(record, source, cancellationToken),
            record.ItemName,
            () => _libraryManager.GetItemById(Guid.Parse(record.LocalItemId!))).ConfigureAwait(false);
    }

    /// <summary>Applies a people hint.</summary>
    /// <param name="hint">The inbound row.</param>
    /// <param name="origin">The configured entry for the origin.</param>
    /// <param name="client">A client bound to the origin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<HintApplyResult> ApplyPeopleAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);

        if (!_configManager.Configuration.EnablePeopleSync)
        {
            return HintApplyResult.Dropped("people sync is off on this server");
        }

        if (string.IsNullOrWhiteSpace(hint.Key))
        {
            return HintApplyResult.Dropped("the hint carries no person name");
        }

        Jellyfin.Sdk.Generated.Models.BaseItemDto? dto;
        try
        {
            dto = await client.GetPersonByNameAsync(hint.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HintApplyResult.RetryLater($"could not read the person from '{origin.DisplayName}': {ex.Message}");
        }

        if (dto is null || string.IsNullOrEmpty(dto.Name))
        {
            return HintApplyResult.Dropped($"it no longer exists on '{origin.DisplayName}'");
        }

        var source = Connect(origin, client);
        var refresh = ActivatorUtilities.CreateInstance<RefreshPeopleSyncTableTask>(_services);
        var record = await refresh.RefreshOneAsync(new PersonWork(source, dto), cancellationToken).ConfigureAwait(false);
        if (record is null || string.IsNullOrEmpty(record.LocalPersonId))
        {
            return HintApplyResult.Dropped($"Person {hint.Key} does not exist on this server so it could not be updated from {origin.DisplayName}");
        }

        return await SettleAndApplyAsync(
            record,
            HintKind.People,
            HintProtocol.PeopleKey(record.PersonName),
            hint,
            origin,
            source,
            version => _publisher.PublishPeople(record.PersonName, Guid.Parse(record.LocalPersonId), version, excludePeerKey: null),
            () => ActivatorUtilities.CreateInstance<SyncMissingPeopleTask>(_services).ApplyRowAsync(record, source, cancellationToken),
            record.PersonName,
            () => _libraryManager.GetItemById(Guid.Parse(record.LocalPersonId))).ConfigureAwait(false);
    }

    private async Task<HintApplyResult> SettleAndApplyAsync<TRecord>(
        TRecord record,
        HintKind kind,
        string localKey,
        InboundHint hint,
        SourceServer origin,
        ScanSource source,
        Action<ObjectVersion> publishLocal,
        Func<Task<bool>> apply,
        string? name,
        Func<MediaBrowser.Controller.Entities.BaseItem?> localItem)
        where TRecord : SyncRecord
    {
        var incoming = HintProtocol.IncomingVersion(hint, kind, localKey);

        if (record.Status == SyncStatus.Ignored)
        {
            return HintApplyResult.Dropped("the row is ignored on this server");
        }

        // A row whose retries ran out still differs. Taking it for "the values match" would record a
        // version for a value this server does not hold.
        record.RetryIfErrored();

        if (record.Status == SyncStatus.Pending)
        {
            return new HintApplyResult(HintApplyOutcome.Unchanged, "waiting for approval on this server");
        }

        if (record.Status != SyncStatus.Queued)
        {
            // The values already match, so the hint's version is the version of the value here too.
            if (hint.Recorded)
            {
                _resolver.Record(incoming);
            }

            return HintApplyResult.Unchanged;
        }

        // Provider work never touches an item locked here, the same as Jellyfin's own refresh. A lock is
        // the one sign of curation that predates versions, so it protects edits no version records.
        if (!hint.Recorded && localItem() is { IsLocked: true })
        {
            record.MarkKept($"kept: the item is locked here, so a provider's work on '{origin.DisplayName}' does not replace it");
            StoreKept(record, kind);
            _logger.LogInformation("Kept {Kind} for {Name}: the item is locked here and the change from '{Origin}' is a provider's work", kind, name, origin.DisplayName);
            return new HintApplyResult(HintApplyOutcome.Unchanged, "the item is locked here");
        }

        // A recorded edit here beats a newer provider's work there, and beats an older edit there.
        var local = _resolver.Recorded(kind, localKey);
        if (local is not null && (!hint.Recorded || VersionDecider.Decide(local, incoming, valuesEqual: false) == VersionDecision.Keep))
        {
            record.MarkKept(hint.Recorded
                ? $"kept: this server's edit is newer than the one on '{origin.DisplayName}'"
                : $"kept: this server's edit beats a provider's work on '{origin.DisplayName}'");
            StoreKept(record, kind);

            // Only an edit made here is announced from here, so a value this server took from a third
            // server never bounces between peers.
            if (_resolver.IsThisServer(local))
            {
                publishLocal(local);
            }

            _logger.LogInformation("Kept this server's {Kind} for {Name}, newer than '{Origin}'", kind, name, origin.DisplayName);
            return new HintApplyResult(HintApplyOutcome.Unchanged, "this server's edit is newer");
        }

        if (!await apply().ConfigureAwait(false))
        {
            return HintApplyResult.RetryLater(record.Reason ?? "the apply failed");
        }

        // The apply task recorded the version it read from the peer. The hint's is at least as exact.
        // Provider work leaves no version, so a later edit anywhere still wins over it.
        if (hint.Recorded)
        {
            _resolver.Record(incoming);
        }

        _logger.LogInformation("Applied a {Kind} hint from '{Origin}' for {Name}{Provider}", kind, origin.DisplayName, name, hint.Recorded ? string.Empty : " (provider work)");
        return HintApplyResult.AppliedTo(localItem());
    }

    private void StoreKept<TRecord>(TRecord record, HintKind kind)
        where TRecord : SyncRecord
    {
        switch (kind)
        {
            case HintKind.Metadata:
                _services.GetRequiredService<MetadataSyncTableManager>().Upsert((MetadataSyncItem)(object)record);
                break;
            case HintKind.People:
                _services.GetRequiredService<PeopleSyncTableManager>().Upsert((PeopleSyncItem)(object)record);
                break;
            default:
                break;
        }
    }

    private ScanSource Connect(SourceServer origin, SourceServerClient client)
    {
        var priority = Math.Max(0, _configManager.Configuration.GetPullServers().FindIndex(s => string.Equals(s.Key, origin.Key, StringComparison.OrdinalIgnoreCase)));
        return new ScanSource(origin, client, priority);
    }
}

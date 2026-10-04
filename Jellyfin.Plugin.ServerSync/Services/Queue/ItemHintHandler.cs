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
    private readonly LocalHintPublisher _publisher;
    private readonly ILogger<ItemHintHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemHintHandler"/> class.
    /// </summary>
    /// <param name="services">The service provider the module tasks are built from.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="resolver">The version resolver.</param>
    /// <param name="publisher">Publishes this server's winning values.</param>
    /// <param name="logger">Logger.</param>
    public ItemHintHandler(
        IServiceProvider services,
        IPluginConfigurationManager configManager,
        VersionConflictResolver resolver,
        LocalHintPublisher publisher,
        ILogger<ItemHintHandler> logger)
    {
        _services = services;
        _configManager = configManager;
        _resolver = resolver;
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>Applies a metadata hint.</summary>
    /// <param name="hint">The inbound row.</param>
    /// <param name="origin">The configured entry for the origin.</param>
    /// <param name="client">A client bound to the origin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<HintApplyResult> ApplyMetadataAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
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

        List<Jellyfin.Sdk.Generated.Models.BaseItemDto> fetched;
        try
        {
            fetched = await client.GetItemsByIdsAsync(new[] { originItemId }, RefreshMetadataSyncTableTask.BuildRequestedFields(config), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HintApplyResult.RetryLater($"could not read the item from '{origin.DisplayName}': {ex.Message}");
        }

        var dto = fetched.FirstOrDefault();
        if (dto is null || !dto.Id.HasValue)
        {
            return HintApplyResult.Dropped($"the item no longer exists on '{origin.DisplayName}'");
        }

        var source = Connect(origin, client);
        var refresh = ActivatorUtilities.CreateInstance<RefreshMetadataSyncTableTask>(_services);
        var record = await refresh.RefreshOneAsync(new MetadataWork(source, mapping, dto, RefreshMetadataSyncTableTask.IsFolderType(dto.Type)), cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return HintApplyResult.Dropped("no local item at the mapped path yet; the next content sync or scan will pick it up");
        }

        return await SettleAndApplyAsync(
            record,
            HintKind.Metadata,
            HintProtocol.MetadataKey(Guid.Parse(record.LocalItemId!)),
            hint,
            origin,
            source,
            version => _publisher.PublishMetadata(Guid.Parse(record.LocalItemId!), record.LocalPath ?? string.Empty, version, excludePeerKey: null),
            () => ActivatorUtilities.CreateInstance<SyncMissingMetadataTask>(_services).ApplyRowAsync(record, source, cancellationToken),
            record.ItemName,
            cancellationToken).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HintApplyResult.RetryLater($"could not read the person from '{origin.DisplayName}': {ex.Message}");
        }

        if (dto is null || string.IsNullOrEmpty(dto.Name))
        {
            return HintApplyResult.Dropped($"'{hint.Key}' no longer exists on '{origin.DisplayName}'");
        }

        var source = Connect(origin, client);
        var refresh = ActivatorUtilities.CreateInstance<RefreshPeopleSyncTableTask>(_services);
        var record = await refresh.RefreshOneAsync(new PersonWork(source, dto), cancellationToken).ConfigureAwait(false);
        if (record is null || string.IsNullOrEmpty(record.LocalPersonId))
        {
            return HintApplyResult.Dropped($"no person named '{hint.Key}' on this server");
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
            cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
        where TRecord : SyncRecord
    {
        var incoming = new ObjectVersion { Kind = kind, Key = localKey, ServerId = hint.VersionServerId, Timestamp = hint.VersionTimestamp };

        if (record.Status == SyncStatus.Ignored)
        {
            return HintApplyResult.Dropped("the row is ignored on this server");
        }

        if (record.Status != SyncStatus.Queued)
        {
            // The values already match, so the hint's version is the version of the value here too.
            _resolver.Record(incoming);
            return HintApplyResult.Unchanged;
        }

        var local = _resolver.Recorded(kind, localKey);
        if (local is not null && VersionDecider.Decide(local, incoming, valuesEqual: false) == VersionDecision.Keep)
        {
            record.Status = SyncStatus.Synced;
            record.StatusDate = DateTime.UtcNow;
            record.Reason = $"kept: this server's edit is newer than the one on '{origin.DisplayName}', which will pull it";
            StoreKept(record, kind);
            publishLocal(local);
            _logger.LogInformation("Kept this server's {Kind} for {Name}, newer than '{Origin}', and told the peers to pull it", kind, name, origin.DisplayName);
            return new HintApplyResult(HintApplyOutcome.Unchanged, "this server's edit is newer");
        }

        if (!await apply().ConfigureAwait(false))
        {
            return HintApplyResult.RetryLater(record.Reason ?? "the apply failed");
        }

        // The apply task recorded the version it read from the peer; the hint's is at least as exact.
        _resolver.Record(incoming);
        _logger.LogInformation("Applied a {Kind} hint from '{Origin}' for {Name}", kind, origin.DisplayName, name);
        return HintApplyResult.Applied;
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

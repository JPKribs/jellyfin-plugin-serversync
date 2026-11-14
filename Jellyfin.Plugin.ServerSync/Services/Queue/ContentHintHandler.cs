using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Tasks;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies a content hint: a file appeared on the origin. The content refresh task builds the row the
/// scan would, which respects the approval mode, and when the row is queued the download task fetches
/// the file the way a run would. A file a higher priority server already tracks is left to that
/// server. Replacements and removals are not hinted and stay with the scheduled Sync Content task.
/// </summary>
[PluginService(ServiceLifetime.Singleton)]
public sealed class ContentHintHandler
{
    private readonly IServiceProvider _services;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ContentSyncTableManager _table;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ContentHintHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentHintHandler"/> class.
    /// </summary>
    /// <param name="services">The service provider the module tasks are built from.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="table">The content table.</param>
    /// <param name="libraryManager">The library, which names the item the activity log mentions.</param>
    /// <param name="logger">Logger.</param>
    public ContentHintHandler(IServiceProvider services, IPluginConfigurationManager configManager, ContentSyncTableManager table, ILibraryManager libraryManager, ILogger<ContentHintHandler> logger)
    {
        _services = services;
        _configManager = configManager;
        _table = table;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Applies one content hint.</summary>
    /// <param name="hint">The inbound row.</param>
    /// <param name="origin">The configured entry for the origin.</param>
    /// <param name="client">A client bound to the origin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async Task<HintApplyResult> ApplyAsync(InboundHint hint, SourceServer origin, SourceServerClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(client);

        var config = _configManager.Configuration;
        if (!config.EnableContentSync)
        {
            return HintApplyResult.Dropped(HintCode.ModuleOff, "content sync is off on this server");
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

        // A failed read throws and is retried by the worker. An answer with no item means it is gone.
        var dto = await client.FindItemDetailsAsync(originItemId, cancellationToken).ConfigureAwait(false);
        if (dto is null || !dto.Id.HasValue)
        {
            // The file left that server. A copy of it here is a real file, and is settled by the scan,
            // with its safety limits. A link to it holds nothing, so it is settled now: it moves to
            // another server that holds the file, or goes.
            var held = _table.GetByKey(originItemId.ToString("N", System.Globalization.CultureInfo.InvariantCulture));
            if (held is { StorageMode: StorageMode.Link }
                && string.Equals(held.ServerKey, origin.Key, StringComparison.OrdinalIgnoreCase)
                && held.Status is not (SyncStatus.Ignored or SyncStatus.Deleting)
                && (string.IsNullOrEmpty(held.LocalPath) || !System.IO.File.Exists(held.LocalPath) || Links.LinkFiles.IsOwnLink(held.LocalPath))
                && _services.GetService<Links.LinkRehomer>() is { } rehomer)
            {
                var movedTo = await rehomer.SettleGoneAsync(held, cancellationToken).ConfigureAwait(false);
                if (movedTo is not null)
                {
                    return HintApplyResult.Dropped(HintCode.SourceGone, $"the file left '{origin.DisplayName}', so its link here now streams from '{movedTo}'");
                }

                FileDeletionService.ProcessPendingDeletions(_table, config, _logger, new List<string>(), cancellationToken);
                return HintApplyResult.Dropped(HintCode.SourceGone, $"the file left '{origin.DisplayName}' and no other server holds it, so its link here was removed");
            }

            return HintApplyResult.Dropped(HintCode.SourceGone, HintProtocol.IsContentRemoval(hint.ItemType)
                ? $"the file left '{origin.DisplayName}'. A copy of it here is settled by the next scan"
                : $"the item no longer exists on '{origin.DisplayName}'");
        }

        if (string.IsNullOrEmpty(dto.Path))
        {
            return HintApplyResult.Dropped(HintCode.Unreadable, $"'{origin.DisplayName}' did not say where the file is, which happens when the key for it is not an administrator's");
        }

        mapping = HintMapping.FindBySourcePath(origin, dto.Path);
        if (mapping is null)
        {
            return HintApplyResult.Dropped(HintCode.NotMapped, $"'{origin.DisplayName}' has the item at '{dto.Path}', which is not in a library mapped here");
        }

        // The same filters the scan applies. A file the mapping leaves out must not arrive by hint, since
        // the next scan would find it out of place and a mapping that deletes stale files would remove it.
        if (PathUtilities.IsItemFiltered(dto.Path, mapping.SourceRootPath, mapping.FilterMode, mapping.FilteredItems))
        {
            return HintApplyResult.Dropped(HintCode.Filtered, mapping.FilterMode == LibraryFilterMode.Whitelist
                ? "the file is not on the library mapping's allow list"
                : "the file is on the library mapping's block list");
        }

        // A .strm points at a file somewhere else, so it is not the origin's to pass on.
        if (UpdateSyncTablesTask.IsStrm(dto))
        {
            return HintApplyResult.Dropped(HintCode.Filtered, "the item is a .strm on the origin, which points at a file it does not hold");
        }

        var watchedByAll = await IsWatchedByAllAsync(client, originItemId, config, cancellationToken).ConfigureAwait(false);
        var scanServers = config.GetPullServers();
        var originIndex = scanServers.FindIndex(s => string.Equals(s.Key, origin.Key, StringComparison.OrdinalIgnoreCase));
        if (HigherPriorityServer(scanServers, originIndex, origin, mapping, dto.Path) is { } owner)
        {
            return HintApplyResult.Dropped(HintCode.Priority, $"'{owner}' has priority for this file");
        }

        // A file the announcing server was itself given is left to the server it first came from. A
        // failed read throws and is retried by the worker.
        var firstFrom = await client.OriginOfAsync(dto.Path, cancellationToken).ConfigureAwait(false);
        var localServerId = _services.GetService<MediaBrowser.Controller.IServerApplicationHost>()?.SystemId;
        if (Links.AlwaysKeep.StorageFor(mapping, dto.Path) != StorageMode.Link
            && UpdateSyncTablesTask.DecidedElsewhere(config, localServerId, origin.Key, firstFrom, PathUtilities.TranslatePath(dto.Path, mapping.SourceRootPath, mapping.LocalRootPath)) is { } decider)
        {
            return HintApplyResult.Dropped(HintCode.Priority, $"the file first came from {decider}, which decides about it");
        }

        var source = new ScanSource(origin, client, Math.Max(0, originIndex));
        var refresh = ActivatorUtilities.CreateInstance<UpdateSyncTablesTask>(_services);

        // The hinted item first. When it is passed over, as the scan passes over a peer's link, its
        // other versions are passed over with it, the same as a scan.
        var record = await refresh.RefreshOneAsync(new ContentRefreshWork(source, mapping, dto, watchedByAll), cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return refresh.LinkingRefusedBy(origin.Key)
                ? HintApplyResult.Dropped(HintCode.Filtered, $"'{origin.DisplayName}' does not offer links to this server, so the file waits until linking is allowed there")
                : HintApplyResult.Dropped(HintCode.Filtered, "the item is filtered out or has no path");
        }

        // A Link mapping links every version of a film, as the scan does. A Copy mapping lists none. A
        // version another server provides is left to it.
        var others = new List<ContentRefreshWork>();
        UpdateSyncTablesTask.AddOtherVersions(others, source, mapping, dto, watchedByAll);
        var records = new List<SyncItem?> { record };
        foreach (var item in others)
        {
            if (HigherPriorityServer(scanServers, originIndex, origin, mapping, item.SourceItem.Path!) is null)
            {
                records.Add(await refresh.RefreshOneAsync(item, cancellationToken).ConfigureAwait(false));
            }
        }

        // Each version is applied on its own, so one that fails does not hold back the others. A retry
        // finds the ones already done synced and applies only the rest.
        string? failure = null;
        var applied = new List<SyncItem>();
        foreach (var row in records.Where(r => r is not null && r.Status == SyncStatus.Queued))
        {
            var download = ActivatorUtilities.CreateInstance<SyncMissingContentTask>(_services);
            if (await download.ApplyRowAsync(row!, source, cancellationToken).ConfigureAwait(false))
            {
                applied.Add(row!);
                _logger.LogInformation(
                    "{Action} {Path} on a hint from '{Origin}'",
                    StorageModes.IsLink(row!.StorageMode) ? "Linked" : "Downloaded",
                    row.LocalPath,
                    origin.DisplayName);
            }
            else
            {
                failure ??= row!.Reason ?? (StorageModes.IsLink(row.StorageMode) ? "the link could not be written" : "the download failed");
            }
        }

        if (failure is not null)
        {
            return HintApplyResult.RetryLater(failure);
        }

        if (applied.Count == 0)
        {
            return record.Status switch
            {
                SyncStatus.Pending => HintApplyResult.Kept(HintCode.AwaitingApproval, "waiting for approval on this server"),
                SyncStatus.Ignored => HintApplyResult.Dropped(HintCode.Ignored, "the row is ignored on this server"),
                _ => HintApplyResult.Unchanged
            };
        }

        // The file is new, so the library may not have it yet. Then the file's name stands in.
        var first = applied[0];
        return HintApplyResult.AppliedTo(string.IsNullOrEmpty(first.LocalPath) ? null : _libraryManager.FindByPath(first.LocalPath, false));
    }

    // The first server in the pull order, ahead of the origin, that already provides the file at this
    // path, as a copy or a link, or null when none does.
    private string? HigherPriorityServer(List<SourceServer> scanServers, int originIndex, SourceServer origin, LibraryMapping mapping, string sourcePath)
    {
        var localPath = PathUtilities.TranslatePath(sourcePath, mapping.SourceRootPath, mapping.LocalRootPath);
        var tracked = _table.GetByLocalPath(localPath)
            ?? (Links.LinkPaths.LinkPathFor(localPath) is { } linkPath && _table.GetByLocalPath(linkPath) is { } link && StorageModes.IsLink(link.StorageMode) ? link : null);
        if (tracked is null || string.Equals(tracked.ServerKey, origin.Key, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trackedIndex = scanServers.FindIndex(s => string.Equals(s.Key, tracked.ServerKey, StringComparison.OrdinalIgnoreCase));
        return trackedIndex >= 0 && trackedIndex < originIndex ? scanServers[trackedIndex].DisplayName : null;
    }

    // Whether every user the content filter names has played the item on the origin, which the scan
    // records as Ignored rather than downloading. A user that cannot be read counts as not having played
    // it, so a filter that cannot be checked never hides a file.
    private async Task<bool> IsWatchedByAllAsync(SourceServerClient client, Guid itemId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!config.SkipWatchedByAllUsers || config.WatchedFilterUserIds is not { Count: > 0 })
        {
            return false;
        }

        // The filter names users of several servers. Only the ones that exist on the origin are asked
        // about, and an origin with none of them has no filter to apply.
        HashSet<Guid> originUsers;
        try
        {
            originUsers = (await client.GetUsersAsync(null, cancellationToken).ConfigureAwait(false))
                .Where(u => u.Id.HasValue)
                .Select(u => u.Id!.Value)
                .ToHashSet();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the users of the origin for the watched by all filter");
            return false;
        }

        var filterUsers = config.WatchedFilterUserIds
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
            .Where(originUsers.Contains)
            .ToList();
        if (filterUsers.Count == 0)
        {
            return false;
        }

        foreach (var userId in filterUsers)
        {
            try
            {
                var page = await client.GetItemsWithUserDataByIdsAsync(userId, new Guid?[] { itemId }, cancellationToken).ConfigureAwait(false);
                if (page?.Items?.FirstOrDefault()?.UserData?.Played != true)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read whether user {User} played item {Item}", userId, itemId);
                return false;
            }
        }

        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Configuration;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tasks.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>
/// Applies a content hint through Sync Content's own refresh and download, so the file arrives the way a scan would bring it.
/// A file a higher ranked server already tracks is left to that server.
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
    /// <param name="services">Builds the refresh and download tasks for one row.</param>
    /// <param name="configManager">Reads the Content settings and how the sources rank.</param>
    /// <param name="table">Tells which server already tracks a file.</param>
    /// <param name="libraryManager">Finds the new local item, for its title.</param>
    /// <param name="logger">Writes each file linked or downloaded.</param>
    public ContentHintHandler(IServiceProvider services, IPluginConfigurationManager configManager, ContentSyncTableManager table, ILibraryManager libraryManager, ILogger<ContentHintHandler> logger)
    {
        _services = services;
        _configManager = configManager;
        _table = table;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Applies one content hint.</summary>
    /// <param name="hint">The hint that names the file on the origin.</param>
    /// <param name="origin">The server entry of the origin, whose library mappings apply.</param>
    /// <param name="client">Reads the item from the origin.</param>
    /// <param name="cancellationToken">Cancels the read and the download.</param>
    /// <returns>How the hint ended.</returns>
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

        var dto = await client.FindItemDetailsAsync(originItemId, cancellationToken).ConfigureAwait(false);
        // An answer with no item means the file is gone. A failed read throws.
        if (dto is null || !dto.Id.HasValue)
        {
            var held = _table.GetByKey(originItemId.ToString("N", System.Globalization.CultureInfo.InvariantCulture));

            // A link holds nothing, so it is settled without the scan's safety limits.
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

        // A filtered file that arrived by hint would be removed by the next scan.
        if (PathUtilities.IsItemFiltered(dto.Path, mapping.SourceRootPath, mapping.FilterMode, mapping.FilteredItems))
        {
            return HintApplyResult.Dropped(HintCode.Filtered, mapping.FilterMode == LibraryFilterMode.Whitelist
                ? "the file is not on the library mapping's allow list"
                : "the file is on the library mapping's block list");
        }

        // A .strm points at a file the origin does not hold.
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

        var firstFrom = await client.OriginOfAsync(dto.Path, cancellationToken).ConfigureAwait(false);
        var localServerId = _services.GetService<MediaBrowser.Controller.IServerApplicationHost>()?.SystemId;

        // A file the announcing server was itself given is left to its origin.
        if (Links.AlwaysKeep.StorageFor(mapping, dto.Path) != StorageMode.Link
            && UpdateSyncTablesTask.DecidedElsewhere(config, localServerId, origin.Key, firstFrom, PathUtilities.TranslatePath(dto.Path, mapping.SourceRootPath, mapping.LocalRootPath)) is { } decider)
        {
            return HintApplyResult.Dropped(HintCode.Priority, $"the file first came from {decider}, which decides about it");
        }

        var source = new ScanSource(origin, client, Math.Max(0, originIndex));
        var refresh = ActivatorUtilities.CreateInstance<UpdateSyncTablesTask>(_services);

        var record = await refresh.RefreshOneAsync(new ContentRefreshWork(source, mapping, dto, watchedByAll), cancellationToken).ConfigureAwait(false);
        // When the hinted item is passed over, its other versions are passed over too.
        if (record is null)
        {
            return refresh.LinkingRefusedBy(origin.Key)
                ? HintApplyResult.Dropped(HintCode.Filtered, $"'{origin.DisplayName}' does not offer links to this server, so the file waits until linking is allowed there")
                : HintApplyResult.Dropped(HintCode.Filtered, "the item is filtered out or has no path");
        }

        var others = new List<ContentRefreshWork>();
        UpdateSyncTablesTask.AddOtherVersions(others, source, mapping, dto, watchedByAll);
        var records = new List<SyncItem?> { record };
        foreach (var item in others)
        {
            // A version a higher ranked server provides is left to it.
            if (HigherPriorityServer(scanServers, originIndex, origin, mapping, item.SourceItem.Path!) is null)
            {
                records.Add(await refresh.RefreshOneAsync(item, cancellationToken).ConfigureAwait(false));
            }
        }

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
                    FormatUtilities.SanitizeForLog(row.LocalPath),
                    FormatUtilities.SanitizeForLog(origin.DisplayName));
            }
            else
            {
                failure ??= row!.Reason ?? (StorageModes.IsLink(row.StorageMode) ? "the link could not be written" : "the download failed");
            }
        }

        // Versions apply one by one, so a retry applies only the ones that failed.
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

        var first = applied[0];
        return HintApplyResult.AppliedTo(string.IsNullOrEmpty(first.LocalPath) ? null : _libraryManager.FindByPath(first.LocalPath, false));
    }

    /// <summary>Names the first server ranked above the origin that already provides the file at this path, or null when none does.</summary>
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

    /// <summary>
    /// Whether every user the content filter names has played the item on the origin, which the scan records as Ignored.
    /// A user that cannot be read counts as not having played it, so a filter that cannot be checked never hides a file.
    /// </summary>
    private async Task<bool> IsWatchedByAllAsync(SourceServerClient client, Guid itemId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!config.SkipWatchedByAllUsers || config.WatchedFilterUserIds is not { Count: > 0 })
        {
            return false;
        }

        HashSet<Guid> originUsers;
        try
        {
            originUsers = (await client.GetUsersAsync(null, cancellationToken).ConfigureAwait(false))
                .Where(u => u.Id.HasValue)
                .Select(u => u.Id!.Value)
                .ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read the users of the origin for the watched by all filter");
            return false;
        }

        var filterUsers = config.WatchedFilterUserIds
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
            .Where(originUsers.Contains)
            .ToList();
        // An origin with none of the filter's users has no filter to apply.
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
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not read whether user {User} played item {Item}", userId, itemId);
                return false;
            }
        }

        return true;
    }
}

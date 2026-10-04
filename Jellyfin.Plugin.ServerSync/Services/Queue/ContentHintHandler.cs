using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Tasks;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Utilities;
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
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly ILogger<ContentHintHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentHintHandler"/> class.
    /// </summary>
    /// <param name="services">The service provider the module tasks are built from.</param>
    /// <param name="configManager">Plugin configuration.</param>
    /// <param name="table">The content table.</param>
    /// <param name="libraryManager">The library, which names the item the activity log mentions.</param>
    /// <param name="logger">Logger.</param>
    public ContentHintHandler(IServiceProvider services, IPluginConfigurationManager configManager, ContentSyncTableManager table, MediaBrowser.Controller.Library.ILibraryManager libraryManager, ILogger<ContentHintHandler> logger)
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
            return HintApplyResult.Dropped("content sync is off on this server");
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

        // A failed read throws and is retried by the worker. An answer with no item means it is gone.
        var dto = await client.FindItemDetailsAsync(originItemId, cancellationToken).ConfigureAwait(false);
        if (dto is null || !dto.Id.HasValue)
        {
            return HintApplyResult.Dropped($"the item no longer exists on '{origin.DisplayName}'");
        }

        if (string.IsNullOrEmpty(dto.Path))
        {
            return HintApplyResult.Dropped($"'{origin.DisplayName}' did not say where the file is, which happens when the key for it is not an administrator's");
        }

        // The same filters the scan applies. A file the mapping leaves out must not arrive by hint, since
        // the next scan would find it out of place and a mapping that deletes stale files would remove it.
        if (PathUtilities.IsItemFiltered(dto.Path, mapping.SourceRootPath, mapping.FilterMode, mapping.FilteredItems))
        {
            return HintApplyResult.Dropped(mapping.FilterMode == LibraryFilterMode.Whitelist
                ? "the file is not on the library mapping's allow list"
                : "the file is on the library mapping's block list");
        }

        var watchedByAll = await IsWatchedByAllAsync(client, originItemId, config, cancellationToken).ConfigureAwait(false);

        // Priority: the earliest configured server that offers a file is the one that provides it.
        var scanServers = config.GetPullServers();
        var originIndex = scanServers.FindIndex(s => string.Equals(s.Key, origin.Key, StringComparison.OrdinalIgnoreCase));
        var localPath = PathUtilities.TranslatePath(dto.Path, mapping.SourceRootPath, mapping.LocalRootPath);
        var tracked = _table.GetByLocalPath(localPath);
        if (tracked is not null && !string.Equals(tracked.ServerKey, origin.Key, StringComparison.OrdinalIgnoreCase))
        {
            var trackedIndex = scanServers.FindIndex(s => string.Equals(s.Key, tracked.ServerKey, StringComparison.OrdinalIgnoreCase));
            if (trackedIndex >= 0 && trackedIndex < originIndex)
            {
                return HintApplyResult.Dropped($"'{scanServers[trackedIndex].DisplayName}' has priority for this file");
            }
        }

        var source = new ScanSource(origin, client, Math.Max(0, originIndex));
        var refresh = ActivatorUtilities.CreateInstance<UpdateSyncTablesTask>(_services);
        var record = await refresh.RefreshOneAsync(new ContentRefreshWork(source, mapping, dto, watchedByAll), cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return HintApplyResult.Dropped("the item is filtered out or has no path");
        }

        switch (record.Status)
        {
            case SyncStatus.Queued:
                break;
            case SyncStatus.Pending:
                return new HintApplyResult(HintApplyOutcome.Unchanged, "waiting for approval on this server");
            case SyncStatus.Ignored:
                return HintApplyResult.Dropped("the row is ignored on this server");
            default:
                return HintApplyResult.Unchanged;
        }

        var download = ActivatorUtilities.CreateInstance<SyncMissingContentTask>(_services);
        if (!await download.ApplyRowAsync(record, source, cancellationToken).ConfigureAwait(false))
        {
            return HintApplyResult.RetryLater(record.Reason ?? "the download failed");
        }

        _logger.LogInformation("Downloaded {Path} on a hint from '{Origin}'", record.LocalPath, origin.DisplayName);

        // The file is new, so the library may not have it yet. Then the file's name stands in.
        return HintApplyResult.AppliedTo(string.IsNullOrEmpty(record.LocalPath) ? null : _libraryManager.FindByPath(record.LocalPath, false));
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

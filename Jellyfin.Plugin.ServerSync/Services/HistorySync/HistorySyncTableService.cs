using System;
using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Builder helpers for <see cref="HistorySyncItem"/> records. Translates a
/// source <see cref="BaseItemDto"/> + (user, library) mapping context into
/// a fully-populated record with merged target state. Used by the Refresh
/// task and by inbound history hints. The Sync task applies the merged values
/// via <see cref="LocalServerClient"/>.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class HistorySyncTableService
{
    private readonly ILogger<HistorySyncTableService> _logger;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILocalItems _localItems;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistorySyncTableService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="userManager">Finds the local user.</param>
    /// <param name="userDataManager">Reads the local user data.</param>
    /// <param name="localItems">Finds local items by path.</param>
    public HistorySyncTableService(
        ILogger<HistorySyncTableService> logger,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILocalItems localItems)
    {
        _logger = logger;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _localItems = localItems;
    }

    /// <summary>
    /// Finds the local user a mapping points at, and logs why when there is none.
    /// </summary>
    /// <param name="userMapping">The user mapping.</param>
    /// <returns>The user, or null when the id is invalid or the user is gone.</returns>
    public User? FindLocalUser(UserMapping userMapping)
    {
        ArgumentNullException.ThrowIfNull(userMapping);
        if (!Guid.TryParse(userMapping.LocalUserId, out var localUserId))
        {
            _logger.LogWarning("Skipping history record: invalid local user ID {Id}", userMapping.LocalUserId);
            return null;
        }

        var localUser = _userManager.GetUserById(localUserId);
        if (localUser == null)
        {
            _logger.LogWarning("Skipping history record: local user {Id} not found", localUserId);
        }

        return localUser;
    }

    /// <summary>
    /// Builds a <see cref="HistorySyncItem"/> from a source item under the
    /// given mapping context. Returns null when the source path is missing,
    /// the local user is unknown, the library filter excludes the path, or
    /// the local item can't be found by translated path.
    /// </summary>
    /// <param name="userMapping">The user mapping the item is read under.</param>
    /// <param name="libraryMapping">The library mapping the item came through.</param>
    /// <param name="sourceItem">The item as the source returned it, with its user data.</param>
    /// <param name="sourceItemId">The item's id on the source.</param>
    /// <param name="negotiateWithSource"><c>true</c> to settle a conflict with the source, not only with this server.</param>
    /// <param name="findUser">Finds the local user for a mapping, or null to look the user up here. A run that builds many rows passes one that remembers its answers.</param>
    /// <returns>The row, or null when the item is not to be tracked.</returns>
    public HistorySyncItem? BuildRecord(
        UserMapping userMapping,
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        string sourceItemId,
        bool negotiateWithSource = false,
        Func<UserMapping, User?>? findUser = null)
    {
        ArgumentNullException.ThrowIfNull(userMapping);
        ArgumentNullException.ThrowIfNull(libraryMapping);
        ArgumentNullException.ThrowIfNull(sourceItem);

        var sourcePath = sourceItem.Path;
        if (string.IsNullOrEmpty(sourcePath))
        {
            return null;
        }

        // Library-filter exclusion, items matching the filter are not
        // tracked. Returning null here causes the base's prune pass to
        // delete any pre-existing row for this key.
        if (libraryMapping.FilterMode != LibraryFilterMode.AllowAll
            && libraryMapping.FilteredItems?.Count > 0
            && PathUtilities.IsItemFiltered(sourcePath, libraryMapping.SourceRootPath, libraryMapping.FilterMode, libraryMapping.FilteredItems))
        {
            return null;
        }

        var localUser = (findUser ?? FindLocalUser)(userMapping);
        if (localUser == null)
        {
            return null;
        }

        var localPath = PathUtilities.TranslatePath(sourcePath, libraryMapping.SourceRootPath, libraryMapping.LocalRootPath);
        var localItem = _localItems.FindByPath(localPath, isFolder: false);
        if (localItem == null)
        {
            return null;
        }

        var localItemId = localItem.Id.ToString("N", CultureInfo.InvariantCulture);
        var localUserData = _userDataManager.GetUserData(localUser, localItem);

        var item = new HistorySyncItem
        {
            SourceUserId = userMapping.SourceUserId,
            LocalUserId = userMapping.LocalUserId,
            SourceLibraryId = libraryMapping.SourceLibraryId,
            LocalLibraryId = libraryMapping.LocalLibraryId ?? string.Empty,
            SourceItemId = sourceItemId,
            LocalItemId = localItemId,
            ItemName = sourceItem.Name ?? System.IO.Path.GetFileNameWithoutExtension(sourcePath),
            SourcePath = sourcePath,
            LocalPath = localPath,

            SourceIsPlayed = sourceItem.UserData?.Played,
            SourcePlayCount = sourceItem.UserData?.PlayCount,
            SourcePlaybackPositionTicks = sourceItem.UserData?.PlaybackPositionTicks,
            SourceLastPlayedDate = sourceItem.UserData?.LastPlayedDate?.UtcDateTime,
            SourceIsFavorite = sourceItem.UserData?.IsFavorite,

            LocalIsPlayed = localUserData?.Played,
            LocalPlayCount = localUserData?.PlayCount,
            LocalPlaybackPositionTicks = localUserData?.PlaybackPositionTicks,
            LocalLastPlayedDate = localUserData?.LastPlayedDate,
            LocalIsFavorite = localUserData?.IsFavorite,
            NegotiateWithSource = negotiateWithSource
        };

        HistorySyncMergeService.MergeHistoryData(item);
        item.UpdateSourceStateBundle();
        return item;
    }
}

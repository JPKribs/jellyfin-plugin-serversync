using System;
using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.HistorySync;

/// <summary>
/// Builds a <see cref="HistorySyncItem"/> with its merged state from a source item, for a scan or an inbound hint.
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
    /// <param name="logger">Writes log entries.</param>
    /// <param name="userManager">Finds the local user of a mapping.</param>
    /// <param name="userDataManager">Reads the local user's history for an item.</param>
    /// <param name="localItems">Finds local items by path.</param>
    public HistorySyncTableService(
        ILogger<HistorySyncTableService> logger,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILocalItems localItems)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(userDataManager);
        ArgumentNullException.ThrowIfNull(localItems);
        _logger = logger;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _localItems = localItems;
    }

    /// <summary>
    /// Finds the local user a mapping points at, and logs why when there is none.
    /// </summary>
    /// <param name="userMapping">The user mapping whose local user is wanted.</param>
    /// <returns>The user, or null when the id is invalid or the user is gone.</returns>
    public User? FindLocalUser(UserMapping userMapping)
    {
        ArgumentNullException.ThrowIfNull(userMapping);
        if (!Guid.TryParse(userMapping.LocalUserId, out var localUserId))
        {
            _logger.LogWarning("Skipping history record: invalid local user ID {Id}", FormatUtilities.SanitizeForLog(userMapping.LocalUserId));
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
    /// Builds the history row for a source item under one user mapping and one library mapping.
    /// Returns null when the item has no path, the Library filter leaves it out, or the local user or local item is missing.
    /// </summary>
    /// <param name="userMapping">The user mapping the item is read under.</param>
    /// <param name="libraryMapping">The library mapping the item came through.</param>
    /// <param name="sourceItem">The item as the source returned it, with its user data.</param>
    /// <param name="sourceItemId">The item's id on the source, stored as half of the row's key.</param>
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
        ArgumentNullException.ThrowIfNull(sourceItemId);

        var sourcePath = sourceItem.Path;
        if (string.IsNullOrEmpty(sourcePath))
        {
            return null;
        }

        // A filtered item returns null so the scan prunes any row it had.
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

using System;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Builder helpers for <see cref="HistorySyncItem"/> records. Translates a
/// source <see cref="BaseItemDto"/> + (user, library) mapping context into
/// a fully-populated record with merged target state. Used by the Refresh
/// task; the Sync task calls the record's <see cref="HistorySyncMergeService"/>
/// directly and applies the merged values via <see cref="LocalServerClient"/>.
/// </summary>
public class HistorySyncTableService
{
    private readonly ILogger<HistorySyncTableService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public HistorySyncTableService(
        ILogger<HistorySyncTableService> logger,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <summary>
    /// Builds a <see cref="HistorySyncItem"/> from a source item under the
    /// given mapping context. Returns null when the source path is missing,
    /// the local user is unknown, the library filter excludes the path, or
    /// the local item can't be found by translated path.
    /// </summary>
    public HistorySyncItem? BuildRecord(
        UserMapping userMapping,
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        string sourceItemId)
    {
        ArgumentNullException.ThrowIfNull(userMapping);
        ArgumentNullException.ThrowIfNull(libraryMapping);
        ArgumentNullException.ThrowIfNull(sourceItem);

        var sourcePath = sourceItem.Path;
        if (string.IsNullOrEmpty(sourcePath))
        {
            return null;
        }

        // Library-filter exclusion — items matching the filter are not
        // tracked. Returning null here causes the base's prune pass to
        // delete any pre-existing row for this key.
        if (libraryMapping.FilterMode != LibraryFilterMode.AllowAll
            && libraryMapping.FilteredItems?.Count > 0
            && PathUtilities.IsItemFiltered(sourcePath, libraryMapping.SourceRootPath, libraryMapping.FilterMode, libraryMapping.FilteredItems))
        {
            return null;
        }

        if (!Guid.TryParse(userMapping.LocalUserId, out var localUserId))
        {
            _logger.LogWarning("Skipping history record: invalid local user ID {Id}", userMapping.LocalUserId);
            return null;
        }

        var localUser = _userManager.GetUserById(localUserId);
        if (localUser == null)
        {
            _logger.LogWarning("Skipping history record: local user {Id} not found", localUserId);
            return null;
        }

        var localPath = PathUtilities.TranslatePath(sourcePath, libraryMapping.SourceRootPath, libraryMapping.LocalRootPath);
        var localItem = _libraryManager.FindByPath(localPath, isFolder: false);
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
            LocalIsFavorite = localUserData?.IsFavorite
        };

        HistorySyncMergeService.MergeHistoryData(item);
        return item;
    }
}

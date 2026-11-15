using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Reads and writes items, users, and user data on this server for History sync.
/// </summary>
[PluginService(ServiceLifetime.Transient)]
public class LocalServerClient
{
    private readonly ILogger<LocalServerClient> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalServerClient"/> class.
    /// </summary>
    /// <param name="logger">Writes log entries.</param>
    /// <param name="libraryManager">Finds this server's items by id and by path.</param>
    /// <param name="userManager">Finds this server's users.</param>
    /// <param name="userDataManager">Reads and saves each user's history for an item.</param>
    public LocalServerClient(
        ILogger<LocalServerClient> logger,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(userDataManager);

        _logger = logger;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <summary>
    /// Finds an item on this server by its file path.
    /// </summary>
    /// <param name="path">The file's path on this server.</param>
    /// <returns>The item, or null when none has that path or the lookup failed.</returns>
    public BaseItem? GetItemByPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            return _libraryManager.FindByPath(path, isFolder: false);
        }
        // A failed lookup reads as not found, so one item never stops a sync.
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to find item at path: {Path}", FormatUtilities.SanitizeForLog(path));
            return null;
        }
    }

    /// <summary>
    /// Gets a user on this server by id.
    /// </summary>
    /// <param name="userId">The id of the user on this server.</param>
    /// <returns>The user, or null when there is none or the lookup failed.</returns>
    public User? GetUser(Guid userId)
    {
        try
        {
            return _userManager.GetUserById(userId);
        }
        // A failed lookup reads as not found, so one item never stops a sync.
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get user: {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets a user's history for an item on this server.
    /// </summary>
    /// <param name="userId">The user whose history is read.</param>
    /// <param name="itemId">The item the history belongs to.</param>
    /// <returns>The history, or null when the user or item is missing or the read failed.</returns>
    public UserItemData? GetUserItemData(Guid userId, Guid itemId)
    {
        try
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return null;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return null;
            }

            return _userDataManager.GetUserData(user, item);
        }
        // A failed lookup reads as not found, so one item never stops a sync.
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get user data for user {UserId}, item {ItemId}", userId, itemId);
            return null;
        }
    }

    /// <summary>
    /// Gets a user's history for the item at a file path on this server.
    /// </summary>
    /// <param name="userId">The user whose history is read.</param>
    /// <param name="path">The item's file path on this server.</param>
    /// <returns>The history, or null when the user or item is missing or the read failed.</returns>
    public UserItemData? GetUserItemDataByPath(Guid userId, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return null;
            }

            var item = _libraryManager.FindByPath(path, isFolder: false);
            if (item == null)
            {
                return null;
            }

            return _userDataManager.GetUserData(user, item);
        }
        // A failed lookup reads as not found, so one item never stops a sync.
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get user data for user {UserId}, path {Path}", userId, FormatUtilities.SanitizeForLog(path));
            return null;
        }
    }

    /// <summary>
    /// Writes the given parts of a user's history for an item, leaving each null part as it is.
    /// </summary>
    /// <param name="userId">The user whose history is written.</param>
    /// <param name="itemId">The item the history belongs to.</param>
    /// <param name="played">The played state to set, or null to keep it.</param>
    /// <param name="playCount">The play count to set, or null to keep it.</param>
    /// <param name="playbackPositionTicks">The resume position to set, in ticks, or null to keep it.</param>
    /// <param name="lastPlayedDate">The last played date to set, or null to keep it.</param>
    /// <param name="isFavorite">The favorite state to set, or null to keep it.</param>
    /// <param name="clearLastPlayedDate">Whether a null <paramref name="lastPlayedDate"/> removes the stored date, as Jellyfin records an unplayed item.</param>
    /// <returns><c>true</c> when the history was saved.</returns>
    public bool UpdateUserItemData(
        Guid userId,
        Guid itemId,
        bool? played,
        int? playCount,
        long? playbackPositionTicks,
        DateTime? lastPlayedDate,
        bool? isFavorite,
        bool clearLastPlayedDate = false)
    {
        try
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                _logger.LogWarning("User not found: {UserId}", userId);
                return false;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                _logger.LogWarning("Item not found: {ItemId}", itemId);
                return false;
            }

            var userData = _userDataManager.GetUserData(user, item);
            if (userData == null)
            {
                _logger.LogWarning("Could not get user data for user {UserId}, item {ItemId}", userId, itemId);
                return false;
            }

            if (played.HasValue)
            {
                userData.Played = played.Value;
            }

            if (playCount.HasValue)
            {
                userData.PlayCount = playCount.Value;
            }

            if (playbackPositionTicks.HasValue)
            {
                userData.PlaybackPositionTicks = playbackPositionTicks.Value;
            }

            if (lastPlayedDate.HasValue)
            {
                userData.LastPlayedDate = lastPlayedDate.Value;
            }
            else if (clearLastPlayedDate)
            {
                userData.LastPlayedDate = null;
            }

            if (isFavorite.HasValue)
            {
                userData.IsFavorite = isFavorite.Value;
            }

            _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserData, default);

            _logger.LogDebug(
                "Updated user data for user {UserId}, item {ItemId}: Played={Played}, PlayCount={PlayCount}, Position={Position}",
                userId, itemId, userData.Played, userData.PlayCount, userData.PlaybackPositionTicks);

            return true;
        }
        // A failed save reports false, so one item never stops a sync.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update user data for user {UserId}, item {ItemId}", userId, itemId);
            return false;
        }
    }

    /// <summary>
    /// Sets whether a user has played an item.
    /// </summary>
    /// <param name="userId">The user whose history is written.</param>
    /// <param name="itemId">The item to mark.</param>
    /// <param name="played">Whether the item is played.</param>
    /// <param name="datePlayed">The last played date to store when marking as played, or null to keep it.</param>
    /// <returns><c>true</c> when the history was saved.</returns>
    public bool SetPlayed(Guid userId, Guid itemId, bool played, DateTime? datePlayed = null)
    {
        try
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return false;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return false;
            }

            var userData = _userDataManager.GetUserData(user, item);
            if (userData == null)
            {
                return false;
            }

            userData.Played = played;

            if (played && datePlayed.HasValue)
            {
                userData.LastPlayedDate = datePlayed.Value;
            }

            _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserData, default);
            return true;
        }
        // A failed save reports false, so one item never stops a sync.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set played status for user {UserId}, item {ItemId}", userId, itemId);
            return false;
        }
    }

    /// <summary>
    /// Sets whether an item is a user's favorite.
    /// </summary>
    /// <param name="userId">The user whose history is written.</param>
    /// <param name="itemId">The item to mark.</param>
    /// <param name="isFavorite">Whether the item is a favorite.</param>
    /// <returns><c>true</c> when the history was saved.</returns>
    public bool SetFavorite(Guid userId, Guid itemId, bool isFavorite)
    {
        try
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return false;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return false;
            }

            var userData = _userDataManager.GetUserData(user, item);
            if (userData == null)
            {
                return false;
            }

            userData.IsFavorite = isFavorite;

            _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserData, default);
            return true;
        }
        // A failed save reports false, so one item never stops a sync.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set favorite status for user {UserId}, item {ItemId}", userId, itemId);
            return false;
        }
    }

    /// <summary>
    /// Gets every item that is not a folder in a library on this server.
    /// </summary>
    /// <param name="libraryId">The id of the library on this server.</param>
    /// <returns>The library's items, or none when the library is missing or the read failed.</returns>
    public List<BaseItem> GetLibraryItems(Guid libraryId)
    {
        try
        {
            if (_libraryManager.GetItemById(libraryId) is not Folder folder)
            {
                return new List<BaseItem>();
            }

            return folder.GetRecursiveChildren()
                .Where(i => i is not Folder)
                .ToList();
        }
        // A failed lookup reads as not found, so one item never stops a sync.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get files for {LibraryId}", libraryId);
            return new List<BaseItem>();
        }
    }

    /// <summary>
    /// Gets every item in a library on this server with a user's history for it.
    /// </summary>
    /// <param name="userId">The user whose history is read.</param>
    /// <param name="libraryId">The id of the library on this server.</param>
    /// <returns>Each item with its history, or none when the user is missing.</returns>
    public List<(BaseItem Item, UserItemData? UserData)> GetLibraryItemsWithUserData(Guid userId, Guid libraryId)
    {
        var result = new List<(BaseItem, UserItemData?)>();

        try
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                return result;
            }

            foreach (var item in GetLibraryItems(libraryId))
            {
                result.Add((item, _userDataManager.GetUserData(user, item)));
            }
        }
        // A failed lookup reads as not found, so one item never stops a sync.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get files with user data for user {UserId}, library {LibraryId}", userId, libraryId);
        }

        return result;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Client for accessing local Jellyfin server APIs.
/// Used by History Sync to read and update user data on the local server.
/// </summary>
public class LocalServerClient
{
    private readonly ILogger<LocalServerClient> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalServerClient"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    public LocalServerClient(
        ILogger<LocalServerClient> logger,
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
    /// Finds a local item by its file path.
    /// </summary>
    /// <param name="path">File path to search for.</param>
    /// <returns>The local item if found, null otherwise.</returns>
    public BaseItem? GetItemByPath(string path)
    {
        try
        {
            return _libraryManager.FindByPath(path, isFolder: false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to find item at path: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Gets the user by their GUID.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <returns>User if found, null otherwise.</returns>
    public User? GetUser(Guid userId)
    {
        try
        {
            return _userManager.GetUserById(userId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get user: {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets a user's playback data for a specific item.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="itemId">Item ID.</param>
    /// <returns>User item data if found, null otherwise.</returns>
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get user data for user {UserId}, item {ItemId}", userId, itemId);
            return null;
        }
    }

    /// <summary>
    /// Gets a user's playback data for an item by path.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="path">Item file path.</param>
    /// <returns>User item data if found, null otherwise.</returns>
    public UserItemData? GetUserItemDataByPath(Guid userId, string path)
    {
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get user data for user {UserId}, path {Path}", userId, path);
            return null;
        }
    }

    /// <summary>
    /// Updates user playback data for an item.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="played">Played status.</param>
    /// <param name="playCount">Play count.</param>
    /// <param name="playbackPositionTicks">Playback position in ticks.</param>
    /// <param name="lastPlayedDate">Last played date.</param>
    /// <param name="isFavorite">Favorite status.</param>
    /// <returns>True if update succeeded.</returns>
    public bool UpdateUserItemData(
        Guid userId,
        Guid itemId,
        bool? played,
        int? playCount,
        long? playbackPositionTicks,
        DateTime? lastPlayedDate,
        bool? isFavorite)
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

            // Apply updates
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

            if (isFavorite.HasValue)
            {
                userData.IsFavorite = isFavorite.Value;
            }

            // Save the updated data - SaveUserData takes User, not Guid
            _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserData, default);

            _logger.LogDebug(
                "Updated user data for user {UserId}, item {ItemId}: Played={Played}, PlayCount={PlayCount}, Position={Position}",
                userId, itemId, userData.Played, userData.PlayCount, userData.PlaybackPositionTicks);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update user data for user {UserId}, item {ItemId}", userId, itemId);
            return false;
        }
    }

    /// <summary>
    /// Sets the played status for an item.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="played">Whether the item is played.</param>
    /// <param name="datePlayed">Date when played (if marking as played).</param>
    /// <returns>True if successful.</returns>
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set played status for user {UserId}, item {ItemId}", userId, itemId);
            return false;
        }
    }

    /// <summary>
    /// Sets the favorite status for an item.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="itemId">Item ID.</param>
    /// <param name="isFavorite">Whether the item is a favorite.</param>
    /// <returns>True if successful.</returns>
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set favorite status for user {UserId}, item {ItemId}", userId, itemId);
            return false;
        }
    }

    /// <summary>
    /// Gets all items in a library folder.
    /// </summary>
    /// <param name="libraryId">Library folder ID.</param>
    /// <returns>List of items in the library.</returns>
    public List<BaseItem> GetLibraryItems(Guid libraryId)
    {
        try
        {
            var folder = _libraryManager.GetItemById(libraryId) as Folder;
            if (folder == null)
            {
                return new List<BaseItem>();
            }

            return folder.GetRecursiveChildren()
                .Where(i => i is not Folder)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get library items for {LibraryId}", libraryId);
            return new List<BaseItem>();
        }
    }

    /// <summary>
    /// Gets items with user data (played, favorites, etc.) for a specific user in a library.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <returns>List of items with their user data.</returns>
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

            var items = GetLibraryItems(libraryId);
            foreach (var item in items)
            {
                var userData = _userDataManager.GetUserData(user, item);
                result.Add((item, userData));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get library items with user data for user {UserId}, library {LibraryId}", userId, libraryId);
        }

        return result;
    }
}

using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>Finds this server's items and people for the hint pipeline.</summary>
public interface ILocalItems
{
    /// <summary>Finds the local item at a path, or this server's link standing in for it.</summary>
    /// <param name="path">The local path.</param>
    /// <returns>The item, or null.</returns>
    BaseItem? FindByPath(string path);

    /// <summary>Finds the local item at a path, or this server's link standing in for it when the path is a file.</summary>
    /// <param name="path">The local path.</param>
    /// <param name="isFolder">Whether the item is a folder, or null when either will do.</param>
    /// <returns>The item, or null.</returns>
    BaseItem? FindByPath(string path, bool? isFolder) => FindByPath(path);

    /// <summary>Finds a local person by name.</summary>
    /// <param name="name">The person's name.</param>
    /// <returns>The person, or null.</returns>
    Person? GetPerson(string name);
}

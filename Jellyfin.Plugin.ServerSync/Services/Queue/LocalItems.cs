using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>Finds this server's items and people for the hint pipeline.</summary>
public interface ILocalItems
{
    /// <summary>Finds the local item at a path, or this server's link standing in for it.</summary>
    /// <param name="path">The local path.</param>
    /// <returns>The item, or null.</returns>
    BaseItem? FindByPath(string path);

    /// <summary>
    /// Finds the local item at <paramref name="path"/>. When nothing is there and the path is a file,
    /// finds this server's link standing in for it instead.
    /// </summary>
    /// <param name="path">The local path.</param>
    /// <param name="isFolder">Whether the item is a folder, or null when either will do.</param>
    /// <returns>The item, or null.</returns>
    BaseItem? FindByPath(string path, bool? isFolder) => FindByPath(path);

    /// <summary>Finds a local person by name.</summary>
    /// <param name="name">The person's name.</param>
    /// <returns>The person, or null.</returns>
    Person? GetPerson(string name);
}

/// <summary>Finds this server's items and people through Jellyfin's library.</summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(ILocalItems))]
public sealed class LocalItems : ILocalItems
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILinkRegistry _links;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalItems"/> class.
    /// </summary>
    /// <param name="libraryManager">The library.</param>
    /// <param name="links">The link registry.</param>
    public LocalItems(ILibraryManager libraryManager, ILinkRegistry links)
    {
        _libraryManager = libraryManager;
        _links = links;
    }

    /// <inheritdoc />
    public BaseItem? FindByPath(string path) => FindByPath(path, null);

    /// <inheritdoc />
    public BaseItem? FindByPath(string path, bool? isFolder)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // The exact path always wins, so every match that worked before a link existed still returns the same item.
        var item = _libraryManager.FindByPath(path, isFolder);
        if (item is not null || isFolder == true)
        {
            return item;
        }

        return _links.LinkPathFor(path) is { } link ? _libraryManager.FindByPath(link, false) : null;
    }

    /// <inheritdoc />
    public Person? GetPerson(string name) => string.IsNullOrWhiteSpace(name) ? null : _libraryManager.GetPerson(name);
}

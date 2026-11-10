using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

public interface ILocalItems
{
    BaseItem? FindByPath(string path);

    /// <summary>
    /// Finds the local item at <paramref name="path"/>. When nothing is there and the path is a file,
    /// finds this server's link standing in for it instead.
    /// </summary>
    BaseItem? FindByPath(string path, bool? isFolder) => FindByPath(path);

    Person? GetPerson(string name);
}

[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(ILocalItems))]
public sealed class LocalItems : ILocalItems
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILinkRegistry _links;

    public LocalItems(ILibraryManager libraryManager, ILinkRegistry links)
    {
        _libraryManager = libraryManager;
        _links = links;
    }

    public BaseItem? FindByPath(string path) => FindByPath(path, null);

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

    public Person? GetPerson(string name) => string.IsNullOrWhiteSpace(name) ? null : _libraryManager.GetPerson(name);
}

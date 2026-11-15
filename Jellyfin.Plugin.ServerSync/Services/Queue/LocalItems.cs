using System;
using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

/// <summary>Finds this server's items and people through Jellyfin's library.</summary>
[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(ILocalItems))]
public sealed class LocalItems : ILocalItems
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILinkRegistry _links;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalItems"/> class.
    /// </summary>
    /// <param name="libraryManager">Finds items by path and people by name.</param>
    /// <param name="links">Names the link that stands in for a source file.</param>
    public LocalItems(ILibraryManager libraryManager, ILinkRegistry links)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(links);

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

        // An item at the exact path wins over a link standing in for it.
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

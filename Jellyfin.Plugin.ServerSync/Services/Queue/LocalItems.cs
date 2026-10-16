using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ServerSync.Services.Queue;

public interface ILocalItems
{
    BaseItem? FindByPath(string path);

    Person? GetPerson(string name);
}

[PluginService(ServiceLifetime.Singleton, ServiceType = typeof(ILocalItems))]
public sealed class LocalItems : ILocalItems
{
    private readonly ILibraryManager _libraryManager;

    public LocalItems(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public BaseItem? FindByPath(string path) => string.IsNullOrEmpty(path) ? null : _libraryManager.FindByPath(path, null);

    public Person? GetPerson(string name) => string.IsNullOrWhiteSpace(name) ? null : _libraryManager.GetPerson(name);
}

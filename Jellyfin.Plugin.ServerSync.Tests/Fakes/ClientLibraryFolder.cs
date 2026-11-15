using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// A library folder that holds the children a test gives it, so listing it needs no live library.
/// </summary>
internal sealed class ClientLibraryFolder : Folder
{
    private readonly List<BaseItem> _children = new();

    /// <inheritdoc/>
    public override IEnumerable<BaseItem> Children => _children;

    /// <summary>Adds a child to the folder.</summary>
    /// <param name="child">The item or folder to hold.</param>
    /// <returns>This folder.</returns>
    public ClientLibraryFolder With(BaseItem child)
    {
        _children.Add(child);
        return this;
    }
}

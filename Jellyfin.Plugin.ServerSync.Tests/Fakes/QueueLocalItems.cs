using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Stands in for this server's library in the hint pipeline. It holds the items and people a test adds and records what was asked for.
/// </summary>
public sealed class QueueLocalItems : ILocalItems
{
    private readonly Dictionary<string, BaseItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Person> _people = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the paths that were looked up, in order.</summary>
    public List<string> PathsAsked { get; } = new();

    /// <summary>Gets the names that were looked up, in order.</summary>
    public List<string> NamesAsked { get; } = new();

    /// <summary>Adds a film at a local path.</summary>
    /// <param name="path">The local path the film answers to.</param>
    /// <returns>The film.</returns>
    public BaseItem AddItem(string path)
    {
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
        _byPath[path] = item;
        return item;
    }

    /// <summary>Adds a person.</summary>
    /// <param name="name">The name the person answers to.</param>
    /// <returns>The person.</returns>
    public Person AddPerson(string name)
    {
        var person = new Person { Id = Guid.NewGuid(), Name = name };
        _people[name] = person;
        return person;
    }

    /// <inheritdoc/>
    public BaseItem? FindByPath(string path)
    {
        PathsAsked.Add(path);
        return _byPath.TryGetValue(path, out var item) ? item : null;
    }

    /// <inheritdoc/>
    public Person? GetPerson(string name)
    {
        NamesAsked.Add(name);
        return _people.TryGetValue(name, out var person) ? person : null;
    }
}

using System;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// Local items and people are found through Jellyfin's library, and an empty path or name never reaches it.
/// </summary>
public sealed class LocalItemsTests
{
    /// <summary>
    /// A missing collaborator is refused at construction.
    /// True: a wiring mistake fails at startup.
    /// False: it fails later, inside the first hint that needs the library.
    /// </summary>
    [Fact]
    public void Constructor_NullCollaborator_Throws()
    {
        var (library, _) = InterfaceStub.Create<ILibraryManager>();

        Assert.Throws<ArgumentNullException>(() => new LocalItems(null!, new NoLinks()));
        Assert.Throws<ArgumentNullException>(() => new LocalItems(library, null!));
    }

    /// <summary>
    /// An empty path finds nothing and the library is not asked.
    /// True: a hint without a path costs no library lookup.
    /// False: Jellyfin is asked for an empty path and may throw inside the worker.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FindByPath_EmptyPath_ReturnsNullWithoutAskingTheLibrary(string? path)
    {
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        var local = new LocalItems(library, new NoLinks());

        var found = local.FindByPath(path!);

        Assert.Null(found);
        Assert.Empty(stub.Calls);
    }

    /// <summary>
    /// A lookup by path alone asks the library for either a file or a folder.
    /// True: a hint for a series folder and one for a film are both found.
    /// False: folders are never matched and their hints wait until they are dropped.
    /// </summary>
    [Fact]
    public void FindByPath_PathAlone_AsksForFileOrFolder()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Path = "/local/movies/a.mkv" };
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        stub.Answer(nameof(ILibraryManager.FindByPath), _ => movie);
        var local = new LocalItems(library, new NoLinks());

        var found = local.FindByPath("/local/movies/a.mkv");

        Assert.Same(movie, found);
        var call = Assert.Single(stub.Calls);
        Assert.Equal("/local/movies/a.mkv", call.Args[0]);
        Assert.Null(call.Args[1]);
    }

    /// <summary>
    /// A blank name finds no person and the library is not asked.
    /// True: a people hint with no name is answered without a lookup.
    /// False: Jellyfin is asked for a blank person and throws.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetPerson_BlankName_ReturnsNullWithoutAskingTheLibrary(string? name)
    {
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        var local = new LocalItems(library, new NoLinks());

        var found = local.GetPerson(name!);

        Assert.Null(found);
        Assert.Empty(stub.Calls);
    }

    /// <summary>
    /// A name is looked up in the library and its person returned.
    /// True: a people hint finds the person it names.
    /// False: every people hint parks as if the person were missing.
    /// </summary>
    [Fact]
    public void GetPerson_KnownName_ReturnsTheLibrarysPerson()
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Jane Doe" };
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        stub.Answer(nameof(ILibraryManager.GetPerson), args => (string)args[0]! == "Jane Doe" ? person : null);
        var local = new LocalItems(library, new NoLinks());

        var found = local.GetPerson("Jane Doe");

        Assert.Same(person, found);
    }
}

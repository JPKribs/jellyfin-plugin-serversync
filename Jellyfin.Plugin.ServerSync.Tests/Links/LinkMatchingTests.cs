using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Links;

public class LinkMatchingTests
{
    private const string Copy = "/media/Movies/Movie (2020)/Movie (2020) - 4K.mkv";
    private const string Link = "/media/Movies/Movie (2020)/Movie (2020) - 4K.strm";

    /// <summary>
    /// With no links, a miss is still a miss and the library is asked exactly once.
    /// True: every lookup behaves as it did before links existed.
    /// False: a server that never uses links would pay for, or match on, a second lookup.
    /// </summary>
    [Fact]
    public void FindByPath_NoLinks_MissIsUnchanged()
    {
        var (library, stub) = Library(new Movie { Path = Link });

        var found = new LocalItems(library, new NoLinks()).FindByPath(Copy, false);

        Assert.Null(found);
        Assert.Single(stub.Calls, c => c.Name == nameof(ILibraryManager.FindByPath));
    }

    /// <summary>
    /// A path that misses finds this server's link standing in for it.
    /// True: history, metadata, and the rest match an item held here as a link.
    /// False: a linked item would look missing to every module that matches by path.
    /// </summary>
    [Fact]
    public void FindByPath_MissWithLink_FindsTheLink()
    {
        var link = new Movie { Path = Link };
        var (library, _) = Library(link);

        var found = new LocalItems(library, Links((Copy, Link))).FindByPath(Copy, false);

        Assert.Same(link, found);
    }

    /// <summary>
    /// The exact path wins over a link for the same name.
    /// True: a real file is always matched first, so nothing that matched before changes.
    /// False: a link could take the place of a real copy.
    /// </summary>
    [Fact]
    public void FindByPath_ExactPathWinsOverLink()
    {
        var copy = new Movie { Path = Copy };
        var (library, _) = Library(copy, new Movie { Path = Link });

        var found = new LocalItems(library, Links((Copy, Link))).FindByPath(Copy, false);

        Assert.Same(copy, found);
    }

    /// <summary>
    /// A .strm the user made is never matched in place of a missing file.
    /// True: only Server Sync's own links count.
    /// False: a user's .strm would start matching items it never matched before.
    /// </summary>
    [Fact]
    public void FindByPath_UsersOwnStrm_IsNotMatched()
    {
        var (library, _) = Library(new Movie { Path = Link });

        var found = new LocalItems(library, Links()).FindByPath(Copy, false);

        Assert.Null(found);
    }

    /// <summary>
    /// A folder lookup never falls back to a link.
    /// True: folders keep their path on every server and only files can be links.
    /// False: a folder lookup could return a file.
    /// </summary>
    [Fact]
    public void FindByPath_Folder_NeverFallsBack()
    {
        var (library, stub) = Library(new Movie { Path = Link });

        var found = new LocalItems(library, Links((Copy, Link))).FindByPath(Copy, true);

        Assert.Null(found);
        Assert.Single(stub.Calls, c => c.Name == nameof(ILibraryManager.FindByPath));
    }

    /// <summary>
    /// A link sits where the copy would, under the same name, with only the extension changed. A path
    /// that is already a .strm, or has no extension, has no link.
    /// True: sidecars and version suffixes keep pairing with the link.
    /// False: a link would not line up with its copy's name.
    /// </summary>
    [Theory]
    [InlineData("/m/Movie (2020) - 4K.mkv", "/m/Movie (2020) - 4K.strm")]
    [InlineData("/m/Show/Season 01/Show - S01E01 - Pilot.mp4", "/m/Show/Season 01/Show - S01E01 - Pilot.strm")]
    [InlineData("/m/Movie.STRM", null)]
    [InlineData("/m/Season 01", null)]
    [InlineData("", null)]
    public void LinkPathFor_SwapsOnlyTheExtension(string copied, string? expected)
    {
        Assert.Equal(expected, LinkPaths.LinkPathFor(copied));
    }

    /// <summary>
    /// A dotted folder name still gets a candidate, so the registry, never the name, decides whether a
    /// path is a link.
    /// True: a folder such as "Mr. Robot" is only a link if a link row says so.
    /// False: a dotted name alone would be matched as a link.
    /// </summary>
    [Fact]
    public void LinkPathFor_DottedFolder_IsOnlyACandidate()
    {
        Assert.Equal("/m/Mr.strm", LinkPaths.LinkPathFor("/m/Mr. Robot"));
        Assert.Null(new LocalItems(Library().Library, Links()).FindByPath("/m/Mr. Robot", false));
    }

    /// <summary>
    /// A path index gets each link again under its copy's path.
    /// True: the history and metadata refreshes find a linked item by the translated source path.
    /// False: those refreshes would skip every linked item.
    /// </summary>
    [Fact]
    public void AddAliases_LinkIsFoundUnderItsCopyPath()
    {
        var link = new Movie { Path = Link };
        var index = Index(link);

        LinkPaths.AddAliases(index, Links((Copy, Link)));

        Assert.Same(link, index[Copy]);
        Assert.Same(link, index[Link]);
    }

    /// <summary>
    /// An alias never replaces a real file at the same path.
    /// True: a real copy always wins in the index, whatever order the library returned items in.
    /// False: a link could hide a real file from history and metadata.
    /// </summary>
    [Fact]
    public void AddAliases_RealFileWins()
    {
        var link = new Movie { Path = Link };
        var copy = new Movie { Path = Copy };
        var index = Index(link, copy);

        LinkPaths.AddAliases(index, Links((Copy, Link)));

        Assert.Same(copy, index[Copy]);
    }

    /// <summary>
    /// A user's own .strm gets no alias.
    /// True: the index is unchanged when nothing is a link.
    /// False: a user's .strm would match source items it never matched before.
    /// </summary>
    [Fact]
    public void AddAliases_UsersOwnStrm_Unchanged()
    {
        var index = Index(new Movie { Path = Link });

        LinkPaths.AddAliases(index, Links());

        Assert.Equal(new[] { Link }, index.Keys.ToArray());
    }

    /// <summary>
    /// A link translates back to the source with the source file's real extension.
    /// True: the peer matches its own copy by path when history is recorded against it.
    /// False: the recorded source path would end in .strm and match nothing on the peer.
    /// </summary>
    [Fact]
    public void SourcePathFor_LinkKeepsRealExtension()
    {
        var source = LinkPaths.SourcePathFor(Link, "/media/Movies", "/data/films", Links((Copy, Link)));

        Assert.Equal(Path.Combine("/data/films", "Movie (2020)", "Movie (2020) - 4K.mkv"), source);
    }

    /// <summary>
    /// Any other path translates exactly as before.
    /// True: history recorded for real files and user .strm files is unchanged.
    /// False: existing source paths would change.
    /// </summary>
    [Theory]
    [InlineData(Copy)]
    [InlineData(Link)]
    public void SourcePathFor_NotALink_Unchanged(string local)
    {
        var source = LinkPaths.SourcePathFor(local, "/media/Movies", "/data/films", Links());

        Assert.Equal(Jellyfin.Plugin.ServerSync.Utilities.PathUtilities.TranslatePath(local, "/media/Movies", "/data/films"), source);
    }

    /// <summary>
    /// A row written for a link records the path its copy would have.
    /// True: a recorded row and a refreshed row for the same link share one priority key, so only one server claims it.
    /// False: the two rows would never collide and two servers could both write history to the link.
    /// </summary>
    [Fact]
    public void RowPathFor_LinkRecordsItsCopyPath()
    {
        Assert.Equal(Copy, LinkPaths.RowPathFor(Link, Links((Copy, Link))));
    }

    /// <summary>
    /// Any other path is recorded as it is.
    /// True: rows for real files and user .strm files are unchanged.
    /// False: existing rows would change their recorded path.
    /// </summary>
    [Theory]
    [InlineData(Copy)]
    [InlineData(Link)]
    public void RowPathFor_NotALink_Unchanged(string local)
    {
        Assert.Equal(local, LinkPaths.RowPathFor(local, Links()));
    }

    /// <summary>
    /// Only video is linked. Jellyfin serves an audio or book .strm as a plain file, so those are copied.
    /// True: no player is ever handed a .strm's text and secret.
    /// False: an audio link would give its URL and secret to whoever played it.
    /// </summary>
    [Theory]
    [InlineData(Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie, true)]
    [InlineData(Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode, true)]
    [InlineData(Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.MusicVideo, true)]
    [InlineData(Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Audio, false)]
    [InlineData(Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.AudioBook, false)]
    [InlineData(Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Book, false)]
    public void CanLink_OnlyVideo(Jellyfin.Sdk.Generated.Models.BaseItemDto_Type kind, bool linkable)
    {
        Assert.Equal(linkable, LinkPaths.CanLink(new Jellyfin.Sdk.Generated.Models.BaseItemDto { Type = kind }));
    }

    private static (ILibraryManager Library, InterfaceStub Stub) Library(params BaseItem[] items)
    {
        var byPath = items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        stub.Answer(nameof(ILibraryManager.FindByPath), args => byPath.TryGetValue((string)args[0]!, out var item) ? item : null);
        return (library, stub);
    }

    private static Dictionary<string, BaseItem> Index(params BaseItem[] items)
    {
        var index = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            index[item.Path] = item;
        }

        return index;
    }

    private static ILinkRegistry Links(params (string Copied, string Link)[] links) => new FakeLinks(links);

    private sealed class FakeLinks : ILinkRegistry
    {
        private readonly (string Copied, string Link)[] _links;

        public FakeLinks((string Copied, string Link)[] links) => _links = links;

        public string? LinkPathFor(string copiedPath)
            => _links.FirstOrDefault(l => string.Equals(l.Copied, copiedPath, StringComparison.OrdinalIgnoreCase)).Link;

        public string? CopiedPathFor(string linkPath)
            => _links.FirstOrDefault(l => string.Equals(l.Link, linkPath, StringComparison.OrdinalIgnoreCase)).Copied;
    }

    /// <summary>
    /// A film Jellyfin lists once with two files is two versions: the first as itself, and the other
    /// as an item of its own with that file's id, path, and size. Remote sources, links, and anything
    /// that is not video are never offered.
    /// True: a Link mapping links every version, so a user refused one can play another.
    /// False: only the first version would ever be linked, and a refused user would have nothing else to play.
    /// </summary>
    [Fact]
    public void OtherVersions_AreItemsOfTheirOwn()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var film = new Jellyfin.Sdk.Generated.Models.BaseItemDto
        {
            Id = first,
            Name = "Film",
            Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie,
            Path = "/m/Film/Film - 4K.mkv",
            MediaSources = new List<Jellyfin.Sdk.Generated.Models.MediaSourceInfo>
            {
                new() { Id = first.ToString("N"), Path = "/m/Film/Film - 4K.mkv", Size = 9000 },
                new() { Id = second.ToString("N"), Path = "/m/Film/Film - 1080p.mkv", Size = 3000 },
                new() { Id = Guid.NewGuid().ToString("N"), Path = "http://elsewhere/film", IsRemote = true },
                new() { Id = Guid.NewGuid().ToString("N"), Path = "/m/Film/Film - Link.strm" }
            }
        };

        var other = Assert.Single(LinkPaths.OtherVersions(film));

        Assert.Equal(second, other.Id);
        Assert.Equal("/m/Film/Film - 1080p.mkv", other.Path);
        Assert.Equal(3000, Jellyfin.Plugin.ServerSync.Utilities.MediaItemUtilities.GetItemSize(other));
        Assert.True(LinkPaths.CanLink(other));

        film.Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Audio;
        Assert.Empty(LinkPaths.OtherVersions(film));
        film.Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie;
        film.MediaSources.RemoveRange(1, 3);
        Assert.Empty(LinkPaths.OtherVersions(film));
    }
}

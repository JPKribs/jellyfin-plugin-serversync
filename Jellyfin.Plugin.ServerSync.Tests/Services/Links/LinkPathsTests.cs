using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// Link paths must map a link to the copy it stands in for and back, and never invent a link from a missing path.
/// </summary>
public sealed class LinkPathsTests
{
    /// <summary>
    /// The extension of a link is .strm, with its dot.
    /// True: every path check agrees on what a link's file looks like.
    /// False: links would be written or looked up under another extension.
    /// </summary>
    [Fact]
    public void Extension_IsStrm()
    {
        var extension = LinkPaths.Extension;

        Assert.Equal(".strm", extension);
    }

    /// <summary>
    /// No item at all, or one with no type, cannot be a link.
    /// True: a source item the listing left blank is skipped.
    /// False: a null item would throw in the middle of a refresh.
    /// </summary>
    /// <param name="hasItem">Whether an item is passed at all.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanLink_NoItemOrNoType_IsFalse(bool hasItem)
    {
        var item = hasItem ? new BaseItemDto() : null;

        var can = LinkPaths.CanLink(item!);

        Assert.False(can);
    }

    /// <summary>
    /// A missing copy path has no link path.
    /// True: an item with no path is never given a link.
    /// False: a null path would throw, or an empty one would become a file named .strm.
    /// </summary>
    /// <param name="path">The copy's path.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void LinkPathFor_NoPath_IsNull(string? path)
    {
        var link = LinkPaths.LinkPathFor(path);

        Assert.Null(link);
    }

    /// <summary>
    /// A link row's copy path is its .strm path with the source file's extension.
    /// True: the copy that replaces a link lands under the name the source file has.
    /// False: a promoted link would be saved as a .strm or with the wrong extension.
    /// </summary>
    /// <param name="local">The row's local path.</param>
    /// <param name="source">The row's source path.</param>
    /// <param name="expected">The copy's path.</param>
    [Theory]
    [InlineData("/media/Film (2020).strm", "/src/Film (2020).mkv", "/media/Film (2020).mkv")]
    [InlineData("/media/Film.STRM", "/src/Other Name.MP4", "/media/Film.MP4")]
    [InlineData("/media/Show.S01/E01.strm", "/src/Show.S01/E01.avi", "/media/Show.S01/E01.avi")]
    public void CopiedPathOf_Link_TakesTheSourceExtension(string local, string source, string expected)
    {
        var row = new SyncItem { LocalPath = local, SourcePath = source };

        var copied = LinkPaths.CopiedPathOf(row);

        Assert.Equal(expected, copied);
    }

    /// <summary>
    /// A row with no local path has no copy path.
    /// True: a row not yet placed here is not taken for a link.
    /// False: the registry would hold a link with no path.
    /// </summary>
    /// <param name="local">The row's local path.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CopiedPathOf_NoLocalPath_IsNull(string? local)
    {
        var row = new SyncItem { LocalPath = local, SourcePath = "/src/Film.mkv" };

        var copied = LinkPaths.CopiedPathOf(row);

        Assert.Null(copied);
    }

    /// <summary>
    /// A missing row is refused.
    /// True: the mistake is named where it was made.
    /// False: a null row would fail with no hint of the cause.
    /// </summary>
    [Fact]
    public void CopiedPathOf_NullRow_Throws()
    {
        SyncItem row = null!;

        var act = () => LinkPaths.CopiedPathOf(row);

        Assert.Throws<ArgumentNullException>(act);
    }

    /// <summary>
    /// A source with no item, or with a single version, has no other versions.
    /// True: an ordinary film yields one link.
    /// False: a null item would throw, or a film would be linked twice.
    /// </summary>
    /// <param name="hasItem">Whether an item is passed at all.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherVersions_NoItemOrOneVersion_IsEmpty(bool hasItem)
    {
        var id = Guid.NewGuid();
        var item = hasItem
            ? new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie, Path = "/m/Film/Film.mkv", MediaSources = new List<MediaSourceInfo> { new() { Id = id.ToString("N"), Path = "/m/Film/Film.mkv" } } }
            : null;

        var others = LinkPaths.OtherVersions(item!).ToList();

        Assert.Empty(others);
    }

    /// <summary>
    /// A version with no usable id, no path, or no entry at all is skipped, and the good one beside it is kept.
    /// True: one malformed version in a source's answer costs only itself.
    /// False: a blank version would throw or be written as a link to nothing.
    /// </summary>
    [Fact]
    public void OtherVersions_MalformedVersions_AreSkipped()
    {
        var first = Guid.NewGuid();
        var good = Guid.NewGuid();
        var item = new BaseItemDto
        {
            Id = first,
            Name = "Film",
            Type = BaseItemDto_Type.Movie,
            Path = "/m/Film/Film - 4K.mkv",
            MediaSources = new List<MediaSourceInfo>
            {
                new() { Id = first.ToString("N"), Path = "/m/Film/Film - 4K.mkv" },
                null!,
                new() { Id = "not-an-id", Path = "/m/Film/Film - 720p.mkv" },
                new() { Id = Guid.NewGuid().ToString("N"), Path = string.Empty },
                new() { Id = Guid.NewGuid().ToString("N"), Path = "/m/Film/NoExtension" },
                new() { Id = first.ToString("N"), Path = "/m/Film/Film - Same Id.mkv" },
                new() { Id = good.ToString("N"), Path = "/m/Film/Film - 1080p.mkv" }
            }
        };

        var others = LinkPaths.OtherVersions(item).ToList();

        var other = Assert.Single(others);
        Assert.Equal(good, other.Id);
        Assert.Equal("Film", other.Name);
        Assert.Same(item.MediaSources[6], Assert.Single(other.MediaSources!));
    }

    /// <summary>
    /// A row path, a source path, and the aliases all need a registry, and the aliases an index.
    /// True: a missing registry is named at the call.
    /// False: a null registry would treat every link as a plain file without a word.
    /// </summary>
    [Fact]
    public void RegistryHelpers_NullArguments_Throw()
    {
        var links = new LinkFakeRegistry();
        var index = new Dictionary<string, BaseItem>();

        Assert.Throws<ArgumentNullException>(() => LinkPaths.RowPathFor("/media/a.strm", null!));
        Assert.Throws<ArgumentNullException>(() => LinkPaths.SourcePathFor("/media/a.strm", "/media", "/src", null!));
        Assert.Throws<ArgumentNullException>(() => LinkPaths.AddAliases(null!, links));
        Assert.Throws<ArgumentNullException>(() => LinkPaths.AddAliases(index, null!));
    }
}

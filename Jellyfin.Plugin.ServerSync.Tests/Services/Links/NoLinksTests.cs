using Jellyfin.Plugin.ServerSync.Services.Links;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The empty registry must say that no path is a link, whatever it is asked.
/// </summary>
public sealed class NoLinksTests
{
    /// <summary>
    /// No copy path has a link, no link path has a copy, and no path is found as a link.
    /// True: code handed the empty registry treats every file as a plain copy.
    /// False: a path would be rewritten as a link where links do not exist.
    /// </summary>
    /// <param name="path">The path asked about.</param>
    [Theory]
    [InlineData("")]
    [InlineData("/media/Movies/Film.mkv")]
    [InlineData("/media/Movies/Film.strm")]
    public void Lookups_AnyPath_FindNothing(string path)
    {
        ILinkRegistry links = new NoLinks();

        var link = links.LinkPathFor(path);
        var copy = links.CopiedPathFor(path);
        var entry = links.Find(path);

        Assert.Null(link);
        Assert.Null(copy);
        Assert.Null(entry);
    }

    /// <summary>
    /// Forgetting what was read does nothing and does not throw.
    /// True: a writer can invalidate any registry without knowing which one it holds.
    /// False: the copy path would fail whenever it told the registry about a change.
    /// </summary>
    [Fact]
    public void Invalidate_Always_IsHarmless()
    {
        ILinkRegistry links = new NoLinks();

        links.Invalidate();

        Assert.Null(links.Find("/media/Movies/Film.strm"));
    }
}

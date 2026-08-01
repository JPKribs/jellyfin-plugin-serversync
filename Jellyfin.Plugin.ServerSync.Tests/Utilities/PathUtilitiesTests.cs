using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Utilities;

public class PathUtilitiesTests
{
    /// <summary>
    /// Trailing slashes on either root are normalised before translation.
    /// True: callers can pass roots with or without trailing slashes and get the same output.
    /// False: small input differences would silently flip every row in a library to a different LocalPath.
    /// </summary>
    [Fact]
    public void TranslatePath_RootsWithTrailingSlash_AreNormalised()
    {
        var a = PathUtilities.TranslatePath("/mnt/source/movies/Foo.mkv", "/mnt/source", "/data/library");
        var b = PathUtilities.TranslatePath("/mnt/source/movies/Foo.mkv", "/mnt/source/", "/data/library/");

        Assert.Equal(a, b);
    }

    /// <summary>
    /// A source path under sourceRoot maps to localRoot + relative tail.
    /// True: standard path translation works for the canonical case.
    /// False: every row's LocalPath would be wrong, breaking item matching.
    /// </summary>
    [Fact]
    public void TranslatePath_BasicTranslation_MapsRelativeTail()
    {
        var result = PathUtilities.TranslatePath(
            "/mnt/source/movies/Foo.mkv",
            "/mnt/source",
            "/data/library");

        Assert.Equal(Path.Combine("/data/library", "movies", "Foo.mkv"), result);
    }

    /// <summary>
    /// Null or empty source path returns localRoot unchanged.
    /// True: callers get a deterministic fallback they can detect (== localRoot means no real translation).
    /// False: NullReferenceException at runtime, crashing the refresh.
    /// </summary>
    [Fact]
    public void TranslatePath_NullOrEmptySource_ReturnsLocalRoot()
    {
        Assert.Equal("/data/library", PathUtilities.TranslatePath(null!, "/mnt/source", "/data/library"));
        Assert.Equal("/data/library", PathUtilities.TranslatePath(string.Empty, "/mnt/source", "/data/library"));
    }

    /// <summary>
    /// Source path not under sourceRoot falls back to localRoot + filename.
    /// True: cross-library paths get a safe fallback rather than escaping the root.
    /// False: paths from an unrelated source could be translated into anywhere on local disk.
    /// </summary>
    [Fact]
    public void TranslatePath_SourceNotUnderRoot_FallsBackToLocalRootPlusFilename()
    {
        var result = PathUtilities.TranslatePath(
            "/some/other/location/Bar.mkv",
            "/mnt/source",
            "/data/library");

        Assert.Equal(Path.Combine("/data/library", "Bar.mkv"), result);
    }

    /// <summary>
    /// Path-traversal segments (..) are stripped from the translated path.
    /// True: a malicious source path can't escape localRoot via .. segments.
    /// False: arbitrary file writes outside the library root would be possible.
    /// </summary>
    [Fact]
    public void TranslatePath_BlocksDotDotTraversal()
    {
        var result = PathUtilities.TranslatePath(
            "/mnt/source/../../../etc/passwd",
            "/mnt/source",
            "/data/library");

        var full = Path.GetFullPath(result);
        Assert.StartsWith(Path.GetFullPath("/data/library"), full, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Single-dot segments (.) are stripped from the translated path.
    /// True: ./ noise in source paths doesn't corrupt the local path.
    /// False: paths with ./ would produce inconsistent translations.
    /// </summary>
    [Fact]
    public void TranslatePath_StripsSingleDotSegments()
    {
        var result = PathUtilities.TranslatePath(
            "/mnt/source/movies/./Foo.mkv",
            "/mnt/source",
            "/data/library");

        Assert.Equal(Path.Combine("/data/library", "movies", "Foo.mkv"), result);
    }

    /// <summary>
    /// Source path equal to sourceRoot returns localRoot.
    /// True: the root-to-root mapping is the identity.
    /// False: empty-tail paths could produce malformed local paths or throw.
    /// </summary>
    [Fact]
    public void TranslatePath_SourceEqualsRoot_ReturnsLocalRoot()
    {
        var result = PathUtilities.TranslatePath("/mnt/source", "/mnt/source", "/data/library");

        Assert.Equal("/data/library", result);
    }

    /// <summary>
    /// Case-insensitive prefix match (Windows-friendly behaviour).
    /// True: paths with case-differing roots still translate correctly.
    /// False: Windows installs with case-variant volume names would mis-translate every path.
    /// </summary>
    [Fact]
    public void TranslatePath_PrefixMatch_IsCaseInsensitive()
    {
        var result = PathUtilities.TranslatePath(
            "/MNT/SOURCE/movies/Foo.mkv",
            "/mnt/source",
            "/data/library");

        Assert.Equal(Path.Combine("/data/library", "movies", "Foo.mkv"), result);
    }

    /// <summary>
    /// AllowAll mode never filters anything.
    /// True: every item passes through regardless of FilteredItems content.
    /// False: AllowAll wouldn't be the inert default and users would see surprising filtering.
    /// </summary>
    [Fact]
    public void IsItemFiltered_AllowAll_NeverFilters()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "/mnt/source/movies/Foo" }
        };

        Assert.False(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Foo/Foo.mkv",
            "/mnt/source",
            LibraryFilterMode.AllowAll,
            filtered));
    }

    /// <summary>
    /// Whitelist with a matching item returns false (keep, don't skip).
    /// True: items in the whitelist are synced.
    /// False: whitelist would behave inverted, blocking the items it's meant to allow.
    /// </summary>
    [Fact]
    public void IsItemFiltered_Whitelist_MatchingItem_IsKept()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "/mnt/source/movies/Foo" }
        };

        Assert.False(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Foo/Foo.mkv",
            "/mnt/source",
            LibraryFilterMode.Whitelist,
            filtered));
    }

    /// <summary>
    /// Whitelist with a non-matching item returns true (skip).
    /// True: items outside the whitelist are correctly filtered out.
    /// False: whitelist would let everything through, defeating its purpose.
    /// </summary>
    [Fact]
    public void IsItemFiltered_Whitelist_NonMatchingItem_IsFiltered()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "/mnt/source/movies/Foo" }
        };

        Assert.True(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Bar/Bar.mkv",
            "/mnt/source",
            LibraryFilterMode.Whitelist,
            filtered));
    }

    /// <summary>
    /// Blacklist with a matching item returns true (skip).
    /// True: items in the blacklist are filtered out.
    /// False: blacklist would behave inverted, syncing what should be excluded.
    /// </summary>
    [Fact]
    public void IsItemFiltered_Blacklist_MatchingItem_IsFiltered()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "/mnt/source/movies/Foo" }
        };

        Assert.True(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Foo/Foo.mkv",
            "/mnt/source",
            LibraryFilterMode.Blacklist,
            filtered));
    }

    /// <summary>
    /// Blacklist with a non-matching item returns false (keep).
    /// True: items not in the blacklist are synced.
    /// False: blacklist would block everything, defeating its purpose.
    /// </summary>
    [Fact]
    public void IsItemFiltered_Blacklist_NonMatchingItem_IsKept()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "/mnt/source/movies/Foo" }
        };

        Assert.False(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Bar/Bar.mkv",
            "/mnt/source",
            LibraryFilterMode.Blacklist,
            filtered));
    }

    /// <summary>
    /// Empty FilteredItems list short-circuits to "no filter."
    /// True: an empty config is treated the same as AllowAll.
    /// False: empty whitelist would block every item in the library on day one.
    /// </summary>
    [Fact]
    public void IsItemFiltered_EmptyFilteredItems_IsKept()
    {
        Assert.False(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Foo.mkv",
            "/mnt/source",
            LibraryFilterMode.Whitelist,
            new List<FilteredItem>()));
    }

    /// <summary>
    /// Null FilteredItems list short-circuits to "no filter."
    /// True: null is tolerated and behaves the same as empty.
    /// False: NullReferenceException at runtime.
    /// </summary>
    [Fact]
    public void IsItemFiltered_NullFilteredItems_IsKept()
    {
        Assert.False(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Foo.mkv",
            "/mnt/source",
            LibraryFilterMode.Whitelist,
            null));
    }

    /// <summary>
    /// Empty source path under Whitelist mode is filtered.
    /// True: a pathless item without a whitelist match is correctly skipped.
    /// False: pathless items would pass whitelist filters silently.
    /// </summary>
    [Fact]
    public void IsItemFiltered_EmptySourcePath_Whitelist_IsFiltered()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "/mnt/source/movies/Foo" }
        };

        Assert.True(PathUtilities.IsItemFiltered(
            string.Empty,
            "/mnt/source",
            LibraryFilterMode.Whitelist,
            filtered));
    }

    /// <summary>
    /// Empty source path under Blacklist mode is kept.
    /// True: a pathless item can't match a blacklist path, so it's allowed through.
    /// False: pathless items would be unconditionally blocked even under blacklist.
    /// </summary>
    [Fact]
    public void IsItemFiltered_EmptySourcePath_Blacklist_IsKept()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "/mnt/source/movies/Foo" }
        };

        Assert.False(PathUtilities.IsItemFiltered(
            string.Empty,
            "/mnt/source",
            LibraryFilterMode.Blacklist,
            filtered));
    }

    /// <summary>
    /// Child paths inherit their parent's filter outcome.
    /// True: selecting a folder in whitelist also includes all items under it.
    /// False: whitelisting a folder wouldn't whitelist its contents.
    /// </summary>
    [Fact]
    public void IsItemFiltered_ChildOfFilteredFolder_InheritsOutcome()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "/mnt/source/movies/Foo" }
        };

        Assert.False(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Foo/subdir/episode.mkv",
            "/mnt/source",
            LibraryFilterMode.Whitelist,
            filtered));
    }

    /// <summary>
    /// Prefix-but-not-segment matches don't count (e.g. "Foobar" doesn't match "Foo").
    /// True: the path-segment boundary is enforced so "Foo" and "Foobar" are distinct entries.
    /// False: "Foobar" would inherit from "Foo" and produce surprising includes/excludes.
    /// </summary>
    [Fact]
    public void IsItemFiltered_PrefixWithoutSegmentBoundary_DoesNotMatch()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "/mnt/source/movies/Foo" }
        };

        Assert.True(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Foobar/movie.mkv",
            "/mnt/source",
            LibraryFilterMode.Whitelist,
            filtered));
    }

    /// <summary>
    /// Relative filter paths (relative to sourceRoot) are matched.
    /// True: operators can store filter paths relative to root without full prefixing.
    /// False: only fully-qualified filter paths would work, surprising users.
    /// </summary>
    [Fact]
    public void IsItemFiltered_RelativeFilterPath_MatchesAfterPrefixing()
    {
        var filtered = new List<FilteredItem>
        {
            new() { Path = "movies/Foo" }
        };

        Assert.False(PathUtilities.IsItemFiltered(
            "/mnt/source/movies/Foo/Foo.mkv",
            "/mnt/source",
            LibraryFilterMode.Whitelist,
            filtered));
    }

    /// <summary>
    /// A sibling root that merely shares a name prefix must not match.
    /// True: "/media/Movies 4K/film.mkv" falls back to localRoot + filename
    /// rather than being treated as living under "/media/Movies".
    /// False: the relative tail is computed from the wrong offset and the file
    /// is written to a garbage folder (localRoot + "/ 4K/film.mkv") with no error.
    /// </summary>
    [Fact]
    public void TranslatePath_SiblingRootSharingPrefix_DoesNotMatchRoot()
    {
        var result = PathUtilities.TranslatePath(
            "/media/Movies 4K/film.mkv",
            "/media/Movies",
            "/data/library");

        Assert.Equal(Path.Combine("/data/library", "film.mkv"), result);
    }

    /// <summary>
    /// The boundary check must not reject the root itself or a real child.
    /// True: exact-root and separator-delimited children still translate.
    /// False: tightening the prefix match would break every normal path.
    /// </summary>
    [Fact]
    public void TranslatePath_ExactRootAndChild_StillMatch()
    {
        Assert.Equal(
            "/data/library",
            PathUtilities.TranslatePath("/media/Movies", "/media/Movies", "/data/library"));

        Assert.Equal(
            Path.Combine("/data/library", "film.mkv"),
            PathUtilities.TranslatePath("/media/Movies/film.mkv", "/media/Movies", "/data/library"));
    }
}

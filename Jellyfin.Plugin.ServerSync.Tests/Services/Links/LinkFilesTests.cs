using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// Link files must hold only a loopback URL with a secret this server alone can derive, and never overwrite a file that is not theirs to replace.
/// </summary>
public sealed class LinkFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serversync-linkfiles-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkFilesTests"/> class.
    /// </summary>
    public LinkFilesTests() => Directory.CreateDirectory(_dir);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The service needs the host and the paths it is built from.
    /// True: a missing dependency shows at construction.
    /// False: the first link written would throw instead.
    /// </summary>
    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var (host, _) = InterfaceStub.Create<MediaBrowser.Controller.IServerApplicationHost>();
        var (paths, _) = InterfaceStub.Create<MediaBrowser.Common.Configuration.IApplicationPaths>();

        Assert.Throws<ArgumentNullException>(() => new LinkFiles(null!, paths));
        Assert.Throws<ArgumentNullException>(() => new LinkFiles(host, null!));
    }

    /// <summary>
    /// The URL names the link route and the id on this server's loopback address, and carries a secret.
    /// True: Jellyfin reads the link from this server alone, with no source credential on disk.
    /// False: a link would point at another host or go out with no secret.
    /// </summary>
    [Fact]
    public void UrlFor_Id_IsLoopbackRouteWithSecret()
    {
        var files = LinkTestFiles.Create(_dir);

        var url = files.UrlFor("abc123");

        Assert.StartsWith(LinkTestFiles.Address + "/Sync/Link/abc123?k=", url, StringComparison.Ordinal);
        var secret = url[(url.IndexOf("?k=", StringComparison.Ordinal) + 3)..];
        Assert.Equal(43, secret.Length);
        Assert.DoesNotContain(secret, c => c is '=' or '+' or '/');
    }

    /// <summary>
    /// The secret in a link's URL matches, and nothing else does: not a missing one, another link's, or a longer one.
    /// True: only a reader of the .strm itself can open the link route.
    /// False: a guessed or borrowed secret would stream the file.
    /// </summary>
    [Fact]
    public void SecretMatches_OnlyTheLinksOwnSecret()
    {
        var files = LinkTestFiles.Create(_dir);
        var own = SecretOf(files.UrlFor("abc"));
        var other = SecretOf(files.UrlFor("abd"));

        Assert.True(files.SecretMatches("abc", own));
        Assert.False(files.SecretMatches("abc", other));
        Assert.False(files.SecretMatches("abc", own + "A"));
        Assert.False(files.SecretMatches("abc", own.ToUpperInvariant() == own ? own.ToLowerInvariant() : own.ToUpperInvariant()));
    }

    /// <summary>
    /// A missing id or a missing secret never matches.
    /// True: a request with no secret is refused before any key is read.
    /// False: an empty secret would match, or a null would throw.
    /// </summary>
    /// <param name="linkId">The link's id.</param>
    /// <param name="presented">The secret presented.</param>
    [Theory]
    [InlineData(null, "secret")]
    [InlineData("", "secret")]
    [InlineData("abc", null)]
    [InlineData("abc", "")]
    public void SecretMatches_MissingIdOrSecret_IsFalse(string? linkId, string? presented)
    {
        var files = LinkTestFiles.Create(_dir);

        var matches = files.SecretMatches(linkId!, presented);

        Assert.False(matches);
        Assert.False(File.Exists(Path.Combine(_dir, "serversync-link.key")));
    }

    /// <summary>
    /// The key is made once as 32 bytes in the data folder, and a later instance derives the same secret from it.
    /// True: links keep working across a restart.
    /// False: every restart would change every link's secret and break them all.
    /// </summary>
    [Fact]
    public void Key_FirstUse_IsStoredAndReused()
    {
        var first = LinkTestFiles.Create(_dir).UrlFor("abc");

        var second = LinkTestFiles.Create(_dir).UrlFor("abc");

        Assert.Equal(first, second);
        Assert.Equal(32, File.ReadAllBytes(Path.Combine(_dir, "serversync-link.key")).Length);
        Assert.False(File.Exists(Path.Combine(_dir, "serversync-link.key.tmp")));
    }

    /// <summary>
    /// A key file of the wrong length is replaced with a fresh 32 byte key.
    /// True: a cut short key never weakens the secrets.
    /// False: links would be signed with a few bytes anyone could guess.
    /// </summary>
    [Fact]
    public void Key_WrongLength_IsReplaced()
    {
        var keyPath = Path.Combine(_dir, "serversync-link.key");
        File.WriteAllBytes(keyPath, new byte[] { 1, 2, 3 });

        LinkTestFiles.Create(_dir).UrlFor("abc");

        Assert.Equal(32, File.ReadAllBytes(keyPath).Length);
    }

    /// <summary>
    /// Servers with keys of their own give one id different secrets.
    /// True: a link copied from another server does not open here.
    /// False: a secret would be a plain hash of the id that any server could make.
    /// </summary>
    [Fact]
    public void UrlFor_AnotherServersKey_GivesAnotherSecret()
    {
        var otherDir = Path.Combine(_dir, "other");
        Directory.CreateDirectory(otherDir);

        var here = LinkTestFiles.Create(_dir).UrlFor("abc");
        var there = LinkTestFiles.Create(otherDir).UrlFor("abc");

        Assert.NotEqual(here, there);
    }

    /// <summary>
    /// Writing a link creates its folder, leaves one line holding the URL, and no temporary file.
    /// True: Jellyfin reads the URL and a later run finds nothing half written.
    /// False: a link deep in a new folder would fail, or a stray temp file would sit in the library.
    /// </summary>
    [Fact]
    public void Write_NewPath_WritesTheUrlLine()
    {
        var files = LinkTestFiles.Create(_dir);
        var path = Path.Combine(_dir, "media", "Film (2020)", "Film (2020).strm");

        files.Write(path, "abc", replace: false);

        Assert.Equal(files.UrlFor("abc") + "\n", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".serversync-tmp"));
        Assert.True(files.IsCurrent(path, "abc"));
    }

    /// <summary>
    /// A file already at the path is left untouched unless the caller says it may be replaced.
    /// True: a user's own .strm survives a sync.
    /// False: a link would silently overwrite a file the plugin did not write.
    /// </summary>
    [Fact]
    public void Write_ExistingFileWithoutReplace_ThrowsAndKeepsIt()
    {
        var files = LinkTestFiles.Create(_dir);
        var path = Path.Combine(_dir, "Mine.strm");
        File.WriteAllText(path, "http://example.invalid/mine");

        var act = () => files.Write(path, "abc", replace: false);

        Assert.Throws<IOException>(act);
        Assert.Equal("http://example.invalid/mine", File.ReadAllText(path));
    }

    /// <summary>
    /// With replace the file at the path becomes the link.
    /// True: a link whose id changed is written again in place.
    /// False: a re-homed link would keep the old id for good.
    /// </summary>
    [Fact]
    public void Write_ExistingFileWithReplace_Overwrites()
    {
        var files = LinkTestFiles.Create(_dir);
        var path = Path.Combine(_dir, "Film.strm");
        files.Write(path, "old", replace: false);

        files.Write(path, "new", replace: true);

        Assert.True(files.IsCurrent(path, "new"));
        Assert.False(files.IsCurrent(path, "old"));
    }

    /// <summary>
    /// A missing file, or a .strm holding another address, is not current.
    /// True: a missing or stale link is written again.
    /// False: a link gone from disk would be reported as in place.
    /// </summary>
    [Fact]
    public void IsCurrent_MissingOrOtherAddress_IsFalse()
    {
        var files = LinkTestFiles.Create(_dir);
        var other = Path.Combine(_dir, "Other.strm");
        File.WriteAllText(other, "http://example.invalid/stream");

        var missing = files.IsCurrent(Path.Combine(_dir, "Missing.strm"), "abc");
        var stale = files.IsCurrent(other, "abc");

        Assert.False(missing);
        Assert.False(stale);
    }

    /// <summary>
    /// The URL read is the first line that is not blank or a comment, with tabs and outer spaces removed.
    /// True: this reads the same line Jellyfin plays.
    /// False: a comment or blank first line would hide a link from its own checks.
    /// </summary>
    /// <param name="content">The file's text.</param>
    /// <param name="expected">The URL read, or null.</param>
    [Theory]
    [InlineData("http://a/Sync/Link/x?k=y\n", "http://a/Sync/Link/x?k=y")]
    [InlineData("\n\n# a note\n  http://a/one  \nhttp://a/two\n", "http://a/one")]
    [InlineData("\thttp://a/\ttabbed\r\n", "http://a/tabbed")]
    [InlineData("", null)]
    [InlineData("# only a comment\n   \n", null)]
    public void ReadUrl_TakesTheFirstUrlLine(string content, string? expected)
    {
        var path = Path.Combine(_dir, "Read.strm");
        File.WriteAllText(path, content);

        var url = LinkFiles.ReadUrl(path);

        Assert.Equal(expected, url);
    }

    /// <summary>
    /// A URL points at the link route only when it holds the whole route segment, in any case.
    /// True: this plugin's links are told from every other .strm.
    /// False: a user's stream would be removed as a link, or a link left behind as a user's file.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <param name="expected">Whether it points at the route.</param>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("http://127.0.0.1:8096/Sync/Link/abc?k=s", true)]
    [InlineData("http://127.0.0.1:8096/sync/link/abc?k=s", true)]
    [InlineData("http://127.0.0.1:8096/Sync/Linker/abc", false)]
    [InlineData("http://127.0.0.1:8096/Sync/Link", false)]
    [InlineData("http://example.invalid/stream.m3u8", false)]
    public void PointsAtLinkRoute_NeedsTheRouteSegment(string? url, bool expected)
    {
        var points = LinkFiles.PointsAtLinkRoute(url);

        Assert.Equal(expected, points);
    }

    /// <summary>
    /// Only an existing .strm whose URL points at the link route is this plugin's own link.
    /// True: cleanup removes links and nothing else.
    /// False: a user's .strm, or a video file that happens to hold the text, would be deleted.
    /// </summary>
    /// <param name="name">The file's name.</param>
    /// <param name="content">The file's text, or null for no file.</param>
    /// <param name="expected">Whether it is this plugin's link.</param>
    [Theory]
    [InlineData("Own.strm", "http://127.0.0.1:8096/Sync/Link/abc?k=s", true)]
    [InlineData("Own.STRM", "http://127.0.0.1:8096/Sync/Link/abc?k=s", true)]
    [InlineData("Users.strm", "http://example.invalid/stream", false)]
    [InlineData("Empty.strm", "", false)]
    [InlineData("Video.mkv", "http://127.0.0.1:8096/Sync/Link/abc?k=s", false)]
    [InlineData("Missing.strm", null, false)]
    public void IsOwnLink_OnlyAStrmAtTheLinkRoute(string name, string? content, bool expected)
    {
        var path = Path.Combine(_dir, name);
        if (content is not null)
        {
            File.WriteAllText(path, content);
        }

        var own = LinkFiles.IsOwnLink(path);

        Assert.Equal(expected, own);
    }

    private static string SecretOf(string url) => url[(url.IndexOf("?k=", StringComparison.Ordinal) + 3)..];
}

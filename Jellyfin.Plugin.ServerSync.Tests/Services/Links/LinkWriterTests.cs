using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link writer must write a link only where it is safe to, and tell the registry whenever a link changes.
/// </summary>
public sealed class LinkWriterTests : IDisposable
{
    private readonly LinkTestTable _store = new();
    private readonly LinkFakeRegistry _registry = new();
    private readonly LinkFiles _files;
    private readonly LinkWriter _writer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkWriterTests"/> class.
    /// </summary>
    public LinkWriterTests()
    {
        _files = LinkTestFiles.Create(_store.Folder);
        _writer = new LinkWriter(_files, _registry, NullLogger<LinkWriter>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _store.Dispose();

    /// <summary>
    /// The writer and its members refuse missing arguments.
    /// True: the mistake is named where it was made.
    /// False: a null would fail after a file had already been touched.
    /// </summary>
    [Fact]
    public void Members_NullArguments_Throw()
    {
        var row = LinkTestTable.Row("a", _store.Media("A.strm"));
        var config = Config();

        Assert.Throws<ArgumentNullException>(() => new LinkWriter(null!, _registry, NullLogger<LinkWriter>.Instance));
        Assert.Throws<ArgumentNullException>(() => new LinkWriter(_files, null!, NullLogger<LinkWriter>.Instance));
        Assert.Throws<ArgumentNullException>(() => new LinkWriter(_files, _registry, null!));
        Assert.Throws<ArgumentNullException>(() => _writer.IsCurrent(null!));
        Assert.Throws<ArgumentNullException>(() => _writer.Write(null!, _store.Table, config));
        Assert.Throws<ArgumentNullException>(() => _writer.Write(row, null!, config));
        Assert.Throws<ArgumentNullException>(() => _writer.Write(row, _store.Table, null!));
        Assert.Throws<ArgumentNullException>(() => LinkWriter.Predecessor(null!, _store.Table));
        Assert.Throws<ArgumentNullException>(() => LinkWriter.Predecessor(row, null!));
        Assert.Throws<ArgumentNullException>(() => _writer.TakeOver(null!, row, _store.Table));
        Assert.Throws<ArgumentNullException>(() => _writer.TakeOver(row, null!, _store.Table));
        Assert.Throws<ArgumentNullException>(() => _writer.TakeOver(row, row, null!));
    }

    /// <summary>
    /// A row's link is current only when its .strm exists and holds the URL for the row's id.
    /// True: a link in place is not written again, and one missing or stale is.
    /// False: every run would rewrite every link, or never repair a broken one.
    /// </summary>
    /// <param name="state">What is at the row's path: nothing, the row's link, another link, or no path at all.</param>
    /// <param name="expected">Whether the link is current.</param>
    [Theory]
    [InlineData("missing", false)]
    [InlineData("own", true)]
    [InlineData("other", false)]
    [InlineData("nopath", false)]
    public void IsCurrent_FollowsTheFileOnDisk(string state, bool expected)
    {
        var path = _store.Media("Film.strm");
        var row = LinkTestTable.Row("aaaa", state == "nopath" ? null : path);
        if (state is "own" or "other")
        {
            _files.Write(path, state == "own" ? "aaaa" : "bbbb", replace: false);
        }

        var current = _writer.IsCurrent(row);

        Assert.Equal(expected, current);
    }

    /// <summary>
    /// A row whose local path is missing or not a .strm is refused, and nothing is written.
    /// True: a link is never written over a video file's name.
    /// False: a row with a copy's path would have its video replaced by a one line text file.
    /// </summary>
    /// <param name="name">The row's local file name, or null for none.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("Film.mkv")]
    public void Write_NoStrmPath_IsRefused(string? name)
    {
        var path = name is null ? null : _store.Media(name);
        var row = LinkTestTable.Row("aaaa", path);

        var act = () => _writer.Write(row, _store.Table, Config());

        Assert.Throws<InvalidOperationException>(act);
        Assert.Equal(0, _registry.Invalidations);
        Assert.False(path is not null && File.Exists(path));
    }

    /// <summary>
    /// Writing a link puts the .strm in place and tells the registry once.
    /// True: the new link is seen by the next lookup.
    /// False: a link just written would be treated as a user's .strm until the snapshot aged out.
    /// </summary>
    [Fact]
    public void Write_FreePath_WritesAndInvalidates()
    {
        var row = LinkTestTable.Row("aaaa", _store.Media("Film.strm"));

        _writer.Write(row, _store.Table, Config());

        Assert.True(_writer.IsCurrent(row));
        Assert.Equal(1, _registry.Invalidations);
    }

    /// <summary>
    /// A link already current is left untouched, and the registry is not told.
    /// True: a run over a settled library writes nothing.
    /// False: every run would rewrite every link and drop the registry's snapshot each time.
    /// </summary>
    [Fact]
    public void Write_AlreadyCurrent_DoesNothing()
    {
        var row = LinkTestTable.Row("aaaa", _store.Media("Film.strm"));
        _writer.Write(row, _store.Table, Config());
        var written = File.GetLastWriteTimeUtc(row.LocalPath!);
        File.SetLastWriteTimeUtc(row.LocalPath!, written.AddDays(-1));

        _writer.Write(row, _store.Table, Config());

        Assert.Equal(written.AddDays(-1), File.GetLastWriteTimeUtc(row.LocalPath!));
        Assert.Equal(1, _registry.Invalidations);
    }

    /// <summary>
    /// A .strm holding no address at all is replaced, as is this plugin's own link under another id.
    /// True: an empty leftover or a link whose id changed is repaired in place.
    /// False: an empty file would block its link for good.
    /// </summary>
    /// <param name="existing">What the .strm holds before the write.</param>
    [Theory]
    [InlineData("")]
    [InlineData("# nothing here\n")]
    [InlineData("http://127.0.0.1:8096/Sync/Link/old?k=stale\n")]
    public void Write_EmptyOrOwnStaleStrm_IsReplaced(string existing)
    {
        var row = LinkTestTable.Row("aaaa", _store.Media("Film.strm"));
        File.WriteAllText(row.LocalPath!, existing);

        _writer.Write(row, _store.Table, Config());

        Assert.True(_writer.IsCurrent(row));
    }

    /// <summary>
    /// A row whose path is not a .strm has no predecessor.
    /// True: only a link can take a link's place.
    /// False: a copy would delete another file's row to take its path.
    /// </summary>
    [Fact]
    public void Predecessor_NotALinkPath_IsNull()
    {
        _store.Table.Upsert(LinkTestTable.Row("old", _store.Media("Film.mkv")));
        var row = LinkTestTable.Row("new", _store.Media("Film.mkv"), StorageMode.Copy);

        var predecessor = LinkWriter.Predecessor(row, _store.Table);

        Assert.Null(predecessor);
    }

    /// <summary>
    /// Taking a path over hands the new row the old one's library item and companions, removes the old row, and tells the registry.
    /// True: the .strm keeps its library item, subtitles, and .nfo when the source file is renamed.
    /// False: the film would be added to the library again and lose its companions.
    /// </summary>
    [Fact]
    public void TakeOver_EmptyRow_InheritsAndRemovesThePredecessor()
    {
        var path = _store.Media("Film.strm");
        var old = LinkTestTable.Row("old", path, sourcePath: "/src/Film.mkv");
        old.LocalItemId = "local-item";
        old.CompanionFiles = "Film.nfo";
        _store.Table.Upsert(old);
        var row = LinkTestTable.Row("new", path, sourcePath: "/src/Film.mp4", status: SyncStatus.Queued);

        _writer.TakeOver(row, old, _store.Table);

        Assert.Equal("local-item", row.LocalItemId);
        Assert.Equal("Film.nfo", row.CompanionFiles);
        Assert.Null(_store.Table.GetBySourceItemId("old"));
        Assert.Equal(1, _registry.Invalidations);
    }

    /// <summary>
    /// A row that already has a library item and companions keeps its own.
    /// True: what the new row knows is not replaced by older values.
    /// False: a takeover would point the row at the wrong library item.
    /// </summary>
    [Fact]
    public void TakeOver_RowWithItsOwnValues_KeepsThem()
    {
        var path = _store.Media("Film.strm");
        var old = LinkTestTable.Row("old", path);
        old.LocalItemId = "old-item";
        old.CompanionFiles = "Old.nfo";
        var row = LinkTestTable.Row("new", path);
        row.LocalItemId = "new-item";
        row.CompanionFiles = "New.nfo";

        _writer.TakeOver(row, old, _store.Table);

        Assert.Equal("new-item", row.LocalItemId);
        Assert.Equal("New.nfo", row.CompanionFiles);
    }

    /// <summary>
    /// Removing a path that holds nothing deletes nothing, and the registry is still told.
    /// True: a link already gone leaves the registry fresh and raises no error.
    /// False: a missing file would throw in the middle of a promotion.
    /// </summary>
    [Fact]
    public void Remove_MissingFile_OnlyInvalidates()
    {
        var path = _store.Media("Gone.strm");

        _writer.Remove(path);

        Assert.False(File.Exists(path));
        Assert.Equal(1, _registry.Invalidations);
    }

    private PluginConfiguration Config()
    {
        var config = new PluginConfiguration();
        config.Servers.Add(new SourceServer
        {
            Url = "http://source",
            ApiKey = "key",
            LibraryMappings = { new LibraryMapping { SourceLibraryId = "s", LocalLibraryId = "l", LocalRootPath = _store.MediaRoot, SourceRootPath = "/src" } }
        });
        return config;
    }
}

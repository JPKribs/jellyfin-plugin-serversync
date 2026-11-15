using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link registry must name only the .strm files the content table records as links, and read the table as seldom as it can.
/// </summary>
public sealed class TableLinkRegistryTests : IDisposable
{
    private readonly LinkTestTable _store = new();

    /// <inheritdoc/>
    public void Dispose() => _store.Dispose();

    /// <summary>
    /// The registry needs the services it resolves the table from.
    /// True: a missing provider shows at construction.
    /// False: the first lookup would throw inside Jellyfin's path matching.
    /// </summary>
    [Fact]
    public void Constructor_NullServices_Throws()
    {
        var act = () => new TableLinkRegistry(null!);

        Assert.Throws<ArgumentNullException>(act);
    }

    /// <summary>
    /// A path that is empty or not a .strm is answered without reading the table.
    /// True: the path misses Jellyfin raises for ordinary files cost nothing.
    /// False: every lookup of every video would open the database.
    /// </summary>
    /// <param name="path">The path asked about.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/media/Film.mkv")]
    [InlineData("/media/Film.strm.bak")]
    public void Find_NotAStrmPath_NeverReadsTheTable(string? path)
    {
        var registry = new TableLinkRegistry(new ServiceCollection().BuildServiceProvider());

        var entry = registry.Find(path!);
        var copied = registry.CopiedPathFor(path!);

        Assert.Null(entry);
        Assert.Null(copied);
    }

    /// <summary>
    /// A link is found under any casing of its path, with its copy path, id, server, and size.
    /// True: a link is recognised on a file system that folds case, with all a stream needs to know.
    /// False: a link reached by another casing would be treated as a user's file and its secret served.
    /// </summary>
    [Fact]
    public void Find_LinkInAnyCase_GivesTheWholeEntry()
    {
        var path = _store.Media("Film (2020).strm");
        var row = LinkTestTable.Row("aaaa", path, sourcePath: "/src/Film (2020).mkv", serverKey: "server-a");
        row.SourceSize = 4321;
        _store.Table.Upsert(row);
        var registry = Registry();

        var entry = registry.Find(path.ToUpperInvariant());

        Assert.Equal(new LinkEntry(path, _store.Media("Film (2020).mkv"), "aaaa", "server-a", 4321), entry);
        Assert.Equal(_store.Media("Film (2020).mkv"), registry.CopiedPathFor(path));
        Assert.Equal(path, registry.LinkPathFor(_store.Media("Film (2020).mkv")));
    }

    /// <summary>
    /// A copy's row is not a link, even when its path would have a .strm beside it.
    /// True: only rows stored as links are streamed.
    /// False: a user's .strm beside a copied film would be taken for this plugin's link.
    /// </summary>
    [Fact]
    public void Find_CopyRow_IsNotALink()
    {
        _store.Table.Upsert(LinkTestTable.Row("copy", _store.Media("Copied.mkv"), StorageMode.Copy));
        var registry = Registry();

        var entry = registry.Find(_store.Media("Copied.strm"));
        var link = registry.LinkPathFor(_store.Media("Copied.mkv"));

        Assert.Null(entry);
        Assert.Null(link);
    }

    /// <summary>
    /// What was read is kept until the registry is told to forget it.
    /// True: lookups on the playback path do not read the database each time.
    /// False: every path miss would run a query.
    /// </summary>
    [Fact]
    public void Find_RowAddedAfterTheRead_IsSeenOnlyAfterInvalidate()
    {
        var path = _store.Media("Late.strm");
        var registry = Registry();
        var before = registry.Find(path);
        _store.Table.Upsert(LinkTestTable.Row("late", path, status: SyncStatus.Queued));

        var cached = registry.Find(path);
        registry.Invalidate();
        var after = registry.Find(path);

        Assert.Null(before);
        Assert.Null(cached);
        Assert.Equal("late", after!.LinkId);
    }

    private TableLinkRegistry Registry()
        => new(new ServiceCollection().AddSingleton(_store.Table).BuildServiceProvider());
}

using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.Net.Http;
using System.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Links;

/// <summary>
/// Writing and tracking links: the content table's storage column, the .strm files, the registry read
/// from the table, and the state rules a refresh applies to link rows.
/// </summary>
public sealed class LinkStorageTests : IDisposable
{
    private readonly string _dir;
    private readonly SyncDatabase _database;
    private readonly ContentSyncTableManager _table;
    private readonly LinkFiles _files;
    private readonly TableLinkRegistry _registry;

    public LinkStorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "serversync-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _dir);
        _table = new ContentSyncTableManager(new FixedProvider(_database), NullLogger<ContentSyncTableManager>.Instance);
        _files = Files(_dir);
        var services = new ServiceCollection().AddSingleton(_table).BuildServiceProvider();
        _registry = new TableLinkRegistry(services);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Media(string name) => Path.Combine(_dir, "media", name);

    /// <summary>
    /// A row keeps its storage mode, and a row that never set one is a copy.
    /// True: link rows survive a save and every existing row reads as a copy.
    /// False: links would turn into copies, or copies into links, on the next read.
    /// </summary>
    [Fact]
    public void Table_StorageMode_RoundTrips_AndDefaultsToCopy()
    {
        _table.Upsert(Row("a", Media("A.strm"), StorageMode.Link));
        _table.Upsert(Row("b", Media("B.mkv"), StorageMode.Copy));

        Assert.Equal(StorageMode.Link, _table.GetBySourceItemId("a")!.StorageMode);
        Assert.Equal(StorageMode.Copy, _table.GetBySourceItemId("b")!.StorageMode);
        Assert.Equal(new[] { "a" }, _table.GetLinks().Select(r => r.SourceItemId).ToArray());
    }

    /// <summary>
    /// The v32 step adds the column to an existing table and leaves every row a copy.
    /// True: an upgrade changes nothing about files already synced.
    /// False: an upgrade would lose rows or mark them as links.
    /// </summary>
    [Fact]
    public void Migration_V32_KeepsRowsAsCopies()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE SyncItems (Id INTEGER PRIMARY KEY AUTOINCREMENT, SourceItemId TEXT NOT NULL, LocalPath TEXT);
                INSERT INTO SyncItems (SourceItemId, LocalPath) VALUES ('old', '/m/Old.mkv');";
            cmd.ExecuteNonQuery();
        }

        DatabaseMigrationService.CreateV32Additions(conn);
        DatabaseMigrationService.CreateV32Additions(conn);

        using var read = conn.CreateCommand();
        read.CommandText = "SELECT StorageMode FROM SyncItems WHERE SourceItemId = 'old'";
        Assert.Equal(0L, read.ExecuteScalar());
    }

    /// <summary>
    /// The registry finds a link by its own path and by the path its copy would have.
    /// True: lookups by a translated source path reach the link.
    /// False: a linked item would look missing to every module.
    /// </summary>
    [Fact]
    public void Registry_FindsLinkByCopyPath()
    {
        _table.Upsert(Row("a", Media("Movie.strm"), StorageMode.Link, sourcePath: "/src/Movie.mkv"));

        Assert.Equal(Media("Movie.strm"), _registry.LinkPathFor(Media("Movie.mkv")));
        Assert.Equal(Media("Movie.mkv"), _registry.CopiedPathFor(Media("Movie.strm")));
        Assert.Equal("a", _registry.Find(Media("Movie.strm"))!.LinkId);
    }

    /// <summary>
    /// A path with another extension, or a .strm that is not a link row, is not a link.
    /// True: only the exact file a link stands in for finds it, and a user's .strm is never a link.
    /// False: Movie.mp4 could match a link for Movie.mkv, or a copied .strm could be treated as a link.
    /// </summary>
    [Fact]
    public void Registry_OtherExtensionOrCopiedStrm_IsNotALink()
    {
        _table.Upsert(Row("a", Media("Movie.strm"), StorageMode.Link, sourcePath: "/src/Movie.mkv"));
        _table.Upsert(Row("b", Media("Show.strm"), StorageMode.Copy, sourcePath: "/src/Show.strm"));

        Assert.Null(_registry.LinkPathFor(Media("Movie.mp4")));
        Assert.Null(_registry.Find(Media("Show.strm")));
    }

    /// <summary>
    /// A link written after the registry was read is found once the registry is told.
    /// True: a new link is matched at once, not only after the snapshot ages out.
    /// False: a new link would look missing for half a minute.
    /// </summary>
    [Fact]
    public void Registry_Invalidate_SeesNewLink()
    {
        Assert.Null(_registry.Find(Media("New.strm")));
        _table.Upsert(Row("n", Media("New.strm"), StorageMode.Link));

        _registry.Invalidate();

        Assert.NotNull(_registry.Find(Media("New.strm")));
    }

    /// <summary>
    /// A link's URL is this server's loopback address, its id, and a secret only this key derives.
    /// True: the proxy can tell its own links from any other URL, and the secret survives a restart.
    /// False: links could be guessed, or every link would break when the server restarts.
    /// </summary>
    [Fact]
    public void Files_Url_IsLoopbackWithStableSecret()
    {
        var url = _files.UrlFor("abc");
        var key = url[(url.IndexOf("?k=", StringComparison.Ordinal) + 3)..];

        Assert.StartsWith("http://127.0.0.1:8096/Sync/Link/abc?k=", url, StringComparison.Ordinal);
        Assert.True(_files.SecretMatches("abc", key));
        Assert.False(_files.SecretMatches("abd", key));
        Assert.False(_files.SecretMatches("abc", key + "x"));
        Assert.True(Files(_dir).SecretMatches("abc", key));
    }

    /// <summary>
    /// A new link row is queued, waits for approval when new files need it, and is settled when its
    /// .strm already holds the current URL.
    /// True: the approval setting applies to links the same way it applies to copies.
    /// False: links would skip approval, or be written again on every run.
    /// </summary>
    [Fact]
    public void State_NewLink_FollowsApprovalAndExistingFile()
    {
        var path = Media("Movie.strm");
        Assert.Equal(SyncStatus.Queued, NewLink(path, ApprovalMode.Enabled)!.Status);
        Assert.Equal(SyncStatus.Pending, NewLink(path, ApprovalMode.RequireApproval)!.Status);
        Assert.Null(NewLink(path, ApprovalMode.Disabled));

        _files.Write(path, "id", replace: false);
        var settled = NewLink(path, ApprovalMode.Enabled)!;
        Assert.Equal(SyncStatus.Synced, settled.Status);
        Assert.Equal(StorageMode.Link, settled.StorageMode);
    }

    /// <summary>
    /// A settled link whose .strm went missing is queued again, and a queued link already in place is settled.
    /// True: links repair themselves, and a size change on the source never re-queues one.
    /// False: a lost .strm would stay lost, or a link would be rewritten whenever the source file changes size.
    /// </summary>
    [Fact]
    public void State_ExistingLink_RepairsAndIgnoresSize()
    {
        var path = Media("Movie.strm");
        var row = Row("id", path, StorageMode.Link, status: SyncStatus.Synced);

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 999, DateTime.UtcNow, path, _files, NullLogger.Instance);
        Assert.Equal(SyncStatus.Queued, row.Status);

        _files.Write(path, "id", replace: false);
        LinkStateService.ProcessExistingLink(row, row.SourcePath, 12345, DateTime.UtcNow, path, _files, NullLogger.Instance);
        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.Equal(12345, row.SourceSize);
    }

    /// <summary>
    /// An ignored link stays ignored.
    /// True: the user's choice on the Sync tab is kept.
    /// False: an ignored link would be written again.
    /// </summary>
    [Fact]
    public void State_IgnoredLink_IsKept()
    {
        var row = Row("id", Media("Movie.strm"), StorageMode.Link, status: SyncStatus.Ignored);

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, row.LocalPath!, _files, NullLogger.Instance);

        Assert.Equal(SyncStatus.Ignored, row.Status);
    }

    /// <summary>
    /// The writer writes a link, and leaves a real copy or a .strm it did not write alone.
    /// True: a link never replaces a user's file.
    /// False: writing a link could overwrite a real copy's neighbour or a user's own .strm.
    /// </summary>
    [Fact]
    public void Writer_WritesOnlyWherePathIsFree()
    {
        var writer = new LinkWriter(_files, _registry, NullLogger<LinkWriter>.Instance);

        var linked = Row("a", Media("A.strm"), StorageMode.Link, sourcePath: "/src/A.mkv");
        writer.Write(linked, _table, Config());
        Assert.True(_files.IsCurrent(linked.LocalPath!, "a"));

        var beside = Row("b", Media("B.strm"), StorageMode.Link, sourcePath: "/src/B.mkv");
        File.WriteAllText(Media("B.mkv"), "real");
        Assert.Throws<InvalidOperationException>(() => writer.Write(beside, _table, Config()));
        Assert.False(File.Exists(beside.LocalPath));

        var users = Row("c", Media("C.strm"), StorageMode.Link, sourcePath: "/src/C.mkv");
        File.WriteAllText(users.LocalPath!, "http://example.com/stream\n");
        Assert.Throws<InvalidOperationException>(() => writer.Write(users, _table, Config()));
        Assert.Equal("http://example.com/stream\n", File.ReadAllText(users.LocalPath!));
    }

    /// <summary>
    /// Two source files that differ only by extension cannot both be linked.
    /// True: the second is refused with a reason and the first keeps its link.
    /// False: the second would overwrite the first's link.
    /// </summary>
    [Fact]
    public void Writer_NameCollision_IsRefused()
    {
        var writer = new LinkWriter(_files, _registry, NullLogger<LinkWriter>.Instance);
        var first = Row("a", Media("Movie.strm"), StorageMode.Link, sourcePath: "/src/Movie.mkv");
        _table.Upsert(first);
        writer.Write(first, _table, Config());

        var second = Row("b", Media("Movie.strm"), StorageMode.Link, sourcePath: "/src/Movie.mp4");
        var error = Assert.Throws<InvalidOperationException>(() => writer.Write(second, _table, Config()));

        Assert.Contains("differ only by their extension", error.Message, StringComparison.Ordinal);
        Assert.True(_files.IsCurrent(first.LocalPath!, "a"));
    }

    /// <summary>
    /// A link under a mapping that now copies becomes a copy, under the approval new files need, and
    /// stays a link while it waits.
    /// True: switching a mapping to Copy turns its links into copies, with approval when that is set.
    /// False: links would stay links forever, or turn into copies without the approval the user asked for.
    /// </summary>
    [Theory]
    [InlineData(ApprovalMode.Enabled, SyncStatus.Queued)]
    [InlineData(ApprovalMode.RequireApproval, SyncStatus.Pending)]
    public void State_LinkUnderCopyMapping_IsPromoted(ApprovalMode mode, SyncStatus expected)
    {
        var path = Media("Movie.strm");
        _files.Write(path, "id", replace: false);
        var row = Row("id", path, StorageMode.Link, status: SyncStatus.Synced);

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, path, _files, NullLogger.Instance, mode);

        Assert.Equal(StorageMode.Promoting, row.StorageMode);
        Assert.Equal(expected, row.Status);
        Assert.Equal(path, row.LocalPath);

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, path, _files, NullLogger.Instance, mode);
        Assert.Equal(expected, row.Status);
    }

    /// <summary>
    /// New files switched off leaves a link alone even under a Copy mapping, and switching back to Link
    /// before approval keeps it a link.
    /// True: nothing downloads that the settings do not allow.
    /// False: a disabled download setting would still pull full copies.
    /// </summary>
    [Fact]
    public void State_PromotionFollowsTheSettings()
    {
        var path = Media("Movie.strm");
        _files.Write(path, "id", replace: false);
        LinkNfo.Write(path, new Jellyfin.Sdk.Generated.Models.BaseItemDto { Name = "x" });
        var row = Row("id", path, StorageMode.Link, status: SyncStatus.Synced);

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, path, _files, NullLogger.Instance, ApprovalMode.Disabled);
        Assert.Equal(StorageMode.Link, row.StorageMode);
        Assert.Equal(SyncStatus.Synced, row.Status);

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, path, _files, NullLogger.Instance, ApprovalMode.RequireApproval);
        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, path, _files, NullLogger.Instance);
        Assert.Equal(StorageMode.Link, row.StorageMode);
        Assert.Equal(SyncStatus.Synced, row.Status);
    }

    /// <summary>
    /// Only a link can be promoted, and a link on its way to a copy is still found as a link.
    /// True: a copy is never marked, and a link keeps playing until its copy is in place.
    /// False: a copy row could be corrupted, or a link would stop playing as soon as it was marked.
    /// </summary>
    [Fact]
    public void Promotion_OnlyLinks_AndStillALink()
    {
        Assert.False(LinkStateService.MarkPromoting(Row("c", Media("C.mkv"), StorageMode.Copy), requireApproval: false));

        var row = Row("p", Media("P.strm"), StorageMode.Link, sourcePath: "/src/P.mkv");
        Assert.True(LinkStateService.MarkPromoting(row, requireApproval: false));
        _table.Upsert(row);

        Assert.Equal(Media("P.mkv"), _registry.CopiedPathFor(Media("P.strm")));
    }

    /// <summary>
    /// Removing a promoted link deletes only a .strm this plugin wrote.
    /// True: a user's own .strm at the same name survives.
    /// False: promotion could delete a file the user made.
    /// </summary>
    [Fact]
    public void Writer_Remove_OnlyItsOwnLinks()
    {
        var writer = new LinkWriter(_files, _registry, NullLogger<LinkWriter>.Instance);
        var ours = Media("Ours.strm");
        var users = Media("Users.strm");
        _files.Write(ours, "o", replace: false);
        File.WriteAllText(users, "http://example.com/stream\n");

        writer.Remove(ours);
        writer.Remove(users);

        Assert.False(File.Exists(ours));
        Assert.True(File.Exists(users));
    }

    /// <summary>
    /// A link is only written inside a mapped library, the same boundary a copy is held to.
    /// True: a row whose path escaped its library writes nothing.
    /// False: a crafted or broken path could put a .strm anywhere the server can write.
    /// </summary>
    [Fact]
    public void Writer_OutsideEveryLibrary_IsRefused()
    {
        var writer = new LinkWriter(_files, _registry, NullLogger<LinkWriter>.Instance);
        var outside = Path.Combine(_dir, "elsewhere", "Movie.strm");
        var row = Row("x", outside, StorageMode.Link);

        Assert.Throws<InvalidOperationException>(() => writer.Write(row, _table, Config()));
        Assert.False(File.Exists(outside));
    }

    private Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration Config()
    {
        var config = new Jellyfin.Plugin.ServerSync.Configuration.PluginConfiguration();
        config.Servers.Add(new SourceServer
        {
            Url = "http://source",
            ApiKey = "key",
            LibraryMappings = { new LibraryMapping { SourceLibraryId = "s", LocalLibraryId = "l", LocalRootPath = Path.Combine(_dir, "media"), SourceRootPath = "/src" } }
        });
        return config;
    }

    /// <summary>
    /// A database names v28 as its oldest reader until it holds a link, and v32 once it does, and a later
    /// migration keeps that floor.
    /// True: an older build that would download media over a .strm sets a database with links aside.
    /// False: a downgrade with links in place would overwrite every link with a copy's bytes.
    /// </summary>
    [Fact]
    public void ReaderFloor_RisesWithTheFirstLink()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        DatabaseMigrationService.CreateInitialSchema(conn);
        JPKribs.Jellyfin.Base.DatabaseSchema.Stamp(conn, DatabaseMigrationService.CurrentSchemaVersion, DatabaseMigrationService.MinReaderVersion);
        Assert.Equal(DatabaseMigrationService.MinReaderVersion, DatabaseMigrationService.MinReaderFor(conn));

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = @"INSERT INTO SyncItems (SourceLibraryId, LocalLibraryId, SourceItemId, SourcePath, SourceSize, SourceCreateDate, Status, StatusDate, StorageMode)
                VALUES ('s', 'l', 'id', '/src/A.mkv', 1, '2026-01-01', 1, '2026-01-01', 1)";
            insert.ExecuteNonQuery();
        }

        Assert.Equal(DatabaseMigrationService.LinkReaderVersion, DatabaseMigrationService.MinReaderFor(conn));
        Assert.True(DatabaseMigrationService.MigrateSchema(conn, DatabaseMigrationService.CurrentSchemaVersion - 1, NullLogger.Instance));
        Assert.Equal(DatabaseMigrationService.LinkReaderVersion, JPKribs.Jellyfin.Base.DatabaseSchema.GetMinReaderVersion(conn));
    }

    /// <summary>
    /// Writing the first link raises the floor in the database itself.
    /// True: the database a running server holds is protected from the moment it has a link.
    /// False: the floor would only rise at the next upgrade, too late for a downgrade before then.
    /// </summary>
    [Fact]
    public void Writer_FirstLink_RaisesTheReaderFloor()
    {
        var writer = new LinkWriter(_files, _registry, NullLogger<LinkWriter>.Instance);
        var row = Row("a", Media("A.strm"), StorageMode.Link, sourcePath: "/src/A.mkv");
        _table.Upsert(row);

        writer.Write(row, _table, Config());

        using var reader = new SqliteConnection($"Data Source={Path.Combine(_dir, "serversync", "sync.db")};Mode=ReadOnly");
        reader.Open();
        Assert.Equal(DatabaseMigrationService.LinkReaderVersion, JPKribs.Jellyfin.Base.DatabaseSchema.GetMinReaderVersion(reader));
    }

    /// <summary>
    /// A movie's .nfo carries its title, year, plot, genres, runtime, and ids, with text that would break
    /// XML escaped and control characters dropped, and the first id marked as the default.
    /// True: Jellyfin reads the link as the film it is, whatever its name says.
    /// False: an ampersand or a stray control character would make the .nfo unreadable.
    /// </summary>
    [Fact]
    public void Nfo_Movie_HoldsTheDetails()
    {
        var item = new Jellyfin.Sdk.Generated.Models.BaseItemDto
        {
            Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie,
            Name = "Tom & Jerry <The Movie>",
            OriginalTitle = "Tom et Jerry",
            ProductionYear = 2021,
            PremiereDate = new DateTimeOffset(2021, 2, 26, 0, 0, 0, TimeSpan.Zero),
            Overview = "A cat \u0001and a mouse.",
            Genres = new List<string> { "Comedy", "Family" },
            RunTimeTicks = TimeSpan.FromMinutes(101).Ticks,
            OfficialRating = "PG",
            ProviderIds = new Jellyfin.Sdk.Generated.Models.BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Imdb"] = "tt1361336", ["Tmdb"] = "587807" } }
        };

        var xml = System.Xml.Linq.XDocument.Parse(LinkNfo.Build(item));
        var movie = xml.Root!;
        Assert.Equal("movie", movie.Name.LocalName);
        Assert.Equal("Tom & Jerry <The Movie>", movie.Element("title")!.Value);
        Assert.Equal("2021", movie.Element("year")!.Value);
        Assert.Equal("2021-02-26", movie.Element("premiered")!.Value);
        Assert.Equal("101", movie.Element("runtime")!.Value);
        Assert.Equal(new[] { "Comedy", "Family" }, movie.Elements("genre").Select(e => e.Value));
        Assert.DoesNotContain("\u0001", movie.Element("plot")!.Value, StringComparison.Ordinal);
        var ids = movie.Elements("uniqueid").ToList();
        Assert.Equal(new[] { "imdb", "tmdb" }, ids.Select(e => e.Attribute("type")!.Value));
        Assert.Equal("true", ids[0].Attribute("default")?.Value);
        Assert.Null(ids[1].Attribute("default"));
    }

    /// <summary>
    /// An episode's .nfo names its show, season, and number, which is what Jellyfin matches episodes by.
    /// True: a linked episode lands under its show and season.
    /// False: a linked episode would be filed by its name alone.
    /// </summary>
    [Fact]
    public void Nfo_Episode_NamesItsShow()
    {
        var item = new Jellyfin.Sdk.Generated.Models.BaseItemDto
        {
            Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Episode,
            Name = "Pilot",
            SeriesName = "Test Show",
            ParentIndexNumber = 1,
            IndexNumber = 2
        };

        var episode = System.Xml.Linq.XDocument.Parse(LinkNfo.Build(item)).Root!;
        Assert.Equal("episodedetails", episode.Name.LocalName);
        Assert.Equal("Test Show", episode.Element("showtitle")!.Value);
        Assert.Equal("1", episode.Element("season")!.Value);
        Assert.Equal("2", episode.Element("episode")!.Value);
        Assert.Null(episode.Element("plot"));
    }

    /// <summary>
    /// The .nfo is written beside the link, replaced only while it carries the marker, and left alone
    /// once the user takes the marker out or puts their own .nfo there.
    /// True: the user's own metadata always wins.
    /// False: a sync would overwrite an .nfo the user edited.
    /// </summary>
    [Fact]
    public void Nfo_ReplacesOnlyItsOwn()
    {
        var link = Media("Nfo Film.strm");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var item = new Jellyfin.Sdk.Generated.Models.BaseItemDto { Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie, Name = "First" };

        Assert.True(LinkNfo.Write(link, item));
        Assert.True(LinkNfo.IsOurs(LinkNfo.PathFor(link)));
        item.Name = "Second";
        Assert.True(LinkNfo.Write(link, item));
        Assert.Contains("Second", File.ReadAllText(LinkNfo.PathFor(link)), StringComparison.Ordinal);

        var edited = string.Join("\n", File.ReadAllLines(LinkNfo.PathFor(link)).Where(l => !l.Contains(LinkNfo.Marker, StringComparison.Ordinal))).Replace("Second", "Mine", StringComparison.Ordinal);
        File.WriteAllText(LinkNfo.PathFor(link), edited);
        item.Name = "Third";
        Assert.False(LinkNfo.Write(link, item));
        Assert.Contains("Mine", File.ReadAllText(LinkNfo.PathFor(link)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A settled link without its .nfo is queued, so links written before they had one get it.
    /// True: every link ends up with an .nfo on the next sync.
    /// False: links from before this change would never have one.
    /// </summary>
    [Fact]
    public void State_LinkWithoutNfo_IsQueued()
    {
        var path = Media("No Nfo.strm");
        _files.Write(path, "id", replace: false);
        var row = Row("id", path, StorageMode.Link, status: SyncStatus.Synced);

        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, path, _files, NullLogger.Instance);
        Assert.Equal(SyncStatus.Queued, row.Status);

        LinkNfo.Write(path, new Jellyfin.Sdk.Generated.Models.BaseItemDto { Name = "x" });
        row.Status = SyncStatus.Synced;
        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, path, _files, NullLogger.Instance);
        Assert.Equal(SyncStatus.Synced, row.Status);
    }

    /// <summary>
    /// An .nfo the user deletes after it was written stays deleted, which is how the docs say to hand a
    /// link's metadata to Jellyfin.
    /// True: the next sync leaves the link settled and writes no .nfo.
    /// False: every refresh would queue the link and put the .nfo back.
    /// </summary>
    [Fact]
    public void State_NfoTheUserDeleted_StaysDeleted()
    {
        var path = Media("Deleted Nfo.strm");
        _files.Write(path, "id", replace: false);
        LinkNfo.Write(path, new Jellyfin.Sdk.Generated.Models.BaseItemDto { Name = "x" });
        var row = Row("id", path, StorageMode.Link, status: SyncStatus.Synced);
        row.CompanionFiles = "Deleted Nfo.en.srt";

        // An .nfo from before rows recorded them is recorded on the first refresh that sees it.
        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, path, _files, NullLogger.Instance);
        Assert.True(LinkNfo.Recorded(row));
        Assert.Contains("Deleted Nfo.en.srt", row.CompanionFiles, StringComparison.Ordinal);

        File.Delete(LinkNfo.PathFor(path));
        LinkStateService.ProcessExistingLink(row, row.SourcePath, 1, DateTime.UtcNow, path, _files, NullLogger.Instance);
        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.True(LinkNfo.RemovedByUser(row));
    }

    /// <summary>
    /// An emoji in a title is kept whole, and half of a broken one is dropped.
    /// True: titles with emoji read right, and broken text from a source never stops the .nfo.
    /// False: an emoji would be mangled, or a broken one would leave the link without metadata.
    /// </summary>
    [Fact]
    public void Nfo_KeepsEmoji_DropsBrokenHalves()
    {
        var item = new Jellyfin.Sdk.Generated.Models.BaseItemDto { Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie, Name = "\U0001F3AC Film \uD83D end" };

        var title = System.Xml.Linq.XDocument.Parse(LinkNfo.Build(item)).Root!.Element("title")!.Value;

        Assert.Equal("\U0001F3AC Film  end", title);
    }

    private SyncItem? NewLink(string path, ApprovalMode mode)
        => LinkStateService.ProcessNewLink(new LibraryMapping { SourceLibraryId = "s", LocalLibraryId = "l" }, "id", "/src/Movie.mkv", 1, DateTime.UtcNow, path, mode, _files);

    private static SyncItem Row(string id, string localPath, StorageMode mode, string sourcePath = "/src/Movie.mkv", SyncStatus status = SyncStatus.Queued)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        return new SyncItem
        {
            SourceItemId = id,
            SourceLibraryId = "s",
            LocalLibraryId = "l",
            SourcePath = sourcePath,
            SourceSize = 1,
            SourceCreateDate = DateTime.UtcNow,
            LocalPath = localPath,
            StorageMode = mode,
            Status = status,
            StatusDate = DateTime.UtcNow,
            ServerKey = "server"
        };
    }

    /// <summary>
    /// The link check flags a link whose file the source no longer has, leaves a healthy one alone,
    /// and settles a flagged one again once the source has the file.
    /// True: a dead link shows on the Sync tab with the reason before someone presses play, and recovers on its own.
    /// False: a dead link would only be found by playing it, or would stay flagged after the source came back.
    /// </summary>
    [Fact]
    public async Task Verify_FlagsDeadLinks_AndSettlesThemAgain()
    {
        _table.Upsert(Row("aaaa", Media("Gone.strm"), StorageMode.Link, status: SyncStatus.Synced));
        _table.Upsert(Row("bbbb", Media("Fine.strm"), StorageMode.Link, status: SyncStatus.Synced));
        _table.Upsert(Row("cccc", Media("Copy.mkv"), StorageMode.Copy, status: SyncStatus.Synced));
        var answer = "{\"Items\":[{\"ItemId\":\"aaaa\",\"Exists\":false,\"Reason\":\"the file is no longer on the source\"},{\"ItemId\":\"bbbb\",\"Exists\":true,\"Size\":4321}]}";
        var handler = new DescribeHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") });
        using var client = new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), "http://peer", "k", "test", "0");

        var found = await Verifier().VerifyAsync(_table, key => key == "server" ? client : null, CancellationToken.None);

        Assert.Equal(new LinkVerification(2, 1, 0, 0), found);
        var gone = _table.GetBySourceItemId("aaaa")!;
        Assert.Equal(SyncStatus.Errored, gone.Status);
        Assert.Equal(LinkVerifier.Prefix + "the file is no longer on the source", gone.Reason);
        Assert.Empty(_table.GetErroredItemsForRetry(3));
        Assert.Equal(SyncStatus.Synced, _table.GetBySourceItemId("bbbb")!.Status);
        Assert.Equal(4321, _table.GetBySourceItemId("bbbb")!.SourceSize);
        Assert.Equal(SyncStatus.Synced, _table.GetBySourceItemId("cccc")!.Status);

        // A refresh leaves the flag alone although the .strm is in place.
        _files.Write(gone.LocalPath!, "aaaa", replace: false);
        LinkStateService.ProcessExistingLink(gone, gone.SourcePath, 1, DateTime.UtcNow, gone.LocalPath!, _files, NullLogger.Instance);
        Assert.Equal(SyncStatus.Errored, gone.Status);

        answer = "{\"Items\":[{\"ItemId\":\"aaaa\",\"Exists\":true,\"Size\":1},{\"ItemId\":\"bbbb\",\"Exists\":true,\"Size\":4321}]}";
        found = await Verifier().VerifyAsync(_table, _ => client, CancellationToken.None);

        Assert.Equal(new LinkVerification(2, 0, 1, 0), found);
        Assert.Equal(SyncStatus.Synced, _table.GetBySourceItemId("aaaa")!.Status);
        Assert.Null(_table.GetBySourceItemId("aaaa")!.Reason);
    }

    /// <summary>
    /// A link the link check flagged, whose file then leaves the source's listing, is removed the way
    /// a settled link is, not held for approval.
    /// True: with removals automatic, a film deleted on the source takes its link with it.
    /// False: every removed film would wait as a pending deletion once the link check had seen it first.
    /// </summary>
    [Fact]
    public void Missing_FlaggedLink_IsRemovedAsASettledOne()
    {
        var path = Media("Flagged.strm");
        _files.Write(path, "id", replace: false);
        var flagged = Row("id", path, StorageMode.Link, status: SyncStatus.Errored);
        flagged.Reason = LinkVerifier.Prefix + "the file is no longer on the source";
        _table.Upsert(flagged);
        var failed = Row("other", Media("Failed.strm"), StorageMode.Link, status: SyncStatus.Errored);
        failed.Reason = "A .strm that Server Sync did not write is already there";
        _files.Write(failed.LocalPath!, "other", replace: false);
        _table.Upsert(failed);

        SyncStateService.ProcessMissingItem(_table, _table.GetBySourceItemId("id")!, ApprovalMode.Enabled, NullLogger.Instance);
        SyncStateService.ProcessMissingItem(_table, _table.GetBySourceItemId("other")!, ApprovalMode.Enabled, NullLogger.Instance);

        Assert.Equal(SyncStatus.Deleting, _table.GetBySourceItemId("id")!.Status);
        Assert.Equal(SyncStatus.Pending, _table.GetBySourceItemId("other")!.Status);
    }

    /// <summary>
    /// A source that refuses the check outright flags every link to it with its reason, and one that
    /// is down or does not know the check flags nothing.
    /// True: switching linking off on the source shows on every link, and an outage is not mistaken for dead links.
    /// False: links would look healthy with linking off, or all be flagged whenever a source restarts.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, SyncStatus.Errored)]
    [InlineData(HttpStatusCode.NotFound, SyncStatus.Synced)]
    [InlineData(HttpStatusCode.BadGateway, SyncStatus.Synced)]
    public async Task Verify_RefusalFlagsAll_OutageFlagsNone(HttpStatusCode status, SyncStatus expected)
    {
        _table.Upsert(Row("aaaa", Media("A.strm"), StorageMode.Link, status: SyncStatus.Synced));
        var handler = new DescribeHandler(() => new HttpResponseMessage(status) { Content = new StringContent("linking is not allowed for local here") });
        using var client = new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), "http://peer", "k", "test", "0");

        await Verifier().VerifyAsync(_table, _ => client, CancellationToken.None);

        var row = _table.GetBySourceItemId("aaaa")!;
        Assert.Equal(expected, row.Status);
        if (expected == SyncStatus.Errored)
        {
            Assert.Equal(LinkVerifier.Prefix + "linking is not allowed for local here", row.Reason);
        }
    }

    /// <summary>
    /// The source's streams sit between this server's external audio and its other external files,
    /// numbered in that order, and the source's own external files are left out.
    /// True: stream numbers match what Jellyfin's own probe would save, so track selection and subtitles line up.
    /// False: the player would pick the wrong track, or a subtitle file here would vanish.
    /// </summary>
    [Fact]
    public void MediaInfo_Merge_KeepsLocalExternals_InProbeOrder()
    {
        var local = new[]
        {
            new MediaStream { Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/here/Film.en.srt", Index = 0 },
            new MediaStream { Type = MediaStreamType.Audio, IsExternal = true, Path = "/here/Film.commentary.mka", Index = 1 }
        };
        var shipped = new[]
        {
            new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0 },
            new MediaStream { Type = MediaStreamType.Audio, Codec = "eac3", Index = 1 },
            new MediaStream { Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/there/Film.de.srt", Index = 2 }
        };

        var merged = LinkMediaInfo.Merge(local, shipped);

        Assert.Equal(new[] { "/here/Film.commentary.mka", null, null, "/here/Film.en.srt" }, merged.Select(s => s.Path));
        Assert.Equal(new[] { 0, 1, 2, 3 }, merged.Select(s => s.Index));
        Assert.Equal("hevc", merged[1].Codec);
    }

    /// <summary>
    /// A trickplay set's tile count is its thumbnails divided over full tiles, rounded up.
    /// True: every tile of a set is taken, and none past the last is asked for.
    /// False: the last tile would be missing, or a set would never count as complete.
    /// </summary>
    [Theory]
    [InlineData(100, 10, 10, 1)]
    [InlineData(101, 10, 10, 2)]
    [InlineData(720, 10, 10, 8)]
    [InlineData(0, 10, 10, 0)]
    [InlineData(50, 0, 10, 0)]
    public void Trickplay_TileCount(int thumbnails, int tileWidth, int tileHeight, int expected)
        => Assert.Equal(expected, new PeerTrickplay { ThumbnailCount = thumbnails, TileWidth = tileWidth, TileHeight = tileHeight }.TileCount());

    private static LinkVerifier Verifier()
    {
        var (library, _) = InterfaceStub.Create<MediaBrowser.Controller.Library.ILibraryManager>();
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "local");
        return new LinkVerifier(library, null, host, NullLogger<LinkVerifier>.Instance);
    }

    private sealed class DescribeHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _answer;

        public DescribeHandler(Func<HttpResponseMessage> answer) => _answer = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_answer());
    }

    private static LinkFiles Files(string dataPath)
    {
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_HttpPort", _ => 8096);
        hostStub.Answer(nameof(IServerApplicationHost.GetLocalApiUrl), args => $"{args[1]}://{args[0]}:{args[2]}");
        var (paths, pathsStub) = InterfaceStub.Create<IApplicationPaths>();
        pathsStub.Answer("get_DataPath", _ => dataPath);
        return new LinkFiles(host, paths);
    }

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }
}

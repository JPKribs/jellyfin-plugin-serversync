using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Database;

/// <summary>
/// The database must open or create its file in its own folder, keep what it holds across restarts, set a corrupt file aside without losing it, and refuse use once disposed.
/// </summary>
public sealed class SyncDatabaseTests : IDisposable
{
    private static readonly ScanWatermark Mark = new("scope", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

    private readonly DatabaseTempFolder _folder = new("database");

    /// <inheritdoc/>
    public void Dispose() => _folder.Dispose();

    /// <summary>
    /// The constructor refuses a missing logger or data folder.
    /// True: a bad registration fails at once.
    /// False: the database would be created under a null path.
    /// </summary>
    [Fact]
    public void Constructor_MissingArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SyncDatabase(null!, _folder.DataPath));
        Assert.Throws<ArgumentNullException>(() => new SyncDatabase(NullLogger<SyncDatabase>.Instance, null!));
    }

    /// <summary>
    /// A first start creates the file in a folder of its own under the data folder, at the current schema.
    /// True: a fresh install has a database every store can use.
    /// False: the first query would fail on a missing file or table.
    /// </summary>
    [Fact]
    public void Constructor_EmptyDataFolder_CreatesTheDatabaseAtTheCurrentSchema()
    {
        using (new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath))
        {
        }

        Assert.True(File.Exists(_folder.DatabaseFile));
        Assert.Equal(SyncSchema.CurrentSchemaVersion, SyncSchema.StoredVersion(_folder.DataPath));
    }

    /// <summary>
    /// A data folder whose path holds a semicolon, a quote, and a space still opens.
    /// True: the path is passed to SQLite as one value.
    /// False: such a folder would break the connection string apart, and the plugin could not start there.
    /// </summary>
    [Fact]
    public void Constructor_DataFolderWithConnectionStringCharacters_StillOpens()
    {
        var odd = Path.Combine(_folder.DataPath, "da ta;Mode=Memory'x");

        using (var database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, odd))
        {
            Store(database).Set("content", "a", Mark);
        }

        Assert.True(File.Exists(Path.Combine(odd, "serversync", "sync.db")));
    }

    /// <summary>
    /// Rows written before a restart are there after it.
    /// True: tracking survives a Jellyfin restart.
    /// False: every restart would start every sync type from nothing.
    /// </summary>
    [Fact]
    public void Constructor_ExistingDatabase_KeepsItsRows()
    {
        using (var first = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath))
        {
            Store(first).Set("content", "a", Mark);
        }

        using var second = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);

        Assert.Equal(Mark, Store(second).Get("content", "a"));
    }

    /// <summary>
    /// A file that is not a database is moved aside whole as a corrupt backup, and a fresh database takes its place.
    /// True: the plugin starts again, and the damaged file is kept for recovery.
    /// False: a corrupt file would stop the plugin for good, or be deleted with the rows it still held.
    /// </summary>
    [Fact]
    public void Constructor_CorruptFile_SetsItAsideAndStartsFresh()
    {
        var garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray();
        Directory.CreateDirectory(_folder.DatabaseFolder);
        File.WriteAllBytes(_folder.DatabaseFile, garbage);

        using var database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);

        var backup = Assert.Single(Directory.GetFiles(_folder.DatabaseFolder, "sync.db.corrupt-*"));
        Assert.Equal(garbage, File.ReadAllBytes(backup));
        Assert.Null(Store(database).Get("content", "a"));
        using var raw = _folder.Raw();
        Assert.Equal(SyncSchema.CurrentSchemaVersion, DatabaseSchema.GetVersion(raw));
    }

    /// <summary>
    /// A reset drops every row and leaves a usable database at the current schema, with no backup.
    /// True: Reset forgets tracking and the next scan starts clean.
    /// False: old rows would survive a reset, or the stores would fail after it.
    /// </summary>
    [Fact]
    public void ResetDatabase_WithRows_LeavesAnEmptyUsableDatabase()
    {
        using var database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        var store = Store(database);
        store.Set("content", "a", Mark);

        database.ResetDatabase();
        var afterReset = store.Get("content", "a");
        store.Set("content", "b", Mark);

        Assert.Null(afterReset);
        Assert.Equal(Mark, store.Get("content", "b"));
        Assert.Equal(new[] { "sync.db" }, Directory.GetFiles(_folder.DatabaseFolder).Select(Path.GetFileName).Where(n => !n!.EndsWith("-wal", StringComparison.Ordinal) && !n.EndsWith("-shm", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A disposed database refuses reads, writes, and a reset, and disposing twice is harmless.
    /// True: a store used during shutdown fails cleanly.
    /// False: a late write would reopen the file after Jellyfin released it, or shutdown would throw.
    /// </summary>
    [Fact]
    public void Dispose_ThenUse_ThrowsObjectDisposed()
    {
        var database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        var store = Store(database);

        database.Dispose();
        database.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.Get("content", "a"));
        Assert.Throws<ObjectDisposedException>(() => store.Set("content", "a", Mark));
        Assert.Throws<ObjectDisposedException>(() => database.ResetDatabase());
    }

    /// <summary>
    /// More reads than there are read connections all finish with the right answer.
    /// True: the dashboard's polling and a scan's lookups share the read connections.
    /// False: a burst of reads would deadlock or fail once the connections ran out.
    /// </summary>
    [Fact]
    public void Reads_MoreThanTheReadConnections_AllComplete()
    {
        using var database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        var store = Store(database);
        store.Set("content", "a", Mark);

        var results = Enumerable.Range(0, SyncDatabase.MaxReadConnections * 8)
            .AsParallel()
            .WithDegreeOfParallelism(SyncDatabase.MaxReadConnections * 2)
            .Select(_ => store.Get("content", "a"))
            .ToList();

        Assert.Equal(SyncDatabase.MaxReadConnections * 8, results.Count);
        Assert.All(results, r => Assert.Equal(Mark, r));
    }

    private static ScanWatermarkStore Store(SyncDatabase database)
        => new(new FixedProvider(database), NullLogger<ScanWatermarkStore>.Instance);
}

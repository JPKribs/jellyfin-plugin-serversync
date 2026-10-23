using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Database;

/// <summary>
/// The one upsert every sync table writes with. An Ignored row must survive every scan as the user
/// left it, and a synced hash must not be wiped by a scan that does not know it.
/// </summary>
public sealed class UpsertBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;

    public UpsertBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-upsert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }

    private static SyncItem Content(SyncStatus status, int retries, string? reason) => new()
    {
        SourceLibraryId = "lib",
        LocalLibraryId = "local",
        SourceItemId = "item-1",
        SourcePath = "/src/a.mkv",
        SourceCreateDate = DateTime.UtcNow,
        LocalPath = "/local/a.mkv",
        Status = status,
        StatusDate = DateTime.UtcNow,
        RetryCount = retries,
        Reason = reason
    };

    /// <summary>
    /// A scan over an Ignored content row keeps its status, reason, and retry count.
    /// True: Content behaves like every other table.
    /// False: the retry count of an Ignored row was overwritten by each scan, unlike the other tables.
    /// </summary>
    [Fact]
    public void Content_IgnoredRow_KeepsItsBookkeeping()
    {
        var manager = new ContentSyncTableManager(new FixedProvider(_database), NullLogger<ContentSyncTableManager>.Instance);
        manager.Upsert(Content(SyncStatus.Ignored, retries: 4, reason: "user said no"));

        manager.Upsert(Content(SyncStatus.Queued, retries: 0, reason: null));

        var row = manager.GetByKey("item-1")!;
        Assert.Equal(SyncStatus.Ignored, row.Status);
        Assert.Equal(4, row.RetryCount);
        Assert.Equal("user said no", row.Reason);
    }

    [Fact]
    public void Content_NonIgnoredRow_TakesTheNewValues()
    {
        var manager = new ContentSyncTableManager(new FixedProvider(_database), NullLogger<ContentSyncTableManager>.Instance);
        manager.Upsert(Content(SyncStatus.Errored, retries: 2, reason: "timed out"));

        manager.Upsert(Content(SyncStatus.Queued, retries: 0, reason: null));

        var row = manager.GetByKey("item-1")!;
        Assert.Equal(SyncStatus.Queued, row.Status);
        Assert.Equal(0, row.RetryCount);
        Assert.Null(row.Reason);
    }

    /// <summary>A synced hash survives a write that does not carry one.</summary>
    [Fact]
    public void People_SyncedHash_SurvivesAWriteWithoutOne()
    {
        var manager = new PeopleSyncTableManager(new FixedProvider(_database), NullLogger<PeopleSyncTableManager>.Instance);
        var person = new PeopleSyncItem { PersonName = "Jane", Status = SyncStatus.Synced, StatusDate = DateTime.UtcNow };
        person.Metadata.SyncedHash = "abc";
        manager.Upsert(person);

        var rescanned = new PeopleSyncItem { PersonName = "Jane", Status = SyncStatus.Queued, StatusDate = DateTime.UtcNow };
        manager.Upsert(rescanned);

        Assert.Equal("abc", manager.GetByKey("Jane")!.Metadata.SyncedHash);
    }

    [Fact]
    public void Sql_NamesTheConflictTargetAndKeepsKeysOutOfTheUpdate()
    {
        var sql = new UpsertBuilder("T").Key("A", 1).Key("B", 2).Set("C", 3).Sql;

        Assert.Contains("ON CONFLICT(A, B) DO UPDATE SET", sql, StringComparison.Ordinal);
        Assert.Contains("C = @p_C", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("A = @p_A", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_WithoutAKey_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => new UpsertBuilder("T").Set("C", 3).Sql);
    }
}

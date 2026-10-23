using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using JPKribs.Jellyfin.Base;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Database;

/// <summary>
/// The operations every sync table shares, run through a real <see cref="ContentSyncTableManager"/>
/// over a temp-file SQLite database. These back the dashboard counts, the bulk Queue / Retry / Ignore
/// actions and the retry ceiling of every module, so a wrong WHERE or SET clause here is wrong in all
/// of them at once.
/// </summary>
public sealed class SyncTableManagerBaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly ContentSyncTableManager _content;

    public SyncTableManagerBaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tablebase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _content = new ContentSyncTableManager(new FixedProvider(_database), NullLogger<ContentSyncTableManager>.Instance);
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

    /// <summary>
    /// A bare manager over the People table whose only job is to hand <see cref="SyncTableManagerBase{TRecord, TKey}.ExecuteRead"/>
    /// and <see cref="SyncTableManagerBase{TRecord, TKey}.ExecuteWrite"/> an operation that fails the way
    /// a locked, unopenable or closed database does, which a real file cannot be made to do on demand.
    /// </summary>
    private sealed class FailingReadManager : SyncTableManagerBase<PeopleSyncItem, string>
    {
        public FailingReadManager(SyncDatabase database)
            : base(database, NullLogger.Instance)
        {
        }

        protected override string TableName => "PeopleSyncItems";

        protected override string KeyColumn => "PersonName";

        public int ReadThrowing(Exception ex) => ExecuteRead<int>(_ => throw ex, fallback: -1);

        public void WriteThrowing(Exception ex) => ExecuteWrite(_ => throw ex);

        protected override PeopleSyncItem MapFromReader(IDataRecord reader) => new() { PersonName = reader.GetString(reader.GetOrdinal("PersonName")) };

        public override PeopleSyncItem? GetByKey(string key) => null;

        public override void DeleteByKey(string key)
        {
        }

        public override void Upsert(PeopleSyncItem record)
        {
        }

        public override PagedResult<PeopleSyncItem> Paginate(PaginationRequest request)
            => new(Array.Empty<PeopleSyncItem>(), 0, 0, 50);
    }

    private static SyncItem Row(string sourceItemId, SyncStatus status, int retryCount = 0, string? reason = null, string? serverKey = null) => new()
    {
        SourceLibraryId = "lib-1",
        LocalLibraryId = "local-1",
        SourceItemId = sourceItemId,
        SourcePath = "/src/" + sourceItemId + ".mkv",
        SourceSize = 100,
        SourceCreateDate = DateTime.UtcNow,
        LocalPath = "/nonexistent/" + sourceItemId + ".mkv",
        Status = status,
        StatusDate = DateTime.UtcNow,
        RetryCount = retryCount,
        Reason = reason,
        ServerKey = serverKey
    };

    private SyncItem Seed(string sourceItemId, SyncStatus status, int retryCount = 0, string? reason = null, string? serverKey = null)
    {
        _content.Upsert(Row(sourceItemId, status, retryCount, reason, serverKey));
        return _content.GetByKey(sourceItemId)!;
    }

    // -----------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------

    /// <summary>
    /// GetById finds the row by its primary key and an unknown id is null.
    /// True: the dashboard's per-row actions reach the row the user clicked.
    /// False: an action on one row lands on another, or a stale id throws instead of reading as gone.
    /// </summary>
    [Fact]
    public void GetById_FindsTheRow_AndUnknownIsNull()
    {
        var a = Seed("a", SyncStatus.Queued);
        var b = Seed("b", SyncStatus.Synced);

        Assert.Equal("a", _content.GetById(a.Id)!.SourceItemId);
        Assert.Equal("b", _content.GetById(b.Id)!.SourceItemId);
        Assert.Null(_content.GetById(b.Id + 1000));
    }

    /// <summary>
    /// GetByStatus returns only rows in that status, and the limit caps how many.
    /// True: a sync run picks up exactly the queued rows, a bounded number at a time.
    /// False: a run applies Synced or Ignored rows again, or the limit is ignored and a huge table is read at once.
    /// </summary>
    [Fact]
    public void GetByStatus_FiltersByStatus_AndHonoursLimit()
    {
        Seed("q1", SyncStatus.Queued);
        Seed("q2", SyncStatus.Queued);
        Seed("q3", SyncStatus.Queued);
        Seed("s1", SyncStatus.Synced);
        Seed("i1", SyncStatus.Ignored);

        var queued = _content.GetByStatus(SyncStatus.Queued);
        Assert.Equal(new[] { "q1", "q2", "q3" }, queued.Select(r => r.SourceItemId).OrderBy(k => k));
        Assert.Equal(2, _content.GetByStatus(SyncStatus.Queued, limit: 2).Count);
        Assert.Empty(_content.GetByStatus(SyncStatus.Errored));
        Assert.Equal("s1", Assert.Single(_content.GetByStatusStrict(SyncStatus.Synced)).SourceItemId);
    }

    /// <summary>
    /// Count, CountByStatus and GetStatusCounts agree with the rows, and statuses with no rows are left out.
    /// True: the dashboard's totals match the table.
    /// False: the badge counts lie, or a zero bucket shows a phantom status.
    /// </summary>
    [Fact]
    public void Counts_MatchTheRows()
    {
        Seed("q1", SyncStatus.Queued);
        Seed("q2", SyncStatus.Queued);
        Seed("s1", SyncStatus.Synced);
        Seed("e1", SyncStatus.Errored);

        Assert.Equal(4, _content.Count());
        Assert.Equal(2, _content.CountByStatus(SyncStatus.Queued));
        Assert.Equal(0, _content.CountByStatus(SyncStatus.Ignored));

        var counts = _content.GetStatusCounts();
        Assert.Equal(3, counts.Count);
        Assert.Equal(2, counts[SyncStatus.Queued]);
        Assert.Equal(1, counts[SyncStatus.Synced]);
        Assert.Equal(1, counts[SyncStatus.Errored]);
        Assert.False(counts.ContainsKey(SyncStatus.Ignored));
    }

    // -----------------------------------------------------------------------
    // Status transitions
    // -----------------------------------------------------------------------

    /// <summary>
    /// Synced clears the retry count and the reason, and stamps LastSyncTime, whatever reason the caller passes.
    /// True: a row that finally applied starts with a clean slate and shows no stale error.
    /// False: a synced row keeps "Download failed" on the dashboard, or carries spent retries into its next failure.
    /// </summary>
    [Fact]
    public void UpdateStatus_Synced_ClearsRetryCountAndReason_AndStampsLastSync()
    {
        var row = Seed("a", SyncStatus.Errored, retryCount: 2, reason: "boom");
        Assert.Null(row.LastSyncTime);

        _content.UpdateStatus(row.Id, SyncStatus.Synced, reason: "ignored by Synced");

        var after = _content.GetById(row.Id)!;
        Assert.Equal(SyncStatus.Synced, after.Status);
        Assert.Equal(0, after.RetryCount);
        Assert.Null(after.Reason);
        Assert.NotNull(after.LastSyncTime);
    }

    /// <summary>
    /// Errored increments the retry count on every transition and stores the reason.
    /// True: the retry ceiling eventually stops a row that can never apply.
    /// False: a broken row is retried on every run forever, or fails with no explanation.
    /// </summary>
    [Fact]
    public void UpdateStatus_Errored_IncrementsRetryCount_AndKeepsReason()
    {
        var row = Seed("a", SyncStatus.Queued);

        _content.UpdateStatus(row.Id, SyncStatus.Errored, "first");
        _content.UpdateStatus(row.Id, SyncStatus.Errored, "second");

        var after = _content.GetById(row.Id)!;
        Assert.Equal(SyncStatus.Errored, after.Status);
        Assert.Equal(2, after.RetryCount);
        Assert.Equal("second", after.Reason);
        Assert.Null(after.LastSyncTime);
    }

    /// <summary>
    /// Queued through UpdateStatus is an operator action and hands back the full retry allowance.
    /// Ignored keeps the count and stores the user's reason.
    /// True: Retry on an exhausted row really retries, and an ignore reason survives.
    /// False: a retried row is refused by the ceiling at once, or ignoring a row wipes its history.
    /// </summary>
    [Fact]
    public void UpdateStatus_Queued_ResetsRetryCount_Ignored_KeepsIt()
    {
        var retried = Seed("a", SyncStatus.Errored, retryCount: 3, reason: "boom");
        var ignored = Seed("b", SyncStatus.Errored, retryCount: 2, reason: "boom");

        _content.UpdateStatus(retried.Id, SyncStatus.Queued);
        _content.UpdateStatus(ignored.Id, SyncStatus.Ignored, "user said so");

        var a = _content.GetById(retried.Id)!;
        Assert.Equal(SyncStatus.Queued, a.Status);
        Assert.Equal(0, a.RetryCount);
        Assert.Null(a.Reason);

        var b = _content.GetById(ignored.Id)!;
        Assert.Equal(SyncStatus.Ignored, b.Status);
        Assert.Equal(2, b.RetryCount);
        Assert.Equal("user said so", b.Reason);
    }

    /// <summary>
    /// UpdateStatusByKey uses the same transition rules as UpdateStatus, addressed by the natural key, and
    /// touches no other row.
    /// True: hint applies and the per-key endpoints settle rows exactly like a full run.
    /// False: a key-addressed Synced keeps the old error, or the WHERE clause reaches other rows.
    /// </summary>
    [Fact]
    public void UpdateStatusByKey_AppliesTransitionRules_ToThatRowOnly()
    {
        Seed("a", SyncStatus.Errored, retryCount: 2, reason: "boom");
        Seed("b", SyncStatus.Errored, retryCount: 1, reason: "other");

        _content.UpdateStatusByKey("a", SyncStatus.Synced);
        _content.UpdateStatusByKey("missing", SyncStatus.Synced);

        var a = _content.GetByKey("a")!;
        Assert.Equal(SyncStatus.Synced, a.Status);
        Assert.Equal(0, a.RetryCount);
        Assert.Null(a.Reason);

        var b = _content.GetByKey("b")!;
        Assert.Equal(SyncStatus.Errored, b.Status);
        Assert.Equal(1, b.RetryCount);
        Assert.Equal("other", b.Reason);
        Assert.Equal(2, _content.Count());
    }

    /// <summary>
    /// The bulk update reports how many rows it updated and which ids no longer exist, and applies the
    /// same transition to each.
    /// True: "5 of 10 updated, 5 not found" is accurate, and every found row gets the retry bookkeeping.
    /// False: rows deleted mid-click vanish silently, or bulk Errored skips the retry increment.
    /// </summary>
    [Fact]
    public void BulkUpdateStatusWithDetails_ReportsUpdatedAndNotFound()
    {
        var a = Seed("a", SyncStatus.Queued);
        var b = Seed("b", SyncStatus.Queued, retryCount: 1);
        var missing = b.Id + 1000;

        var (updated, notFound) = _content.BulkUpdateStatusWithDetails(new[] { a.Id, missing, b.Id }, SyncStatus.Errored, "bulk");

        Assert.Equal(2, updated);
        Assert.Equal(new[] { missing }, notFound);
        Assert.Equal(1, _content.GetById(a.Id)!.RetryCount);
        Assert.Equal(2, _content.GetById(b.Id)!.RetryCount);
        Assert.All(new[] { a.Id, b.Id }, id => Assert.Equal("bulk", _content.GetById(id)!.Reason));

        var (none, noneMissing) = _content.BulkUpdateStatusWithDetails(Array.Empty<long>(), SyncStatus.Synced);
        Assert.Equal(0, none);
        Assert.Empty(noneMissing);
    }

    /// <summary>
    /// Content's BatchUpdateStatus, addressed by source item id, follows the shared transition rules and
    /// clears PendingType.
    /// True: approving a batch of pending downloads queues them with no leftover approval flag.
    /// False: approved rows stay flagged as pending downloads, or a batch Synced keeps their errors.
    /// </summary>
    [Fact]
    public void BatchUpdateStatus_UsesTransitionRules_AndClearsPendingType()
    {
        var pending = Row("p", SyncStatus.Pending);
        pending.PendingType = PendingType.Download;
        _content.Upsert(pending);
        Seed("e", SyncStatus.Errored, retryCount: 2, reason: "boom");

        var count = _content.BatchUpdateStatus(new[] { "p", "e", "missing" }, SyncStatus.Synced, "not kept");

        Assert.Equal(2, count);
        var p = _content.GetByKey("p")!;
        Assert.Equal(SyncStatus.Synced, p.Status);
        Assert.Null(p.PendingType);
        var e = _content.GetByKey("e")!;
        Assert.Equal(0, e.RetryCount);
        Assert.Null(e.Reason);
    }

    /// <summary>
    /// "Retry errors" queues every row in the given status with a fresh allowance, and leaves the rest.
    /// True: one click retries every errored row, not just the loaded page.
    /// False: rows past the first page stay errored, or Synced rows get re-queued as well.
    /// </summary>
    [Fact]
    public void QueueAllWithStatus_QueuesOnlyThatStatus_WithFreshAllowance()
    {
        Seed("e1", SyncStatus.Errored, retryCount: 3, reason: "boom");
        Seed("e2", SyncStatus.Errored, retryCount: 1, reason: "boom");
        Seed("s1", SyncStatus.Synced);

        var queued = _content.QueueAllWithStatus(SyncStatus.Errored);

        Assert.Equal(2, queued);
        Assert.All(new[] { "e1", "e2" }, key =>
        {
            var row = _content.GetByKey(key)!;
            Assert.Equal(SyncStatus.Queued, row.Status);
            Assert.Equal(0, row.RetryCount);
            Assert.Null(row.Reason);
        });
        Assert.Equal(SyncStatus.Synced, _content.GetByKey("s1")!.Status);
    }

    // -----------------------------------------------------------------------
    // Deletes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Removing a server removes exactly its rows. Rows from another server, and keyless rows written
    /// before servers became a list, stay.
    /// True: removing one server leaves the others' tracking untouched.
    /// False: removing one server wipes another's rows, or the legacy rows of the first server.
    /// </summary>
    [Fact]
    public void DeleteByServerKey_RemovesOnlyThatServersRows()
    {
        Seed("a1", SyncStatus.Synced, serverKey: "server-a");
        Seed("a2", SyncStatus.Queued, serverKey: "server-a");
        Seed("b1", SyncStatus.Synced, serverKey: "server-b");
        Seed("legacy", SyncStatus.Synced);

        var deleted = _content.DeleteByServerKey("server-a");

        Assert.Equal(2, deleted);
        Assert.Equal(new[] { "b1", "legacy" }, _content.GetAllStrict().Select(r => r.SourceItemId).OrderBy(k => k));
        Assert.Equal(0, _content.DeleteByServerKey("server-a"));
        Assert.Throws<ArgumentException>(() => _content.DeleteByServerKey(string.Empty));
    }

    /// <summary>
    /// DeleteById removes one row, and ResetTable removes every row whatever its status, Ignored included.
    /// True: the per-module Reset Database really starts clean.
    /// False: Reset leaves Ignored rows behind, or a single delete takes others with it.
    /// </summary>
    [Fact]
    public void DeleteById_RemovesOne_ResetTable_RemovesAll()
    {
        var a = Seed("a", SyncStatus.Synced);
        Seed("b", SyncStatus.Ignored, reason: "user");
        Seed("c", SyncStatus.Errored);

        _content.DeleteById(a.Id);
        Assert.Equal(2, _content.Count());

        Assert.Equal(2, _content.ResetTable());
        Assert.Equal(0, _content.Count());
    }

    // -----------------------------------------------------------------------
    // Lenient reads and strict writes
    // -----------------------------------------------------------------------

    /// <summary>
    /// A read that fails because the database is busy, locked, read-only, unopenable or corrupt, or
    /// because its connection went away, returns the fallback instead of throwing.
    /// True: the dashboard shows empty counts while the database is briefly unavailable.
    /// False: a lock held by a long write turns every dashboard poll into a 500.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(14)]
    public void ExecuteRead_TransientSqliteError_ReturnsFallback(int code)
    {
        var manager = new FailingReadManager(_database);

        Assert.Equal(-1, manager.ReadThrowing(new SqliteException("transient", code)));
    }

    /// <summary>
    /// A read whose connection was closed under it returns the fallback.
    /// True: a database reset during a dashboard poll reads as empty.
    /// False: the poll racing a reset throws.
    /// </summary>
    [Fact]
    public void ExecuteRead_ConnectionError_ReturnsFallback()
    {
        var manager = new FailingReadManager(_database);

        Assert.Equal(-1, manager.ReadThrowing(new InvalidOperationException("The connection was closed")));
    }

    /// <summary>
    /// Any other read failure propagates: a SQL error, or an InvalidOperationException that is not
    /// about the connection.
    /// True: a broken query or schema surfaces as an error instead of reading as an empty table.
    /// False: a missing column makes every count read 0, and a prune trusting it removes rows.
    /// </summary>
    [Fact]
    public void ExecuteRead_OtherErrors_Propagate()
    {
        var manager = new FailingReadManager(_database);

        Assert.Throws<SqliteException>(() => manager.ReadThrowing(new SqliteException("no such column", 1)));
        Assert.Throws<InvalidOperationException>(() => manager.ReadThrowing(new InvalidOperationException("sequence contains no elements")));
    }

    /// <summary>
    /// Writes never fall back: even a transient SQLite error propagates to the caller.
    /// True: the caller sees a failed write and can retry or mark the row.
    /// False: a write lost to a lock is reported as done.
    /// </summary>
    [Fact]
    public void ExecuteWrite_SqliteError_Propagates()
    {
        var manager = new FailingReadManager(_database);

        var ex = Assert.Throws<SqliteException>(() => manager.WriteThrowing(new SqliteException("busy", 5)));
        Assert.Equal(5, ex.SqliteErrorCode);
    }
}

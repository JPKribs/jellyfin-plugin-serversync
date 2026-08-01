using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// The Content deletion state machine, run against a REAL SQLite-backed
/// <see cref="ContentSyncTableManager"/> (temp-file database) so the Upsert
/// SQL — including its Ignored CASE guard — is exercised, not mocked.
/// These transitions gate actual file deletion; a wrong transition here is
/// user data loss, so every branch gets its own test.
/// </summary>
public sealed class SyncStateServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly ContentSyncTableManager _manager;

    public SyncStateServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new ContentSyncTableManager(new FixedProvider(_database), NullLogger<ContentSyncTableManager>.Instance);
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

    private static SyncItem Row(string sourceItemId, SyncStatus status, PendingType? pendingType = null, string? localPath = null) => new()
    {
        SourceLibraryId = "lib-1",
        LocalLibraryId = "local-1",
        SourceItemId = sourceItemId,
        SourcePath = "/src/" + sourceItemId + ".mkv",
        SourceSize = 100,
        SourceCreateDate = DateTime.UtcNow,
        LocalPath = localPath ?? "/nonexistent/" + sourceItemId + ".mkv",
        Status = status,
        PendingType = pendingType,
        StatusDate = DateTime.UtcNow
    };

    // -----------------------------------------------------------------------
    // ProcessMissingItem — the prune's per-row action
    // -----------------------------------------------------------------------

    /// <summary>
    /// Ignored rows are user overrides: a missing-from-source Ignored row
    /// must be left untouched, not deleted or re-marked.
    /// </summary>
    [Fact]
    public void ProcessMissingItem_Ignored_NoOp()
    {
        var row = Row("a", SyncStatus.Ignored);
        _manager.Upsert(row);
        var persisted = _manager.GetByKey("a")!;

        var result = SyncStateService.ProcessMissingItem(_manager, persisted, ApprovalMode.Enabled, NullLogger.Instance);

        Assert.False(result.Changed);
        Assert.Equal(SyncStatus.Ignored, _manager.GetByKey("a")!.Status);
    }

    /// <summary>
    /// Rows already awaiting deletion approval must not be re-processed —
    /// re-marking them would reset StatusDate and could double-count in
    /// the breaker's stale math.
    /// </summary>
    [Fact]
    public void ProcessMissingItem_AlreadyPendingDeletion_NoOp()
    {
        var row = Row("a", SyncStatus.Pending, PendingType.Deletion);
        _manager.Upsert(row);
        var persisted = _manager.GetByKey("a")!;

        var result = SyncStateService.ProcessMissingItem(_manager, persisted, ApprovalMode.Enabled, NullLogger.Instance);

        Assert.False(result.Changed);
    }

    /// <summary>
    /// A row already marked Deleting is still awaiting the Sync run that will
    /// remove its file, so a second refresh must leave it alone.
    /// True: the scheduled deletion survives until Sync executes it.
    /// False: the row falls through to the not-Synced branch below and its
    /// tracking row is deleted while the file stays on disk — the file is then
    /// orphaned permanently, with nothing left pointing at it. Reachable any
    /// time a refresh lands between the mark and the sync (aborted pre-flight,
    /// cancelled run, or the default 10h/12h trigger cadence drifting).
    /// </summary>
    [Fact]
    public void ProcessMissingItem_AlreadyDeleting_NoOp()
    {
        var row = Row("a", SyncStatus.Deleting);
        _manager.Upsert(row);
        var persisted = _manager.GetByKey("a")!;

        var result = SyncStateService.ProcessMissingItem(_manager, persisted, ApprovalMode.Enabled, NullLogger.Instance);

        Assert.False(result.Changed);

        // The row must still be there for FileDeletionService to pick up.
        var after = _manager.GetByKey("a");
        Assert.NotNull(after);
        Assert.Equal(SyncStatus.Deleting, after!.Status);
    }

    /// <summary>
    /// A non-Synced row (never downloaded) loses only its tracking row — no
    /// file is scheduled for deletion.
    /// </summary>
    [Fact]
    public void ProcessMissingItem_NotSynced_RemovesTrackingOnly()
    {
        var row = Row("a", SyncStatus.Queued);
        _manager.Upsert(row);
        var persisted = _manager.GetByKey("a")!;

        var result = SyncStateService.ProcessMissingItem(_manager, persisted, ApprovalMode.Enabled, NullLogger.Instance);

        Assert.True(result.Changed);
        Assert.Null(_manager.GetByKey("a"));
    }

    /// <summary>
    /// RequireApproval gates the file deletion behind the user: the row goes
    /// to Pending+Deletion, NEVER straight to Deleting.
    /// </summary>
    [Fact]
    public void ProcessMissingItem_SyncedRequireApproval_MarksPendingDeletion()
    {
        var file = Path.Combine(_tempDir, "a.mkv");
        File.WriteAllText(file, "x");
        var row = Row("a", SyncStatus.Synced, localPath: file);
        _manager.Upsert(row);
        var persisted = _manager.GetByKey("a")!;

        SyncStateService.ProcessMissingItem(_manager, persisted, ApprovalMode.RequireApproval, NullLogger.Instance);

        var after = _manager.GetByKey("a")!;
        Assert.Equal(SyncStatus.Pending, after.Status);
        Assert.Equal(PendingType.Deletion, after.PendingType);
    }

    /// <summary>
    /// Auto-delete mode marks Deleting — the only path that feeds
    /// FileDeletionService.ProcessPendingDeletions.
    /// </summary>
    [Fact]
    public void ProcessMissingItem_SyncedAutoDelete_MarksDeleting()
    {
        var file = Path.Combine(_tempDir, "a.mkv");
        File.WriteAllText(file, "x");
        var row = Row("a", SyncStatus.Synced, localPath: file);
        _manager.Upsert(row);
        var persisted = _manager.GetByKey("a")!;

        SyncStateService.ProcessMissingItem(_manager, persisted, ApprovalMode.Enabled, NullLogger.Instance);

        Assert.Equal(SyncStatus.Deleting, _manager.GetByKey("a")!.Status);
    }

    /// <summary>
    /// Synced but the local file is already gone: nothing to delete on either
    /// side, so only the tracking row is removed.
    /// </summary>
    [Fact]
    public void ProcessMissingItem_SyncedFileAlreadyGone_RemovesTrackingOnly()
    {
        var row = Row("a", SyncStatus.Synced, localPath: Path.Combine(_tempDir, "never-created.mkv"));
        _manager.Upsert(row);
        var persisted = _manager.GetByKey("a")!;

        SyncStateService.ProcessMissingItem(_manager, persisted, ApprovalMode.Enabled, NullLogger.Instance);

        Assert.Null(_manager.GetByKey("a"));
    }

    // -----------------------------------------------------------------------
    // ProcessExistingItem — reappearance rescues
    // -----------------------------------------------------------------------

    /// <summary>
    /// An item scheduled for deletion that reappears on the source is
    /// restored to Queued instead of being deleted and re-downloaded.
    /// </summary>
    [Fact]
    public void ProcessExistingItem_DeletingReappears_RestoredToQueued()
    {
        var row = Row("a", SyncStatus.Deleting);

        var updated = SyncStateService.ProcessExistingItem(
            row, row.SourcePath, row.SourceSize, row.SourceCreateDate, row.LocalPath!,
            ApprovalMode.Enabled, detectUpdatedFiles: false, sizeMatchToleranceBytes: 0, NullLogger.Instance);

        Assert.Equal(SyncStatus.Queued, updated.Status);
        Assert.Null(updated.PendingType);
    }

    /// <summary>
    /// Same rescue for rows still awaiting deletion approval.
    /// </summary>
    [Fact]
    public void ProcessExistingItem_PendingDeletionReappears_RestoredToQueued()
    {
        var row = Row("a", SyncStatus.Pending, PendingType.Deletion);

        var updated = SyncStateService.ProcessExistingItem(
            row, row.SourcePath, row.SourceSize, row.SourceCreateDate, row.LocalPath!,
            ApprovalMode.Enabled, detectUpdatedFiles: false, sizeMatchToleranceBytes: 0, NullLogger.Instance);

        Assert.Equal(SyncStatus.Queued, updated.Status);
        Assert.Null(updated.PendingType);
    }

    // -----------------------------------------------------------------------
    // ContentSyncTableManager.Upsert — the SQL-level Ignored guard
    // -----------------------------------------------------------------------

    /// <summary>
    /// A refresh working from a stale snapshot upserts with the OLD status;
    /// the SQL CASE guard must keep a concurrently-set Ignored override
    /// rather than reverting it. This is the DB-level race the in-memory
    /// snapshot checks cannot close.
    /// </summary>
    [Fact]
    public void Upsert_DoesNotOverwriteIgnoredStatus()
    {
        _manager.Upsert(Row("a", SyncStatus.Synced));

        // User ignores the item mid-refresh.
        var ignored = _manager.GetByKey("a")!;
        ignored.Status = SyncStatus.Ignored;
        ignored.Reason = "user override";
        _manager.Upsert(ignored);

        // Refresh finishes its stale-snapshot upsert with Status=Queued.
        var stale = Row("a", SyncStatus.Queued);
        stale.SourceSize = 999;
        _manager.Upsert(stale);

        var after = _manager.GetByKey("a")!;
        Assert.Equal(SyncStatus.Ignored, after.Status);
        Assert.Equal("user override", after.Reason);
        Assert.Equal(999, after.SourceSize);
    }

    /// <summary>
    /// The guard must not be sticky the other way: a non-Ignored row takes
    /// the incoming status normally.
    /// </summary>
    [Fact]
    public void Upsert_NonIgnoredRow_TakesIncomingStatus()
    {
        _manager.Upsert(Row("a", SyncStatus.Synced));
        _manager.Upsert(Row("a", SyncStatus.Queued));

        Assert.Equal(SyncStatus.Queued, _manager.GetByKey("a")!.Status);
    }

    /// <summary>
    /// An Ignored row can still be intentionally ignored again (idempotent)
    /// and un-ignored via the direct status update path the UI uses.
    /// </summary>
    [Fact]
    public void UpdateStatusByKey_CanUnignore()
    {
        _manager.Upsert(Row("a", SyncStatus.Ignored));
        _manager.UpdateStatusByKey("a", SyncStatus.Queued);

        Assert.Equal(SyncStatus.Queued, _manager.GetByKey("a")!.Status);
    }

    /// <summary>
    /// Queued through UpdateStatus is an operator action and must reset the
    /// retry counter, in one well-formed UPDATE. This exercises the real SQL
    /// against SQLite — the transition clauses briefly emitted "RetryCount = 0"
    /// twice for this path.
    /// True: a Retry click hands the row its full MaxRetryCount allowance.
    /// False: a row at the cap gets one attempt and drops back out, or the
    /// UPDATE itself is malformed and every Queue/Retry action fails.
    /// </summary>
    [Fact]
    public void UpdateStatus_Queued_ResetsRetryCount()
    {
        var row = Row("a", SyncStatus.Errored);
        row.RetryCount = 3;
        _manager.Upsert(row);

        _manager.UpdateStatus("a", SyncStatus.Queued);

        var after = _manager.GetByKey("a")!;
        Assert.Equal(SyncStatus.Queued, after.Status);
        Assert.Equal(0, after.RetryCount);
    }

    /// <summary>
    /// Errored through UpdateStatus increments the retry counter.
    /// True: repeated failures accumulate toward the ceiling.
    /// False: the ceiling never trips because failures don't count.
    /// </summary>
    [Fact]
    public void UpdateStatus_Errored_IncrementsRetryCount()
    {
        var row = Row("a", SyncStatus.Queued);
        row.RetryCount = 1;
        _manager.Upsert(row);

        _manager.UpdateStatus("a", SyncStatus.Errored, errorMessage: "boom");

        var after = _manager.GetByKey("a")!;
        Assert.Equal(2, after.RetryCount);
    }
}

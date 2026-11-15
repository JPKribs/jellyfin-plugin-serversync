using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Common;

/// <summary>
/// The status steps every sync row shares leave the row in the state the next scan and the dashboard expect.
/// </summary>
public sealed class SyncRecordTests
{
    private static readonly DateTime At = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private static PeopleSyncItem Row(SyncStatus status = SyncStatus.Queued)
    {
        var row = new PeopleSyncItem { PersonName = "Jane Doe", Status = status, Reason = "old reason", RetryCount = 2 };
        row.Metadata.UpdateSource("{\"a\":1}");
        return row;
    }

    /// <summary>
    /// Keeping this server's value settles the row with the reason and leaves the synced hash alone.
    /// True: the row shows as synced with why, and the next scan compares the values again.
    /// False: a kept row would record the source's value as applied and never be looked at again.
    /// </summary>
    [Fact]
    public void MarkKept_QueuedRow_SettlesWithReasonAndKeepsSyncedHash()
    {
        var row = Row();

        row.MarkKept("local value is newer");

        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.Equal("local value is newer", row.Reason);
        Assert.Null(row.Metadata.SyncedHash);
        Assert.Null(row.LastSyncTime);
    }

    /// <summary>
    /// Keeping a value does not reset the retry count.
    /// True: only a real apply or an operator gives a row its retries back.
    /// False: a row that keeps failing could be retried without end by being kept in between.
    /// </summary>
    [Fact]
    public void MarkKept_RowWithRetries_LeavesRetryCount()
    {
        var row = Row();

        row.MarkKept("kept");

        Assert.Equal(2, row.RetryCount);
    }

    /// <summary>
    /// An apply settles the row at the given time with no reason and no retries, and records what was applied.
    /// True: an applied row is clean, dated, and carries the hash of what it now holds.
    /// False: an old error would stay on a row that succeeded, or the row would queue again on the next scan.
    /// </summary>
    [Fact]
    public void MarkApplied_ErroredRow_ClearsReasonAndRetriesAndRecordsHash()
    {
        var row = Row(SyncStatus.Errored);

        row.MarkApplied(At);

        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.Equal(At, row.StatusDate);
        Assert.Equal(At, row.LastSyncTime);
        Assert.Null(row.Reason);
        Assert.Equal(0, row.RetryCount);
        Assert.Equal(row.Metadata.SourceHash, row.Metadata.SyncedHash);
    }

    /// <summary>
    /// A fresh change queues an errored row again with its retries reset.
    /// True: a row that gave up is tried again when its source changes.
    /// False: a row that once errored out would stay stuck after the cause was fixed.
    /// </summary>
    [Fact]
    public void RetryIfErrored_ErroredRow_QueuesWithFullRetries()
    {
        var row = Row(SyncStatus.Errored);

        row.RetryIfErrored();

        Assert.Equal(SyncStatus.Queued, row.Status);
        Assert.Equal(0, row.RetryCount);
    }

    /// <summary>
    /// A row in any other status is left exactly as it was.
    /// True: an ignored, synced, or pending row keeps its status and retry count.
    /// False: a fresh change would queue a row the operator ignored.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Pending)]
    [InlineData(SyncStatus.Queued)]
    [InlineData(SyncStatus.Synced)]
    [InlineData(SyncStatus.Ignored)]
    [InlineData(SyncStatus.Deleting)]
    public void RetryIfErrored_OtherStatus_LeavesRowAlone(SyncStatus status)
    {
        var row = Row(status);

        row.RetryIfErrored();

        Assert.Equal(status, row.Status);
        Assert.Equal(2, row.RetryCount);
    }
}

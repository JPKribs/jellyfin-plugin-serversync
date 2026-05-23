using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

public class ContentSyncItemTests
{
    private static SyncItem Make(SyncStatus status, PendingType? pending = null) => new()
    {
        SourceLibraryId = "src-lib",
        LocalLibraryId = "loc-lib",
        SourceItemId = "src-item",
        SourcePath = "/source/path",
        SourceSize = 1000,
        Status = status,
        PendingType = pending
    };

    /// <summary>
    /// Queued rows report HasChanges.
    /// True: the apply pass picks up rows the Refresh phase queued for action.
    /// False: queued rows would be skipped and never sync.
    /// </summary>
    [Fact]
    public void HasChanges_Queued_IsTrue()
    {
        var item = Make(SyncStatus.Queued);

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// Deleting rows report HasChanges.
    /// True: pending soft-deletes are processed in FinalizeAsync's pending-deletion pass.
    /// False: soft-deleted rows would never get cleaned up locally.
    /// </summary>
    [Fact]
    public void HasChanges_Deleting_IsTrue()
    {
        var item = Make(SyncStatus.Deleting);

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// Pending rows with a PendingType report HasChanges.
    /// True: a known-pending operation (Download/Replacement/Deletion) is actionable.
    /// False: known-pending operations would silently stall.
    /// </summary>
    [Fact]
    public void HasChanges_PendingWithPendingType_IsTrue()
    {
        var item = Make(SyncStatus.Pending, PendingType.Download);

        Assert.True(item.HasChanges);
    }

    /// <summary>
    /// Pending rows without a PendingType report no changes.
    /// True: interrupted Refresh passes leave inactionable Pending rows that are correctly ignored.
    /// False: half-built rows would crash apply when the missing PendingType is dereferenced.
    /// </summary>
    [Fact]
    public void HasChanges_PendingWithoutPendingType_IsFalse()
    {
        var item = Make(SyncStatus.Pending, pending: null);

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// Synced rows report no changes.
    /// True: already-synced rows aren't pointlessly re-queued.
    /// False: every refresh would re-queue every Synced row.
    /// </summary>
    [Fact]
    public void HasChanges_Synced_IsFalse()
    {
        var item = Make(SyncStatus.Synced);

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// Errored rows report no changes (retry path is separate).
    /// True: errored rows wait for retry-count-driven retry rather than auto-re-queueing.
    /// False: errored rows would loop forever, retrying every refresh without operator intervention.
    /// </summary>
    [Fact]
    public void HasChanges_Errored_IsFalse()
    {
        var item = Make(SyncStatus.Errored);

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// Ignored rows report no changes.
    /// True: operator-marked Ignored overrides survive refresh and never re-queue.
    /// False: Ignored markings would be silently undone on the next refresh.
    /// </summary>
    [Fact]
    public void HasChanges_Ignored_IsFalse()
    {
        var item = Make(SyncStatus.Ignored);

        Assert.False(item.HasChanges);
    }

    /// <summary>
    /// MarkSynced is a no-op for ContentSync (no SyncableValue fields to mark).
    /// True: calling MarkSynced on a Content row doesn't mutate Status or PendingType.
    /// False: an unintended side effect would mark rows as in a stale state.
    /// </summary>
    [Fact]
    public void MarkSynced_IsNoOp()
    {
        var item = Make(SyncStatus.Synced, PendingType.Download);

        item.MarkSynced();

        Assert.Equal(SyncStatus.Synced, item.Status);
        Assert.Equal(PendingType.Download, item.PendingType);
    }
}

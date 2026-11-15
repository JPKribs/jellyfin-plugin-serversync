using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The maintenance store tracks what keeps failing across every sync table and gives fresh retries only to rows it is safe to retry.
/// </summary>
public sealed class MaintenanceStoreTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly QueueTestDatabase _db = new();
    private readonly MaintenanceStore _store;
    private readonly HistorySyncTableManager _history;
    private readonly ContentSyncTableManager _content;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaintenanceStoreTests"/> class.
    /// </summary>
    public MaintenanceStoreTests()
    {
        _store = new MaintenanceStore(_db.Provider, NullLogger<MaintenanceStore>.Instance);
        _history = new HistorySyncTableManager(_db.Provider, NullLogger<HistorySyncTableManager>.Instance);
        _content = new ContentSyncTableManager(_db.Provider, NullLogger<ContentSyncTableManager>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static HistorySyncItem History(string item, string? reason = null, DateTime? erroredAt = null, int retries = 0) => new()
    {
        SourceUserId = "su",
        LocalUserId = "lu",
        SourceLibraryId = "lib",
        LocalLibraryId = "local",
        SourceItemId = item,
        ItemName = "Item " + item,
        ServerKey = "server",
        Status = SyncStatus.Errored,
        StatusDate = erroredAt ?? Start,
        Reason = reason,
        RetryCount = retries
    };

    private static SyncItem Content(string item, string? reason = null, DateTime? erroredAt = null) => new()
    {
        SourceItemId = item,
        SourceLibraryId = "lib",
        LocalLibraryId = "local",
        SourcePath = "/media/movies/" + item + ".mkv",
        LocalPath = "/local/movies/" + item + ".mkv",
        ServerKey = "server",
        Status = SyncStatus.Errored,
        StatusDate = erroredAt ?? Start,
        Reason = reason
    };

    /// <summary>
    /// With nothing failing there is nothing to report.
    /// True: a healthy server writes no attention entry.
    /// False: the administrator is told about failures that do not exist.
    /// </summary>
    [Fact]
    public void TrackFailures_NothingFailing_ReportsNothing()
    {
        var due = _store.TrackFailures(Start.AddDays(2), Start.AddDays(1));

        Assert.Empty(due);
    }

    /// <summary>
    /// Rows of one kind that have failed for a day are reported together, with one of their reasons.
    /// True: the administrator gets one line saying how many and an example of why.
    /// False: each row is its own entry, or the count is wrong.
    /// </summary>
    [Fact]
    public void TrackFailures_SeveralRowsFailingForADay_CountsThemWithAReason()
    {
        _history.Upsert(History("one", "alpha refused"));
        _history.Upsert(History("two", "beta refused"));
        _store.TrackFailures(Start, Start.AddDays(-1));

        var due = _store.TrackFailures(Start.AddDays(2), Start.AddDays(1));

        var report = Assert.Single(due);
        Assert.Equal("watch history", report.Label);
        Assert.Equal(2, report.Count);
        Assert.Equal("beta refused", report.Reason);
    }

    /// <summary>
    /// A failing content row is reported as a file.
    /// True: the entry names the kind of thing in the words the dashboard uses.
    /// False: a failing download is reported under another sync type.
    /// </summary>
    [Fact]
    public void TrackFailures_ContentRowFailingForADay_IsLabelledFile()
    {
        _content.Upsert(Content("film", "the download failed"));
        _store.TrackFailures(Start, Start.AddDays(-1));

        var due = _store.TrackFailures(Start.AddDays(2), Start.AddDays(1));

        var report = Assert.Single(due);
        Assert.Equal("file", report.Label);
        Assert.Equal(1, report.Count);
    }

    /// <summary>
    /// A row first seen failing after the cutoff is not yet reported.
    /// True: a failure gets a day of retries before anyone is told.
    /// False: every brief failure raises an entry.
    /// </summary>
    [Fact]
    public void TrackFailures_FailingForLessThanADay_IsNotReported()
    {
        _history.Upsert(History("one", "refused"));
        _store.TrackFailures(Start, Start.AddDays(-1));

        var due = _store.TrackFailures(Start.AddHours(1), Start.AddHours(-23));

        Assert.Empty(due);
    }

    /// <summary>
    /// A row whose retries were set aside for good is never tracked.
    /// True: a row parked on purpose does not nag the administrator.
    /// False: the same row is raised although nothing will ever retry it.
    /// </summary>
    [Fact]
    public void TrackFailures_RowSetAsideForGood_IsNeverReported()
    {
        _history.Upsert(History("parked", "refused", retries: 1_000_000));
        _store.TrackFailures(Start, Start.AddDays(-1));

        var due = _store.TrackFailures(Start.AddDays(2), Start.AddDays(1));

        Assert.Empty(due);
    }

    /// <summary>
    /// With nothing errored nothing is queued again.
    /// True: an upkeep run on a healthy server changes no row.
    /// False: synced rows are queued and applied again every six hours.
    /// </summary>
    [Fact]
    public void RequeueErrored_NothingErrored_RequeuesNothing()
    {
        var row = History("fine");
        row.Status = SyncStatus.Synced;
        _history.Upsert(row);

        var requeued = _store.RequeueErrored(Start.AddDays(1));

        Assert.Equal(0, requeued);
        Assert.Equal(SyncStatus.Synced, _history.GetByKey(("su", "fine"))?.Status);
    }

    /// <summary>
    /// A row that errored after the cutoff keeps its error.
    /// True: a row that failed a moment ago is not retried at once.
    /// False: a failing row spins through its retries on every run.
    /// </summary>
    [Fact]
    public void RequeueErrored_ErroredAfterTheCutoff_IsLeftAlone()
    {
        _history.Upsert(History("recent", erroredAt: Start, retries: 3));

        var requeued = _store.RequeueErrored(Start.AddHours(-1));

        Assert.Equal(0, requeued);
        var held = _history.GetByKey(("su", "recent"));
        Assert.Equal(SyncStatus.Errored, held?.Status);
        Assert.Equal(3, held?.RetryCount);
    }

    /// <summary>
    /// An errored content row is never queued again here.
    /// True: a download or a deletion is retried only by Sync Content's own rules.
    /// False: upkeep restarts a file operation that was stopped for a reason.
    /// </summary>
    [Fact]
    public void RequeueErrored_ErroredContentRow_IsLeftAlone()
    {
        _content.Upsert(Content("film", erroredAt: Start.AddDays(-2)));

        var requeued = _store.RequeueErrored(Start);

        Assert.Equal(0, requeued);
        Assert.Equal(SyncStatus.Errored, _content.GetByKey("film")?.Status);
    }

    /// <summary>
    /// Every old errored row is queued again with its retries cleared, and the count says how many.
    /// True: after a peer comes back, what failed while it was away is tried again.
    /// False: rows that burned their retries wait for someone to press retry.
    /// </summary>
    [Fact]
    public void RequeueErrored_SeveralOldErrors_RequeuesAndCountsThemAll()
    {
        _history.Upsert(History("one", erroredAt: Start.AddDays(-2), retries: 5));
        _history.Upsert(History("two", erroredAt: Start.AddDays(-3), retries: 2));

        var requeued = _store.RequeueErrored(Start);

        Assert.Equal(2, requeued);
        Assert.Equal(SyncStatus.Queued, _history.GetByKey(("su", "one"))?.Status);
        Assert.Equal(0, _history.GetByKey(("su", "two"))?.RetryCount);
    }
}

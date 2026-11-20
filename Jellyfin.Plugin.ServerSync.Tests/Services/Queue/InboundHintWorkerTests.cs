using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// Every due hint ends as finished and reported to its origin, parked, or waiting to retry, and one failing hint never stops the pass.
/// </summary>
[Collection(QueueStateCollection.Name)]
public sealed class InboundHintWorkerTests : IDisposable
{
    private const string Me = "this-server";
    private const string CompletePath = "/Sync/Peer/Complete";

    private readonly QueueTestDatabase _db = new();
    private readonly FakeConfigManager _config = QueueSamples.Config();
    private readonly QueueClientFactory _factory = new();
    private readonly HintActivityHarness _activity = new();
    private readonly QueueLocalItems _local = new();
    private readonly InboundHintStore _inbound;
    private readonly InboundHintWorker _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboundHintWorkerTests"/> class.
    /// </summary>
    public InboundHintWorkerTests()
    {
        _inbound = new InboundHintStore(_db.Provider, NullLogger<InboundHintStore>.Instance);
        var triage = new HintTriage(_config, _local, new VersionStore(_db.Provider, NullLogger<VersionStore>.Instance));
        var history = new HistoryHintHandler(null!, null!, null!, null!, null!, null!, null!, _config, triage, null!, NullLogger<HistoryHintHandler>.Instance);
        var items = new ItemHintHandler(new QueueServices(), _config, triage, QueueSamples.Resolver(_db.Provider, _config), null!, null!, NullLogger<ItemHintHandler>.Instance);
        var content = new ContentHintHandler(new QueueServices(), _config, new ContentSyncTableManager(_db.Provider, NullLogger<ContentSyncTableManager>.Instance), null!, NullLogger<ContentHintHandler>.Instance);
        _worker = new InboundHintWorker(_inbound, _config, _factory, history, items, content, null!, _activity.Log, triage, QueueSamples.Host(Me), NullLogger<InboundHintWorker>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _worker.Dispose();
        _activity.Dispose();
        _db.Dispose();
    }

    private QueuePeerHandler Origin => _factory.Peer("origin");

    private SourceServer AddOrigin(ServerMode mode = ServerMode.Sync)
    {
        var origin = QueueSamples.Server("origin", mode);
        _config.Configuration.Servers.Add(origin);
        return origin;
    }

    private InboundHint Receive(HintKind kind, string? path = null, string? key = null, string id = "1", int failedAttempts = 0, DateTime? receivedAt = null)
    {
        var at = receivedAt ?? DateTime.UtcNow;
        var row = InboundHint.FromHint(QueueSamples.Hint(kind, path, key ?? "key-" + id, at.AddMinutes(-1), id), at);
        _inbound.Enqueue(row);
        for (var i = 0; i < failedAttempts; i++)
        {
            _inbound.Defer(row, at, "failed before");
        }

        return row;
    }

    /// <summary>
    /// With nothing due a pass finishes nothing and builds no client.
    /// True: an idle server makes no request to any peer.
    /// False: every tick opens a connection to every origin.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_NothingDue_FinishesNothing()
    {
        AddOrigin();

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(0, finished);
        Assert.Empty(_factory.Created);
        Assert.Empty(_worker.RecentlyCompleted);
    }

    /// <summary>
    /// A hint that can never apply here is finished, removed, remembered with its code, and reported to its origin.
    /// True: the origin clears its row and learns why, by callback or from this server's status.
    /// False: the origin keeps the row as sent and sends it again.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_HintThatCannotApplyHere_IsFinishedAndReportedToItsOrigin()
    {
        AddOrigin();
        var row = Receive(HintKind.Users);

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        var remembered = Assert.Single(_worker.RecentlyCompleted);
        var report = QueuePeerHandler.Read<CompleteRequest>(Origin.LastBody(CompletePath)!);
        Assert.Equal(1, finished);
        Assert.Equal(0, _inbound.Count());
        Assert.Equal(row.HintId, remembered.HintId);
        Assert.Equal(HintCode.Unsupported, remembered.Code);
        Assert.Equal(row.VersionTimestamp, remembered.VersionTimestamp);
        Assert.Equal(Me, report.SenderServerId);
        var reported = Assert.Single(report.Items);
        Assert.Equal(row.HintId, reported.HintId);
        Assert.Equal(HintCode.Unsupported, reported.Code);
    }

    /// <summary>
    /// The hints one origin finished in a pass are reported in one request.
    /// True: a busy pass costs the origin one callback.
    /// False: every finished hint is its own request.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_SeveralHintsFromOneOrigin_AreReportedTogether()
    {
        AddOrigin();
        Receive(HintKind.Users, id: "1");
        Receive(HintKind.Users, id: "2");
        Receive(HintKind.Users, id: "3");

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(3, finished);
        Assert.Equal(1, Origin.CountOf(CompletePath));
        Assert.Equal(3, QueuePeerHandler.Read<CompleteRequest>(Origin.LastBody(CompletePath)!).Items.Count);
    }

    /// <summary>
    /// An origin that refuses the completion report, or cannot be reached, does not undo the finished hint.
    /// True: the hint is done here and the origin reads it from this server's status later.
    /// False: a failed callback fails the pass, or the hint is applied again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyAsync_OriginCannotBeToldOfTheCompletion_StillFinishesTheHint(bool answers)
    {
        AddOrigin();
        Receive(HintKind.Users);
        if (answers)
        {
            Origin.OnStatus(CompletePath, HttpStatusCode.Forbidden);
        }
        else
        {
            Origin.OnUnreachable(CompletePath);
        }

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(1, finished);
        Assert.Equal(0, _inbound.Count());
        Assert.Single(_worker.RecentlyCompleted);
    }

    /// <summary>
    /// A hint from a server that is not set up to be pulled from here waits with a counted attempt.
    /// True: a server that is removed and added back still gets its change applied.
    /// False: the hint is dropped the moment the entry is missing, or spins without a wait.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_OriginNotSetUpToBePulledFrom_DefersTheHint(bool configuredAsPushOnly)
    {
        if (configuredAsPushOnly)
        {
            AddOrigin(ServerMode.Push);
        }

        Receive(HintKind.Users);
        var before = DateTime.UtcNow;

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        var stored = Assert.Single(_inbound.GetAll());
        Assert.Equal(0, finished);
        Assert.Equal(1, stored.Attempts);
        Assert.NotNull(stored.LastError);
        Assert.True(stored.NextAttempt >= before + HintProtocol.NextDelay(1));
        Assert.Empty(_factory.Created);
    }

    /// <summary>
    /// On its last attempt a hint from a server that is still not set up is removed, and not remembered as finished.
    /// True: the queue does not keep hints for a server that is gone for good.
    /// False: the row is retried every hour forever.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_OriginStillNotSetUpOnTheLastAttempt_RemovesTheHint()
    {
        Receive(HintKind.Users, failedAttempts: HintProtocol.MaxInboundAttempts - 1);

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(0, finished);
        Assert.Equal(0, _inbound.Count());
        Assert.Empty(_worker.RecentlyCompleted);
    }

    /// <summary>
    /// A hint whose origin has an address the client factory refuses waits with a counted attempt, and is removed on its last.
    /// True: a bad URL on one entry never fails the pass, and its hints do not wait forever.
    /// False: one invalid address stops every origin's hints from applying.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(HintProtocol.MaxInboundAttempts - 1, 0)]
    public async Task ApplyAsync_OriginsAddressIsRefused_DefersThenRemovesTheHint(int failedAttempts, int rowsLeft)
    {
        AddOrigin();
        _factory.Refuse("origin", "the address is private");
        Receive(HintKind.Users, failedAttempts: failedAttempts);

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(0, finished);
        Assert.Equal(rowsLeft, _inbound.Count());
        Assert.All(_inbound.GetAll(), row => Assert.Equal(1, row.Attempts));
    }

    /// <summary>
    /// A hint whose handler asks for a retry, or throws, waits with a counted attempt and the pass goes on.
    /// True: an origin that fails a read costs that hint a retry and nothing else.
    /// False: one failing hint ends the pass and the hints behind it never apply.
    /// </summary>
    [Theory]
    [InlineData(HintKind.Metadata)]
    [InlineData(HintKind.History)]
    public async Task ApplyAsync_ApplyFailsForNow_DefersThatHintAndFinishesTheRest(HintKind kind)
    {
        AddOrigin();
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        Origin.OnStatus("/Items", HttpStatusCode.InternalServerError);
        var key = kind == HintKind.History ? HintProtocol.HistoryKey(QueueSamples.UserId, QueueSamples.ItemId) : QueueSamples.ItemId.ToString("N");
        var failing = Receive(kind, "/media/movies/a.mkv", key, id: "1");
        Receive(HintKind.Users, id: "2");

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        var stored = Assert.Single(_inbound.GetAll());
        Assert.Equal(1, finished);
        Assert.Equal(failing.Id, stored.Id);
        Assert.Equal(1, stored.Attempts);
        Assert.NotNull(stored.LastError);
        Assert.True(stored.NextAttempt > DateTime.UtcNow);
    }

    /// <summary>
    /// A hint that fails on its last attempt is given up on, finished, and reported as such.
    /// True: the origin stops waiting, and the scheduled task carries the change.
    /// False: the hint is retried for good, or vanishes without the origin hearing.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ApplyFailsOnTheLastAttempt_GivesUpAndReportsIt()
    {
        AddOrigin();
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        Origin.OnStatus("/Items", HttpStatusCode.InternalServerError);
        Receive(HintKind.Metadata, "/media/movies/a.mkv", QueueSamples.ItemId.ToString("N"), failedAttempts: HintProtocol.MaxInboundAttempts - 1);

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(1, finished);
        Assert.Equal(0, _inbound.Count());
        Assert.Equal(HintCode.GaveUp, Assert.Single(_worker.RecentlyCompleted).Code);
        Assert.Equal(HintCode.GaveUp, Assert.Single(QueuePeerHandler.Read<CompleteRequest>(Origin.LastBody(CompletePath)!).Items).Code);
    }

    /// <summary>
    /// A hint whose handler drops it is finished with the handler's code.
    /// True: the origin hears the real reason and can hold the object back.
    /// False: every drop reads as applied and the origin keeps sending.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_HandlerDropsTheHint_ReportsTheHandlersCode()
    {
        AddOrigin();
        Receive(HintKind.Metadata, "/elsewhere/a.mkv", QueueSamples.ItemId.ToString("N"));

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(1, finished);
        Assert.Equal(HintCode.NotMapped, Assert.Single(_worker.RecentlyCompleted).Code);
    }

    /// <summary>
    /// A hint whose local item has not arrived is parked until the end of its window, counts no attempt, and is not reported.
    /// True: the hint waits for its item and applies when it lands.
    /// False: it burns its retries while the file downloads, or is reported done when nothing was written.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_LocalItemNotHereYet_ParksTheHintForItsWindow()
    {
        AddOrigin();
        var row = Receive(HintKind.Metadata, "/media/movies/a.mkv", QueueSamples.ItemId.ToString("N"));

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        var stored = Assert.Single(_inbound.GetAll());
        Assert.Equal(0, finished);
        Assert.Equal(HintTriage.PathParkKey(QueueSamples.LocalPath("a.mkv")), stored.ParkedPath);
        Assert.Equal(row.ReceivedAt + HintProtocol.ParkWindow, stored.NextAttempt);
        Assert.Equal(0, stored.Attempts);
        Assert.Empty(_worker.RecentlyCompleted);
        Assert.Equal(0, Origin.CountOf(CompletePath));
    }

    /// <summary>
    /// A hint still waiting for its item after its window is dropped and reported as having no local item.
    /// True: a hint for an item that never arrives leaves the queue and the origin is told.
    /// False: it is parked again and again, forever.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ItemNeverArrivedWithinTheWindow_DropsTheHint()
    {
        AddOrigin();
        Receive(HintKind.Metadata, "/media/movies/a.mkv", QueueSamples.ItemId.ToString("N"), receivedAt: DateTime.UtcNow - HintProtocol.ParkWindow - TimeSpan.FromMinutes(1));

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(1, finished);
        Assert.Equal(0, _inbound.Count());
        Assert.Equal(HintCode.NoLocalItem, Assert.Single(_worker.RecentlyCompleted).Code);
    }

    /// <summary>
    /// Several metadata hints from one origin are read from it in one page.
    /// True: a burst of edits costs the origin one item request.
    /// False: every hint reads its own item and a busy origin is flooded.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_TwoMetadataHintsFromOneOrigin_ReadsTheirItemsInOnePage()
    {
        AddOrigin();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _local.AddItem(QueueSamples.LocalPath("a.mkv"));
        _local.AddItem(QueueSamples.LocalPath("b.mkv"));
        Origin.On("/Items", _ => QueuePeerHandler.JsonText(
            "{\"Items\":[{\"Id\":\"" + first.ToString("D") + "\",\"Path\":\"/moved/a.mkv\"},{\"Id\":\"" + second.ToString("D") + "\",\"Path\":\"/moved/b.mkv\"}],\"TotalRecordCount\":2,\"StartIndex\":0}"));
        Receive(HintKind.Metadata, "/media/movies/a.mkv", first.ToString("N"), id: "1");
        Receive(HintKind.Metadata, "/media/movies/b.mkv", second.ToString("N"), id: "2");

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(2, finished);
        Assert.Equal(1, Origin.CountOf("/Items"));
        Assert.All(_worker.RecentlyCompleted, done => Assert.Equal(HintCode.NotMapped, done.Code));
    }

    /// <summary>
    /// The quick pass leaves content rows for their own lane.
    /// True: a request that asks for an apply never waits on a download.
    /// False: a peer's queue request blocks until a film has been copied.
    /// </summary>
    [Fact]
    public async Task ApplyNowAsync_ContentRowDue_LeavesItForItsOwnLane()
    {
        AddOrigin();
        Receive(HintKind.Content, "/media/movies/a.mkv", QueueSamples.ItemId.ToString("N"), id: "1");
        Receive(HintKind.Users, id: "2");

        var finished = await _worker.ApplyNowAsync(CancellationToken.None);

        var left = Assert.Single(_inbound.GetAll());
        Assert.Equal(1, finished);
        Assert.Equal(HintKind.Content, left.Kind);
        Assert.Equal(0, Origin.CountOf("/Items"));
    }

    /// <summary>
    /// A full pass applies the content lane too.
    /// True: a content hint is not left behind by the pass that the dashboard's button runs.
    /// False: content hints only ever apply on the idle tick.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ContentRowDue_AppliesItInItsOwnLane()
    {
        AddOrigin();
        _config.Configuration.EnableContentSync = false;
        Receive(HintKind.Content, "/media/movies/a.mkv", QueueSamples.ItemId.ToString("N"));

        var finished = await _worker.ApplyAsync(CancellationToken.None);

        Assert.Equal(1, finished);
        Assert.Equal(HintCode.ModuleOff, Assert.Single(_worker.RecentlyCompleted).Code);
    }

    /// <summary>
    /// A cancelled pass stops and leaves its rows as they were.
    /// True: a server shutting down loses no hint and counts no attempt.
    /// False: rows are deferred or dropped on the strength of a cancelled pass.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_CancelledToken_ThrowsAndLeavesRowsAlone()
    {
        AddOrigin();
        Receive(HintKind.Users);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _worker.ApplyAsync(cancelled.Token));

        Assert.Equal(0, Assert.Single(_inbound.GetAll()).Attempts);
    }

    /// <summary>
    /// A dismissed row is removed and remembered as given up, and a row that is not there is not dismissed.
    /// True: the operator's discard sticks, since the origin reads it as finished and does not send it again.
    /// False: the discarded hint comes straight back from its origin.
    /// </summary>
    [Fact]
    public void Dismiss_ExistingAndMissingRow_RemovesAndRemembersOnlyTheExisting()
    {
        var row = Receive(HintKind.Metadata);

        var dismissed = _worker.Dismiss(row.Id);
        var again = _worker.Dismiss(row.Id);

        var remembered = Assert.Single(_worker.RecentlyCompleted);
        Assert.True(dismissed);
        Assert.False(again);
        Assert.Equal(0, _inbound.Count());
        Assert.Equal(row.HintId, remembered.HintId);
        Assert.Equal(HintCode.GaveUp, remembered.Code);
        Assert.Equal(row.VersionTimestamp, remembered.VersionTimestamp);
    }

    /// <summary>
    /// Dismissing the failing rows removes only rows whose apply has failed.
    /// True: one press clears what is stuck and leaves waiting work alone.
    /// False: hints that are simply queued are thrown away with the failures.
    /// </summary>
    [Fact]
    public void DismissFailing_FailingAndWaitingRows_RemovesOnlyTheFailing()
    {
        Receive(HintKind.Metadata, id: "1", failedAttempts: 2);
        Receive(HintKind.Metadata, id: "2", failedAttempts: 1);
        var waiting = Receive(HintKind.Metadata, id: "3");

        var dismissed = _worker.DismissFailing();

        Assert.Equal(2, dismissed);
        Assert.Equal(waiting.Id, Assert.Single(_inbound.GetAll()).Id);
        Assert.Equal(2, _worker.RecentlyCompleted.Count);
    }

    /// <summary>
    /// Waking a park key makes its rows due, and a key nothing waits under wakes nothing.
    /// True: an item that arrives applies its waiting hints at once.
    /// False: the hints wait out their window although their item is here.
    /// </summary>
    [Fact]
    public void WakeParked_ParkedAndUnknownKeys_WakesOnlyWhatWaits()
    {
        var row = Receive(HintKind.Metadata);
        _inbound.Park(row, "path:/local/movies/a.mkv", DateTime.UtcNow.AddHours(6), "waiting");

        var none = _worker.WakeParked("path:/local/movies/other.mkv");
        var woken = _worker.WakeParked("path:/local/movies/a.mkv");

        var stored = Assert.Single(_inbound.GetAll());
        Assert.Equal(0, none);
        Assert.Equal(1, woken);
        Assert.Null(stored.ParkedPath);
        Assert.True(stored.NextAttempt <= DateTime.UtcNow);
        Assert.Throws<ArgumentNullException>(() => _worker.WakeParked(null!));
    }

    /// <summary>
    /// Starting moves rows parked past the current window into it, and stopping ends both lanes.
    /// True: hints parked under a longer window by an older build are tried again, and shutdown does not hang.
    /// False: old parked rows wait for days, or the server cannot stop.
    /// </summary>
    [Fact]
    public async Task StartAsync_RowParkedPastTheWindow_IsMadeDueAndStopEndsTheLanes()
    {
        var row = Receive(HintKind.Metadata);
        _inbound.Park(row, "path:/local/movies/a.mkv", DateTime.UtcNow.AddDays(6), "waiting");

        await _worker.StartAsync(CancellationToken.None);
        await _worker.StopAsync(CancellationToken.None);

        var stored = Assert.Single(_inbound.GetAll());
        Assert.True(stored.NextAttempt <= DateTime.UtcNow);
        Assert.NotNull(stored.ParkedPath);
    }

    /// <summary>
    /// Waking twice, and stopping a worker that never started, do not throw.
    /// True: a burst of arriving hints and an early shutdown are both harmless.
    /// False: a second wake throws into the peer's request.
    /// </summary>
    [Fact]
    public async Task WakeAndStop_WithoutStarting_DoNotThrow()
    {
        _worker.Wake();
        _worker.Wake();

        await _worker.StopAsync(CancellationToken.None);

        Assert.Empty(_worker.RecentlyCompleted);
    }
}

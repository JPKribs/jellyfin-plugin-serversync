using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// Each peer's answer to a delivery leaves its rows sent, removed, failed, paused, or waiting to retry, and never stops delivery to the other peers.
/// </summary>
[Collection(QueueStateCollection.Name)]
public sealed class OutboundHintWorkerTests : IDisposable
{
    private const string Me = "this-server";
    private const string QueuePath = "/Sync/Peer/Queue";
    private const string StatusPath = "/Sync/Peer/Status";
    private static readonly DateTime Edited = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly QueueTestDatabase _db = new();
    private readonly FakeConfigManager _config = QueueSamples.Config();
    private readonly QueueClientFactory _factory = new();
    private readonly HintActivityHarness _activity = new();
    private readonly HintSuppression _suppression = new();
    private readonly InterfaceStub _library;
    private readonly OutboundHintStore _outbound;
    private readonly OutboundHintWorker _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundHintWorkerTests"/> class.
    /// </summary>
    public OutboundHintWorkerTests()
    {
        _outbound = new OutboundHintStore(_db.Provider, NullLogger<OutboundHintStore>.Instance);
        var (library, stub) = InterfaceStub.Create<ILibraryManager>();
        _library = stub;
        _worker = new OutboundHintWorker(_outbound, _config, _factory, QueueSamples.Host(Me), _activity.Log, _suppression, library, NullLogger<OutboundHintWorker>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _worker.Dispose();
        _activity.Dispose();
        _db.Dispose();
    }

    private SourceServer AddPeer(string key = "peer", ServerMode mode = ServerMode.Sync)
    {
        var peer = QueueSamples.Server(key, mode, key + "-id");
        _config.Configuration.Servers.Add(peer);
        return peer;
    }

    private OutboundHint Queue(string key = "item", string peer = "peer", HintKind kind = HintKind.Metadata, string? itemId = null, string? itemType = null)
    {
        var row = new OutboundHint
        {
            PeerKey = peer,
            Kind = kind,
            Key = key,
            ItemPath = QueueSamples.LocalPath("a.mkv"),
            ItemId = itemId,
            ItemType = itemType,
            UserId = QueueSamples.LocalUserId.ToString("N"),
            VersionServerId = Me,
            VersionTimestamp = Edited
        };
        _outbound.Enqueue(row, Me);
        return row;
    }

    private OutboundHint QueueSent(string key = "item", TimeSpan? ago = null)
    {
        var row = Queue(key);
        _outbound.MarkSent(new[] { row }, DateTime.UtcNow - (ago ?? TimeSpan.FromHours(1)));
        return row;
    }

    /// <summary>Makes a peer answer every hint in a batch with one code.</summary>
    private static void Answers(QueuePeerHandler peer, HintCode code)
        => peer.On(QueuePath, body => QueuePeerHandler.Json(new QueueResponse
        {
            Items = QueuePeerHandler.Read<QueueRequest>(body).Items.Select(hint => new QueueResult { HintId = hint.HintId, Code = code, Reason = "because" }).ToList()
        }));

    private OutboundHint Only() => Assert.Single(_outbound.GetAll());

    /// <summary>
    /// A missing peer or report is refused.
    /// True: a caller's mistake is named.
    /// False: a peer's completion request ends in a null reference.
    /// </summary>
    [Fact]
    public void Complete_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _worker.Complete(null!, new List<CompletedHint>()));
        Assert.Throws<ArgumentNullException>(() => _worker.Complete(QueueSamples.Server(), null!));
    }

    /// <summary>
    /// A peer that has not said what it wants is taken to accept every kind.
    /// True: hints flow to a new peer at once, and it declines what it does not want.
    /// False: nothing is sent until a first answer that an older peer may never give.
    /// </summary>
    [Fact]
    public void PeerAccepts_PeerNeverAsked_AcceptsEverything()
    {
        Assert.True(_worker.PeerAccepts("never-seen", HintKind.Metadata));
        Assert.True(_worker.PeerWants("never-seen", HintKind.Metadata, QueueSamples.LocalPath("a.mkv"), Guid.Empty, "item"));
        Assert.Empty(_worker.PeerStates);
    }

    /// <summary>
    /// With nothing queued a pass sends no hints and reports no more to do.
    /// True: an idle server posts nothing to its peers.
    /// False: empty batches are posted on every tick.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_NothingQueued_SendsNothing()
    {
        AddPeer();

        var more = await _worker.DeliverAsync(CancellationToken.None);

        Assert.False(more);
        Assert.Equal(0, _factory.Peer("peer").CountOf(QueuePath));
    }

    /// <summary>
    /// A batch the peer accepts is marked sent, and carried this server's id and the row's own hint.
    /// True: an accepted hint waits for its completion and is not sent twice.
    /// False: the row stays pending and is delivered again on every pass.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeerAcceptsTheBatch_MarksItsRowsSent()
    {
        AddPeer();
        var row = Queue();
        Answers(_factory.Peer("peer"), HintCode.Queued);

        var more = await _worker.DeliverAsync(CancellationToken.None);

        var stored = Only();
        var request = QueuePeerHandler.Read<QueueRequest>(_factory.Peer("peer").LastBody(QueuePath)!);
        Assert.False(more);
        Assert.Equal(OutboundState.Sent, stored.State);
        Assert.NotNull(stored.SentAt);
        Assert.Equal(Me, request.SenderServerId);
        var sent = Assert.Single(request.Items);
        Assert.Equal(row.HintId, sent.HintId);
        Assert.Equal(Me, sent.OriginServerId);
        Assert.Equal(Edited, sent.VersionTimestamp);
        Assert.Null(_worker.PeerStates["peer"].PausedUntil);
        Assert.NotNull(_worker.PeerStates["peer"].LastAttempt);
    }

    /// <summary>
    /// A hint the peer answers nothing about is taken as accepted.
    /// True: an older peer that answers with an empty list still receives hints.
    /// False: unanswered rows stay pending and are sent forever.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeerAnswersWithoutNamingTheHint_MarksItSent()
    {
        AddPeer();
        Queue();
        _factory.Peer("peer").OnJson(QueuePath, new QueueResponse());

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Equal(OutboundState.Sent, Only().State);
    }

    /// <summary>
    /// A hint the peer closes on receipt is removed, and held back only when the answer will repeat.
    /// True: a declined hint leaves the queue, and an object the peer filters is not sent again.
    /// False: closed rows wait for a completion that never comes, or every edit of a filtered item is sent.
    /// </summary>
    [Theory]
    [InlineData(HintCode.NotMapped, false)]
    [InlineData(HintCode.Filtered, false)]
    [InlineData(HintCode.AlreadyCurrent, true)]
    [InlineData(HintCode.Superseded, true)]
    public async Task DeliverAsync_PeerClosesTheHintOnReceipt_RemovesItAndHoldsWhatRepeats(HintCode code, bool stillWanted)
    {
        AddPeer();
        Queue("item");
        Answers(_factory.Peer("peer"), code);

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Empty(_outbound.GetAll());
        Assert.Equal(stillWanted, _worker.PeerWants("peer", HintKind.Metadata, QueueSamples.LocalPath("a.mkv"), Guid.Empty, "item"));
    }

    /// <summary>
    /// A peer that answers with a server error, or does not answer, leaves the rows pending with a counted attempt and a wait.
    /// True: a peer that is restarting gets its hints once it is back.
    /// False: the rows are lost, or retried in a tight loop.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeliverAsync_PeerFailsOrDoesNotAnswer_DefersWithBackoff(bool answers)
    {
        AddPeer();
        Queue();
        if (answers)
        {
            _factory.Peer("peer").OnStatus(QueuePath, HttpStatusCode.InternalServerError, "boom");
        }
        else
        {
            _factory.Peer("peer").OnUnreachable(QueuePath);
        }

        var before = DateTime.UtcNow;

        var more = await _worker.DeliverAsync(CancellationToken.None);

        var stored = Only();
        Assert.False(more);
        Assert.Equal(OutboundState.Pending, stored.State);
        Assert.Equal(1, stored.Attempts);
        Assert.NotNull(stored.LastError);
        Assert.InRange(stored.NextAttempt, before.AddSeconds(_config.Configuration.HintRetrySeconds), DateTime.UtcNow.AddSeconds(_config.Configuration.HintRetrySeconds));
        Assert.NotNull(_worker.PeerStates["peer"].Reason);
        Assert.Null(_worker.PeerStates["peer"].PausedUntil);
    }

    /// <summary>
    /// A row that has used its last attempt is marked failed.
    /// True: a hint that can never be delivered stops being tried and shows as failed.
    /// False: it is retried forever although a limit was set.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_LastAttemptFails_MarksTheRowFailed()
    {
        AddPeer();
        _config.Configuration.HintMaxRetries = 1;
        Queue();
        _factory.Peer("peer").OnStatus(QueuePath, HttpStatusCode.ServiceUnavailable);

        await _worker.DeliverAsync(CancellationToken.None);

        var stored = Only();
        Assert.Equal(OutboundState.Failed, stored.State);
        Assert.NotNull(stored.LastError);
    }

    /// <summary>
    /// A batch the peer calls malformed is marked failed and written to the Activity page.
    /// True: a request the peer can never accept is not sent again, and the administrator is told.
    /// False: the same bad batch is posted on every pass, silently.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeerCallsTheBatchMalformed_FailsItAndTellsTheAdministrator()
    {
        AddPeer();
        Queue();
        _factory.Peer("peer").OnStatus(QueuePath, HttpStatusCode.BadRequest, "bad field");

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Equal(OutboundState.Failed, Only().State);
        Assert.Contains("ServerSync.HintRejected", _activity.Types);
    }

    /// <summary>
    /// A peer that refuses the request pauses, its rows wait out the pause without being lost, and the pause is written once.
    /// True: a refused key or a missing plugin stops delivery with a reason, and the changes go once it is fixed.
    /// False: a refusing peer is hammered on every pass, or its backlog is thrown away.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UpgradeRequired)]
    [InlineData((HttpStatusCode)428)]
    public async Task DeliverAsync_PeerRefuses_PausesThePeerAndKeepsItsRows(HttpStatusCode status)
    {
        AddPeer();
        Queue();
        _factory.Peer("peer").OnStatus(QueuePath, status, "refused");

        await _worker.DeliverAsync(CancellationToken.None);
        await _worker.DeliverAsync(CancellationToken.None);

        var stored = Only();
        var state = _worker.PeerStates["peer"];
        Assert.Equal(OutboundState.Pending, stored.State);
        Assert.NotNull(state.PausedUntil);
        Assert.True(state.PausedUntil > DateTime.UtcNow);
        Assert.Equal(state.PausedUntil, stored.NextAttempt);
        Assert.NotNull(state.Reason);
        Assert.Equal(1, _factory.Peer("peer").CountOf(QueuePath));
        Assert.Single(_activity.Types, type => type == "ServerSync.PeerPaused");
    }

    /// <summary>
    /// A pause ends at once when the peer's entry is changed.
    /// True: fixing the key or the address sends the waiting changes on the next pass.
    /// False: a corrected entry still waits out a pause given for the old one.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PausedPeersEntryChanged_SendsAgainAtOnce()
    {
        var peer = AddPeer();
        Queue();
        _factory.Peer("peer").OnStatus(QueuePath, HttpStatusCode.Unauthorized);
        await _worker.DeliverAsync(CancellationToken.None);
        peer.ApiKey = "corrected-key";
        Answers(_factory.Peer("peer"), HintCode.Queued);

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Equal(OutboundState.Sent, Only().State);
        Assert.Null(_worker.PeerStates["peer"].PausedUntil);
    }

    /// <summary>
    /// Lifting pauses clears a delivery pause.
    /// True: the operator's retry tries a paused server on the next pass.
    /// False: the press does nothing until the pause runs out by itself.
    /// </summary>
    [Fact]
    public async Task LiftPauses_PeerPausedOverADelivery_IsNoLongerPaused()
    {
        AddPeer();
        Queue();
        _factory.Peer("peer").OnStatus(QueuePath, HttpStatusCode.Forbidden);
        await _worker.DeliverAsync(CancellationToken.None);

        _worker.LiftPauses();

        Assert.Null(_worker.PeerStates["peer"].PausedUntil);
    }

    /// <summary>
    /// A peer whose address the client factory refuses is paused, and its rows are kept.
    /// True: an address the SSRF guard rejects is named on the dashboard and nothing is sent to it.
    /// False: the worker's pass fails, and no peer is delivered to.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeersAddressIsRefused_PausesThePeer()
    {
        AddPeer();
        Queue();
        _factory.Refuse("peer", "the address is private");

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Equal(OutboundState.Pending, Only().State);
        Assert.NotNull(_worker.PeerStates["peer"].PausedUntil);
        Assert.Contains("ServerSync.PeerPaused", _activity.Types);
    }

    /// <summary>
    /// A peer on a protocol this build cannot talk to is sent nothing, keeps its rows, and stays paused through the operator's retry.
    /// True: changes wait here until both servers are updated, and nothing is misread by an incompatible peer.
    /// False: hints are posted to a peer that would read them wrongly, or are dropped while it is behind.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeerOnAnIncompatibleProtocol_SendsNothingAndStaysBlocked()
    {
        AddPeer();
        Queue();
        _factory.Peer("peer").OnJson("/Sync/Peer/Capabilities", new PeerCapabilities { ServerId = "peer-id", PluginVersion = "99.0", ProtocolVersion = HintProtocol.ProtocolVersion + 1, MinProtocolVersion = HintProtocol.ProtocolVersion + 1 });

        await _worker.DeliverAsync(CancellationToken.None);
        _worker.LiftPauses();

        var state = _worker.PeerStates["peer"];
        Assert.True(state.VersionBlocked);
        Assert.NotNull(state.PausedUntil);
        Assert.NotNull(state.Reason);
        Assert.Equal(OutboundState.Pending, Only().State);
        Assert.Equal(0, _factory.Peer("peer").CountOf(QueuePath));
    }

    /// <summary>
    /// Once a blocked peer answers with a compatible protocol, a forced refresh sends its waiting rows.
    /// True: updating the peer releases the changes that waited for it.
    /// False: the peer stays blocked until this server is restarted.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_BlockedPeerBecomesCompatible_SendsItsWaitingRows()
    {
        AddPeer();
        Queue();
        var peer = _factory.Peer("peer");
        peer.OnJson("/Sync/Peer/Capabilities", new PeerCapabilities { ServerId = "peer-id", ProtocolVersion = HintProtocol.ProtocolVersion + 1, MinProtocolVersion = HintProtocol.ProtocolVersion + 1 });
        await _worker.DeliverAsync(CancellationToken.None);
        peer.OnJson("/Sync/Peer/Capabilities", new PeerCapabilities { ServerId = "peer-id", ProtocolVersion = HintProtocol.ProtocolVersion, MinProtocolVersion = HintProtocol.MinProtocolVersion });
        Answers(peer, HintCode.Queued);

        await _worker.DeliverAsync(CancellationToken.None, refreshCapabilities: true);

        Assert.False(_worker.PeerStates["peer"].VersionBlocked);
        Assert.Equal(OutboundState.Sent, Only().State);
    }

    /// <summary>
    /// A kind the peer says it does not apply is not sent, and its queued rows are removed.
    /// True: a peer with Metadata off receives no metadata hints.
    /// False: every edit is posted to a peer that can only decline it.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeerDoesNotApplyTheKind_DropsTheRowUnsent()
    {
        AddPeer();
        Queue();
        _factory.Peer("peer").OnJson("/Sync/Peer/Interest", new PeerInterest { Kinds = { HintKind.History }, Revision = "r1" });

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Empty(_outbound.GetAll());
        Assert.Equal(0, _factory.Peer("peer").CountOf(QueuePath));
        Assert.False(_worker.PeerAccepts("peer", HintKind.Metadata));
        Assert.True(_worker.PeerAccepts("peer", HintKind.History));
    }

    /// <summary>
    /// A peer whose settings changed gets back the objects it had declined.
    /// True: mapping a library on the peer lets its items flow without waiting a day.
    /// False: an object declined under the old settings stays held although the peer now wants it.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeersInterestRevisionChanged_ForgetsWhatItDeclined()
    {
        AddPeer();
        _suppression.Note("peer", HintKind.Metadata, "item", HintCode.NotMapped, null, DateTime.UtcNow);
        _factory.Peer("peer").OnJson("/Sync/Peer/Interest", new PeerInterest
        {
            Kinds = { HintKind.Metadata },
            Libraries = { new PeerInterestLibrary { Root = QueueSamples.LocalRoot } },
            Revision = "r2"
        });

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Equal(0, _suppression.Count);
        Assert.True(_worker.PeerWants("peer", HintKind.Metadata, QueueSamples.LocalPath("a.mkv"), Guid.Empty, "item"));
    }

    /// <summary>
    /// A row whose item or person no longer exists here is removed unsent, and a playlist deletion is sent all the same.
    /// True: a peer is not told to fetch something that is gone, and still hears of a deleted playlist.
    /// False: hints for deleted items fail on the peer, or a deletion is never announced because its playlist is gone.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_ObjectGoneHere_DropsTheRowButSendsAPlaylistDeletion()
    {
        AddPeer();
        var gone = Guid.NewGuid().ToString("N");
        Queue("gone-item", itemId: gone);
        Queue("Gone Person", kind: HintKind.People);
        var deletion = Queue(HintProtocol.PlaylistKey(Guid.NewGuid()), kind: HintKind.Playlists, itemId: Guid.NewGuid().ToString("N"), itemType: HintProtocol.PlaylistDeleted);
        Answers(_factory.Peer("peer"), HintCode.Queued);

        await _worker.DeliverAsync(CancellationToken.None);

        var request = QueuePeerHandler.Read<QueueRequest>(_factory.Peer("peer").LastBody(QueuePath)!);
        Assert.Equal(deletion.HintId, Assert.Single(request.Items).HintId);
        Assert.Equal(deletion.Id, Only().Id);
        Assert.Contains(_library.Calls, call => call.Name == nameof(ILibraryManager.GetItemById));
        Assert.Contains(_library.Calls, call => call.Name == nameof(ILibraryManager.GetPerson));
    }

    /// <summary>
    /// Rows for a server that left the configuration, or no longer takes changes, are removed.
    /// True: removing a server, or setting it to Pull, leaves nothing queued for it.
    /// False: its rows stay forever and nothing ever delivers them.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliverAsync_ServerRemovedOrNoLongerTakingChanges_DropsItsRows(bool stillConfigured)
    {
        if (stillConfigured)
        {
            AddPeer(mode: ServerMode.Pull);
        }

        Queue();

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Empty(_outbound.GetAll());
        Assert.Equal(0, _factory.Peer("peer").CountOf(QueuePath));
    }

    /// <summary>
    /// One peer that fails does not keep another peer from its hints.
    /// True: a server that is down delays only its own changes.
    /// False: one bad peer blocks the whole pool.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_OnePeerFails_TheOtherIsStillDelivered()
    {
        AddPeer("down");
        AddPeer("up");
        Queue("item", "down");
        Queue("item", "up");
        _factory.Peer("down").OnUnreachable(QueuePath);
        Answers(_factory.Peer("up"), HintCode.Queued);

        await _worker.DeliverAsync(CancellationToken.None);

        var rows = _outbound.GetAll().ToDictionary(row => row.PeerKey);
        Assert.Equal(OutboundState.Pending, rows["down"].State);
        Assert.Equal(OutboundState.Sent, rows["up"].State);
    }

    /// <summary>
    /// A full batch sends the batch size and says more may be due, leaving the rest pending.
    /// True: a backlog drains batch by batch without one enormous request.
    /// False: the worker idles for half a minute between batches, or posts thousands of hints at once.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_MoreRowsThanOneBatch_SendsOneBatchAndSaysMoreIsDue()
    {
        AddPeer();
        for (var i = 0; i < 101; i++)
        {
            Queue("item-" + i.ToString(CultureInfo.InvariantCulture));
        }

        Answers(_factory.Peer("peer"), HintCode.Queued);

        var more = await _worker.DeliverAsync(CancellationToken.None);

        var states = _outbound.CountByState();
        Assert.True(more);
        Assert.Equal(100, states[OutboundState.Sent]);
        Assert.Equal(1, states[OutboundState.Pending]);
        Assert.Equal(100, QueuePeerHandler.Read<QueueRequest>(_factory.Peer("peer").LastBody(QueuePath)!).Items.Count);
    }

    /// <summary>
    /// A cancelled pass stops and leaves its rows pending.
    /// True: a server shutting down loses no hint.
    /// False: rows are marked failed or deferred on the strength of a cancelled request.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_CancelledToken_ThrowsAndLeavesRowsPending()
    {
        AddPeer();
        Queue();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _worker.DeliverAsync(cancelled.Token));

        var stored = Only();
        Assert.Equal(OutboundState.Pending, stored.State);
        Assert.Equal(0, stored.Attempts);
    }

    /// <summary>
    /// A sent row the peer no longer holds and never finished is put back to pending and written to the Activity page.
    /// True: a hint lost when the peer restarted is delivered again.
    /// False: the change waits for the next scheduled scan.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeerLostASentHint_SendsItAgain()
    {
        AddPeer();
        QueueSent();
        _factory.Peer("peer").OnJson(StatusPath, new QueueStatusResponse { ServerId = "peer-id" });

        await _worker.DeliverAsync(CancellationToken.None);

        var stored = Only();
        Assert.Equal(OutboundState.Pending, stored.State);
        Assert.Null(stored.SentAt);
        Assert.Contains("ServerSync.HintResent", _activity.Types);
    }

    /// <summary>
    /// A sent row the peer still holds is left to wait.
    /// True: a slow apply on the peer is not interrupted by a second copy of the hint.
    /// False: the hint is sent again while the first is still being applied.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeerStillHoldsTheSentHint_LeavesItSent()
    {
        AddPeer();
        var row = QueueSent();
        _factory.Peer("peer").OnJson(StatusPath, new QueueStatusResponse { Inbound = { new InboundHintDto { HintId = row.HintId } } });

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Equal(OutboundState.Sent, Only().State);
    }

    /// <summary>
    /// A sent row the peer's status lists as finished at the row's stamp is completed from that status.
    /// True: a peer that cannot call back still clears its finished work.
    /// False: its rows sit as sent until they expire a week later.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeersStatusListsTheHintAsFinished_CompletesIt()
    {
        AddPeer();
        var row = QueueSent();
        _factory.Peer("peer").OnJson(StatusPath, new QueueStatusResponse { CompletedHints = { new CompletedHint { HintId = row.HintId, VersionTimestamp = Edited, Code = HintCode.Applied } } });

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// A completion the peer lists for an older stamp than the row carries does not finish the row.
    /// True: an edit made after the peer applied the last one is sent again.
    /// False: the newer edit is completed on the strength of the older one and never delivered.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_PeersStatusListsAnOlderStampAsFinished_SendsTheRowAgain()
    {
        AddPeer();
        var row = QueueSent();
        _factory.Peer("peer").OnJson(StatusPath, new QueueStatusResponse { CompletedHints = { new CompletedHint { HintId = row.HintId, VersionTimestamp = Edited.AddMinutes(-5) } } });

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Equal(OutboundState.Pending, Only().State);
    }

    /// <summary>
    /// A peer whose status cannot be read leaves its sent rows as they are.
    /// True: a failed status request neither completes nor resends anything.
    /// False: one bad answer resends every waiting hint.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeliverAsync_PeersStatusCannotBeRead_LeavesSentRowsAlone(bool answers)
    {
        AddPeer();
        QueueSent();
        if (answers)
        {
            _factory.Peer("peer").OnStatus(StatusPath, HttpStatusCode.InternalServerError);
        }
        else
        {
            _factory.Peer("peer").OnUnreachable(StatusPath);
        }

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Equal(OutboundState.Sent, Only().State);
    }

    /// <summary>
    /// A row sent within the grace period is not asked about.
    /// True: the peer has time to apply and report before anyone checks.
    /// False: every pass polls every peer about hints it only just received.
    /// </summary>
    [Fact]
    public async Task DeliverAsync_RowSentWithinTheGracePeriod_DoesNotAskThePeer()
    {
        AddPeer();
        QueueSent(ago: TimeSpan.FromMinutes(1));

        await _worker.DeliverAsync(CancellationToken.None);

        Assert.Equal(0, _factory.Peer("peer").CountOf(StatusPath));
        Assert.Equal(OutboundState.Sent, Only().State);
    }

    /// <summary>
    /// Waking twice, and stopping a worker that was started or never started, all end cleanly.
    /// True: a burst of edits and a server shutdown never throw into Jellyfin.
    /// False: a second wake throws inside a save event, or shutdown hangs on the worker.
    /// </summary>
    [Fact]
    public async Task WakeStartAndStop_InAnyOrder_EndCleanly()
    {
        using var neverStarted = new OutboundHintWorker(_outbound, _config, _factory, QueueSamples.Host(Me), _activity.Log, _suppression, null!, NullLogger<OutboundHintWorker>.Instance);

        _worker.Wake();
        _worker.Wake();
        await neverStarted.StopAsync(CancellationToken.None);
        await _worker.StartAsync(CancellationToken.None);
        await _worker.StopAsync(CancellationToken.None);

        Assert.Empty(_outbound.GetAll());
    }
}

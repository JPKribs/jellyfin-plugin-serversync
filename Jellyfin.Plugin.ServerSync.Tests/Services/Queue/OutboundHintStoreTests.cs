using System;
using System.Globalization;
using System.Linq;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The outbound hint store keeps each owed hint whole, keeps one peer's rows apart from another's, and stays within its cap.
/// </summary>
public sealed class OutboundHintStoreTests : IDisposable
{
    private static readonly DateTime Edited = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly QueueTestDatabase _db = new();
    private readonly OutboundHintStore _outbound;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundHintStoreTests"/> class.
    /// </summary>
    public OutboundHintStoreTests()
    {
        _outbound = new OutboundHintStore(_db.Provider, NullLogger<OutboundHintStore>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static OutboundHint Row(string peer = "peer-a", string key = "item", int minute = 0, HintKind kind = HintKind.Metadata, string? itemType = null) => new()
    {
        PeerKey = peer,
        Kind = kind,
        Key = key,
        ItemType = itemType,
        VersionServerId = "me",
        VersionTimestamp = Edited.AddMinutes(minute)
    };

    private OutboundHint Stored(string peer = "peer-a", string key = "item", int minute = 0, HintKind kind = HintKind.Metadata, string? itemType = null)
    {
        var row = Row(peer, key, minute, kind, itemType);
        _outbound.Enqueue(row, "me");
        return row;
    }

    /// <summary>
    /// A missing argument is refused by every call that takes one.
    /// True: a caller's mistake is named.
    /// False: it surfaces as a null reference under the write lock.
    /// </summary>
    [Fact]
    public void Calls_NullArgument_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => _outbound.Enqueue(null!, "me"));
        Assert.Throws<ArgumentNullException>(() => _outbound.MarkSent(null!, Edited));
        Assert.Throws<ArgumentNullException>(() => _outbound.MarkFailed(null!, "reason"));
        Assert.Throws<ArgumentNullException>(() => _outbound.Defer(null!, Edited, "error"));
        Assert.Throws<ArgumentNullException>(() => _outbound.DeleteSent(null!));
        Assert.Throws<ArgumentNullException>(() => _outbound.Complete("peer-a", null!));
        Assert.Throws<ArgumentNullException>(() => _outbound.GetByHintIds("peer-a", null!));
        Assert.Throws<ArgumentNullException>(() => _outbound.Resend(null!, Edited));
    }

    /// <summary>
    /// A new row reads back with every field, pending and due, under a hint id that starts with this server's id.
    /// True: the peer receives exactly the change that was made, from an id it can trace to its sender.
    /// False: a user, path, or stamp is lost before delivery, or the peer refuses the id.
    /// </summary>
    [Fact]
    public void Enqueue_NewRow_ReadsBackEveryFieldPendingAndDue()
    {
        var before = DateTime.UtcNow;
        var row = new OutboundHint
        {
            PeerKey = "peer-a",
            Kind = HintKind.History,
            Key = "user|item",
            ItemPath = "/local/movies/a.mkv",
            ItemId = "item",
            ItemType = "Movie",
            UserId = "user",
            UserName = "viewer",
            VersionServerId = "me",
            VersionTimestamp = Edited,
            Recorded = false
        };

        _outbound.Enqueue(row, "me");

        var stored = Assert.Single(_outbound.GetAll());
        Assert.Equal(row.Id, stored.Id);
        Assert.StartsWith("me:", stored.HintId, StringComparison.Ordinal);
        Assert.Equal("peer-a", stored.PeerKey);
        Assert.Equal(HintKind.History, stored.Kind);
        Assert.Equal("user|item", stored.Key);
        Assert.Equal("/local/movies/a.mkv", stored.ItemPath);
        Assert.Equal("item", stored.ItemId);
        Assert.Equal("Movie", stored.ItemType);
        Assert.Equal("user", stored.UserId);
        Assert.Equal("viewer", stored.UserName);
        Assert.Equal("me", stored.VersionServerId);
        Assert.Equal(Edited, stored.VersionTimestamp);
        Assert.False(stored.Recorded);
        Assert.Equal(OutboundState.Pending, stored.State);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.SentAt);
        Assert.Null(stored.LastError);
        Assert.InRange(stored.CreatedAt, before, DateTime.UtcNow);
        Assert.Equal(stored.CreatedAt, stored.NextAttempt);
    }

    /// <summary>
    /// A new edit of an object whose row failed puts the row back to pending with a clean record.
    /// True: an edit made after a peer gave up on the last one is sent with its full allowance.
    /// False: the new edit stays failed until someone presses retry.
    /// </summary>
    [Fact]
    public void Enqueue_NewEditOfAFailedRow_StartsOverPending()
    {
        var row = Stored(minute: 1);
        _outbound.Defer(new[] { row }, DateTime.UtcNow.AddHours(1), "no answer");
        _outbound.MarkFailed(row, "gave up");

        _outbound.Enqueue(Row(minute: 2), "me");

        var stored = Assert.Single(_outbound.GetAll());
        Assert.Equal(OutboundState.Pending, stored.State);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.LastError);
        Assert.Equal(row.HintId, stored.HintId);
        Assert.Single(_outbound.GetDue("peer-a", DateTime.UtcNow.AddSeconds(1), 10));
    }

    /// <summary>
    /// A history edit joins the parts of a pending row for the same object.
    /// True: a play still waiting to be sent is not lost when a favorite follows it.
    /// False: the peer hears only of the favorite and the play waits for a scan.
    /// </summary>
    [Fact]
    public void Enqueue_HistoryPartsForAPendingRow_AddsTheEarlierParts()
    {
        Stored(key: "user|item", minute: 1, kind: HintKind.History, itemType: "Movie+Played");

        _outbound.Enqueue(Row(key: "user|item", minute: 2, kind: HintKind.History, itemType: "Movie+Favorite"), "me");

        Assert.Equal("Movie+Played+Favorite", _outbound.GetAll().Single().ItemType);
    }

    /// <summary>
    /// A history edit does not take on the parts of a row the peer already accepted.
    /// True: a part already delivered is not announced a second time as a fresh change.
    /// False: the peer weighs a stale play against its own newer one.
    /// </summary>
    [Fact]
    public void Enqueue_HistoryPartsForASentRow_KeepsOnlyItsOwnParts()
    {
        var sent = Stored(key: "user|item", minute: 1, kind: HintKind.History, itemType: "Movie+Played");
        _outbound.MarkSent(new[] { sent }, DateTime.UtcNow);

        _outbound.Enqueue(Row(key: "user|item", minute: 2, kind: HintKind.History, itemType: "Movie+Favorite"), "me");

        Assert.Equal("Movie+Favorite", _outbound.GetAll().Single().ItemType);
    }

    /// <summary>
    /// Each peer with rows is listed once, and an empty queue lists none.
    /// True: a delivery pass visits every peer that is owed something, once.
    /// False: a peer is skipped, or delivered to twice in one pass.
    /// </summary>
    [Fact]
    public void GetPeerKeys_RowsForTwoPeers_ListsEachOnce()
    {
        var empty = _outbound.GetPeerKeys();
        Stored("peer-a", "one");
        Stored("peer-a", "two");
        Stored("peer-b", "one");

        var keys = _outbound.GetPeerKeys();

        Assert.Empty(empty);
        Assert.Equal(new[] { "peer-a", "peer-b" }, keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Rows are counted by state, and a state with no rows is absent.
    /// True: the dashboard's pending, sent, and failed numbers match the queue.
    /// False: the totals mislead about what a peer is still owed.
    /// </summary>
    [Fact]
    public void CountByState_RowsInEachState_CountsThem()
    {
        var emptyCounts = _outbound.CountByState();
        Stored(key: "pending-1");
        Stored(key: "pending-2");
        var sent = Stored(key: "sent");
        _outbound.MarkSent(new[] { sent }, DateTime.UtcNow);
        var failed = Stored(key: "failed");
        _outbound.MarkFailed(failed, "bad");

        var counts = _outbound.CountByState();

        Assert.Empty(emptyCounts);
        Assert.Equal(2, counts[OutboundState.Pending]);
        Assert.Equal(1, counts[OutboundState.Sent]);
        Assert.Equal(1, counts[OutboundState.Failed]);
    }

    /// <summary>
    /// Due rows are one peer's own, oldest first, up to the limit.
    /// True: a batch never carries another peer's hints, and edits go out in order.
    /// False: a peer receives hints meant for another server.
    /// </summary>
    [Fact]
    public void GetDue_RowsForTwoPeers_ReturnsOnePeersOldestUpToTheLimit()
    {
        var first = Stored("peer-a", "one");
        Stored("peer-b", "other");
        Stored("peer-a", "two");
        Stored("peer-a", "three");

        var due = _outbound.GetDue("peer-a", DateTime.UtcNow.AddSeconds(1), 2);

        Assert.Equal(2, due.Count);
        Assert.Equal(first.Id, due[0].Id);
        Assert.All(due, row => Assert.Equal("peer-a", row.PeerKey));
    }

    /// <summary>
    /// Bringing a peer forward makes its waiting rows due and leaves other peers and sent rows alone.
    /// True: once a pause is lifted the peer's backlog goes at once.
    /// False: the rows sit out a pause that no longer applies, or another peer's backoff is cut short.
    /// </summary>
    [Fact]
    public void BringForward_DeferredRows_MakesOnlyThatPeersPendingRowsDue()
    {
        var mine = Stored("peer-a", "one");
        var theirs = Stored("peer-b", "one");
        var later = DateTime.UtcNow.AddHours(1);
        _outbound.Defer(new[] { mine }, later, "paused");
        _outbound.Defer(new[] { theirs }, later, "paused");
        var now = DateTime.UtcNow;

        var brought = _outbound.BringForward("peer-a", now);

        Assert.Equal(1, brought);
        Assert.Single(_outbound.GetDue("peer-a", now, 10));
        Assert.Empty(_outbound.GetDue("peer-b", now, 10));
    }

    /// <summary>
    /// Deleting a peer's rows removes them in every state and leaves the other peers'.
    /// True: a server removed from the configuration leaves nothing behind.
    /// False: its rows linger, or another peer's pending changes are lost with it.
    /// </summary>
    [Fact]
    public void DeleteForPeer_RowsInSeveralStates_RemovesOnlyThatPeers()
    {
        Stored("peer-a", "pending");
        var sent = Stored("peer-a", "sent");
        _outbound.MarkSent(new[] { sent }, DateTime.UtcNow);
        Stored("peer-b", "pending");

        var removed = _outbound.DeleteForPeer("peer-a");

        Assert.Equal(2, removed);
        Assert.Equal("peer-b", Assert.Single(_outbound.GetAll()).PeerKey);
    }

    /// <summary>
    /// A row is deleted by id once.
    /// True: the operator's discard removes the row and says so.
    /// False: a second press reports a row that is gone as removed.
    /// </summary>
    [Fact]
    public void Delete_ExistingRow_RemovesItOnce()
    {
        var row = Stored();

        var first = _outbound.Delete(row.Id);
        var second = _outbound.Delete(row.Id);

        Assert.True(first);
        Assert.False(second);
    }

    /// <summary>
    /// Rows are found by hint id for the peer that owns them only, and no ids find nothing.
    /// True: a peer's completion report resolves only its own rows.
    /// False: a peer that names another peer's hint id reaches that peer's row.
    /// </summary>
    [Fact]
    public void GetByHintIds_OwnOtherAndUnknownIds_FindsOnlyThePeersOwn()
    {
        var mine = Stored("peer-a", "one");
        var theirs = Stored("peer-b", "one");

        var none = _outbound.GetByHintIds("peer-a", Array.Empty<string>());
        var found = _outbound.GetByHintIds("peer-a", new[] { mine.HintId, theirs.HintId, "me:unknown", mine.HintId });

        Assert.Empty(none);
        Assert.Equal(mine.Id, Assert.Single(found).Value.Id);
        Assert.True(found.ContainsKey(mine.HintId));
    }

    /// <summary>
    /// A completion with no peer is refused.
    /// True: a completion can never be applied to every peer's rows at once.
    /// False: a blank peer key removes rows across the whole queue.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Complete_BlankPeer_Throws(string peerKey)
    {
        Assert.Throws<ArgumentException>(() => _outbound.Complete(peerKey, Array.Empty<CompletedHint>()));
    }

    /// <summary>
    /// A completion dated ahead of this server's clock still completes the row it names.
    /// True: a peer with a fast clock can finish its work.
    /// False: its rows are never completed and are sent again and again.
    /// </summary>
    [Fact]
    public void Complete_CompletionDatedInTheFuture_RemovesTheRow()
    {
        var row = Stored();

        var removed = _outbound.Complete("peer-a", new[] { new CompletedHint { HintId = row.HintId, VersionTimestamp = new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc) } });

        Assert.Equal(1, removed);
        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// A report with nothing in it completes nothing.
    /// True: an empty report from a peer is harmless.
    /// False: an empty report clears the peer's rows.
    /// </summary>
    [Fact]
    public void Complete_EmptyReport_RemovesNothing()
    {
        Stored();

        var removed = _outbound.Complete("peer-a", Array.Empty<CompletedHint>());

        Assert.Equal(0, removed);
        Assert.Single(_outbound.GetAll());
    }

    /// <summary>
    /// Marking nothing sent changes nothing, and a failed row is not marked sent.
    /// True: only a row that was pending when it was sent becomes sent.
    /// False: a row the peer rejected for good reads as delivered.
    /// </summary>
    [Fact]
    public void MarkSent_EmptyListAndFailedRow_ChangeNothing()
    {
        var failed = Stored();
        _outbound.MarkFailed(failed, "bad");

        _outbound.MarkSent(Array.Empty<OutboundHint>(), DateTime.UtcNow);
        _outbound.MarkSent(new[] { failed }, DateTime.UtcNow);

        var stored = _outbound.GetAll().Single();
        Assert.Equal(OutboundState.Failed, stored.State);
        Assert.Null(stored.SentAt);
    }

    /// <summary>
    /// A sent row is listed as waiting from the moment its grace period ends.
    /// True: a row the peer lost is noticed as soon as the grace has passed.
    /// False: a lost row is never checked, or is checked before the peer could finish it.
    /// </summary>
    [Fact]
    public void GetSentBefore_ExactlyAtTheSentTime_IncludesTheRow()
    {
        var row = Stored();
        var sentAt = new DateTime(2026, 10, 3, 13, 0, 0, DateTimeKind.Utc);
        _outbound.MarkSent(new[] { row }, sentAt);

        var atTheTime = _outbound.GetSentBefore("peer-a", sentAt);
        var justBefore = _outbound.GetSentBefore("peer-a", sentAt.AddTicks(-1));
        var otherPeer = _outbound.GetSentBefore("peer-b", sentAt);

        Assert.Single(atTheTime);
        Assert.Empty(justBefore);
        Assert.Empty(otherPeer);
    }

    /// <summary>
    /// Only a sent row is put back by a resend, and it says why.
    /// True: a row the peer lost goes again, and a row still pending keeps its own backoff.
    /// False: a resend resets rows that were never lost.
    /// </summary>
    [Fact]
    public void Resend_SentAndPendingRows_PutsBackOnlyTheSent()
    {
        var sent = Stored(key: "sent");
        _outbound.MarkSent(new[] { sent }, DateTime.UtcNow);
        var pending = Stored(key: "pending");
        var later = DateTime.UtcNow.AddHours(1);
        _outbound.Defer(new[] { pending }, later, "no answer");
        var now = DateTime.UtcNow;

        _outbound.Resend(new[] { sent.Id, pending.Id, long.MaxValue }, now);

        var rows = _outbound.GetAll().ToDictionary(r => r.Key);
        Assert.Equal(OutboundState.Pending, rows["sent"].State);
        Assert.Null(rows["sent"].SentAt);
        Assert.NotNull(rows["sent"].LastError);
        Assert.Equal(1, rows["pending"].Attempts);
        Assert.Equal("no answer", rows["pending"].LastError);
    }

    /// <summary>
    /// Expiry removes old failed rows and never a pending one.
    /// True: a change still owed to a peer is kept however long it waits.
    /// False: a paused peer's backlog is thrown away by upkeep.
    /// </summary>
    [Fact]
    public void ExpireSettled_PendingAndFailedRows_RemovesOnlyTheFailed()
    {
        Stored(key: "pending");
        var failed = Stored(key: "failed");
        _outbound.MarkFailed(failed, "bad");

        var removed = _outbound.ExpireSettled(DateTime.UtcNow.AddDays(1));

        Assert.Equal(1, removed);
        Assert.Equal("pending", Assert.Single(_outbound.GetAll()).Key);
    }

    /// <summary>
    /// A failed row that had been sent is retried as a pending row that was never sent.
    /// True: the retried row is delivered again and not mistaken for one awaiting completion.
    /// False: it keeps a sent time and the recovery check treats it as lost.
    /// </summary>
    [Fact]
    public void RetryFailed_RowThatWasSent_ClearsItsSentTime()
    {
        var row = Stored();
        _outbound.MarkSent(new[] { row }, DateTime.UtcNow);
        _outbound.MarkFailed(row, "bad");

        var retried = _outbound.RetryFailed(DateTime.UtcNow);

        var stored = _outbound.GetAll().Single();
        Assert.Equal(1, retried);
        Assert.Equal(OutboundState.Pending, stored.State);
        Assert.Null(stored.SentAt);
    }

    /// <summary>
    /// A declined row is removed while it carries the stamp that was sent.
    /// True: a hint the peer will not use leaves the queue.
    /// False: declined rows pile up as pending and are sent on every pass.
    /// </summary>
    [Fact]
    public void DeleteSent_RowAsSent_RemovesIt()
    {
        var row = Stored();

        var removed = _outbound.DeleteSent(row);

        Assert.True(removed);
        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// At the cap nothing is trimmed, and one row over it drops the oldest pending row of that peer alone.
    /// True: a paused peer's queue stops at the cap and keeps its newest changes.
    /// False: the queue grows without bound, or the newest edit is the one dropped.
    /// </summary>
    [Fact]
    public void TrimPending_OneRowOverTheCap_DropsTheOldest()
    {
        InsertPending("peer-a", HintProtocol.MaxPendingPerPeer);
        Stored("peer-b", "other");

        var atTheCap = _outbound.TrimPending("peer-a");
        InsertPending("peer-a", 1, startAt: HintProtocol.MaxPendingPerPeer);
        var overTheCap = _outbound.TrimPending("peer-a");

        Assert.Equal(0, atTheCap);
        Assert.Equal(1, overTheCap);
        var left = _outbound.GetAll();
        Assert.Equal(HintProtocol.MaxPendingPerPeer + 1, left.Count);
        Assert.DoesNotContain(left, row => row.Key == "bulk-0");
        Assert.Contains(left, row => row.Key == "bulk-" + HintProtocol.MaxPendingPerPeer.ToString(CultureInfo.InvariantCulture));
        Assert.Contains(left, row => row.PeerKey == "peer-b");
    }

    /// <summary>Writes many pending rows in one transaction, each a second newer than the last.</summary>
    private void InsertPending(string peer, int count, int startAt = 0)
    {
        new QueueStoreProbe(_db.Provider, NullLogger.Instance).RunWrite(conn =>
        {
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO OutboundHints (HintId, PeerKey, Kind, Key, VersionServerId, VersionTimestamp, Recorded, State, Attempts, NextAttempt, CreatedAt)
                VALUES (@hint, @peer, 1, @key, 'me', @at, 1, 0, 0, @at, @at)";
            var hint = cmd.Parameters.Add("@hint", Microsoft.Data.Sqlite.SqliteType.Text);
            var key = cmd.Parameters.Add("@key", Microsoft.Data.Sqlite.SqliteType.Text);
            var at = cmd.Parameters.Add("@at", Microsoft.Data.Sqlite.SqliteType.Text);
            cmd.Parameters.AddWithValue("@peer", peer);
            for (var i = startAt; i < startAt + count; i++)
            {
                var name = "bulk-" + i.ToString(CultureInfo.InvariantCulture);
                hint.Value = "me:" + name;
                key.Value = name;
                at.Value = UtcTime.Format(Edited.AddSeconds(i));
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }
}

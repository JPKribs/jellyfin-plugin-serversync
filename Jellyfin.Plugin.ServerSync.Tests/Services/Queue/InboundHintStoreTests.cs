using System;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// The inbound hint store keeps each received hint whole, hands rows out in the order they came, and changes a row only while it still carries the stamp that was tried.
/// </summary>
public sealed class InboundHintStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly QueueTestDatabase _db = new();
    private readonly InboundHintStore _inbound;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboundHintStoreTests"/> class.
    /// </summary>
    public InboundHintStoreTests()
    {
        _inbound = new InboundHintStore(_db.Provider, NullLogger<InboundHintStore>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private static InboundHint Row(string id = "1", HintKind kind = HintKind.Metadata, string? key = null, DateTime? at = null, string origin = "origin-a", string? itemType = null)
        => InboundHint.FromHint(
            new SyncHint
            {
                HintId = origin + ":" + id,
                OriginServerId = origin,
                Kind = kind,
                Key = key ?? "key-" + id,
                ItemType = itemType,
                VersionServerId = origin,
                VersionTimestamp = at ?? Now.AddMinutes(-5)
            },
            Now);

    private InboundHint Stored(string id = "1", HintKind kind = HintKind.Metadata, string? key = null, DateTime? at = null, string origin = "origin-a", string? itemType = null)
    {
        var row = Row(id, kind, key, at, origin, itemType);
        _inbound.Enqueue(row);
        return row;
    }

    /// <summary>
    /// A server id is stored trimmed and in lower case, and a missing one as empty text.
    /// True: one peer is one origin however it spells its id.
    /// False: a peer slips past its limit by changing the case of its id, or a null id throws.
    /// </summary>
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  AbC  ", "abc")]
    [InlineData("already-lower", "already-lower")]
    public void OriginKey_AnySpelling_IsTrimmedAndLowered(string? given, string expected)
    {
        var key = InboundHintStore.OriginKey(given);

        Assert.Equal(expected, key);
    }

    /// <summary>
    /// A missing row is refused by every call that takes one.
    /// True: a caller's mistake is named.
    /// False: it surfaces as a null reference under the write lock.
    /// </summary>
    [Fact]
    public void Calls_NullRow_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => _inbound.Enqueue(null!));
        Assert.Throws<ArgumentNullException>(() => _inbound.Defer(null!, Now, "error"));
        Assert.Throws<ArgumentNullException>(() => _inbound.Postpone(null!, Now, "busy"));
        Assert.Throws<ArgumentNullException>(() => _inbound.Park(null!, "path:/a", Now, "waiting"));
    }

    /// <summary>
    /// A stored hint reads back with every field it arrived with.
    /// True: the apply works from exactly what the origin sent.
    /// False: a user, path, or stamp is lost between arrival and apply.
    /// </summary>
    [Fact]
    public void Enqueue_NewHint_ReadsBackEveryField()
    {
        var row = InboundHint.FromHint(
            new SyncHint
            {
                HintId = "origin-a:1",
                OriginServerId = "origin-a",
                Kind = HintKind.History,
                Key = "user|item",
                ItemPath = "/media/movies/a.mkv",
                ItemId = "item",
                ItemType = "Movie",
                UserId = "user",
                UserName = "viewer",
                VersionServerId = "origin-a",
                VersionTimestamp = Now.AddMinutes(-5),
                Recorded = false
            },
            Now);

        _inbound.Enqueue(row);

        var stored = Assert.Single(_inbound.GetAll());
        Assert.Equal(row.Id, stored.Id);
        Assert.True(stored.Id > 0);
        Assert.Equal("origin-a:1", stored.HintId);
        Assert.Equal("origin-a", stored.OriginServerId);
        Assert.Equal(HintKind.History, stored.Kind);
        Assert.Equal("user|item", stored.Key);
        Assert.Equal("/media/movies/a.mkv", stored.ItemPath);
        Assert.Equal("item", stored.ItemId);
        Assert.Equal("Movie", stored.ItemType);
        Assert.Equal("user", stored.UserId);
        Assert.Equal("viewer", stored.UserName);
        Assert.Equal("origin-a", stored.VersionServerId);
        Assert.Equal(Now.AddMinutes(-5), stored.VersionTimestamp);
        Assert.False(stored.Recorded);
        Assert.Equal(Now, stored.ReceivedAt);
        Assert.Equal(Now, stored.NextAttempt);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.LastError);
        Assert.Null(stored.ParkedPath);
    }

    /// <summary>
    /// A hint whose next attempt lies before its receipt is due at its receipt.
    /// True: no row is ever due before it arrived.
    /// False: a row with an unset time sorts as due since year one and reads as ancient.
    /// </summary>
    [Fact]
    public void Enqueue_NextAttemptBeforeReceipt_IsDueAtReceipt()
    {
        var row = Row();
        row.NextAttempt = Now.AddDays(-30);

        _inbound.Enqueue(row);

        Assert.Equal(Now, _inbound.GetAll().Single().NextAttempt);
    }

    /// <summary>
    /// An error is stored on arrival only for a hint that arrives parked.
    /// True: a parked hint shows what it waits for, and a queued one shows no error it never had.
    /// False: a fresh hint is listed as failing.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("path:/local/movies/a.mkv", "no local item yet")]
    public void Enqueue_WithAReason_KeepsItOnlyWhenParked(string? parkKey, string? expectedError)
    {
        var row = Row();
        row.ParkedPath = parkKey;
        row.LastError = "no local item yet";

        _inbound.Enqueue(row);

        Assert.Equal(expectedError, _inbound.GetAll().Single().LastError);
    }

    /// <summary>
    /// A newer hint for an object whose row has failed starts its attempts again.
    /// True: a fresh edit gets its full allowance of retries.
    /// False: it inherits the count of the edit it replaced and is dropped early.
    /// </summary>
    [Fact]
    public void Enqueue_NewerHintForAFailingRow_ClearsItsAttempts()
    {
        var first = Stored(key: "item", at: Now.AddMinutes(-5));
        _inbound.Defer(first, Now.AddHours(1), "failed");

        _inbound.Enqueue(Row(key: "item", at: Now.AddMinutes(-1)));

        var stored = Assert.Single(_inbound.GetAll());
        Assert.Equal(0, stored.Attempts);
        Assert.Equal(0, _inbound.CountFailing());
    }

    /// <summary>
    /// A history hint that replaces a waiting one carries the parts of both.
    /// True: a play still waiting to apply is not lost when a favorite arrives for the same item.
    /// False: the later hint applies only its own part and the earlier change waits for a scan.
    /// </summary>
    [Fact]
    public void Enqueue_HistoryPartsForAWaitingRow_AddsTheEarlierParts()
    {
        Stored(kind: HintKind.History, key: "user|item", at: Now.AddMinutes(-5), itemType: "Movie+Played");

        _inbound.Enqueue(Row(kind: HintKind.History, key: "user|item", at: Now.AddMinutes(-1), itemType: "Movie+Favorite"));

        Assert.Equal("Movie+Played+Favorite", _inbound.GetAll().Single().ItemType);
    }

    /// <summary>
    /// A history hint that names no parts replaces the waiting type as it is.
    /// True: a hint from a sender that does not say what changed applies everything, as it always did.
    /// False: it is narrowed to the parts of an older hint and misses a change.
    /// </summary>
    [Fact]
    public void Enqueue_HistoryWithoutParts_ReplacesTheWaitingType()
    {
        Stored(kind: HintKind.History, key: "user|item", at: Now.AddMinutes(-5), itemType: "Movie+Played");

        _inbound.Enqueue(Row(kind: HintKind.History, key: "user|item", at: Now.AddMinutes(-1), itemType: "Movie"));

        Assert.Equal("Movie", _inbound.GetAll().Single().ItemType);
    }

    /// <summary>
    /// An item type with a plus sign is only read as parts for history.
    /// True: a metadata hint's type is stored exactly as sent.
    /// False: types of other kinds are rewritten on arrival.
    /// </summary>
    [Fact]
    public void Enqueue_MetadataTypeForAWaitingRow_IsNotMerged()
    {
        Stored(kind: HintKind.Metadata, key: "item", at: Now.AddMinutes(-5), itemType: "Old+Played");

        _inbound.Enqueue(Row(kind: HintKind.Metadata, key: "item", at: Now.AddMinutes(-1), itemType: "New+Favorite"));

        Assert.Equal("New+Favorite", _inbound.GetAll().Single().ItemType);
    }

    /// <summary>
    /// Due rows come oldest first, up to the limit, and only once their time has come.
    /// True: hints apply in the order they arrived and a deferred one waits.
    /// False: a later edit applies before an earlier one, or a deferred row is tried at once.
    /// </summary>
    [Fact]
    public void GetDue_SeveralRows_ReturnsTheOldestDueUpToTheLimit()
    {
        var first = Stored("1");
        var second = Stored("2");
        Stored("3");
        _inbound.Defer(first, Now.AddMinutes(10), "failed");

        var due = _inbound.GetDue(Now, 1, content: false);
        var atTheDeferredTime = _inbound.GetDue(Now.AddMinutes(10), 10, content: false);

        Assert.Equal(second.Id, Assert.Single(due).Id);
        Assert.Equal(3, atTheDeferredTime.Count);
        Assert.Equal(first.Id, atTheDeferredTime[0].Id);
    }

    /// <summary>
    /// A limit of zero returns nothing.
    /// True: a caller that asks for no rows gets none.
    /// False: zero is read as no limit and the whole queue is loaded.
    /// </summary>
    [Fact]
    public void GetDue_LimitZero_ReturnsNothing()
    {
        Stored();

        var due = _inbound.GetDue(Now, 0, content: false);

        Assert.Empty(due);
    }

    /// <summary>
    /// The newest rows come newest first and the oldest rows oldest first, each up to its limit.
    /// True: the dashboard shows the latest hints and the worker's view shows the next to apply.
    /// False: a long queue hides the rows the reader asked for.
    /// </summary>
    [Fact]
    public void GetRecentAndGetOldest_SeveralRows_OrderFromTheirOwnEnd()
    {
        var first = Stored("1");
        Stored("2");
        var third = Stored("3");

        var recent = _inbound.GetRecent(2);
        var oldest = _inbound.GetOldest(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal(third.Id, recent[0].Id);
        Assert.Equal(2, oldest.Count);
        Assert.Equal(first.Id, oldest[0].Id);
    }

    /// <summary>
    /// An empty queue counts zero everywhere and has nothing to retry.
    /// True: the dashboard of a quiet server shows zeros.
    /// False: an empty table throws or reports phantom rows.
    /// </summary>
    [Fact]
    public void Counts_EmptyQueue_AreZero()
    {
        Assert.Equal(0, _inbound.Count());
        Assert.Equal(0, _inbound.CountFailing());
        Assert.Equal(0, _inbound.CountForOrigin("origin-a"));
        Assert.Equal(0, _inbound.RetryFailing(Now));
        Assert.Empty(_inbound.GetFailing());
        Assert.Empty(_inbound.GetForOrigin("origin-a"));
        Assert.Equal(0, _inbound.DeleteForOrigin("origin-a"));
        Assert.Null(_inbound.Get(1));
    }

    /// <summary>
    /// A row is deleted by id once.
    /// True: the operator's discard removes the row and says so.
    /// False: a second press reports a row that is no longer there as removed.
    /// </summary>
    [Fact]
    public void Delete_ExistingRow_RemovesItOnce()
    {
        var row = Stored();

        var first = _inbound.Delete(row.Id);
        var second = _inbound.Delete(row.Id);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(0, _inbound.Count());
    }

    /// <summary>
    /// A row is removed when the apply worked from a stamp at least as new as the row's.
    /// True: a finished row leaves the queue.
    /// False: a completed hint stays and is applied again on every pass.
    /// </summary>
    [Fact]
    public void Remove_AppliedFromALaterStamp_RemovesTheRow()
    {
        var row = Stored(at: Now.AddMinutes(-5));

        var removed = _inbound.Remove(row.Id, Now);

        Assert.True(removed);
        Assert.Equal(0, _inbound.Count());
    }

    /// <summary>
    /// A postponed row waits without counting an attempt, and says why.
    /// True: a row held up by the scheduled task keeps its full allowance of retries.
    /// False: a busy server burns a hint's retries and drops it.
    /// </summary>
    [Fact]
    public void Postpone_DueRow_WaitsWithoutCountingAnAttempt()
    {
        var row = Stored();

        _inbound.Postpone(row, Now.AddMinutes(1), "the scheduled task is running");

        var stored = _inbound.GetAll().Single();
        Assert.Equal(0, stored.Attempts);
        Assert.Equal("the scheduled task is running", stored.LastError);
        Assert.Equal(Now.AddMinutes(1), stored.NextAttempt);
        Assert.Empty(_inbound.GetDue(Now, 10, content: false));
    }

    /// <summary>
    /// A park key that is empty or blank is refused.
    /// True: a row is never parked under a key nothing can wake.
    /// False: a blank key parks rows that then wait out their whole window.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Park_BlankKey_Throws(string parkKey)
    {
        var row = Stored();

        Assert.Throws<ArgumentException>(() => _inbound.Park(row, parkKey, Now.AddHours(6), "waiting"));
    }

    /// <summary>
    /// A parked row carries its key, its reason, and its deadline, counts no attempt, and leaves the origin's count.
    /// True: a hint waiting for its item takes none of the origin's room and none of its own retries.
    /// False: parked hints fill the queue and the origin is told this server is busy.
    /// </summary>
    [Fact]
    public void Park_DueRow_SetsItAsideWithoutCountingIt()
    {
        var row = Stored();

        _inbound.Park(row, "path:/local/movies/a.mkv", Now.AddHours(6), "no local item yet");

        var stored = _inbound.GetAll().Single();
        Assert.Equal("path:/local/movies/a.mkv", stored.ParkedPath);
        Assert.Equal("no local item yet", stored.LastError);
        Assert.Equal(Now.AddHours(6), stored.NextAttempt);
        Assert.Equal(0, stored.Attempts);
        Assert.Equal(0, _inbound.CountForOrigin("origin-a"));
    }

    /// <summary>
    /// Waking a park key makes its rows due with the wait and its reason cleared.
    /// True: once the item arrives, its hints apply at once and no longer read as waiting.
    /// False: a woken row still shows the old reason, or stays parked.
    /// </summary>
    [Fact]
    public void WakeParked_ParkedRow_IsDueWithItsReasonCleared()
    {
        var row = Stored();
        _inbound.Park(row, "path:/local/movies/a.mkv", Now.AddHours(6), "no local item yet");

        var woken = _inbound.WakeParked("path:/local/movies/a.mkv", Now.AddMinutes(1));

        var stored = _inbound.GetAll().Single();
        Assert.Equal(1, woken);
        Assert.Null(stored.ParkedPath);
        Assert.Null(stored.LastError);
        Assert.Equal(Now.AddMinutes(1), stored.NextAttempt);
    }

    /// <summary>
    /// Trimming to zero removes every parked row of that origin, and nothing else.
    /// True: the cap on parked rows touches neither waiting work nor another origin.
    /// False: a trim deletes hints that are about to apply, or another peer's.
    /// </summary>
    [Fact]
    public void TrimParked_KeepNone_RemovesOnlyThatOriginsParkedRows()
    {
        var parked = Stored("1");
        _inbound.Park(parked, "path:/a", Now.AddHours(6), "waiting");
        Stored("2");
        var other = Stored("3", origin: "origin-b");
        _inbound.Park(other, "path:/b", Now.AddHours(6), "waiting");

        var removed = _inbound.TrimParked("ORIGIN-A", 0);

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "origin-a:2", "origin-b:3" }, _inbound.GetAll().Select(r => r.HintId).ToArray());
    }

    /// <summary>
    /// Retrying the failing rows leaves a row that never failed exactly as it was.
    /// True: the operator's retry does not bring forward a row that is waiting for another reason.
    /// False: a postponed or parked row is forced due by a press meant for failures.
    /// </summary>
    [Fact]
    public void RetryFailing_RowThatNeverFailed_KeepsItsTime()
    {
        var waiting = Stored();
        _inbound.Postpone(waiting, Now.AddMinutes(30), "busy");

        var retried = _inbound.RetryFailing(Now);

        Assert.Equal(0, retried);
        Assert.Equal(Now.AddMinutes(30), _inbound.GetAll().Single().NextAttempt);
    }
}

using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public sealed class HintStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly OutboundHintStore _outbound;
    private readonly InboundHintStore _inbound;
    private readonly VersionStore _versions;

    public HintStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        var provider = new FixedProvider(_database);
        _outbound = new OutboundHintStore(provider, NullLogger<OutboundHintStore>.Instance);
        _inbound = new InboundHintStore(provider, NullLogger<InboundHintStore>.Instance);
        _versions = new VersionStore(provider, NullLogger<VersionStore>.Instance);
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

    private static OutboundHint Row(string peer = "peer-a", string key = "u|i", int minute = 0) => new()
    {
        PeerKey = peer,
        Kind = HintKind.History,
        Key = key,
        ItemPath = "/local/Movies/A/a.mp4",
        VersionServerId = "me",
        VersionTimestamp = new DateTime(2026, 10, 3, 12, minute, 0, DateTimeKind.Utc)
    };

    /// <summary>
    /// Two edits before delivery are one row carrying the newer version, and the hint id names the row.
    /// True: a burst of edits costs one delivery.
    /// False: every tick of playback progress would be its own hint.
    /// </summary>
    [Fact]
    public void Enqueue_CoalescesOnPeerKindKey()
    {
        var first = Row(minute: 1);
        _outbound.Enqueue(first, "me");
        var second = Row(minute: 2);
        _outbound.Enqueue(second, "me");

        var rows = _outbound.GetAll();
        Assert.Single(rows);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal($"me:{first.Id}", rows[0].HintId);
        Assert.Equal(2, rows[0].VersionTimestamp.Minute);
    }

    /// <summary>
    /// Rows go pending, then sent, then away once the peer completes them.
    /// True: a finished hint leaves no trace.
    /// False: the queue grows forever.
    /// </summary>
    [Fact]
    public void StateTransitions_PendingSentComplete()
    {
        var row = Row(minute: 1);
        _outbound.Enqueue(row, "me");
        var now = DateTime.UtcNow;

        Assert.Single(_outbound.GetDue("peer-a", now, 10));
        _outbound.MarkSent(new[] { row.Id }, now);
        Assert.Empty(_outbound.GetDue("peer-a", now, 10));
        Assert.Equal(OutboundState.Sent, _outbound.GetAll()[0].State);

        var removed = _outbound.Complete(row.PeerKey, new[] { new CompletedHint { HintId = row.HintId, VersionTimestamp = row.VersionTimestamp } });
        Assert.Equal(1, removed);
        Assert.Empty(_outbound.GetAll());
    }

    /// <summary>
    /// An edit made after delivery puts the row back to pending, and a late completion of the earlier
    /// version does not remove it.
    /// True: nothing is lost between a delivery and its completion.
    /// False: the second edit would wait for the next full scan.
    /// </summary>
    [Fact]
    public void Complete_WithOlderVersion_KeepsRependedRow()
    {
        var row = Row(minute: 1);
        _outbound.Enqueue(row, "me");
        _outbound.MarkSent(new[] { row.Id }, DateTime.UtcNow);

        _outbound.Enqueue(Row(minute: 2), "me");
        var again = _outbound.GetAll()[0];
        Assert.Equal(OutboundState.Pending, again.State);
        Assert.Null(again.SentAt);

        var removed = _outbound.Complete(row.PeerKey, new[] { new CompletedHint { HintId = row.HintId, VersionTimestamp = row.VersionTimestamp } });
        Assert.Equal(0, removed);
        Assert.Single(_outbound.GetAll());
    }

    /// <summary>
    /// A failed delivery defers the row with its error and counts the attempt. A malformed answer
    /// parks it for good.
    /// True: the dashboard can show why a peer is behind.
    /// False: failures would be silent.
    /// </summary>
    [Fact]
    public void Defer_And_Fail_RecordWhy()
    {
        var row = Row();
        _outbound.Enqueue(row, "me");
        var later = DateTime.UtcNow.AddMinutes(5);

        _outbound.Defer(new[] { row.Id }, later, "peer answered 503");
        var deferred = _outbound.GetAll()[0];
        Assert.Equal(1, deferred.Attempts);
        Assert.Equal("peer answered 503", deferred.LastError);
        Assert.Empty(_outbound.GetDue("peer-a", DateTime.UtcNow, 10));
        Assert.Single(_outbound.GetDue("peer-a", later.AddSeconds(1), 10));

        _outbound.MarkFailed(row.Id, "bad");
        Assert.Equal(OutboundState.Failed, _outbound.GetAll()[0].State);
        Assert.Empty(_outbound.GetDue("peer-a", later.AddHours(1), 10));
    }

    /// <summary>
    /// Sent rows older than the grace period are found, and a resend puts them back to pending.
    /// True: work a peer lost is recovered through its queue status.
    /// False: a crash on the peer after accepting would lose the hint until the next scan.
    /// </summary>
    [Fact]
    public void LostWork_IsResent()
    {
        var row = Row();
        _outbound.Enqueue(row, "me");
        var sentAt = DateTime.UtcNow.AddHours(-2);
        _outbound.MarkSent(new[] { row.Id }, sentAt);

        var stale = _outbound.GetSentBefore("peer-a", DateTime.UtcNow.AddHours(-1));
        Assert.Single(stale);
        Assert.Empty(_outbound.GetSentBefore("peer-a", DateTime.UtcNow.AddHours(-3)));

        _outbound.Resend(stale.Select(r => r.Id), DateTime.UtcNow);
        var again = _outbound.GetAll()[0];
        Assert.Equal(OutboundState.Pending, again.State);
        Assert.Single(_outbound.GetDue("peer-a", DateTime.UtcNow, 10));
    }

    /// <summary>
    /// Inbound rows coalesce on origin, kind, and key and are removed only while they still carry the version they were applied from.
    /// True: a notice that arrives during an apply is applied again with its newer version.
    /// False: the newer edit would be swallowed by the earlier apply's completion.
    /// </summary>
    [Fact]
    public void Inbound_CoalescesAndRemovesByVersion()
    {
        // The origin reuses one hint id per object, so both notices carry "a:1" and only the version tells
        // them apart. Both versions lie in the past, since a version ahead of the receipt is read as then.
        var now = DateTime.UtcNow;
        var older = now.AddMinutes(-2);
        var newer = now.AddMinutes(-1);
        var first = InboundHint.FromHint(new SyncHint { HintId = "a:1", OriginServerId = "a", Kind = HintKind.History, Key = "u|i", VersionTimestamp = older }, now);
        _inbound.Enqueue(first);
        var second = InboundHint.FromHint(new SyncHint { HintId = "a:1", OriginServerId = "a", Kind = HintKind.History, Key = "u|i", VersionTimestamp = newer }, now);
        _inbound.Enqueue(second);

        Assert.Equal(1, _inbound.Count());
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(newer, _inbound.GetAll()[0].VersionTimestamp);

        // An apply that worked from the older version leaves the refreshed row for another pass.
        Assert.False(_inbound.Remove(first.Id, older));
        Assert.Equal(1, _inbound.Count());
        Assert.True(_inbound.Remove(first.Id, newer));
        Assert.Equal(0, _inbound.Count());
    }

    /// <summary>
    /// A hint carries whether it is a hand made edit or a provider's work, on both ends, and a row that
    /// gathers both kinds of change is a hand made edit.
    /// True: a scan's poster is applied only where nothing was chosen by hand, and one hand made edit in the window counts.
    /// False: provider work would look like an edit, or an edit gathered behind provider work would be downgraded.
    /// </summary>
    [Fact]
    public void Recorded_RoundTripsAndHandEditsWin()
    {
        var now = DateTime.UtcNow;
        var provider = new OutboundHint { PeerKey = "peer-a", Kind = HintKind.Metadata, Key = "item", VersionServerId = "me", VersionTimestamp = now, Recorded = false };
        _outbound.Enqueue(provider, "me");
        Assert.False(_outbound.GetAll()[0].Recorded);
        Assert.False(provider.ToHint("me").Recorded);

        var edit = new OutboundHint { PeerKey = "peer-a", Kind = HintKind.Metadata, Key = "item", VersionServerId = "me", VersionTimestamp = now.AddSeconds(1), Recorded = true };
        _outbound.Enqueue(edit, "me");
        Assert.True(_outbound.GetAll()[0].Recorded);
        var again = new OutboundHint { PeerKey = "peer-a", Kind = HintKind.Metadata, Key = "item", VersionServerId = "me", VersionTimestamp = now.AddSeconds(2), Recorded = false };
        _outbound.Enqueue(again, "me");
        Assert.True(_outbound.GetAll()[0].Recorded);

        _inbound.Enqueue(InboundHint.FromHint(new SyncHint { HintId = "a:1", OriginServerId = "a", Kind = HintKind.Metadata, Key = "k", VersionTimestamp = now, Recorded = false }, now));
        Assert.False(_inbound.GetAll()[0].Recorded);
        _inbound.Enqueue(InboundHint.FromHint(new SyncHint { HintId = "a:1", OriginServerId = "a", Kind = HintKind.Metadata, Key = "k", VersionTimestamp = now.AddSeconds(1), Recorded = true }, now));
        Assert.True(_inbound.GetAll()[0].Recorded);
    }

    /// <summary>
    /// A deferred inbound row waits out its delay and keeps the error.
    /// True: a flaky origin is retried later, not spun on.
    /// False: the worker would loop on the same row.
    /// </summary>
    [Fact]
    public void Inbound_Defer_WaitsAndKeepsError()
    {
        var now = DateTime.UtcNow;
        var row = InboundHint.FromHint(new SyncHint { HintId = "a:1", OriginServerId = "a", Kind = HintKind.History, Key = "u|i", VersionTimestamp = now }, now);
        _inbound.Enqueue(row);
        _inbound.Defer(row.Id, now.AddMinutes(1), "origin unreachable");

        Assert.Empty(_inbound.GetDue(now, 10, content: false));
        var due = _inbound.GetDue(now.AddMinutes(2), 10, content: false);
        Assert.Single(due);
        Assert.Equal(1, due[0].Attempts);
        Assert.Equal("origin unreachable", due[0].LastError);
    }

    /// <summary>
    /// Versions are stored per kind and key and replaced on set.
    /// True: a later decision sees the version the last apply left.
    /// False: conflict decisions would run against stale versions.
    /// </summary>
    [Fact]
    public void Versions_RoundTrip()
    {
        Assert.Null(_versions.Get(HintKind.History, "k"));
        _versions.Set(new ObjectVersion { Kind = HintKind.History, Key = "k", ServerId = "a", Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        _versions.Set(new ObjectVersion { Kind = HintKind.History, Key = "k", ServerId = "b", Timestamp = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) });

        var stored = _versions.Get(HintKind.History, "k");
        Assert.Equal("b", stored?.ServerId);
        Assert.Equal(2, stored?.Timestamp.Day);
        Assert.Single(_versions.GetMany(HintKind.History, new[] { "k", "missing" }));
    }

    /// <summary>
    /// A stored version never lies ahead of this server's clock.
    /// True: nothing in the store can beat every later real edit.
    /// False: a forged stamp, once stored, wins every conflict forever.
    /// </summary>
    [Fact]
    public void Versions_NeverStoreTheFuture()
    {
        _versions.Set(new ObjectVersion { Kind = HintKind.Metadata, Key = "k", ServerId = "peer", Timestamp = new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        var stored = _versions.Get(HintKind.Metadata, "k");
        Assert.NotNull(stored);
        Assert.True(stored!.Timestamp <= DateTime.UtcNow.AddSeconds(5));
        Assert.True(stored.Timestamp >= DateTime.UtcNow.AddMinutes(-1));
    }

    /// <summary>
    /// A completion only removes rows queued for the peer that reports it.
    /// True: a peer can finish its own work and nothing else, whatever ids it names.
    /// False: any peer could clear another peer's pending hints by guessing ids.
    /// </summary>
    [Fact]
    public void Complete_OnlyTouchesTheReportingPeersRows()
    {
        var mine = Row("peer-a");
        var theirs = Row("peer-b");
        _outbound.Enqueue(mine, "me");
        _outbound.Enqueue(theirs, "me");

        var removed = _outbound.Complete("peer-b", new[] { new CompletedHint { HintId = mine.HintId, VersionTimestamp = new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc) } });
        Assert.Equal(0, removed);
        Assert.Equal(2, _outbound.GetRecent(10).Count);

        removed = _outbound.Complete("peer-a", new[] { new CompletedHint { HintId = mine.HintId, VersionTimestamp = mine.VersionTimestamp } });
        Assert.Equal(1, removed);
        Assert.Single(_outbound.GetRecent(10));
    }

    /// <summary>
    /// The item's type rides along on both ends.
    /// True: the queue view can show an episode wide and a film tall.
    /// False: every row is a poster.
    /// </summary>
    [Fact]
    public void ItemType_RoundTrips()
    {
        var row = Row();
        row.ItemType = "Episode";
        _outbound.Enqueue(row, "me");
        Assert.Equal("Episode", _outbound.GetRecent(1)[0].ItemType);

        var hint = row.ToHint("me");
        Assert.Equal("Episode", hint.ItemType);
        _inbound.Enqueue(InboundHint.FromHint(hint, DateTime.UtcNow));
        Assert.Equal("Episode", _inbound.GetAll()[0].ItemType);
    }

    /// <summary>
    /// A received hint never carries a version ahead of this server's clock.
    /// True: the row the handlers decide on is already bounded.
    /// False: the far future reaches the conflict decision and the version store.
    /// </summary>
    [Fact]
    public void Inbound_FromHint_BoundsTheVersion()
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var hint = Row().ToHint("me");
        hint.VersionTimestamp = new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now, InboundHint.FromHint(hint, now).VersionTimestamp);
    }

    /// <summary>
    /// One origin cannot fill the inbound queue without bound.
    /// True: past the cap its oldest rows go and the newest stay.
    /// False: a hostile or runaway peer grows the database forever.
    /// </summary>
    [Fact]
    public void Inbound_CapsRowsPerOrigin()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < HintProtocol.MaxPendingPerPeer + 5; i++)
        {
            var hint = Row(key: "k" + i).ToHint("origin");
            hint.HintId = "origin:" + i;
            _inbound.Enqueue(InboundHint.FromHint(hint, now));
        }

        var rows = _inbound.GetAll();
        Assert.Equal(HintProtocol.MaxPendingPerPeer, rows.Count);
        Assert.DoesNotContain(rows, r => r.Key == "k0");
        Assert.Contains(rows, r => r.Key == "k" + (HintProtocol.MaxPendingPerPeer + 4));
    }

    /// <summary>
    /// Content rows and the rest are read as separate lanes.
    /// True: a long download never holds up a history or metadata hint behind it.
    /// False: one lane would starve the other.
    /// </summary>
    [Fact]
    public void Inbound_LanesSplitContentFromTheRest()
    {
        var now = DateTime.UtcNow;
        _inbound.Enqueue(InboundHint.FromHint(new SyncHint { HintId = "a:1", OriginServerId = "a", Kind = HintKind.Content, Key = "f", VersionTimestamp = now }, now));
        _inbound.Enqueue(InboundHint.FromHint(new SyncHint { HintId = "a:2", OriginServerId = "a", Kind = HintKind.History, Key = "u|i", VersionTimestamp = now }, now));

        Assert.Single(_inbound.GetDue(now, 10, content: true));
        Assert.Equal(HintKind.Content, _inbound.GetDue(now, 10, content: true)[0].Kind);
        Assert.Single(_inbound.GetDue(now, 10, content: false));
        Assert.Equal(HintKind.History, _inbound.GetDue(now, 10, content: false)[0].Kind);
    }

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }
}

using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.HistorySync;

/// <summary>
/// Tests for the receiving side of history negotiation against an in memory store.
/// </summary>
public class PeerHistoryServiceTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly DateTime Noon = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class MemoryStore : IUserHistoryStore
    {
        public Dictionary<(Guid, Guid), PeerHistoryState> Rows { get; } = new();

        public bool FailWrites { get; set; }

        public int Writes { get; private set; }

        public PeerHistoryState? Read(Guid userId, Guid itemId)
            => Rows.TryGetValue((userId, itemId), out var s) ? Clone(s) : null;

        public bool Write(Guid userId, Guid itemId, PeerHistoryState state)
        {
            Writes++;
            if (FailWrites)
            {
                return false;
            }

            var row = Rows[(userId, itemId)];
            if (state.Played.HasValue)
            {
                row.Played = state.Played;
            }

            if (state.PlayCount.HasValue)
            {
                row.PlayCount = state.PlayCount;
            }

            if (state.PlaybackPositionTicks.HasValue)
            {
                row.PlaybackPositionTicks = state.PlaybackPositionTicks;
            }

            if (state.LastPlayedDate.HasValue)
            {
                row.LastPlayedDate = state.LastPlayedDate;
            }
            else if (state.Played == false)
            {
                row.LastPlayedDate = null;
            }

            if (state.IsFavorite.HasValue)
            {
                row.IsFavorite = state.IsFavorite;
            }

            return true;
        }

        private static PeerHistoryState Clone(PeerHistoryState s) => new()
        {
            Played = s.Played,
            PlayCount = s.PlayCount,
            PlaybackPositionTicks = s.PlaybackPositionTicks,
            LastPlayedDate = s.LastPlayedDate,
            IsFavorite = s.IsFavorite
        };
    }

    private static PeerHistoryState State(bool played, int count, DateTime? last, bool favorite) => new()
    {
        Played = played,
        PlayCount = count,
        PlaybackPositionTicks = 0,
        LastPlayedDate = last,
        IsFavorite = favorite
    };

    private static (PeerHistoryService Service, MemoryStore Store) Build(PeerHistoryState? row)
    {
        var store = new MemoryStore();
        if (row is not null)
        {
            store.Rows[(User, Item)] = row;
        }

        return (new PeerHistoryService(store, NullLogger<PeerHistoryService>.Instance), store);
    }

    private static PeerHistoryEntry Entry(PeerHistoryState? expected, PeerHistoryState proposed) => new()
    {
        UserId = User.ToString("N"),
        ItemId = Item.ToString("N"),
        Expected = expected,
        Proposed = proposed
    };

    /// <summary>
    /// A matching expectation lets the proposal through, and the answer carries the verified state.
    /// </summary>
    [Fact]
    public void Handle_ExpectationMatches_WritesAndVerifies()
    {
        var (service, store) = Build(State(false, 0, null, false));

        var result = service.Handle(Entry(State(false, 0, null, false), State(true, 1, Noon, true)));

        Assert.Equal(PeerHistoryOutcome.Applied, result.Outcome);
        Assert.Equal(1, store.Writes);
        Assert.True(result.Current!.Played);
        Assert.True(store.Rows[(User, Item)].IsFavorite);
    }

    /// <summary>
    /// A live state that moved since the sender read it is reported back untouched.
    /// True: a play on this server between the sender's refresh and its sync is never overwritten.
    /// </summary>
    [Fact]
    public void Handle_ExpectationMismatch_IsStaleAndWritesNothing()
    {
        var (service, store) = Build(State(true, 2, Noon.AddHours(1), false));

        var result = service.Handle(Entry(State(false, 0, null, false), State(true, 1, Noon, true)));

        Assert.Equal(PeerHistoryOutcome.Stale, result.Outcome);
        Assert.Equal(0, store.Writes);
        Assert.Equal(2, result.Current!.PlayCount);
    }

    /// <summary>
    /// A proposal this server already holds is answered without a write.
    /// </summary>
    [Fact]
    public void Handle_AlreadyHeld_IsUnchanged()
    {
        var (service, store) = Build(State(true, 1, Noon, true));

        var result = service.Handle(Entry(State(false, 0, null, false), State(true, 1, Noon, true)));

        Assert.Equal(PeerHistoryOutcome.Unchanged, result.Outcome);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>
    /// Unknown ids and missing rows are NotFound, and a bad id string never reaches the store.
    /// </summary>
    [Fact]
    public void Handle_MissingOrInvalidIds_AreNotFound()
    {
        var (service, _) = Build(null);

        var missing = service.Handle(Entry(null, State(true, 1, Noon, true)));
        var invalid = service.Handle(new PeerHistoryEntry { UserId = "nope", ItemId = "nope", Proposed = State(true, 1, Noon, true) });

        Assert.Equal(PeerHistoryOutcome.NotFound, missing.Outcome);
        Assert.Equal(PeerHistoryOutcome.NotFound, invalid.Outcome);
    }

    /// <summary>
    /// A write the store refuses is reported as Failed with a reason.
    /// </summary>
    [Fact]
    public void Handle_WriteFails_IsFailed()
    {
        var (service, store) = Build(State(false, 0, null, false));
        store.FailWrites = true;

        var result = service.Handle(Entry(State(false, 0, null, false), State(true, 1, Noon, true)));

        Assert.Equal(PeerHistoryOutcome.Failed, result.Outcome);
        Assert.Contains("write", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unplayed proposal clears the stored date and still verifies.
    /// </summary>
    [Fact]
    public void Handle_UnplayedProposal_ClearsDate()
    {
        var (service, store) = Build(State(true, 1, Noon, false));

        var result = service.Handle(Entry(State(true, 1, Noon, false), State(false, 0, null, false)));

        Assert.Equal(PeerHistoryOutcome.Applied, result.Outcome);
        Assert.Null(store.Rows[(User, Item)].LastPlayedDate);
    }

    /// <summary>
    /// Negotiate answers every entry in order and counts outcomes.
    /// </summary>
    [Fact]
    public void Negotiate_AnswersEveryEntryInOrder()
    {
        var (service, _) = Build(State(false, 0, null, false));
        var request = new PeerHistoryRequest
        {
            SenderServerId = "peer",
            Items =
            {
                Entry(State(false, 0, null, false), State(true, 1, Noon, false)),
                new PeerHistoryEntry { UserId = "x", ItemId = "y", Proposed = State(true, 1, Noon, false) }
            }
        };

        var response = service.Negotiate(request);

        Assert.Equal(2, response.Items.Count);
        Assert.Equal(PeerHistoryOutcome.Applied, response.Items[0].Outcome);
        Assert.Equal(PeerHistoryOutcome.NotFound, response.Items[1].Outcome);
    }
}

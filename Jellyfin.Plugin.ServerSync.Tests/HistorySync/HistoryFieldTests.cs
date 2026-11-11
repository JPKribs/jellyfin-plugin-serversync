using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.HistorySync;

/// <summary>
/// The History settings choose which parts of watch history are copied. A part that is switched off
/// is never written on this server, never proposed to the other, and never counts as work.
/// </summary>
public class HistoryFieldTests
{
    private static readonly DateTime Noon = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static HistorySyncItem Differing(bool negotiate = false) => new()
    {
        SourceUserId = "src-user",
        LocalUserId = "loc-user",
        SourceItemId = "item-1",
        NegotiateWithSource = negotiate,
        SourceIsPlayed = true,
        SourcePlayCount = 3,
        SourcePlaybackPositionTicks = 500,
        SourceLastPlayedDate = Noon,
        SourceIsFavorite = true,
        LocalIsPlayed = false,
        LocalPlayCount = 1,
        LocalPlaybackPositionTicks = 100,
        LocalLastPlayedDate = Noon.AddDays(-1),
        LocalIsFavorite = false
    };

    [Fact]
    public void AllOn_MergesEveryPart()
    {
        var item = Differing();
        using (HistorySyncMergeService.WithFields(HistoryFields.All))
        {
            HistorySyncMergeService.MergeHistoryData(item);
        }

        Assert.True(item.MergedIsPlayed);
        Assert.Equal(3, item.MergedPlayCount);
        Assert.Equal(500, item.MergedPlaybackPositionTicks);
        Assert.Equal(Noon, item.MergedLastPlayedDate);
        Assert.True(item.MergedIsFavorite);
    }

    [Theory]
    [InlineData(HistoryFields.Played)]
    [InlineData(HistoryFields.Position)]
    [InlineData(HistoryFields.PlayCount)]
    [InlineData(HistoryFields.LastPlayed)]
    [InlineData(HistoryFields.Favorite)]
    public void OnePartOff_ThatPartHasNoMergedValueAndTheRestStillMerge(HistoryFields off)
    {
        var item = Differing();
        using (HistorySyncMergeService.WithFields(HistoryFields.All & ~off))
        {
            HistorySyncMergeService.MergeHistoryData(item);

            Assert.Equal(off != HistoryFields.Played, item.MergedIsPlayed.HasValue);
            Assert.Equal(off != HistoryFields.Position, item.MergedPlaybackPositionTicks.HasValue);
            Assert.Equal(off != HistoryFields.PlayCount, item.MergedPlayCount.HasValue);
            Assert.Equal(off != HistoryFields.LastPlayed, item.MergedLastPlayedDate.HasValue);
            Assert.Equal(off != HistoryFields.Favorite, item.MergedIsFavorite.HasValue);
            Assert.True(HistorySyncMergeService.HasChangesToSync(item));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EverythingOff_NothingIsWork(bool twoWay)
    {
        var item = Differing(twoWay);
        using (HistorySyncMergeService.WithFields(HistoryFields.None))
        {
            HistorySyncMergeService.MergeHistoryData(item);

            Assert.True(PeerHistoryNegotiator.IsEmpty(PeerHistoryNegotiator.MergedStateOf(item)));
            Assert.False(HistorySyncMergeService.HasChangesToSync(item));
        }
    }

    [Fact]
    public void FavoriteOff_AFavoriteThatDiffersAloneIsNotWork_OnEitherSide()
    {
        var item = new HistorySyncItem
        {
            SourceUserId = "s", LocalUserId = "l", SourceItemId = "i", NegotiateWithSource = true,
            SourceIsPlayed = true, LocalIsPlayed = true,
            SourcePlayCount = 1, LocalPlayCount = 1,
            SourceLastPlayedDate = Noon, LocalLastPlayedDate = Noon,
            SourceIsFavorite = false, LocalIsFavorite = true
        };

        using (HistorySyncMergeService.WithFields(HistoryFields.All & ~HistoryFields.Favorite))
        {
            HistorySyncMergeService.MergeHistoryData(item);

            Assert.False(HistorySyncMergeService.HasChangesToSync(item));
            Assert.Null(PeerHistoryNegotiator.MergedStateOf(item).IsFavorite);
        }
    }

    [Fact]
    public void DateOff_ADateThatDiffersAloneIsNotWork()
    {
        var item = new HistorySyncItem
        {
            SourceUserId = "s", LocalUserId = "l", SourceItemId = "i", NegotiateWithSource = true,
            SourceIsPlayed = true, LocalIsPlayed = true,
            SourcePlayCount = 1, LocalPlayCount = 1,
            SourceLastPlayedDate = Noon, LocalLastPlayedDate = Noon.AddHours(3)
        };

        using (HistorySyncMergeService.WithFields(HistoryFields.All & ~HistoryFields.LastPlayed))
        {
            HistorySyncMergeService.MergeHistoryData(item);

            Assert.False(HistorySyncMergeService.HasChangesToSync(item));
        }
    }

    [Fact]
    public void DateOff_AnItemMarkedUnplayedHereAloneStillWinsAgainstTheAgreedState()
    {
        // Both servers agreed the item was played. It is then marked unplayed here only. With the
        // date left out of the comparison the source has not moved, so the local change wins.
        using (HistorySyncMergeService.WithFields(HistoryFields.All & ~HistoryFields.LastPlayed))
        {
            var item = new HistorySyncItem
            {
                SourceUserId = "s", LocalUserId = "l", SourceItemId = "i", NegotiateWithSource = true,
                SourceIsPlayed = true, SourceLastPlayedDate = Noon, SourcePlayCount = 1,
                LocalIsPlayed = false, LocalLastPlayedDate = null, LocalPlayCount = 1,
                MergedIsPlayed = true, MergedPlayCount = 1
            };
            item.RecordNegotiatedBase(Noon);

            HistorySyncMergeService.MergeHistoryData(item);

            Assert.False(item.MergedIsPlayed);
            Assert.True(HistorySyncMergeService.SourceDiffersFromMerged(item));
        }
    }

    [Fact]
    public void AfterAnApply_ThePartSwitchedOffKeepsItsLocalReading()
    {
        var item = Differing();
        using (HistorySyncMergeService.WithFields(HistoryFields.All & ~HistoryFields.Favorite))
        {
            HistorySyncMergeService.MergeHistoryData(item);
            PeerHistoryNegotiator.ApplyLocalState(item, PeerHistoryNegotiator.MergedStateOf(item));
        }

        Assert.True(item.LocalIsPlayed);
        Assert.False(item.LocalIsFavorite);
    }

    [Fact]
    public void AWriteFromAnotherServer_LeavesThePartSwitchedOffHereAlone()
    {
        var user = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var store = new Store();
        store.Rows[(user, itemId)] = new PeerHistoryState { Played = false, PlayCount = 0, PlaybackPositionTicks = 0, IsFavorite = true };
        var service = new PeerHistoryService(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<PeerHistoryService>.Instance);

        PeerHistoryResult result;
        using (HistorySyncMergeService.WithFields(HistoryFields.All & ~HistoryFields.Favorite))
        {
            result = service.Handle(new PeerHistoryEntry
            {
                UserId = user.ToString("N"),
                ItemId = itemId.ToString("N"),
                Expected = new PeerHistoryState { Played = false, PlayCount = 0, PlaybackPositionTicks = 0, IsFavorite = true },
                Proposed = new PeerHistoryState { Played = true, PlayCount = 1, PlaybackPositionTicks = 0, LastPlayedDate = Noon, IsFavorite = false }
            });
        }

        Assert.Equal(PeerHistoryOutcome.Applied, result.Outcome);
        Assert.True(store.Rows[(user, itemId)].Played);
        Assert.True(store.Rows[(user, itemId)].IsFavorite);
    }

    private sealed class Store : IUserHistoryStore
    {
        public Dictionary<(Guid, Guid), PeerHistoryState> Rows { get; } = new();

        public PeerHistoryState? Read(Guid userId, Guid itemId)
            => Rows.TryGetValue((userId, itemId), out var s)
                ? new PeerHistoryState { Played = s.Played, PlayCount = s.PlayCount, PlaybackPositionTicks = s.PlaybackPositionTicks, LastPlayedDate = s.LastPlayedDate, IsFavorite = s.IsFavorite }
                : null;

        public bool Write(Guid userId, Guid itemId, PeerHistoryState state)
        {
            var row = Rows[(userId, itemId)];
            row.Played = state.Played ?? row.Played;
            row.PlayCount = state.PlayCount ?? row.PlayCount;
            row.PlaybackPositionTicks = state.PlaybackPositionTicks ?? row.PlaybackPositionTicks;
            row.LastPlayedDate = state.LastPlayedDate ?? (state.Played == false ? null : row.LastPlayedDate);
            row.IsFavorite = state.IsFavorite ?? row.IsFavorite;
            return true;
        }
    }
}

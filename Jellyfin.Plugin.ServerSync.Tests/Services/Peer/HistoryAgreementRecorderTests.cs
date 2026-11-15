using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Peer;

/// <summary>
/// The recorder must leave this server's history row for a peer holding exactly what the two servers agreed on, creating the row when the mappings allow it, and must never fail the negotiation that already happened.
/// </summary>
public sealed class HistoryAgreementRecorderTests : IDisposable
{
    private const string PeerKey = "peer";
    private const string PeerServerId = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";
    private static readonly Guid LocalUser = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid SourceUser = Guid.Parse("22222222222222222222222222222222");
    private static readonly Guid Item = Guid.Parse("33333333333333333333333333333333");
    private static readonly DateTime Watched = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly DatabaseTempFolder _folder = new("agreement");
    private readonly SyncDatabase _database;
    private readonly HistorySyncTableManager _table;
    private readonly FakeConfigManager _config = new();
    private readonly ILibraryManager _libraryManager;
    private readonly InterfaceStub _library;
    private readonly HistoryAgreementRecorder _recorder;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryAgreementRecorderTests"/> class over a peer that maps one library and one user, and a library that holds one film in it.
    /// </summary>
    public HistoryAgreementRecorderTests()
    {
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _folder.DataPath);
        _table = new HistorySyncTableManager(new FixedProvider(_database), NullLogger<HistorySyncTableManager>.Instance);
        _config.Configuration.Servers.Add(new SourceServer
        {
            Key = PeerKey,
            ServerId = PeerServerId,
            Url = "https://peer.example",
            ApiKey = "k",
            LibraryMappings = new List<LibraryMapping>
            {
                new() { SourceLibraryId = "src-lib", LocalLibraryId = "local-lib", SourceRootPath = "/src/movies", LocalRootPath = "/local/movies" }
            },
            UserMappings = new List<UserMapping>
            {
                new() { SourceUserId = SourceUser.ToString("N"), LocalUserId = LocalUser.ToString("N") }
            }
        });
        (_libraryManager, _library) = InterfaceStub.Create<ILibraryManager>();
        _library.Answer(nameof(ILibraryManager.GetItemById), args => (Guid)args[0]! == Item ? new Movie { Id = Item, Path = "/local/movies/Film (2020)/film.mkv", Name = "Film" } : null);
        _recorder = new HistoryAgreementRecorder(_config, _table, _libraryManager, new NoLinks(), NullLogger<HistoryAgreementRecorder>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _database.Dispose();
        _folder.Dispose();
    }

    /// <summary>
    /// The constructor refuses each missing dependency, and a record refuses a missing state.
    /// True: a broken registration or call fails at once.
    /// False: the first agreement would be lost to a null reference.
    /// </summary>
    [Fact]
    public void ConstructorAndRecord_MissingArgument_Throw()
    {
        var logger = NullLogger<HistoryAgreementRecorder>.Instance;
        var links = new NoLinks();

        Assert.Throws<ArgumentNullException>(() => new HistoryAgreementRecorder(null!, _table, _libraryManager, links, logger));
        Assert.Throws<ArgumentNullException>(() => new HistoryAgreementRecorder(_config, null!, _libraryManager, links, logger));
        Assert.Throws<ArgumentNullException>(() => new HistoryAgreementRecorder(_config, _table, null!, links, logger));
        Assert.Throws<ArgumentNullException>(() => new HistoryAgreementRecorder(_config, _table, _libraryManager, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new HistoryAgreementRecorder(_config, _table, _libraryManager, links, null!));
        Assert.Throws<ArgumentNullException>(() => _recorder.Record(PeerServerId, LocalUser, Item, "u", "i", null!));
    }

    /// <summary>
    /// A first agreement on an item creates the row from the sender's ids and this server's mappings, holding the agreed state on every side.
    /// True: the next change either way merges against what both servers hold.
    /// False: with no row, a later scan would treat the agreed state as a fresh difference and could let an older play outrank a newer unmark.
    /// </summary>
    [Fact]
    public void Record_NoRowYet_CreatesItFromTheMappingsWithTheAgreedState()
    {
        _recorder.Record(PeerServerId.ToUpperInvariant(), LocalUser, Item, SourceUser.ToString("N"), "peer-item", Agreed());

        var row = Assert.Single(Rows());
        Assert.Equal(SourceUser.ToString("N"), row.SourceUserId);
        Assert.Equal(LocalUser.ToString("N"), row.LocalUserId);
        Assert.Equal("peer-item", row.SourceItemId);
        Assert.Equal("src-lib", row.SourceLibraryId);
        Assert.Equal("local-lib", row.LocalLibraryId);
        Assert.Equal("Film", row.ItemName);
        Assert.Equal("/src/movies/Film (2020)/film.mkv", row.SourcePath);
        Assert.Equal("/local/movies/Film (2020)/film.mkv", row.LocalPath);
        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.True(row.HasNegotiatedBase);
        Assert.Equal((true, 2, 0L, Watched, true), (row.SourceIsPlayed, row.SourcePlayCount, row.SourcePlaybackPositionTicks, row.SourceLastPlayedDate, row.SourceIsFavorite));
        Assert.Equal((true, 2, 0L, Watched, true), (row.LocalIsPlayed, row.LocalPlayCount, row.LocalPlaybackPositionTicks, row.LocalLastPlayedDate, row.LocalIsFavorite));
        Assert.Equal((true, 2, 0L, Watched, true), (row.MergedIsPlayed, row.MergedPlayCount, row.MergedPlaybackPositionTicks, row.MergedLastPlayedDate, row.MergedIsFavorite));
        Assert.Equal((true, 2, Watched, true), (row.NegotiatedIsPlayed, row.NegotiatedPlayCount, row.NegotiatedLastPlayedDate, row.NegotiatedIsFavorite));
    }

    /// <summary>
    /// An agreement on an item that already has a row updates that row and creates no second one.
    /// True: a queued or errored row becomes Synced with the agreed state and its error cleared.
    /// False: the old row would stay queued and push its stale state back over the agreement.
    /// </summary>
    [Fact]
    public void Record_ExistingRow_IsUpdatedInPlace()
    {
        _table.Upsert(Row(SyncStatus.Errored, reason: "peer was down"));

        _recorder.Record(PeerServerId, LocalUser, Item, null, null, Agreed());

        var row = Assert.Single(Rows());
        Assert.Equal("existing-item", row.SourceItemId);
        Assert.Equal(SyncStatus.Synced, row.Status);
        Assert.Null(row.Reason);
        Assert.True(row.MergedIsPlayed);
        Assert.Equal(2, row.MergedPlayCount);
        Assert.True(row.LocalIsFavorite);
        Assert.True(row.HasNegotiatedBase);
        Assert.Empty(_library.Calls);
    }

    /// <summary>
    /// A row another local user holds for the same item is left alone.
    /// True: one user's agreement never overwrites another user's history.
    /// False: every user of an item would be marked played when one watched it.
    /// </summary>
    [Fact]
    public void Record_RowOfAnotherUser_IsLeftAlone()
    {
        var other = Row(SyncStatus.Queued);
        other.LocalUserId = Guid.NewGuid().ToString("N");
        other.SourceUserId = "other-source-user";
        _table.Upsert(other);

        _recorder.Record(PeerServerId, LocalUser, Item, SourceUser.ToString("N"), "peer-item", Agreed());

        var rows = Rows();
        Assert.Equal(2, rows.Count);
        var untouched = Assert.Single(rows, r => r.SourceUserId == "other-source-user");
        Assert.Equal(SyncStatus.Queued, untouched.Status);
        Assert.Null(untouched.MergedIsPlayed);
    }

    /// <summary>
    /// An ignored row is not changed.
    /// True: an item the operator ignored stays ignored and untracked.
    /// False: a peer's negotiation would undo the operator's Ignore.
    /// </summary>
    [Fact]
    public void Record_IgnoredRow_IsNotChanged()
    {
        _table.Upsert(Row(SyncStatus.Ignored));

        _recorder.Record(PeerServerId, LocalUser, Item, SourceUser.ToString("N"), "peer-item", Agreed());

        var row = Assert.Single(Rows());
        Assert.Equal(SyncStatus.Ignored, row.Status);
        Assert.Null(row.MergedIsPlayed);
        Assert.False(row.HasNegotiatedBase);
    }

    /// <summary>
    /// A sender this server does not list, or no sender at all, records nothing.
    /// True: only a configured peer can write history rows here.
    /// False: any caller could plant rows under a server key that does not exist.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ffffffffffffffffffffffffffffffff")]
    public void Record_UnknownOrMissingSender_RecordsNothing(string? senderServerId)
    {
        _recorder.Record(senderServerId, LocalUser, Item, SourceUser.ToString("N"), "peer-item", Agreed());

        Assert.Empty(Rows());
        Assert.Empty(_library.Calls);
    }

    /// <summary>
    /// A new row is not created without the sender's ids, the local item, a library mapping that holds it, and a mapping for the user.
    /// True: a row is only ever created with a complete key on both servers.
    /// False: a half keyed row would never match a scan and would be pruned, or would sync an unmapped user.
    /// </summary>
    [Theory]
    [InlineData("no-sender-user")]
    [InlineData("no-sender-item")]
    [InlineData("item-missing")]
    [InlineData("item-outside-mapped-libraries")]
    [InlineData("item-without-a-path")]
    [InlineData("user-not-mapped")]
    [InlineData("user-mapping-disabled")]
    public void Record_NoRowAndSomethingMissing_CreatesNothing(string missing)
    {
        var senderUser = missing == "no-sender-user" ? " " : SourceUser.ToString("N");
        var senderItem = missing == "no-sender-item" ? null : "peer-item";
        var user = missing == "user-not-mapped" ? Guid.NewGuid() : LocalUser;
        var item = missing == "item-missing" ? Guid.NewGuid() : Item;
        if (missing == "item-outside-mapped-libraries")
        {
            _library.Answer(nameof(ILibraryManager.GetItemById), _ => new Movie { Id = Item, Path = "/local/shows/film.mkv" });
        }

        if (missing == "item-without-a-path")
        {
            _library.Answer(nameof(ILibraryManager.GetItemById), _ => new Movie { Id = Item });
        }

        if (missing == "user-mapping-disabled")
        {
            _config.Configuration.Servers[0].UserMappings[0].IsEnabled = false;
        }

        _recorder.Record(PeerServerId, user, item, senderUser, senderItem, Agreed());

        Assert.Empty(_table.GetByLocalItem(PeerKey, item.ToString("N")));
    }

    /// <summary>
    /// A failure while recording is swallowed.
    /// True: the peer, which already holds the agreed state, gets its answer.
    /// False: a local fault after the write would fail the negotiation and the peer would retry a change that already landed.
    /// </summary>
    [Fact]
    public void Record_LibraryThrows_DoesNotThrow()
    {
        _library.Answer(nameof(ILibraryManager.GetItemById), _ => throw new InvalidOperationException("library busy"));

        var failure = Record.Exception(() => _recorder.Record(PeerServerId, LocalUser, Item, SourceUser.ToString("N"), "peer-item", Agreed()));

        Assert.Null(failure);
        Assert.Empty(Rows());
    }

    private static PeerHistoryState Agreed() => new() { Played = true, PlayCount = 2, PlaybackPositionTicks = 0, LastPlayedDate = Watched, IsFavorite = true };

    private static HistorySyncItem Row(SyncStatus status, string? reason = null) => new()
    {
        SourceUserId = SourceUser.ToString("N"),
        LocalUserId = LocalUser.ToString("N"),
        SourceLibraryId = "src-lib",
        LocalLibraryId = "local-lib",
        SourceItemId = "existing-item",
        LocalItemId = Item.ToString("N"),
        ServerKey = PeerKey,
        Status = status,
        StatusDate = Watched,
        Reason = reason
    };

    private IList<HistorySyncItem> Rows() => _table.GetByLocalItem(PeerKey, Item.ToString("N"));
}

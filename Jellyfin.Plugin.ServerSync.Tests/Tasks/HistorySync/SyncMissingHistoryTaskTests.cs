using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tasks.HistorySync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.HistorySync;

/// <summary>
/// The history apply writes a queued row's merged state to this server only after the source has agreed where it must, reads it back, and errors the row with the reason whenever any of that fails.
/// </summary>
public sealed class SyncMissingHistoryTaskTests : IDisposable
{
    private const string ServerKey = "server-a";
    private static readonly Guid SourceUser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceItem = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LocalItem = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTime Played = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly HistorySyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();
    private readonly SourceServer _server;
    private readonly User _localUser = new("local", "Default", "Default");
    private readonly UserItemData _userData = new() { Key = "k" };
    private bool _userExists = true;
    private bool _writesStick = true;
    private int _saves;

    public SyncMissingHistoryTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-history-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new HistorySyncTableManager(new FixedProvider(_database), NullLogger<HistorySyncTableManager>.Instance);
        _server = new SourceServer
        {
            Key = ServerKey,
            Name = "Source A",
            Url = "http://server-a:8096",
            ApiKey = "k",
            Mode = ServerMode.Pull,
            LibraryMappings = { new LibraryMapping { SourceLibraryId = "lib-1", LocalRootPath = "/local" } }
        };
        _config.Configuration.Servers.Add(_server);
        _config.Configuration.EnableHistorySync = true;
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

    private sealed class Probe : SyncMissingHistoryTask
    {
        public Probe(SyncMissingHistoryTaskTests test, LocalServerClient local, IServerApplicationHost host, ApplyGuard? guard)
            : base(NullLogger<SyncMissingHistoryTask>.Instance, test._config, test._factory, local, test._manager, host, guard)
        {
        }

        public Task Apply(HistorySyncItem record) => ApplyAsync(record, CancellationToken.None);

        public Task Verify(HistorySyncItem record) => VerifyAfterApplyAsync(record, CancellationToken.None);

        public List<IList<HistorySyncItem>> Groups(IList<HistorySyncItem> items) => GetApplyGroups(items).ToList();

        public bool Enabled() => IsEnabled();

        public IDisposable? Guard(HistorySyncItem record) => EnterApplyGuard(record);

        public void Completed(PluginConfiguration config, DateTime at) => RecordRunCompleted(config, at);
    }

    private Probe Task(ApplyGuard? guard = null)
    {
        var (users, userStub) = InterfaceStub.Create<IUserManager>();
        userStub.Answer(nameof(IUserManager.GetUserById), args => _userExists && (Guid)args[0]! == _localUser.Id ? _localUser : null);
        var (library, libraryStub) = InterfaceStub.Create<ILibraryManager>();
        libraryStub.Answer(nameof(ILibraryManager.GetItemById), args => (Guid)args[0]! == LocalItem ? new Movie { Id = LocalItem } : null);
        var (userData, dataStub) = InterfaceStub.Create<IUserDataManager>();
        dataStub.Answer(nameof(IUserDataManager.GetUserData), _ => _writesStick ? _userData : new UserItemData { Key = "k" });
        dataStub.Answer(nameof(IUserDataManager.SaveUserData), _ =>
        {
            _saves++;
            return null;
        });
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "local-server-id");
        var local = new LocalServerClient(NullLogger<LocalServerClient>.Instance, library, users, userData);
        return new Probe(this, local, host, guard);
    }

    private HistorySyncItem Row(string sourceItem = "") => new()
    {
        SourceUserId = SourceUser.ToString("N"),
        LocalUserId = _localUser.Id.ToString("N"),
        SourceLibraryId = "lib-1",
        SourceItemId = sourceItem.Length > 0 ? sourceItem : SourceItem.ToString("N"),
        LocalItemId = LocalItem.ToString("N"),
        ItemName = "Film",
        ServerKey = ServerKey,
        Status = SyncStatus.Queued,
        MergedIsPlayed = true,
        MergedPlayCount = 2,
        MergedPlaybackPositionTicks = 0,
        MergedLastPlayedDate = Played,
        MergedIsFavorite = true
    };

    private HistorySyncItem Queue()
    {
        var row = Row();
        _manager.Upsert(row);
        return row;
    }

    private HistorySyncItem Stored() => _manager.GetByKey((SourceUser.ToString("N"), SourceItem.ToString("N")))!;

    private void Negotiate()
    {
        _server.Mode = ServerMode.Sync;
        _server.LibraryMappings[0].NegotiateHistory = true;
    }

    private TaskSourceHandler Peer(string historyAnswer)
        => _factory.HandlerFor(ServerKey)
            .OnJson("/Sync/Peer/Capabilities", "{\"ServerId\":\"peer\",\"PluginVersion\":\"" + _config.PluginVersion + "\"}")
            .OnJson("/Sync/Peer/History", historyAnswer);

    /// <summary>
    /// A queued row's merged state is written to the local user data, read back, and the row becomes Synced with the run stamped.
    /// True: watch history from the source lands here and the row stops being queued.
    /// False: history never arrives, or arrives while the row stays queued and is applied again every run.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_QueuedRow_WritesTheMergedStateAndMarksItSynced()
    {
        Queue();

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.True(_userData.Played);
        Assert.Equal(2, _userData.PlayCount);
        Assert.Equal(Played, _userData.LastPlayedDate);
        Assert.True(_userData.IsFavorite);
        Assert.Equal(1, _saves);
        Assert.Equal(SyncStatus.Synced, Stored().Status);
        Assert.NotNull(_config.Configuration.LastHistorySyncTime);
        Assert.Empty(_config.Configuration.LastRunFailures);
    }

    /// <summary>
    /// A local write that fails makes the row Errored with the reason, counts a retry, and records the run as unclean.
    /// True: the administrator sees which row failed and why, and the retry ceiling can count it.
    /// False: a failed write reads as Synced and the history is silently lost.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LocalWriteFails_ErrorsTheRowAndRecordsTheFailure()
    {
        Queue();
        _userExists = false;

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        var stored = Stored();
        Assert.Equal(SyncStatus.Errored, stored.Status);
        Assert.Equal("Failed to update user data", stored.Reason);
        Assert.Equal(1, stored.RetryCount);
        var failure = Assert.Single(_config.Configuration.LastRunFailures);
        Assert.Equal("History", failure.ModuleKey);
        Assert.Equal("Sync", failure.Phase);
    }

    /// <summary>
    /// A write that does not read back as written makes the row Errored, naming each value that did not land.
    /// True: a write Jellyfin dropped is caught and retried, with the differing values in the reason.
    /// False: the row is marked Synced while this server still shows the old state.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WriteDoesNotReadBack_ErrorsTheRowWithTheDifference()
    {
        Queue();
        _writesStick = false;

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        var stored = Stored();
        Assert.Equal(SyncStatus.Errored, stored.Status);
        Assert.StartsWith("verification mismatch: Played wanted=True, got=False; PlayCount wanted=2, got=0", stored.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// When no server answers, the queued row stays Queued, nothing is written, and the run is recorded as failed.
    /// True: a source outage leaves the queue intact for the next run and shows on the dashboard.
    /// False: every queued row is errored each time the source is down.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NoServerAnswers_LeavesTheRowQueuedAndRecordsTheFailure()
    {
        Queue();
        _factory.HandlerFor(ServerKey).SystemInfoStatus = HttpStatusCode.InternalServerError;

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Equal(SyncStatus.Queued, Stored().Status);
        Assert.Equal(0, _saves);
        Assert.Equal("no scan server is reachable or every configured key is invalid", Assert.Single(_config.Configuration.LastRunFailures).Reason);
        Assert.Null(_config.Configuration.LastHistorySyncTime);
    }

    /// <summary>
    /// History being off, or no source being configured, ends the task before any server is asked.
    /// True: the task does nothing when the sync type is off.
    /// False: queued rows are applied after History was turned off.
    /// </summary>
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task ExecuteAsync_SwitchedOffOrNoSource_AsksNoServer(bool historyOn, bool hasServer, bool expectedEnabled)
    {
        _config.Configuration.EnableHistorySync = historyOn;
        _server.IsEnabled = hasServer;
        var task = Task();

        await task.ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Equal(expectedEnabled, task.Enabled());
        Assert.Equal(expectedEnabled, _factory.Created.Count > 0);
    }

    /// <summary>
    /// A row with no local item, no local user, or ids that are not ids is refused with a reason that says which.
    /// True: a row that could not be matched here errors with the cause the table shows.
    /// False: the row fails with a parse error, or writes to the wrong user or item.
    /// </summary>
    [Theory]
    [InlineData("", "22222222222222222222222222222222", "Local item not found")]
    [InlineData(null, "22222222222222222222222222222222", "Local item not found")]
    [InlineData("44444444444444444444444444444444", "", "Local user not found")]
    [InlineData("not-an-id", "22222222222222222222222222222222", "Invalid user or item ID")]
    [InlineData("44444444444444444444444444444444", "not-an-id", "Invalid user or item ID")]
    public async Task ApplyAsync_UnusableLocalIds_ThrowsTheReason(string? localItemId, string localUserId, string reason)
    {
        var row = Row();
        row.LocalItemId = localItemId;
        row.LocalUserId = localUserId;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Task().Apply(row));

        Assert.Equal(reason, thrown.Message);
        Assert.Equal(0, _saves);
    }

    /// <summary>
    /// A merged state of not played clears the stored last played date.
    /// True: an item marked unplayed on the source reads as never played here.
    /// False: the item shows as unplayed but still carries a last played date.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_MergedNotPlayed_ClearsTheLastPlayedDate()
    {
        _userData.Played = true;
        _userData.LastPlayedDate = Played;
        var row = Row();
        row.MergedIsPlayed = false;
        row.MergedLastPlayedDate = null;

        await Task().Apply(row);

        Assert.False(_userData.Played);
        Assert.Null(_userData.LastPlayedDate);
    }

    /// <summary>
    /// A merged value of null leaves that part of the local state as it is.
    /// True: a part of history that is not synced is never overwritten.
    /// False: turning a part off wipes it on every apply.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_NullMergedValues_KeepTheLocalValues()
    {
        _userData.PlayCount = 7;
        _userData.IsFavorite = true;
        var row = Row();
        row.MergedPlayCount = null;
        row.MergedIsFavorite = null;

        await Task().Apply(row);

        Assert.Equal(7, _userData.PlayCount);
        Assert.True(_userData.IsFavorite);
    }

    /// <summary>
    /// The last played date is compared to the second when read back.
    /// True: Jellyfin dropping the fraction of a second does not fail the row.
    /// False: every row with a last played date errors after a correct write.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_DateDiffersBelowASecond_Passes()
    {
        var row = Row();
        row.MergedLastPlayedDate = Played.AddMilliseconds(400);
        _userData.Played = true;
        _userData.PlayCount = 2;
        _userData.IsFavorite = true;
        _userData.LastPlayedDate = Played;

        var thrown = await Record.ExceptionAsync(() => Task().Verify(row));

        Assert.Null(thrown);
    }

    /// <summary>
    /// A user data row that cannot be read back fails the check.
    /// True: an item or user that vanished during the apply errors the row.
    /// False: the row is marked Synced with nothing to show for it.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_UserDataGone_Throws()
    {
        _userExists = false;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Task().Verify(Row()));

        Assert.Equal("user-data row not found after apply", thrown.Message);
    }

    /// <summary>
    /// Without negotiation every row is applied as one group.
    /// True: a plain pull applies its rows without batching by server.
    /// False: rows are split or dropped when no library negotiates.
    /// </summary>
    [Fact]
    public void GetApplyGroups_NotNegotiating_IsOneGroup()
    {
        var rows = new List<HistorySyncItem> { Row("a"), Row("b") };

        var groups = Task().Groups(rows);

        Assert.Same(rows, Assert.Single(groups));
    }

    /// <summary>
    /// With negotiation the rows are batched by server, and no batch is larger than the peer route accepts.
    /// True: each offer goes to the server its rows came from and is never refused for its size.
    /// False: one server is offered another server's rows, or an oversized batch is rejected whole.
    /// </summary>
    [Fact]
    public void GetApplyGroups_Negotiating_BatchesByServerUpToTheRouteLimit()
    {
        Negotiate();
        var rows = new List<HistorySyncItem>();
        for (var i = 0; i < 501; i++)
        {
            rows.Add(Row("a" + i));
        }

        var other = Row("other");
        other.ServerKey = "SERVER-B";
        rows.Insert(1, other);

        var groups = Task().Groups(rows);

        Assert.Equal(new[] { 500, 1, 1 }, groups.Select(g => g.Count).ToArray());
        Assert.All(groups[0], r => Assert.Equal(ServerKey, r.ServerKey));
        Assert.Same(other, groups[2][0]);
    }

    /// <summary>
    /// When the source accepts the offered state, the local write follows and the agreed state becomes the row's base.
    /// True: both servers hold the merged state and the next merge starts from it.
    /// False: this server writes without the source, or forgets what the two agreed.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SourceAcceptsTheOffer_WritesLocallyAndRecordsTheBase()
    {
        Negotiate();
        Queue();
        var peer = Peer("{\"Items\":[{\"Outcome\":\"Applied\"}]}");

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        var stored = Stored();
        Assert.Equal(SyncStatus.Synced, stored.Status);
        Assert.Equal(1, _saves);
        Assert.True(stored.HasNegotiatedBase);
        Assert.True(stored.SourceIsPlayed);
        Assert.Equal(1, peer.CountOf("/Sync/Peer/History"));
        Assert.Contains(peer.Bodies, b => b.Contains("local-server-id", StringComparison.Ordinal) && b.Contains(SourceItem.ToString("N"), StringComparison.Ordinal));
    }

    /// <summary>
    /// When the source rejects the offered state, the row is Errored with the source's reason and nothing is written here.
    /// True: the two servers never drift further apart over a write only one of them took.
    /// False: this server takes the merged state the source refused.
    /// </summary>
    [Theory]
    [InlineData("{\"Items\":[{\"Outcome\":\"NotFound\",\"Reason\":\"no such item\"}]}", "source rejected the write: no such item")]
    [InlineData("{\"Items\":[{\"Outcome\":\"Failed\",\"Reason\":\"disk\"}]}", "source failed the write: disk")]
    [InlineData("{\"Items\":[]}", "source failed the write: source returned no result for this row")]
    public async Task ExecuteAsync_SourceRejectsTheOffer_ErrorsTheRowWithoutWriting(string answer, string reason)
    {
        Negotiate();
        Queue();
        Peer(answer);

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        var stored = Stored();
        Assert.Equal(SyncStatus.Errored, stored.Status);
        Assert.Equal(reason, stored.Reason);
        Assert.Equal(0, _saves);
    }

    /// <summary>
    /// A source that answers the offer with a server error errors the row and writes nothing here.
    /// True: a failing peer route costs one errored row, retried next run.
    /// False: the local write goes ahead without the source's agreement.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SourceErrorsOnTheOffer_ErrorsTheRowWithoutWriting()
    {
        Negotiate();
        Queue();
        Peer("{}").On("/Sync/Peer/History", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Equal(SyncStatus.Errored, Stored().Status);
        Assert.Equal(0, _saves);
    }

    /// <summary>
    /// A source that reports its state moved since the scan is offered the row once more, and an acceptance then applies it.
    /// True: a play on the source during the run is merged in and not lost.
    /// False: the row errors on every concurrent change, or overwrites the newer source state.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SourceMovedSinceTheScan_OffersOnceMoreThenApplies()
    {
        Negotiate();
        Queue();
        var answers = new Queue<string>(new[]
        {
            "{\"Items\":[{\"Outcome\":\"Stale\",\"Current\":{\"Played\":true,\"PlayCount\":5}}]}",
            "{\"Items\":[{\"Outcome\":\"Applied\"}]}"
        });
        var peer = Peer("{}").On("/Sync/Peer/History", _ => TaskSourceHandler.Json(answers.Dequeue()));

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Equal(SyncStatus.Synced, Stored().Status);
        Assert.Equal(2, peer.CountOf("/Sync/Peer/History"));
        Assert.Equal(1, _saves);
    }

    /// <summary>
    /// A source with no Server Sync, when every library negotiates, stops the run before any row is touched and says why.
    /// True: a missing plugin shows as one clear run failure and the rows stay queued.
    /// False: every row is errored with the same message, or history is written one way without agreement.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SourceHasNoServerSync_StopsTheRunAndKeepsTheRowQueued()
    {
        Negotiate();
        Queue();

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Equal(SyncStatus.Queued, Stored().Status);
        Assert.Equal(0, _saves);
        Assert.Equal(
            "'Source A' does not run Server Sync. Install Server Sync there, or turn off Negotiate With Source Server",
            Assert.Single(_config.Configuration.LastRunFailures).Reason);
    }

    /// <summary>
    /// A source that refuses this server's key is applied one way: the local write happens, no offer is sent, and no base is recorded.
    /// True: history still arrives from a source that has not whitelisted this server.
    /// False: a missing whitelist entry stops history, or a base is recorded for a state the source never took.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ExecuteAsync_SourceRefusesTheKey_AppliesOneWay(HttpStatusCode refusal)
    {
        Negotiate();
        Queue();
        var peer = _factory.HandlerFor(ServerKey).On("/Sync/Peer/Capabilities", _ => new HttpResponseMessage(refusal));

        await Task().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        var stored = Stored();
        Assert.Equal(SyncStatus.Synced, stored.Status);
        Assert.Equal(1, _saves);
        Assert.False(stored.HasNegotiatedBase);
        Assert.Equal(0, peer.CountOf("/Sync/Peer/History"));
    }

    /// <summary>
    /// With no guard kept, or ids that are not ids, no write is registered.
    /// True: a server that keeps no guard, or a row that cannot be applied, registers nothing.
    /// False: the apply throws before it can report the row's real problem.
    /// </summary>
    [Fact]
    public void EnterApplyGuard_NoGuardOrUnusableIds_ReturnsNull()
    {
        var unusable = Row();
        unusable.LocalItemId = "not-an-id";

        var withoutGuard = Task().Guard(Row());
        var withUnusableIds = Task(new ApplyGuard()).Guard(unusable);

        Assert.Null(withoutGuard);
        Assert.Null(withUnusableIds);
    }

    /// <summary>
    /// With a guard, the write is registered under the local user and item for as long as the handle lives.
    /// True: the change observer sees the write as the echo of a sync and sends no hint for it.
    /// False: every applied row is announced to every peer as an edit made here.
    /// </summary>
    [Fact]
    public void EnterApplyGuard_WithGuard_RegistersTheLocalUserAndItem()
    {
        var guard = new ApplyGuard();
        var key = HintProtocol.GuardKey(Jellyfin.Plugin.ServerSync.Models.Queue.HintKind.History, HintProtocol.HistoryKey(_localUser.Id, LocalItem));

        var handle = Task(guard).Guard(Row());
        var during = guard.IsApplying(key);
        handle!.Dispose();

        Assert.True(during);
        Assert.False(guard.IsApplying(key));
    }

    /// <summary>
    /// A finished run stamps the History time.
    /// True: the dashboard shows when History last ran.
    /// False: the History card never updates, or another sync type's time moves.
    /// </summary>
    [Fact]
    public void RecordRunCompleted_StampsTheHistoryTime()
    {
        var config = new PluginConfiguration();

        Task().Completed(config, Played);

        Assert.Equal(Played, config.LastHistorySyncTime);
        Assert.Null(config.LastUserSyncTime);
    }
}

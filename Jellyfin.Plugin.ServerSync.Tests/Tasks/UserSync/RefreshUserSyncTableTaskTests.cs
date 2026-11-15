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
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Jellyfin.Plugin.ServerSync.Tasks.UserSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SourceUserDto = Jellyfin.Sdk.Generated.Models.UserDto;
using SourceUserPolicy = Jellyfin.Sdk.Generated.Models.UserPolicy;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.UserSync;

/// <summary>
/// The users scan lists one piece of work for each mapped user and each category that is on, skips a mapping it cannot read, and picks which server speaks for a local user by the precedence setting.
/// </summary>
public sealed class RefreshUserSyncTableTaskTests : IDisposable
{
    private static readonly DateTime At = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly UserSyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();
    private readonly List<ScanSource> _sources = new();
    private readonly User _localUser = new("alice", "Default", "Default");
    private readonly IUserManager _users;
    private bool _localUserExists = true;

    public RefreshUserSyncTableTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-user-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new UserSyncTableManager(new FixedProvider(_database), NullLogger<UserSyncTableManager>.Instance);
        _config.Configuration.EnableUserSync = true;
        (_users, var userStub) = InterfaceStub.Create<IUserManager>();
        userStub.Answer(nameof(IUserManager.GetUserById), args => _localUserExists && (Guid)args[0]! == _localUser.Id ? _localUser : null);
        userStub.Answer(nameof(IUserManager.GetUserDto), _ => new MediaBrowser.Model.Dto.UserDto { Name = "alice" });
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _database.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Probe : RefreshUserSyncTableTask
    {
        public Probe(RefreshUserSyncTableTaskTests test)
            : base(NullLogger<RefreshUserSyncTableTask>.Instance, test._config, test._factory, test._users, new UserSyncTableService(NullLogger<UserSyncTableService>.Instance), test._manager)
        {
        }

        /// <summary>Runs in place of the listing, while the run holds the stored rows.</summary>
        public Func<Task>? DuringRun { get; set; }

        public string MutexKey => ModuleMutexKey;

        public bool Enabled() => IsEnabled();

        public Task<IList<UserCategoryWork>> List(ScanSource source, IProgress<double> progress) => base.GetListAsync(source, progress, CancellationToken.None);

        public string? KeyOf(UserCategoryWork? work) => PriorityKeyOf(work!);

        public string? KeyOf(UserSyncItem? record) => PriorityKeyOf(record!);

        public (string SourceUserId, string LocalUserId, string PropertyCategory) RowKey(UserSyncItem record) => ExtractKey(record);

        public Task<IReadOnlyDictionary<string, UserCategoryWork>> Contest(params UserCategoryWork[] offers)
            => ContestAsync(new Dictionary<string, IReadOnlyList<UserCategoryWork>> { ["user"] = offers }, CancellationToken.None);

        public UserCategoryWork? Observed(UserCategoryWork passedOver, UserCategoryWork speaking) => ObservedOffer(passedOver, speaking);

        public void Decide(UserSyncItem record) => DecideStatus(record);

        public bool Outranks(UserSyncItem record) => MayOutrankOwner(record);

        public bool Retires(UserSyncItem record) => CanRetire(record);

        public void Completed(PluginConfiguration config, DateTime at) => RecordRunCompleted(config, at);

        protected override async Task<IList<UserCategoryWork>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (DuringRun is null)
            {
                return await base.GetListAsync(source, progress, cancellationToken).ConfigureAwait(false);
            }

            await DuringRun().ConfigureAwait(false);
            DuringRun = null;
            return Array.Empty<UserCategoryWork>();
        }
    }

    private SourceServer AddServer(string key, params Guid[] sourceUsers)
    {
        var server = new SourceServer { Key = key, Name = key, Url = "http://" + key + ":8096", ApiKey = "k" };
        foreach (var sourceUser in sourceUsers)
        {
            server.UserMappings.Add(new UserMapping
            {
                SourceUserId = sourceUser.ToString("N"),
                SourceUserName = "Alice",
                LocalUserId = _localUser.Id.ToString("N"),
                LocalUserName = "alice"
            });
            _factory.HandlerFor(key).OnJson("/Users/" + sourceUser, "{\"Id\":\"" + sourceUser + "\",\"Name\":\"Alice\",\"Policy\":{\"IsHidden\":true},\"Configuration\":{}}");
        }

        _config.Configuration.Servers.Add(server);
        return server;
    }

    private ScanSource Source(SourceServer server, int priority)
    {
        var source = new ScanSource(server, _factory.ClientFor(server), priority);
        _sources.Add(source);
        return source;
    }

    private UserCategoryWork Offer(SourceServer server, int priority, bool hidden = false, string category = UserPropertyCategory.Policy) => new()
    {
        Source = Source(server, priority),
        Mapping = server.UserMappings[0],
        Category = category,
        SourceUser = new SourceUserDto { Name = "Alice", Policy = new SourceUserPolicy { IsHidden = hidden } },
        LocalUserDto = new MediaBrowser.Model.Dto.UserDto { Name = "alice" },
        LocalUser = _localUser
    };

    private void Seen(SourceServer server, DateTime? appliedAt = null)
        => _manager.Upsert(new UserSyncItem
        {
            SourceUserId = server.UserMappings[0].SourceUserId,
            LocalUserId = server.UserMappings[0].LocalUserId,
            PropertyCategory = UserPropertyCategory.Policy,
            ServerKey = server.Key,
            Status = SyncStatus.Synced,
            SourceValue = UserSyncMergeService.ExtractPolicyJson(new SourceUserPolicy { IsHidden = false }),
            LastSyncTime = appliedAt
        });

    private async Task<IReadOnlyDictionary<string, UserCategoryWork>> ContestDuringRunAsync(params UserCategoryWork[] offers)
    {
        var task = new Probe(this);
        IReadOnlyDictionary<string, UserCategoryWork>? winners = null;
        task.DuringRun = async () => winners = await task.Contest(offers);

        await task.ExecuteAsync(new TaskProgress(), CancellationToken.None);

        return winners!;
    }

    /// <summary>
    /// The scan runs only with Users on, a source, and an enabled user mapping.
    /// True: the scan stays idle until a user is mapped.
    /// False: a scan with no mappings connects to every source and prunes the table.
    /// </summary>
    [Theory]
    [InlineData("none", true)]
    [InlineData("users", false)]
    [InlineData("server", false)]
    [InlineData("mapping", false)]
    public void IsEnabled_NeedsUsersASourceAndAMapping(string off, bool expected)
    {
        var server = AddServer("a", Guid.NewGuid());
        _config.Configuration.EnableUserSync = off != "users";
        server.IsEnabled = off != "server";
        server.UserMappings[0].IsEnabled = off != "mapping";

        Assert.Equal(expected, new Probe(this).Enabled());
    }

    /// <summary>
    /// A mapped user gives one piece of work for each category that is on, in the order policy, configuration, image.
    /// True: turning a category off stops it being compared at all.
    /// False: a category that is off still gets rows and is applied.
    /// </summary>
    [Theory]
    [InlineData(true, true, true, "Policy,Configuration,ProfileImage")]
    [InlineData(true, false, false, "Policy")]
    [InlineData(false, true, false, "Configuration")]
    [InlineData(false, false, true, "ProfileImage")]
    [InlineData(false, false, false, "")]
    public async Task GetListAsync_MappedUser_ListsEachCategoryThatIsOn(bool policy, bool configuration, bool image, string expected)
    {
        var server = AddServer("a", Guid.NewGuid());
        _config.Configuration.UserSyncPolicy = policy;
        _config.Configuration.UserSyncConfiguration = configuration;
        _config.Configuration.UserSyncProfileImage = image;
        var progress = new TaskProgress();

        var work = await new Probe(this).List(Source(server, 0), progress);

        Assert.Equal(expected, string.Join(",", work.Select(w => w.Category)));
        Assert.All(work, w => Assert.Equal("Alice", w.SourceUser.Name));
        Assert.All(work, w => Assert.Same(_localUser, w.LocalUser));
        Assert.Equal(100.0, progress.Values[^1]);
    }

    /// <summary>
    /// A mapping whose source user cannot be read gives no work, and the next mapping is still listed.
    /// True: one unreadable user does not stop the others from syncing.
    /// False: a deleted source user ends the scan for every user.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetListAsync_SourceUserAnswersWithError_SkipsThatMappingOnly(HttpStatusCode status)
    {
        var missing = Guid.NewGuid();
        var present = Guid.NewGuid();
        var server = AddServer("a", missing, present);
        _factory.HandlerFor("a").On("/Users/" + missing, _ => new HttpResponseMessage(status));

        var work = await new Probe(this).List(Source(server, 0), new TaskProgress());

        Assert.Equal(3, work.Count);
        Assert.All(work, w => Assert.Equal(present.ToString("N"), w.Mapping.SourceUserId));
    }

    /// <summary>
    /// A mapping whose local user is gone gives no work.
    /// True: a deleted local user is skipped without an error.
    /// False: the scan throws, or builds rows for a user who does not exist.
    /// </summary>
    [Fact]
    public async Task GetListAsync_LocalUserGone_ListsNothing()
    {
        var server = AddServer("a", Guid.NewGuid());
        _localUserExists = false;

        var work = await new Probe(this).List(Source(server, 0), new TaskProgress());

        Assert.Empty(work);
    }

    /// <summary>
    /// A mapping whose ids are not ids is skipped without a request, and the next mapping is still listed.
    /// True: one damaged mapping does not stop the scan.
    /// False: a bad id in the settings throws out of the scan.
    /// </summary>
    [Fact]
    public async Task GetListAsync_MappingWithBadIds_SkipsThatMappingOnly()
    {
        var present = Guid.NewGuid();
        var server = AddServer("a", present);
        server.UserMappings.Insert(0, new UserMapping { SourceUserId = "not-an-id", LocalUserId = "also-not" });

        var work = await new Probe(this).List(Source(server, 0), new TaskProgress());

        Assert.Equal(3, work.Count);
        Assert.Single(_factory.HandlerFor("a").Requests);
    }

    /// <summary>
    /// A server with no enabled mapping lists nothing and still finishes its progress.
    /// True: an unmapped server is a quick no-op.
    /// False: the bar stalls, or dividing by zero mappings throws.
    /// </summary>
    [Fact]
    public async Task GetListAsync_NoMappings_ListsNothingAndFinishes()
    {
        var server = AddServer("a");
        var progress = new TaskProgress();

        var work = await new Probe(this).List(Source(server, 0), progress);

        Assert.Empty(work);
        Assert.Equal(new[] { 0.0, 100.0 }, progress.Values);
    }

    /// <summary>
    /// Two servers collide when they map a user onto the same local user, for each category on its own.
    /// True: one local user's policy has one speaker, and their image another if need be.
    /// False: two servers each apply their policy to the same local user in turn.
    /// </summary>
    [Fact]
    public void PriorityKeyOf_IsTheLocalUserAndCategory()
    {
        var a = AddServer("a", Guid.NewGuid());
        var b = AddServer("b", Guid.NewGuid());
        var task = new Probe(this);
        var local = _localUser.Id.ToString("N");

        Assert.Equal(local + "|Policy", task.KeyOf(Offer(a, 0)));
        Assert.Equal(task.KeyOf(Offer(a, 0)), task.KeyOf(Offer(b, 1)));
        Assert.NotEqual(task.KeyOf(Offer(a, 0)), task.KeyOf(Offer(a, 0, category: UserPropertyCategory.Configuration)));
        Assert.Equal(local + "|Policy", task.KeyOf(new UserSyncItem { LocalUserId = local, PropertyCategory = UserPropertyCategory.Policy }));
        Assert.Null(task.KeyOf((UserCategoryWork?)null));
        Assert.Null(task.KeyOf((UserSyncItem?)null));
    }

    /// <summary>
    /// A row's key is its source user, local user, and category.
    /// True: each category of each mapping is one row.
    /// False: a user's policy and configuration overwrite each other.
    /// </summary>
    [Fact]
    public void ExtractKey_IsTheSourceUserLocalUserAndCategory()
    {
        var key = new Probe(this).RowKey(new UserSyncItem { SourceUserId = "s", LocalUserId = "l", PropertyCategory = "Policy" });

        Assert.Equal(("s", "l", "Policy"), key);
    }

    /// <summary>
    /// Under Ranked precedence, and under Newest before anything was seen, the highest ranked server speaks.
    /// True: Ranked always follows the list order, and a first scan does too.
    /// False: a lower ranked server takes over a user the administrator ranked another server for.
    /// </summary>
    [Theory]
    [InlineData(SyncPrecedence.Rank)]
    [InlineData(SyncPrecedence.Newest)]
    public async Task ContestAsync_RankedOrNothingSeen_LeavesRankToDecide(SyncPrecedence precedence)
    {
        _config.Configuration.UserSyncPrecedence = precedence;
        var a = AddServer("a", Guid.NewGuid());
        var b = AddServer("b", Guid.NewGuid());

        var winners = await new Probe(this).Contest(Offer(a, 0), Offer(b, 1, hidden: true));

        Assert.Empty(winners);
    }

    /// <summary>
    /// Under Newest, a lower ranked server whose settings changed since they were last seen speaks, when the top server's did not.
    /// True: a change made on any server reaches this one.
    /// False: the top server's unchanged settings overwrite a newer change made elsewhere.
    /// </summary>
    [Fact]
    public async Task ContestAsync_NewestAndOnlyALowerServerChanged_ThatServerSpeaks()
    {
        var a = AddServer("a", Guid.NewGuid());
        var b = AddServer("b", Guid.NewGuid());
        Seen(a);
        Seen(b);
        var changed = Offer(b, 1, hidden: true);

        var winners = await ContestDuringRunAsync(Offer(a, 0), changed);

        Assert.Same(changed, Assert.Single(winners).Value);
    }

    /// <summary>
    /// Under Newest, when no server changed, the one whose settings were applied last keeps speaking.
    /// True: the user's settings stay as the newest change left them.
    /// False: the next quiet scan reverts the user to the top ranked server's older settings.
    /// </summary>
    [Fact]
    public async Task ContestAsync_NewestAndNoneChanged_TheLastAppliedServerSpeaks()
    {
        var a = AddServer("a", Guid.NewGuid());
        var b = AddServer("b", Guid.NewGuid());
        Seen(a, appliedAt: At);
        Seen(b, appliedAt: At.AddHours(1));
        var lastApplied = Offer(b, 1);

        var winners = await ContestDuringRunAsync(Offer(a, 0), lastApplied);

        Assert.Same(lastApplied, Assert.Single(winners).Value);
    }

    /// <summary>
    /// A server passed over before it was ever seen is recorded as observed, except for images and under Ranked.
    /// True: the next scan can tell whether that server changed since.
    /// False: a passed over server is never recorded, so its later change is never noticed.
    /// </summary>
    [Theory]
    [InlineData(SyncPrecedence.Newest, UserPropertyCategory.Policy, true)]
    [InlineData(SyncPrecedence.Newest, UserPropertyCategory.Configuration, true)]
    [InlineData(SyncPrecedence.Newest, UserPropertyCategory.ProfileImage, false)]
    [InlineData(SyncPrecedence.Rank, UserPropertyCategory.Policy, false)]
    public void ObservedOffer_UnseenServer_IsRecordedOnlyForSettingsUnderNewest(SyncPrecedence precedence, string category, bool recorded)
    {
        _config.Configuration.UserSyncPrecedence = precedence;
        var a = AddServer("a", Guid.NewGuid());
        var b = AddServer("b", Guid.NewGuid());
        var passedOver = Offer(b, 1, category: category);

        var observed = new Probe(this).Observed(passedOver, Offer(a, 0, category: category));

        Assert.Equal(recorded, observed is not null);
        if (observed is not null)
        {
            Assert.True(observed.Observe);
            Assert.Same(passedOver.Mapping, observed.Mapping);
            Assert.Same(passedOver.Source, observed.Source);
            Assert.Equal(category, observed.Category);
        }
    }

    /// <summary>
    /// An observed row is stored as Synced with a reason and is never queued, and an Ignored one stays Ignored.
    /// True: a server that does not speak for a user never has its settings applied.
    /// False: the passed over server's settings are queued and overwrite the speaker's.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Queued, SyncStatus.Synced)]
    [InlineData(SyncStatus.Errored, SyncStatus.Synced)]
    [InlineData(SyncStatus.Ignored, SyncStatus.Ignored)]
    public void DecideStatus_ObservedRow_IsNeverQueued(SyncStatus before, SyncStatus after)
    {
        var row = new UserSyncItem { PropertyCategory = UserPropertyCategory.Policy, Status = before, Observed = true };
        row.UpdateMergedValue("{\"IsHidden\":true}");

        new Probe(this).Decide(row);

        Assert.Equal(after, row.Status);
        if (after == SyncStatus.Synced)
        {
            Assert.Equal("seen: another server speaks for this user's settings", row.Reason);
        }
    }

    /// <summary>
    /// A row that speaks and has a change is Queued, and one kept at the agreed value is Synced with the reason it was kept.
    /// True: a real change is applied, and a change made here is not undone while the other server has yet to take it.
    /// False: a change is never queued, or a local edit is reverted by the next scan.
    /// </summary>
    [Fact]
    public void DecideStatus_SpeakingRow_QueuesAChangeAndExplainsAKeptValue()
    {
        var changed = new UserSyncItem { PropertyCategory = UserPropertyCategory.Policy };
        changed.UpdateMergedValue("{\"IsHidden\":true}");
        var kept = new UserSyncItem { PropertyCategory = UserPropertyCategory.Policy, KeepAgreedValue = true };
        var task = new Probe(this);

        task.Decide(changed);
        task.Decide(kept);

        Assert.Equal(SyncStatus.Queued, changed.Status);
        Assert.Null(changed.Reason);
        Assert.Equal(SyncStatus.Synced, kept.Status);
        Assert.Equal("kept: changed on this server, and the other server has not changed it since the two agreed", kept.Reason);
    }

    /// <summary>
    /// Under Newest every server's row is kept and may outrank the owner, and under Ranked a covered row is retired unless it is Ignored.
    /// True: Newest keeps what was last seen of each server, and Ranked clears rows a higher server now covers.
    /// False: Newest loses its memory of a server on every scan, or Ranked deletes an Ignored row.
    /// </summary>
    [Theory]
    [InlineData(SyncPrecedence.Newest, SyncStatus.Synced, false, true)]
    [InlineData(SyncPrecedence.Newest, SyncStatus.Ignored, false, true)]
    [InlineData(SyncPrecedence.Rank, SyncStatus.Synced, true, false)]
    [InlineData(SyncPrecedence.Rank, SyncStatus.Ignored, false, false)]
    public void CanRetire_FollowsThePrecedence(SyncPrecedence precedence, SyncStatus status, bool retires, bool outranks)
    {
        _config.Configuration.UserSyncPrecedence = precedence;
        var task = new Probe(this);
        var row = new UserSyncItem { Status = status };

        Assert.Equal(retires, task.Retires(row));
        Assert.Equal(outranks, task.Outranks(row));
    }

    /// <summary>
    /// A finished scan stamps the Users time, under the lock the Users apply shares.
    /// True: the dashboard shows the scan, and the scan and the apply never overlap.
    /// False: another sync type's time moves, or the two tasks write the table together.
    /// </summary>
    [Fact]
    public void RecordRunCompleted_StampsTheUsersTimeUnderTheUserLock()
    {
        var task = new Probe(this);
        var config = new PluginConfiguration();

        task.Completed(config, At);

        Assert.Equal(At, config.LastUserSyncTime);
        Assert.Equal("User", task.MutexKey);
        Assert.Equal("ServerSyncRefreshUserTable", task.Key);
    }
}

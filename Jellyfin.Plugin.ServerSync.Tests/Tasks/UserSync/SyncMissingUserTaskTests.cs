using System;
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
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using ImageInfo = Jellyfin.Database.Implementations.Entities.ImageInfo;
using UserDto = MediaBrowser.Model.Dto.UserDto;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.UserSync;

/// <summary>
/// The users apply writes only the settings that may be copied to the local user, reads them back, replaces a profile image only once the new one is in hand, and errors the row whenever a step fails.
/// </summary>
public sealed class SyncMissingUserTaskTests : IDisposable
{
    private const string ServerKey = "server-a";
    private static readonly Guid SourceUser = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly UserSyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly TaskClientFactory _factory = new();
    private readonly SourceServer _server;
    private readonly User _localUser = new("alice", "Default", "Default");
    private readonly InterfaceStub _userStub;
    private readonly IUserManager _users;
    private readonly IServerConfigurationManager _serverConfig;
    private UserPolicy? _policy = new() { AuthenticationProviderId = "Default", PasswordResetProviderId = "Default" };
    private UserConfiguration? _configuration = new();
    private bool _userExists = true;

    public SyncMissingUserTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-user-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new UserSyncTableManager(new FixedProvider(_database), NullLogger<UserSyncTableManager>.Instance);
        _server = new SourceServer { Key = ServerKey, Name = "Source A", Url = "http://server-a:8096", ApiKey = "k" };
        _config.Configuration.Servers.Add(_server);
        _config.Configuration.EnableUserSync = true;

        (_users, _userStub) = InterfaceStub.Create<IUserManager>();
        _userStub.Answer(nameof(IUserManager.GetUserById), args => _userExists && (Guid)args[0]! == _localUser.Id ? _localUser : null);
        _userStub.Answer(nameof(IUserManager.GetUserDto), _ => new UserDto { Name = "alice", Policy = _policy, Configuration = _configuration });
        _userStub.Answer(nameof(IUserManager.UpdatePolicyAsync), _ => Task.CompletedTask);
        _userStub.Answer(nameof(IUserManager.UpdateConfigurationAsync), _ => Task.CompletedTask);
        _userStub.Answer(nameof(IUserManager.ClearProfileImageAsync), _ =>
        {
            _localUser.ProfileImage = null;
            return Task.CompletedTask;
        });
        _userStub.Answer(nameof(IUserManager.UpdateUserAsync), _ => Task.CompletedTask);

        var (paths, pathStub) = InterfaceStub.Create<IServerApplicationPaths>();
        pathStub.Answer("get_UserConfigurationDirectoryPath", _ => Path.Combine(_tempDir, "users"));
        (_serverConfig, var configStub) = InterfaceStub.Create<IServerConfigurationManager>();
        configStub.Answer("get_ApplicationPaths", _ => paths);
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

    private sealed class Probe : SyncMissingUserTask
    {
        public Probe(SyncMissingUserTaskTests test)
            : base(NullLogger<SyncMissingUserTask>.Instance, test._config, test._factory, test._users, null!, test._serverConfig, test._manager)
        {
        }

        public string MutexKey => ModuleMutexKey;

        public Task Apply(UserSyncItem record) => ApplyAsync(record, CancellationToken.None);

        public Task Verify(UserSyncItem record) => VerifyAfterApplyAsync(record, CancellationToken.None);

        public void Succeeded(UserSyncItem record) => OnApplySucceeded(record);

        public bool Enabled() => IsEnabled();

        public IDisposable? Guard(UserSyncItem record) => EnterApplyGuard(record);

        public void Completed(PluginConfiguration config, DateTime at) => RecordRunCompleted(config, at);

        public void Connect(ScanSource source) => Sources = new[] { source };
    }

    private Probe NewTask() => new(this);

    private Probe TaskWithSource(out ScanSource source)
    {
        var task = NewTask();
        source = new ScanSource(_server, _factory.ClientFor(_server), 0);
        task.Connect(source);
        return task;
    }

    private UserSyncItem Row(string category, string? merged = null) => new()
    {
        SourceUserId = SourceUser.ToString("N"),
        LocalUserId = _localUser.Id.ToString("N"),
        SourceUserName = "Alice",
        LocalUserName = "alice",
        PropertyCategory = category,
        MergedValue = merged,
        ServerKey = ServerKey,
        Status = SyncStatus.Queued
    };

    private int Calls(string member) => _userStub.Calls.Count(c => c.Name == member);

    private string ImageRoute => "/Users/" + SourceUser + "/Images/Primary";

    private string ProfilePath => Path.Combine(_tempDir, "users", "alice", "profile.jpg");

    /// <summary>
    /// A queued policy row is written to the local user, read back, and stored as Synced with the run stamped.
    /// True: a policy change on the source reaches the local user and the row stops being queued.
    /// False: the change never lands, or lands while the row is applied again on every run.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_QueuedPolicyRow_AppliesItAndMarksItSynced()
    {
        var row = Row(UserPropertyCategory.Policy);
        row.UpdateMergedValue("{\"EnableMediaPlayback\":false,\"IsHidden\":true}");
        _manager.Upsert(row);

        await NewTask().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        var stored = _manager.GetByKey((row.SourceUserId, row.LocalUserId, UserPropertyCategory.Policy))!;
        Assert.Equal(SyncStatus.Synced, stored.Status);
        Assert.False(_policy!.EnableMediaPlayback);
        Assert.Equal(1, Calls(nameof(IUserManager.UpdatePolicyAsync)));
        Assert.NotNull(_config.Configuration.LastUserSyncTime);
    }

    /// <summary>
    /// A row whose local user is gone becomes Errored, naming the user, and the run is recorded as unclean.
    /// True: a deleted local user shows as a row error the administrator can act on.
    /// False: the row is marked Synced, or the whole run fails for one missing user.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LocalUserGone_ErrorsTheRowWithTheUserName()
    {
        var row = Row(UserPropertyCategory.Policy);
        row.UpdateMergedValue("{\"IsHidden\":true}");
        _manager.Upsert(row);
        _userExists = false;

        await NewTask().ExecuteAsync(new TaskProgress(), CancellationToken.None);

        var stored = _manager.GetByKey((row.SourceUserId, row.LocalUserId, UserPropertyCategory.Policy))!;
        Assert.Equal(SyncStatus.Errored, stored.Status);
        Assert.Equal("Local user not found: alice", stored.Reason);
        Assert.Equal("User", Assert.Single(_config.Configuration.LastRunFailures).ModuleKey);
    }

    /// <summary>
    /// The merged policy's properties are set on the local policy, saved once, and recorded as what this server holds.
    /// True: each copied setting takes the source's value and the next scan sees them as equal.
    /// False: settings stay as they were, or the row is queued again by the next scan.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_Policy_SetsThePropertiesAndSavesOnce()
    {
        var row = Row(UserPropertyCategory.Policy, "{\"EnableMediaPlayback\":false,\"IsHidden\":true,\"MaxParentalRating\":13}");

        await NewTask().Apply(row);

        Assert.False(_policy!.EnableMediaPlayback);
        Assert.True(_policy.IsHidden);
        Assert.Equal(13, _policy.MaxParentalRating);
        Assert.Equal(1, Calls(nameof(IUserManager.UpdatePolicyAsync)));
        Assert.Equal(row.MergedValue, row.LocalValue);
    }

    /// <summary>
    /// A property that must never be copied, or that the local policy does not have, is skipped, and with nothing left to write nothing is saved.
    /// True: a tampered table row cannot make a local user an administrator.
    /// False: a stored value is trusted and administrator rights follow the sync table.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_PolicyPropertyThatMayNotBeCopied_IsNotWritten()
    {
        var row = Row(UserPropertyCategory.Policy, "{\"IsAdministrator\":true,\"NoSuchSetting\":1}");

        await NewTask().Apply(row);

        Assert.False(_policy!.IsAdministrator);
        Assert.Equal(0, Calls(nameof(IUserManager.UpdatePolicyAsync)));
    }

    /// <summary>
    /// A value that cannot be read as the property's type is skipped and the rest are still written.
    /// True: one malformed setting does not stop the others from syncing.
    /// False: a single bad value errors the whole policy row forever.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_PolicyValueOfTheWrongType_SkipsOnlyThatProperty()
    {
        var row = Row(UserPropertyCategory.Policy, "{\"EnableMediaPlayback\":\"nope\",\"IsHidden\":true}");

        await NewTask().Apply(row);

        Assert.True(_policy!.EnableMediaPlayback);
        Assert.True(_policy.IsHidden);
        Assert.Equal(1, Calls(nameof(IUserManager.UpdatePolicyAsync)));
    }

    /// <summary>
    /// A row with no merged value writes and saves nothing.
    /// True: an empty row is a quiet no-op.
    /// False: the apply throws on a null value, or saves an unchanged policy.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    public async Task ApplyAsync_NoMergedValue_SavesNothing(string? merged)
    {
        var row = Row(UserPropertyCategory.Policy, merged);

        await NewTask().Apply(row);

        Assert.Equal(0, Calls(nameof(IUserManager.UpdatePolicyAsync)));
        Assert.Equal(merged, row.LocalValue);
    }

    /// <summary>
    /// The merged configuration's properties are set on the local configuration and saved through the configuration save.
    /// True: playback preferences follow the source through their own save path.
    /// False: configuration is written through the policy save, or not at all.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_Configuration_SetsThePropertiesAndSaves()
    {
        var row = Row(UserPropertyCategory.Configuration, "{\"PlayDefaultAudioTrack\":false,\"HidePlayedInLatest\":false}");

        await NewTask().Apply(row);

        Assert.False(_configuration!.PlayDefaultAudioTrack);
        Assert.False(_configuration.HidePlayedInLatest);
        Assert.Equal(1, Calls(nameof(IUserManager.UpdateConfigurationAsync)));
        Assert.Equal(0, Calls(nameof(IUserManager.UpdatePolicyAsync)));
    }

    /// <summary>
    /// A local user whose policy cannot be read fails the apply, naming the user.
    /// True: the row errors with a reason and nothing is saved.
    /// False: the apply fails with a null reference and no usable reason.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_LocalPolicyUnavailable_Throws()
    {
        _policy = null;
        var row = Row(UserPropertyCategory.Policy, "{\"IsHidden\":true}");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Apply(row));

        Assert.Equal("Could not retrieve current policy for user alice", thrown.Message);
    }

    /// <summary>
    /// A row of an unknown category is refused by the apply and by the check.
    /// True: a row from a newer or damaged table errors with its category named.
    /// False: an unknown category is silently marked Synced.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_UnknownCategory_Throws()
    {
        var row = Row("Preferences", "{}");

        var applied = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Apply(row));
        var verified = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Verify(row));

        Assert.Equal("Unknown property category: Preferences", applied.Message);
        Assert.Equal("Unknown property category: Preferences", verified.Message);
    }

    /// <summary>
    /// A policy that reads back as merged passes, and one that differs fails naming the properties.
    /// True: a setting Jellyfin did not keep errors the row with that setting's name.
    /// False: the row is marked Synced while the local user still has the old setting.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_Policy_PassesWhenEqualAndNamesWhatDiffers()
    {
        _policy!.IsHidden = true;
        var matching = Row(UserPropertyCategory.Policy, "{\"IsHidden\":true,\"NoSuchSetting\":1}");
        var differing = Row(UserPropertyCategory.Policy, "{\"IsHidden\":false,\"EnableMediaPlayback\":false}");

        var passed = await Record.ExceptionAsync(() => NewTask().Verify(matching));
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Verify(differing));

        Assert.Null(passed);
        Assert.Equal("Policy verification mismatch on: IsHidden, EnableMediaPlayback", failed.Message);
    }

    /// <summary>
    /// A policy that cannot be read back fails the check.
    /// True: an apply that cannot be confirmed is not called a success.
    /// False: the row is marked Synced on trust.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_PolicyUnavailable_Throws()
    {
        _policy = null;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Verify(Row(UserPropertyCategory.Policy, "{\"IsHidden\":true}")));

        Assert.Equal("policy unavailable after apply (cannot verify)", thrown.Message);
    }

    /// <summary>
    /// A configuration that differs from the merged value fails the check naming the property.
    /// True: a preference Jellyfin did not keep errors the row.
    /// False: the row is marked Synced while the preference is unchanged.
    /// </summary>
    [Fact]
    public async Task VerifyAfterApplyAsync_ConfigurationDiffers_NamesTheProperty()
    {
        _configuration!.PlayDefaultAudioTrack = true;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Verify(Row(UserPropertyCategory.Configuration, "{\"PlayDefaultAudioTrack\":false}")));

        Assert.Equal("Configuration verification mismatch on: PlayDefaultAudioTrack", thrown.Message);
    }

    /// <summary>
    /// An image this server already holds is marked synced without a download.
    /// True: an unchanged profile image costs no request.
    /// False: every run downloads and rewrites every profile image.
    /// </summary>
    [Theory]
    [InlineData("abc", "ABC", 10L, 10L)]
    [InlineData(null, null, 10L, 10L)]
    public async Task ApplyAsync_ProfileImageAlreadyHere_MarksItSyncedWithoutADownload(string? sourceHash, string? localHash, long sourceSize, long localSize)
    {
        var row = Row(UserPropertyCategory.ProfileImage);
        row.SourceImageHash = sourceHash;
        row.LocalImageHash = localHash;
        row.SourceImageSize = sourceSize;
        row.LocalImageSize = localSize;
        var task = TaskWithSource(out var source);

        await task.Apply(row);
        source.Dispose();

        Assert.Equal(sourceHash, row.SyncedImageHash);
        Assert.Equal(sourceSize, row.SyncedImageSize);
        Assert.Empty(_factory.HandlerFor(ServerKey).Requests);
    }

    /// <summary>
    /// When the source has no image, a local image is removed and the row records that neither side has one.
    /// True: removing a profile image on the source removes it here.
    /// False: a removed image stays here forever, or the row is queued again every scan.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ApplyAsync_SourceHasNoProfileImage_ClearsTheLocalOne(bool localHasImage, int expectedClears)
    {
        _localUser.ProfileImage = localHasImage ? new ImageInfo(Path.Combine(_tempDir, "old.jpg")) : null;
        var row = Row(UserPropertyCategory.ProfileImage);
        row.LocalImageHash = "old";
        row.LocalImageSize = 5;
        var task = TaskWithSource(out var source);

        await task.Apply(row);
        source.Dispose();

        Assert.Equal(expectedClears, Calls(nameof(IUserManager.ClearProfileImageAsync)));
        Assert.Null(row.LocalImageHash);
        Assert.Equal(0, row.LocalImageSize);
        Assert.Equal(0, row.SyncedImageSize);
        Assert.Empty(_factory.HandlerFor(ServerKey).Requests);
    }

    /// <summary>
    /// A new image is downloaded, put in place as the user's profile image, saved on the user, and its hash and size recorded, with no working file left behind.
    /// True: the local user shows the source's image and the next scan sees the two as equal.
    /// False: the image is not replaced, or leftovers pile up in the user's folder.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_NewProfileImage_ReplacesTheLocalImage()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        _factory.HandlerFor(ServerKey).On(ImageRoute, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var row = Row(UserPropertyCategory.ProfileImage);
        row.SourceImageHash = "new";
        row.SourceImageSize = 5;
        row.SourceValue = "source";
        var task = TaskWithSource(out var source);

        await task.Apply(row);
        source.Dispose();

        Assert.Equal(bytes, File.ReadAllBytes(ProfilePath));
        Assert.Equal(ProfilePath, _localUser.ProfileImage!.Path);
        Assert.Equal(1, Calls(nameof(IUserManager.UpdateUserAsync)));
        Assert.Equal(5, row.LocalImageSize);
        Assert.False(string.IsNullOrEmpty(row.LocalImageHash));
        Assert.Equal(row.LocalImageHash, row.SyncedImageHash);
        Assert.Equal(new[] { ProfilePath }, Directory.GetFiles(Path.GetDirectoryName(ProfilePath)!));
    }

    /// <summary>
    /// A source that has no image to give, or gives an empty one, fails the apply and leaves the local image untouched.
    /// True: a failed download never costs the user the image they have.
    /// False: the old image is cleared before the new one is known to exist.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Failed to download profile image for Alice")]
    [InlineData(HttpStatusCode.OK, "Downloaded profile image for Alice was empty (0 bytes)")]
    public async Task ApplyAsync_ProfileImageDownloadGivesNothing_ThrowsAndKeepsTheLocalImage(HttpStatusCode status, string reason)
    {
        _factory.HandlerFor(ServerKey).On(ImageRoute, _ => new HttpResponseMessage(status) { Content = new ByteArrayContent(Array.Empty<byte>()) });
        var existing = new ImageInfo(Path.Combine(_tempDir, "old.jpg"));
        _localUser.ProfileImage = existing;
        var row = Row(UserPropertyCategory.ProfileImage);
        row.SourceImageHash = "new";
        row.SourceImageSize = 5;
        var task = TaskWithSource(out var source);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => task.Apply(row));
        source.Dispose();

        Assert.Equal(reason, thrown.Message);
        Assert.Same(existing, _localUser.ProfileImage);
        Assert.Equal(0, Calls(nameof(IUserManager.ClearProfileImageAsync)));
        Assert.False(File.Exists(ProfilePath));
    }

    /// <summary>
    /// A profile image row whose server did not connect fails, naming the server.
    /// True: the row errors with a reason that points at the unreachable server.
    /// False: the image is fetched from some other server, with another user's id.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ProfileImageWithoutItsServer_ThrowsNamingTheServer()
    {
        var row = Row(UserPropertyCategory.ProfileImage);
        row.SourceImageHash = "new";

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => NewTask().Apply(row));

        Assert.Equal("Source server 'Source A' is not available this run", thrown.Message);
    }

    /// <summary>
    /// The image check passes only when the local image matches the source's, by hash, else by size, or both are absent.
    /// True: a row is marked Synced only when the local image really is the source's.
    /// False: a wrong or leftover image is recorded as synced and never corrected.
    /// </summary>
    [Theory]
    [InlineData(null, null, null, null, null)]
    [InlineData("a", 3L, "A", 9L, null)]
    [InlineData(null, 3L, null, 3L, null)]
    [InlineData(null, null, "x", null, "ProfileImage: local still has an image after apply intended to clear it")]
    [InlineData(null, 0L, null, 4L, "ProfileImage: local still has an image after apply intended to clear it")]
    [InlineData("a", 3L, "b", 3L, "ProfileImage: hash mismatch after apply (source a, local b)")]
    [InlineData(null, 3L, null, 4L, "ProfileImage: size mismatch after apply (source 3, local 4)")]
    public async Task VerifyAfterApplyAsync_ProfileImage_FollowsHashThenSize(string? sourceHash, long? sourceSize, string? localHash, long? localSize, string? failure)
    {
        var row = Row(UserPropertyCategory.ProfileImage);
        row.SourceImageHash = sourceHash;
        row.SourceImageSize = sourceSize;
        row.LocalImageHash = localHash;
        row.LocalImageSize = localSize;

        var thrown = await Record.ExceptionAsync(() => NewTask().Verify(row));

        Assert.Equal(failure, thrown?.Message);
    }

    /// <summary>
    /// After a successful apply a settings row records the merged value as what this server holds, and an image row leaves its value alone.
    /// True: the next scan can tell a change made here from one that was just applied.
    /// False: an applied change looks like a local edit and is sent back to the source.
    /// </summary>
    [Fact]
    public void OnApplySucceeded_SettingsRow_RecordsTheMergedValueAsLocal()
    {
        var policy = Row(UserPropertyCategory.Policy, "{\"IsHidden\":true}");
        policy.LocalValue = "{\"IsHidden\":false}";
        var image = Row(UserPropertyCategory.ProfileImage, "merged");
        image.LocalValue = "local";
        image.SourceImageHash = "hash";
        var task = NewTask();

        task.Succeeded(policy);
        task.Succeeded(image);

        Assert.Equal("{\"IsHidden\":true}", policy.LocalValue);
        Assert.False(policy.HasChanges);
        Assert.Equal("local", image.LocalValue);
        Assert.Equal("hash", image.SyncedImageHash);
    }

    /// <summary>
    /// The task runs only with Users on and a source configured, and asks no server otherwise.
    /// True: turning Users off stops queued settings from being applied.
    /// False: settings keep changing after the sync type was turned off.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task ExecuteAsync_SwitchedOffOrNoSource_AsksNoServer(bool usersOn, bool hasServer, bool expected)
    {
        _config.Configuration.EnableUserSync = usersOn;
        _server.IsEnabled = hasServer;
        var task = NewTask();

        await task.ExecuteAsync(new TaskProgress(), CancellationToken.None);

        Assert.Equal(expected, task.Enabled());
        Assert.Equal(expected, _factory.Created.Count > 0);
    }

    /// <summary>
    /// With no stamp recorder kept, no write is registered.
    /// True: a server that keeps no stamps applies rows without a guard.
    /// False: the apply throws before it reaches the user.
    /// </summary>
    [Fact]
    public void EnterApplyGuard_NoRecorder_ReturnsNull()
    {
        Assert.Null(NewTask().Guard(Row(UserPropertyCategory.Policy)));
    }

    /// <summary>
    /// A finished run stamps the Users time, under the lock the Users scan shares.
    /// True: the dashboard shows when Users last ran, and the scan and the apply never overlap.
    /// False: another sync type's time moves, or the scan overwrites a row mid apply.
    /// </summary>
    [Fact]
    public void RecordRunCompleted_StampsTheUsersTimeUnderTheUserLock()
    {
        var task = NewTask();
        var config = new PluginConfiguration();
        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        task.Completed(config, at);

        Assert.Equal(at, config.LastUserSyncTime);
        Assert.Null(config.LastHistorySyncTime);
        Assert.Equal("User", task.MutexKey);
    }
}

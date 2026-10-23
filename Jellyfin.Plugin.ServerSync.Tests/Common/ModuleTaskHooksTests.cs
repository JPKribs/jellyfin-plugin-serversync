using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Common;

/// <summary>
/// The per-module overrides of the refresh and sync task bases: which rows each module's prune may
/// touch, and what each module marks as applied after a successful apply. The bases are tested on
/// their own. These pin the module-specific answers, reached through probe subclasses of the real tasks.
/// </summary>
public sealed class ModuleTaskHooksTests : IDisposable
{
    private static readonly string SourceUser = Guid.Parse("11111111111111111111111111111111").ToString("N");
    private static readonly string LocalUser = Guid.Parse("22222222222222222222222222222222").ToString("N");

    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly FixedProvider _provider;
    private readonly FakeConfigManager _config = new();
    private readonly SourceServer _server;

    public ModuleTaskHooksTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-hooks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _provider = new FixedProvider(_database);
        _server = new SourceServer
        {
            Key = "server-a",
            Url = "http://server-a:8096",
            ApiKey = "k",
            Mode = ServerMode.Pull,
            LibraryMappings = { new LibraryMapping { SourceLibraryId = "lib-1", LocalRootPath = "/local" } },
            UserMappings = { new UserMapping { SourceUserId = SourceUser, LocalUserId = LocalUser } }
        };
        _config.Configuration.Servers.Add(_server);
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

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }

    private sealed class FakeConfigManager : IPluginConfigurationManager
    {
        public PluginConfiguration Configuration { get; } = new();

        public string DecryptApiKey(string protectedKey) => protectedKey;

        public string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl) => requestApiKey ?? string.Empty;

        public void SaveConfiguration()
        {
        }

        public string GetTempDownloadPath() => string.Empty;

        public string LocalServerName => string.Empty;

        public string PluginVersion => "0.0.0-test";
    }

    private sealed class FakeClientFactory : ISourceServerClientFactory
    {
        public SourceServerClient Create(SourceServer server) => throw new NotSupportedException();
    }

    private sealed class HistoryRefreshProbe : RefreshHistorySyncTableTask
    {
        public HistoryRefreshProbe(IPluginConfigurationManager config, HistorySyncTableManager manager)
            : base(NullLogger<RefreshHistorySyncTableTask>.Instance, config, new FakeClientFactory(), null!, null!, manager)
        {
        }

        public bool InScope(HistorySyncItem record) => IsInScope(record);
    }

    private sealed class UserRefreshProbe : RefreshUserSyncTableTask
    {
        public UserRefreshProbe(IPluginConfigurationManager config, UserSyncTableManager manager)
            : base(NullLogger<RefreshUserSyncTableTask>.Instance, config, new FakeClientFactory(), null!, null!, manager)
        {
        }

        public bool InScope(UserSyncItem record) => IsInScope(record);
    }

    private sealed class MetadataRefreshProbe : RefreshMetadataSyncTableTask
    {
        public MetadataRefreshProbe(IPluginConfigurationManager config, MetadataSyncTableManager manager)
            : base(NullLogger<RefreshMetadataSyncTableTask>.Instance, config, new FakeClientFactory(), null!, manager, null!, null!)
        {
        }

        public bool InScope(MetadataSyncItem record) => IsInScope(record);
    }

    private sealed class HistorySyncProbe : SyncMissingHistoryTask
    {
        public HistorySyncProbe(IPluginConfigurationManager config, HistorySyncTableManager manager)
            : base(NullLogger<SyncMissingHistoryTask>.Instance, config, new FakeClientFactory(), null!, manager, null!)
        {
        }

        public void Succeeded(HistorySyncItem record) => OnApplySucceeded(record);
    }

    private sealed class MetadataSyncProbe : SyncMissingMetadataTask
    {
        public MetadataSyncProbe(IPluginConfigurationManager config, MetadataSyncTableManager manager)
            : base(NullLogger<SyncMissingMetadataTask>.Instance, null!, null!, new FakeClientFactory(), config, null!, manager, null!)
        {
        }

        public void Succeeded(MetadataSyncItem record) => OnApplySucceeded(record);
    }

    private sealed class PeopleSyncProbe : SyncMissingPeopleTask
    {
        public PeopleSyncProbe(IPluginConfigurationManager config, PeopleSyncTableManager manager)
            : base(NullLogger<SyncMissingPeopleTask>.Instance, null!, null!, new FakeClientFactory(), config, manager, null!)
        {
        }

        public void Succeeded(PeopleSyncItem record) => OnApplySucceeded(record);
    }

    private HistoryRefreshProbe HistoryRefresh() => new(_config, new HistorySyncTableManager(_provider, NullLogger<HistorySyncTableManager>.Instance));

    private static HistorySyncItem History(string library = "lib-1", string? sourceUser = null, string? localUser = null) => new()
    {
        SourceLibraryId = library,
        SourceUserId = sourceUser ?? SourceUser,
        LocalUserId = localUser ?? LocalUser,
        SourceItemId = "item"
    };

    // -----------------------------------------------------------------------
    // Prune scope
    // -----------------------------------------------------------------------

    /// <summary>
    /// A history row is in scope only while both its library mapping and its exact user mapping are enabled.
    /// True: disabling a library or a user mapping keeps that history, Ignored overrides included.
    /// False: unticking a user mapping prunes every history row for that user.
    /// </summary>
    [Fact]
    public void HistoryRefresh_InScope_NeedsBothLibraryAndUserMapping()
    {
        var task = HistoryRefresh();

        Assert.True(task.InScope(History()));
        Assert.True(task.InScope(History(sourceUser: SourceUser.ToUpperInvariant())));
        Assert.False(task.InScope(History(library: "lib-other")));
        Assert.False(task.InScope(History(localUser: Guid.NewGuid().ToString("N"))));

        _server.UserMappings[0].IsEnabled = false;
        Assert.False(task.InScope(History()));

        _server.UserMappings[0].IsEnabled = true;
        _server.LibraryMappings[0].IsEnabled = false;
        Assert.False(task.InScope(History()));
    }

    /// <summary>
    /// A server that is disabled takes its mappings out of scope with it.
    /// True: switching a server off preserves all of its rows.
    /// False: disabling a server prunes the history it brought.
    /// </summary>
    [Fact]
    public void HistoryRefresh_DisabledServer_IsOutOfScope()
    {
        var task = HistoryRefresh();
        _server.IsEnabled = false;

        Assert.False(task.InScope(History()));
    }

    /// <summary>
    /// A user row is in scope only while its exact user mapping is enabled, and, on a standard user's
    /// key, only for that user.
    /// True: a disabled mapping, or a key that may only sync itself, keeps the other users' rows.
    /// False: rows for a user the key may no longer sync are pruned, losing their Ignored overrides.
    /// </summary>
    [Fact]
    public void UserRefresh_InScope_FollowsTheEnabledUserMapping()
    {
        var task = new UserRefreshProbe(_config, new UserSyncTableManager(_provider, NullLogger<UserSyncTableManager>.Instance));
        var row = new UserSyncItem { SourceUserId = SourceUser, LocalUserId = LocalUser, PropertyCategory = "Policy" };

        Assert.True(task.InScope(row));
        Assert.False(task.InScope(new UserSyncItem { SourceUserId = SourceUser, LocalUserId = Guid.NewGuid().ToString("N"), PropertyCategory = "Policy" }));

        _server.AccessLevel = SourceServer.StandardUserAccess;
        _server.AuthenticatedUserId = Guid.NewGuid().ToString("N");
        Assert.False(task.InScope(row));

        _server.AuthenticatedUserId = SourceUser;
        Assert.True(task.InScope(row));

        _server.UserMappings[0].IsEnabled = false;
        Assert.False(task.InScope(row));
    }

    /// <summary>
    /// A metadata row is in scope only while its library mapping is enabled.
    /// True: disabling a mapping keeps its metadata rows and their overrides.
    /// False: toggling a mapping off and on loses every Ignored metadata row in it.
    /// </summary>
    [Fact]
    public void MetadataRefresh_InScope_FollowsTheEnabledLibraryMapping()
    {
        var task = new MetadataRefreshProbe(_config, new MetadataSyncTableManager(_provider, NullLogger<MetadataSyncTableManager>.Instance));
        var row = new MetadataSyncItem { SourceLibraryId = "lib-1", SourceItemId = "item" };

        Assert.True(task.InScope(row));
        Assert.False(task.InScope(new MetadataSyncItem { SourceLibraryId = "lib-other", SourceItemId = "item" }));

        _server.LibraryMappings[0].IsEnabled = false;
        Assert.False(task.InScope(row));
    }

    // -----------------------------------------------------------------------
    // What a successful apply marks
    // -----------------------------------------------------------------------

    /// <summary>
    /// After a history apply the local snapshot takes the merged values, so the row reads as settled
    /// until the next refresh re-reads the local state.
    /// True: an applied row is not queued again by the same run's bookkeeping.
    /// False: the row still compares as changed and is re-applied on every run.
    /// </summary>
    [Fact]
    public void HistorySync_OnApplySucceeded_CopiesMergedIntoLocal()
    {
        var task = new HistorySyncProbe(_config, new HistorySyncTableManager(_provider, NullLogger<HistorySyncTableManager>.Instance));
        var played = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var row = History();
        row.LocalIsPlayed = false;
        row.LocalPlayCount = 0;
        row.MergedIsPlayed = true;
        row.MergedPlayCount = 3;
        row.MergedPlaybackPositionTicks = 42;
        row.MergedLastPlayedDate = played;
        row.MergedIsFavorite = true;

        task.Succeeded(row);

        Assert.True(row.LocalIsPlayed);
        Assert.Equal(3, row.LocalPlayCount);
        Assert.Equal(42, row.LocalPlaybackPositionTicks);
        Assert.Equal(played, row.LocalLastPlayedDate);
        Assert.True(row.LocalIsFavorite);
    }

    /// <summary>
    /// Metadata and People mark nothing on success: their categories are marked one by one as each
    /// verifies, so the whole-row default must not run.
    /// True: a category that was not applied this run is compared again next refresh.
    /// False: an unapplied category's synced hash advances and the change is never applied.
    /// </summary>
    [Fact]
    public void MetadataAndPeopleSync_OnApplySucceeded_MarkNoCategory()
    {
        var metadataTask = new MetadataSyncProbe(_config, new MetadataSyncTableManager(_provider, NullLogger<MetadataSyncTableManager>.Instance));
        var metadata = new MetadataSyncItem { SourceLibraryId = "lib-1", SourceItemId = "item" };
        metadata.Metadata.SourceHash = "m";
        metadata.Images.SourceHash = "i";
        metadata.People.SourceHash = "p";

        metadataTask.Succeeded(metadata);

        Assert.Null(metadata.Metadata.SyncedHash);
        Assert.Null(metadata.Images.SyncedHash);
        Assert.Null(metadata.People.SyncedHash);

        var peopleTask = new PeopleSyncProbe(_config, new PeopleSyncTableManager(_provider, NullLogger<PeopleSyncTableManager>.Instance));
        var person = new PeopleSyncItem { PersonName = "Someone" };
        person.Metadata.SourceHash = "m";
        person.Images.SourceHash = "i";

        peopleTask.Succeeded(person);

        Assert.Null(person.Metadata.SyncedHash);
        Assert.Null(person.Images.SyncedHash);
    }

    // -----------------------------------------------------------------------
    // Metadata request shaping
    // -----------------------------------------------------------------------

    /// <summary>
    /// The metadata listing always asks for path, creation date and provider ids, and asks for each
    /// category's fields only when that category is synced. People in particular is left out when off.
    /// True: matching always works, and a library scan without people sync is not slowed by cast lists.
    /// False: rows cannot be matched to local items, or every page carries the full cast for nothing.
    /// </summary>
    [Fact]
    public void BuildRequestedFields_FollowsTheEnabledCategories()
    {
        var config = new PluginConfiguration
        {
            MetadataSyncMetadata = false,
            MetadataSyncGenres = false,
            MetadataSyncTags = false,
            MetadataSyncStudios = false,
            MetadataSyncPeople = false
        };

        var minimal = RefreshMetadataSyncTableTask.BuildRequestedFields(config);
        Assert.Equal(new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.ProviderIds }, minimal);

        config.MetadataSyncPeople = true;
        config.MetadataSyncTags = true;
        var some = RefreshMetadataSyncTableTask.BuildRequestedFields(config);
        Assert.Contains(ItemFields.People, some);
        Assert.Contains(ItemFields.Tags, some);
        Assert.DoesNotContain(ItemFields.Genres, some);
        Assert.DoesNotContain(ItemFields.Overview, some);

        config.MetadataSyncMetadata = true;
        var full = RefreshMetadataSyncTableTask.BuildRequestedFields(config);
        Assert.Contains(ItemFields.Overview, full);
        Assert.Equal(full.Length, full.Distinct().Count());
    }

    /// <summary>
    /// Series, seasons, albums, artists and box sets are folders to the metadata module, and leaf items are not.
    /// True: folder rows are matched by folder rules and leaf rows by file.
    /// False: a series is matched as a file and never finds its local copy.
    /// </summary>
    [Theory]
    [InlineData(BaseItemDto_Type.Series, true)]
    [InlineData(BaseItemDto_Type.Season, true)]
    [InlineData(BaseItemDto_Type.MusicAlbum, true)]
    [InlineData(BaseItemDto_Type.MusicArtist, true)]
    [InlineData(BaseItemDto_Type.BoxSet, true)]
    [InlineData(BaseItemDto_Type.Movie, false)]
    [InlineData(BaseItemDto_Type.Episode, false)]
    [InlineData(BaseItemDto_Type.Audio, false)]
    [InlineData(null, false)]
    public void IsFolderType_MatchesTheFolderKinds(BaseItemDto_Type? type, bool expected)
        => Assert.Equal(expected, RefreshMetadataSyncTableTask.IsFolderType(type));
}

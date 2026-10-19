using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Cleanup;
using Jellyfin.Plugin.ServerSync.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// Removed files set to Ignore, run through the Content refresh's prune against a real sync
/// database and real folders: a title whose files are all gone from the source is hidden with a
/// .ignore file, and shown again when it returns or when Removed files leaves Ignore.
/// </summary>
public sealed class RemovedFilesIgnoreTests : IDisposable
{
    private const string LibraryId = "lib-1";

    private readonly string _tempDir;
    private readonly string _root;
    private readonly SyncDatabase _database;
    private readonly ContentSyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly TestableRefresh _task;

    public RemovedFilesIgnoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(_root);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, Path.Combine(_tempDir, "data"));
        var provider = new FixedProvider(_database);
        _manager = new ContentSyncTableManager(provider, NullLogger<ContentSyncTableManager>.Instance);

        var server = new SourceServer { Name = "Source", Url = "http://source:8096", ApiKey = "k", Mode = ServerMode.Pull, IsEnabled = true };
        server.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = LibraryId, SourceRootPath = "/src/Movies", LocalLibraryId = "local-1", LocalRootPath = _root, IsEnabled = true });
        _config.Configuration.Servers.Add(server);
        _config.Configuration.MissingContentAction = MissingContentAction.Ignore;

        var (libraryManager, _) = InterfaceStub.Create<ILibraryManager>();
        _task = new TestableRefresh(_config, _manager, libraryManager, new ScanWatermarkStore(provider, NullLogger<ScanWatermarkStore>.Instance));
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

    private sealed class NoClients : ISourceServerClientFactory
    {
        public SourceServerClient Create(SourceServer server) => throw new NotSupportedException("The prune never connects.");
    }

    /// <summary>Runs the prune step on its own, as the refresh runs it after listing the source.</summary>
    private sealed class TestableRefresh : UpdateSyncTablesTask
    {
        public TestableRefresh(IPluginConfigurationManager config, ContentSyncTableManager manager, ILibraryManager libraryManager, ScanWatermarkStore watermarks)
            : base(NullLogger<UpdateSyncTablesTask>.Instance, config, manager, new NoClients(), libraryManager, watermarks)
        {
        }

        public Task<int> PruneAsync(ContentSyncTableManager manager, IEnumerable<string> listedNow)
        {
            OnRunStarting();
            var seen = new HashSet<string>(listedNow, StringComparer.OrdinalIgnoreCase);

            // The listing found something in the library, so the empty answer guard does not hold the prune.
            RecordMappingSeen(LibraryId, Math.Max(1, seen.Count));
            var existing = manager.GetAllStrict().ToDictionary(row => row.SourceItemId, StringComparer.OrdinalIgnoreCase);
            return PruneStaleAsync(existing, seen, new Progress<double>(), CancellationToken.None);
        }
    }

    private SyncItem Track(string title, string file, SyncStatus status = SyncStatus.Synced)
    {
        var folder = Path.Combine(_root, title);
        Directory.CreateDirectory(folder);
        var localPath = Path.Combine(folder, file);
        File.WriteAllText(localPath, "media");
        var row = new SyncItem
        {
            SourceLibraryId = LibraryId,
            LocalLibraryId = "local-1",
            SourceItemId = Guid.NewGuid().ToString("N"),
            SourcePath = "/src/Movies/" + title + "/" + file,
            SourceSize = 5,
            SourceCreateDate = DateTime.UtcNow,
            LocalPath = localPath,
            Status = status,
            StatusDate = DateTime.UtcNow
        };
        _manager.Upsert(row);
        return row;
    }

    private string IgnoreFile(string title) => Path.Combine(_root, title, IgnoreFileWriter.FileName);

    /// <summary>
    /// A title whose every file is gone from the source gets an empty .ignore and its rows become
    /// hidden. The files stay on disk, and a title that is still listed is left alone.
    /// True: Ignore hides what the source dropped without deleting a byte.
    /// False: the title stays visible, or a title still on the source is hidden.
    /// </summary>
    [Fact]
    public async Task GoneTitle_IsHiddenWithAnIgnoreFile_AndItsFilesStay()
    {
        var gone = Track("Gone (2020)", "Gone (2020).mkv");
        var kept = Track("Kept (2021)", "Kept (2021).mkv");

        await _task.PruneAsync(_manager, new[] { kept.SourceItemId });

        Assert.True(File.Exists(IgnoreFile("Gone (2020)")));
        Assert.Equal(0, new FileInfo(IgnoreFile("Gone (2020)")).Length);
        Assert.True(File.Exists(gone.LocalPath));
        var hidden = _manager.GetBySourceItemId(gone.SourceItemId)!;
        Assert.Equal(SyncStatus.Ignored, hidden.Status);
        Assert.Equal(SyncStateService.HiddenReason, hidden.Reason);

        Assert.False(File.Exists(IgnoreFile("Kept (2021)")));
        Assert.Equal(SyncStatus.Synced, _manager.GetBySourceItemId(kept.SourceItemId)!.Status);
    }

    /// <summary>
    /// A title with a file still listed by the source, or a media file the plugin does not track,
    /// is never hidden even when some of its rows are gone.
    /// True: one gone episode or a user's own file never hides a whole show.
    /// False: a .ignore hides media the user still has on the source or added themselves.
    /// </summary>
    [Fact]
    public async Task PartlyGoneTitle_OrUntrackedMedia_IsNotHidden()
    {
        var goneEpisode = Track("Show", "Show S01E01.mkv");
        var listedEpisode = Track("Show", "Show S01E02.mkv");
        var ownGone = Track("Own (2019)", "Own (2019).mkv");
        File.WriteAllText(Path.Combine(_root, "Own (2019)", "Own (2019) - Director's Cut.mkv"), "user's own file");

        await _task.PruneAsync(_manager, new[] { listedEpisode.SourceItemId });

        Assert.False(File.Exists(IgnoreFile("Show")));
        Assert.False(File.Exists(IgnoreFile("Own (2019)")));
        Assert.NotEqual(SyncStatus.Ignored, _manager.GetBySourceItemId(goneEpisode.SourceItemId)?.Status);
        Assert.NotEqual(SyncStatus.Ignored, _manager.GetBySourceItemId(ownGone.SourceItemId)?.Status);
    }

    /// <summary>
    /// A hidden title that the source lists again loses its .ignore and its rows go back to Synced.
    /// True: a title that returns on the source shows again on its own.
    /// False: it stays hidden after the .ignore is no longer wanted.
    /// </summary>
    [Fact]
    public async Task HiddenTitle_ReturningOnTheSource_IsShownAgain()
    {
        var gone = Track("Gone (2020)", "Gone (2020).mkv");
        await _task.PruneAsync(_manager, Array.Empty<string>());
        Assert.True(File.Exists(IgnoreFile("Gone (2020)")));

        await _task.PruneAsync(_manager, new[] { gone.SourceItemId });

        Assert.False(File.Exists(IgnoreFile("Gone (2020)")));
        Assert.Equal(SyncStatus.Synced, _manager.GetBySourceItemId(gone.SourceItemId)!.Status);
    }

    /// <summary>
    /// Switching Removed files away from Ignore shows every hidden title again, and a .ignore the
    /// plugin did not write (one with content) is never removed.
    /// True: leaving Ignore undoes only what Ignore did.
    /// False: titles stay hidden, or a user's own .ignore is deleted.
    /// </summary>
    [Fact]
    public async Task LeavingIgnore_ShowsHiddenTitles_AndKeepsAUsersOwnIgnore()
    {
        var gone = Track("Gone (2020)", "Gone (2020).mkv");
        var users = Track("Users (2018)", "Users (2018).mkv", SyncStatus.Ignored);
        File.WriteAllText(IgnoreFile("Users (2018)"), "mine");
        await _task.PruneAsync(_manager, new[] { users.SourceItemId });
        Assert.True(File.Exists(IgnoreFile("Gone (2020)")));

        _config.Configuration.MissingContentAction = MissingContentAction.Keep;
        await _task.PruneAsync(_manager, new[] { users.SourceItemId });

        Assert.False(File.Exists(IgnoreFile("Gone (2020)")));
        Assert.Equal(SyncStatus.Synced, _manager.GetBySourceItemId(gone.SourceItemId)!.Status);
        Assert.Equal("mine", File.ReadAllText(IgnoreFile("Users (2018)")));
        Assert.Equal(SyncStatus.Ignored, _manager.GetBySourceItemId(users.SourceItemId)!.Status);
    }
}

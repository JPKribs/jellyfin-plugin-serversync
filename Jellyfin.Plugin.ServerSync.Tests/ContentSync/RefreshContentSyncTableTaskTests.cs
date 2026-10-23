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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// The prune of Refresh Content Sync Table, run against a real <see cref="ContentSyncTableManager"/>
/// and real files in a temp library. This is where "gone from the source" turns into a scheduled
/// file deletion, a hidden title, or a cancelled deletion, so every decision is checked against the
/// table and the disk afterwards.
/// </summary>
public sealed class RefreshContentSyncTableTaskTests : IDisposable
{
    private const string Library = "lib-1";

    private readonly string _tempDir;
    private readonly string _localRoot;
    private readonly SyncDatabase _database;
    private readonly ContentSyncTableManager _manager;
    private readonly FakeConfigManager _config = new();
    private readonly LibraryMapping _mapping;
    private readonly Probe _task;

    public RefreshContentSyncTableTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-refresh-content-" + Guid.NewGuid().ToString("N"));
        _localRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_localRoot);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new ContentSyncTableManager(new FixedProvider(_database), NullLogger<ContentSyncTableManager>.Instance);

        _mapping = new LibraryMapping { SourceLibraryId = Library, LocalLibraryId = "local-1", SourceRootPath = "/src", LocalRootPath = _localRoot };
        _config.Configuration.Servers.Add(new SourceServer { Key = "server-a", Url = "http://server-a:8096", ApiKey = "k", Mode = ServerMode.Pull, LibraryMappings = { _mapping } });
        _task = new Probe(_config, _manager);
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
        public SourceServerClient Create(SourceServer server)
            => throw new NotSupportedException("The prune tests never connect.");
    }

    /// <summary>
    /// Reaches the protected prune and its per-mapping bookkeeping. The library manager and the
    /// watermark store are only used by the listing and finalize phases, which these tests do not run.
    /// </summary>
    private sealed class Probe : UpdateSyncTablesTask
    {
        public Probe(IPluginConfigurationManager config, ContentSyncTableManager manager)
            : base(NullLogger<UpdateSyncTablesTask>.Instance, config, manager, new FakeClientFactory(), null!, null!)
        {
        }

        public bool PruneCandidate(SyncItem record) => IsPruneCandidate(record);

        public void Seen(string sourceLibraryId, int count) => RecordMappingSeen(sourceLibraryId, count);

        public void StartRun() => OnRunStarting();

        public Task<int> Prune(IReadOnlyDictionary<string, SyncItem> existing, HashSet<string> seen)
            => PruneStaleAsync(existing, seen, new Progress<double>(), CancellationToken.None);
    }

    private void Mode(MissingContentAction action, ApprovalMode deleteMode = ApprovalMode.Enabled)
    {
        _config.Configuration.MissingContentAction = action;
        _config.Configuration.DeleteMissingContentMode = deleteMode;
    }

    /// <summary>Writes a tracked row, with a real local file unless <paramref name="withFile"/> is false.</summary>
    private SyncItem Track(string id, SyncStatus status = SyncStatus.Synced, string title = "Title", bool withFile = true, PendingType? pendingType = null, string library = Library, string? reason = null)
    {
        var localPath = Path.Combine(_localRoot, title, id + ".mkv");
        if (withFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            File.WriteAllText(localPath, "x");
        }

        var row = new SyncItem
        {
            SourceLibraryId = library,
            LocalLibraryId = "local-1",
            SourceItemId = id,
            SourcePath = "/src/" + title + "/" + id + ".mkv",
            SourceSize = 1,
            SourceCreateDate = DateTime.UtcNow,
            LocalPath = localPath,
            Status = status,
            PendingType = pendingType,
            StatusDate = DateTime.UtcNow,
            Reason = reason
        };
        _manager.Upsert(row);
        return _manager.GetByKey(id)!;
    }

    private Dictionary<string, SyncItem> Existing() => _manager.GetAllStrict().ToDictionary(r => r.SourceItemId, StringComparer.OrdinalIgnoreCase);

    private Task<int> Prune(params string[] seen) => _task.Prune(Existing(), new HashSet<string>(seen, StringComparer.OrdinalIgnoreCase));

    private SyncItem? Row(string id) => _manager.GetByKey(id);

    // -----------------------------------------------------------------------
    // Prune candidates
    // -----------------------------------------------------------------------

    /// <summary>
    /// Rows the prune would leave alone anyway, Ignored, Deleting and awaiting deletion approval, are
    /// not candidates, so they do not count toward the prune circuit breaker. Every other row is.
    /// True: removals that are already decided never add up into a tripped breaker.
    /// False: a library slowly emptying of approved deletions trips the breaker and blocks every later prune.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Ignored, null, false)]
    [InlineData(SyncStatus.Deleting, null, false)]
    [InlineData(SyncStatus.Pending, PendingType.Deletion, false)]
    [InlineData(SyncStatus.Pending, PendingType.Download, true)]
    [InlineData(SyncStatus.Pending, PendingType.Replacement, true)]
    [InlineData(SyncStatus.Synced, null, true)]
    [InlineData(SyncStatus.Queued, null, true)]
    [InlineData(SyncStatus.Errored, null, true)]
    public void IsPruneCandidate_ExcludesRowsAlreadyDecided(SyncStatus status, PendingType? pendingType, bool expected)
        => Assert.Equal(expected, _task.PruneCandidate(new SyncItem { Status = status, PendingType = pendingType }));

    // -----------------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------------

    /// <summary>
    /// With Delete and automatic deletion a stale synced row whose file is on disk is scheduled for
    /// deletion, a stale row whose file is gone is dropped, and seen rows are untouched.
    /// True: an item removed on the source is removed here, and nothing still on the source is.
    /// False: removed items linger forever, or a file still on the source is scheduled for deletion.
    /// </summary>
    [Fact]
    public async Task Delete_Automatic_SchedulesFilesAndDropsFilelessRows()
    {
        Mode(MissingContentAction.Delete);
        _task.Seen(Library, 1);
        Track("kept");
        Track("gone");
        Track("gone-no-file", withFile: false);

        var changed = await Prune("kept");

        Assert.Equal(2, changed);
        Assert.Equal(SyncStatus.Synced, Row("kept")!.Status);
        Assert.Equal(SyncStatus.Deleting, Row("gone")!.Status);
        Assert.Null(Row("gone-no-file"));
    }

    /// <summary>
    /// With deletion requiring approval a stale file is marked as awaiting approval instead.
    /// True: nothing is deleted without the user's say when they asked for approval.
    /// False: approval mode schedules deletions on its own.
    /// </summary>
    [Fact]
    public async Task Delete_RequireApproval_MarksPendingDeletion()
    {
        Mode(MissingContentAction.Delete, ApprovalMode.RequireApproval);
        _task.Seen(Library, 1);
        Track("gone");

        await Prune();

        var row = Row("gone")!;
        Assert.Equal(SyncStatus.Pending, row.Status);
        Assert.Equal(PendingType.Deletion, row.PendingType);
    }

    /// <summary>
    /// A row excluded because the user narrowed a blacklist goes through approval even when deletion is
    /// automatic, since the item is still on the source.
    /// True: editing a filter never silently deletes files.
    /// False: adding a folder to the blacklist deletes everything in it on the next refresh.
    /// </summary>
    [Fact]
    public async Task Delete_Automatic_BlacklistedRow_NeedsApproval()
    {
        Mode(MissingContentAction.Delete);
        _mapping.FilterMode = LibraryFilterMode.Blacklist;
        _mapping.FilteredItems.Add(new FilteredItem { Path = "/src/Excluded" });
        _task.Seen(Library, 1);
        Track("filtered", title: "Excluded");
        Track("removed", title: "Other");

        await Prune();

        Assert.Equal(PendingType.Deletion, Row("filtered")!.PendingType);
        Assert.Equal(SyncStatus.Pending, Row("filtered")!.Status);
        Assert.Equal(SyncStatus.Deleting, Row("removed")!.Status);
    }

    /// <summary>
    /// Rows under a mapping that is disabled or removed are out of scope and never pruned.
    /// True: disabling a mapping keeps its files and tracking.
    /// False: disabling a mapping schedules every file under it for deletion.
    /// </summary>
    [Fact]
    public async Task Delete_RowsOfAnUnmappedLibrary_AreNotPruned()
    {
        Mode(MissingContentAction.Delete);
        _task.Seen(Library, 1);
        _task.Seen("lib-disabled", 1);
        Track("elsewhere", library: "lib-disabled");

        var changed = await Prune();

        Assert.Equal(0, changed);
        Assert.Equal(SyncStatus.Synced, Row("elsewhere")!.Status);
    }

    // -----------------------------------------------------------------------
    // The zero-seen guard
    // -----------------------------------------------------------------------

    /// <summary>
    /// A mapping the source reported no items for this run, or never reported at all, is not pruned.
    /// True: a recreated source library, or a transient empty answer, deletes nothing.
    /// False: one empty 200 answer schedules a whole library's files for deletion.
    /// </summary>
    [Fact]
    public async Task ZeroSeenMapping_BlocksThePrune()
    {
        Mode(MissingContentAction.Delete);
        Track("a");
        Track("b");

        Assert.Equal(0, await Prune());

        _task.Seen(Library, 0);
        Assert.Equal(0, await Prune());

        Assert.All(new[] { "a", "b" }, id => Assert.Equal(SyncStatus.Synced, Row(id)!.Status));
    }

    /// <summary>
    /// How several mappings sharing one source library combine their counts: a zero is sticky, an
    /// intentionally emptied whitelist (-1) allows the prune but never overrides a real answer, and
    /// positive counts allow it.
    /// True: one mapping's empty answer protects the library even when another mapping answered.
    /// False: the order the mappings were listed in decides whether a library is wiped.
    /// </summary>
    [Theory]
    [InlineData(new[] { 5, 0 }, false)]
    [InlineData(new[] { 0, 5 }, false)]
    [InlineData(new[] { -1, 0 }, false)]
    [InlineData(new[] { -1 }, true)]
    [InlineData(new[] { -1, 3 }, true)]
    [InlineData(new[] { 3, -1 }, true)]
    [InlineData(new[] { 2, 3 }, true)]
    public async Task RecordMappingSeen_MergesCountsSafely(int[] counts, bool pruneAllowed)
    {
        Mode(MissingContentAction.Delete);
        foreach (var count in counts)
        {
            _task.Seen(Library, count);
        }

        Track("gone");

        await Prune();

        Assert.Equal(pruneAllowed ? SyncStatus.Deleting : SyncStatus.Synced, Row("gone")!.Status);
    }

    /// <summary>
    /// A new run forgets the previous run's counts, so an earlier positive answer cannot vouch for a
    /// later empty one.
    /// True: each run's prune is judged by what the source said in that run.
    /// False: a library that answered yesterday is wiped by today's empty answer.
    /// </summary>
    [Fact]
    public async Task OnRunStarting_ForgetsThePreviousRunsCounts()
    {
        Mode(MissingContentAction.Delete);
        _task.Seen(Library, 10);
        _task.StartRun();
        Track("gone");

        await Prune();

        Assert.Equal(SyncStatus.Synced, Row("gone")!.Status);
    }

    // -----------------------------------------------------------------------
    // Keep
    // -----------------------------------------------------------------------

    /// <summary>
    /// With Keep a stale row is left alone, and deletions still waiting from an earlier Delete setting
    /// are cancelled back to Synced.
    /// True: switching to Keep keeps every file, including ones already scheduled.
    /// False: files scheduled before the switch are still deleted by the next sync.
    /// </summary>
    [Fact]
    public async Task Keep_LeavesStaleRows_AndCancelsWaitingDeletions()
    {
        Mode(MissingContentAction.Keep, ApprovalMode.Disabled);
        Track("stale");
        Track("deleting", SyncStatus.Deleting);
        Track("approval", SyncStatus.Pending, pendingType: PendingType.Deletion);
        Track("download", SyncStatus.Pending, pendingType: PendingType.Download);

        var changed = await Prune();

        Assert.Equal(2, changed);
        Assert.Equal(SyncStatus.Synced, Row("stale")!.Status);
        Assert.Equal(SyncStatus.Synced, Row("deleting")!.Status);
        var approval = Row("approval")!;
        Assert.Equal(SyncStatus.Synced, approval.Status);
        Assert.Null(approval.PendingType);
        Assert.Equal(PendingType.Download, Row("download")!.PendingType);
    }

    /// <summary>
    /// With Delete, deletions already waiting stay waiting.
    /// True: the stand-down runs only when the setting moved away from Delete.
    /// False: every refresh cancels the deletions the previous refresh scheduled.
    /// </summary>
    [Fact]
    public async Task Delete_KeepsWaitingDeletions()
    {
        Mode(MissingContentAction.Delete);
        _task.Seen(Library, 1);
        Track("deleting", SyncStatus.Deleting);

        await Prune();

        Assert.Equal(SyncStatus.Deleting, Row("deleting")!.Status);
    }

    // -----------------------------------------------------------------------
    // Ignore (hide with a .ignore file)
    // -----------------------------------------------------------------------

    /// <summary>
    /// With Ignore a title whose every item is gone from the source is hidden with a .ignore file and its
    /// rows are marked hidden. Its files stay.
    /// True: a title removed on the source disappears here without deleting anything.
    /// False: the title stays visible, or its files are deleted.
    /// </summary>
    [Fact]
    public async Task Ignore_HidesATitleThatIsWhollyGone()
    {
        Mode(MissingContentAction.Ignore, ApprovalMode.Disabled);
        _task.Seen(Library, 1);
        var a = Track("e1", title: "Show");
        Track("e2", title: "Show");

        var changed = await Prune();

        Assert.Equal(2, changed);
        var folder = Path.Combine(_localRoot, "Show");
        Assert.True(File.Exists(Path.Combine(folder, IgnoreFileWriter.FileName)));
        Assert.True(File.Exists(a.LocalPath));
        Assert.All(new[] { "e1", "e2" }, id =>
        {
            var row = Row(id)!;
            Assert.Equal(SyncStatus.Ignored, row.Status);
            Assert.Equal(SyncStateService.HiddenReason, row.Reason);
        });
    }

    /// <summary>
    /// A title that is only partly gone, or that holds a media file nothing tracks, is not hidden.
    /// True: hiding never takes episodes still on the source, or a user's own files, out of the library.
    /// False: one removed episode hides the whole show, or a file the user added is hidden with it.
    /// </summary>
    [Fact]
    public async Task Ignore_KeepsTitlesPartlyAliveOrHoldingUntrackedMedia()
    {
        Mode(MissingContentAction.Ignore, ApprovalMode.Disabled);
        _task.Seen(Library, 1);
        Track("alive", title: "Partly");
        Track("gone", title: "Partly");
        Track("tracked", title: "Mixed");
        File.WriteAllText(Path.Combine(_localRoot, "Mixed", "users-own.mkv"), "x");

        var changed = await Prune("alive");

        Assert.Equal(0, changed);
        Assert.False(File.Exists(Path.Combine(_localRoot, "Partly", IgnoreFileWriter.FileName)));
        Assert.False(File.Exists(Path.Combine(_localRoot, "Mixed", IgnoreFileWriter.FileName)));
        Assert.Equal(SyncStatus.Synced, Row("gone")!.Status);
        Assert.Equal(SyncStatus.Synced, Row("tracked")!.Status);
    }

    /// <summary>
    /// A hidden title with an item back on the source is shown again: its .ignore file goes and its rows
    /// return to Synced.
    /// True: a title re-added on the source reappears here.
    /// False: a returning title stays hidden forever.
    /// </summary>
    [Fact]
    public async Task Ignore_ShowsAHiddenTitleWhenAnItemReturns()
    {
        Mode(MissingContentAction.Ignore, ApprovalMode.Disabled);
        _task.Seen(Library, 1);
        Track("e1", SyncStatus.Ignored, title: "Back", reason: SyncStateService.HiddenReason);
        Track("e2", SyncStatus.Ignored, title: "Back", reason: SyncStateService.HiddenReason);
        var marker = Path.Combine(_localRoot, "Back", IgnoreFileWriter.FileName);
        File.WriteAllText(marker, string.Empty);

        await Prune("e1");

        Assert.False(File.Exists(marker));
        Assert.Equal(SyncStatus.Synced, Row("e1")!.Status);
        Assert.Equal(SyncStatus.Synced, Row("e2")!.Status);
    }

    /// <summary>
    /// Moving away from Ignore shows every hidden title again, and leaves Ignored rows the user ignored
    /// by hand alone.
    /// True: switching the setting off undoes what it did and nothing else.
    /// False: titles stay hidden after the setting is off, or the user's own ignores are undone.
    /// </summary>
    [Fact]
    public async Task LeavingIgnore_ShowsHiddenTitles_ButNotUserIgnores()
    {
        Mode(MissingContentAction.Keep, ApprovalMode.Disabled);
        Track("hidden", SyncStatus.Ignored, title: "Hidden", reason: SyncStateService.HiddenReason);
        Track("mine", SyncStatus.Ignored, title: "Mine", reason: "I do not want this");
        var marker = Path.Combine(_localRoot, "Hidden", IgnoreFileWriter.FileName);
        File.WriteAllText(marker, string.Empty);

        await Prune();

        Assert.False(File.Exists(marker));
        Assert.Equal(SyncStatus.Synced, Row("hidden")!.Status);
        Assert.Equal(SyncStatus.Ignored, Row("mine")!.Status);
    }
}

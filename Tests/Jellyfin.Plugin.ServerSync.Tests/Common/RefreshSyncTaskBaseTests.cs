using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Common;

/// <summary>
/// Focused tests for <see cref="RefreshSyncTaskBase{TRecord, TSource, TKey}"/>'s
/// pruning contract. The base owns the "any row not seen this run gets
/// deleted" sweep — its scoping and skip-pruning hooks must hold or every
/// module silently loses tracking rows on transient source-side hiccups.
/// </summary>
public class RefreshSyncTaskBaseTests
{
    // -----------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------

    private sealed class TestRecord : SyncRecord
    {
        public string Key { get; set; } = string.Empty;

        public string ScopeId { get; set; } = string.Empty;

        public override bool HasChanges => false;

        public override void MarkSynced()
        {
        }
    }

    private sealed class FakeManager : ISyncTableManager<TestRecord, string>
    {
        public List<long> Deleted { get; } = new();

        public IList<TestRecord> All { get; set; } = new List<TestRecord>();

        public TestRecord? GetById(long id) => null;

        public TestRecord? GetByKey(string key) => null;

        public IList<TestRecord> GetByStatus(SyncStatus status, int? limit = null) => Array.Empty<TestRecord>();

        public IList<TestRecord> GetAll() => All;

        public int Count() => All.Count;

        public int CountByStatus(SyncStatus status) => 0;

        public void Upsert(TestRecord record)
        {
        }

        public void DeleteById(long id) => Deleted.Add(id);

        public void DeleteByKey(string key)
        {
        }

        public int DeleteByStatus(SyncStatus status) => 0;

        public int ResetTable() => 0;

        public void UpdateStatus(long id, SyncStatus status, string? reason = null)
        {
        }

        public void UpdateStatusByKey(string key, SyncStatus status, string? reason = null)
        {
        }

        public int BulkUpdateStatus(IReadOnlyList<long> ids, SyncStatus status, string? reason = null) => 0;

        public PagedResult<TestRecord> Paginate(PaginationRequest request)
            => new() { Items = Array.Empty<TestRecord>(), TotalCount = 0, Page = 1, PageSize = 0 };
    }

    private sealed class FakeConfigManager : IPluginConfigurationManager
    {
        public PluginConfiguration Configuration { get; } = new();

        public void SaveConfiguration()
        {
        }

        public string GetTempDownloadPath() => string.Empty;

        public string LocalServerName => string.Empty;

        public string PluginVersion => "0.0.0-test";
    }

    private sealed class FakeClientFactory : ISourceServerClientFactory
    {
        public SourceServerClient Create(string serverUrl, string apiKey)
            => throw new NotSupportedException("Client creation isn't exercised by the prune-path tests.");
    }

    /// <summary>
    /// Bare-bones concrete subclass that lets tests invoke the otherwise-
    /// protected <see cref="RefreshSyncTaskBase{TRecord, TSource, TKey}.PruneStaleAsync"/>
    /// and flip <see cref="IsInScope"/> / <see cref="ShouldSkipPruning"/> per
    /// scenario. Discovery is stubbed to return nothing — these tests focus on
    /// the prune sweep only.
    /// </summary>
    private sealed class TestableRefreshTask : RefreshSyncTaskBase<TestRecord, string, string>
    {
        public TestableRefreshTask(FakeManager manager, FakeConfigManager configManager, FakeClientFactory clientFactory)
            : base(NullLogger.Instance, manager, clientFactory, configManager)
        {
        }

        public Func<TestRecord, bool>? ScopeFilter { get; set; }

        public Func<TestRecord, bool>? SkipFilter { get; set; }

        public override string Name => "Test Refresh";

        public override string Key => "TestRefresh";

        public override string Description => "Test";

        public override string Category => "Test";

        protected override string ModuleMutexKey => "Test";

        protected override bool IsEnabled() => true;

        protected override Task<IList<string>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken)
            => Task.FromResult<IList<string>>(Array.Empty<string>());

        protected override Task<TestRecord?> BuildRecordAsync(string source, IReadOnlyDictionary<string, TestRecord> existing, CancellationToken cancellationToken)
            => Task.FromResult<TestRecord?>(null);

        protected override string ExtractKey(TestRecord record) => record.Key;

        protected override bool IsInScope(TestRecord record) => ScopeFilter?.Invoke(record) ?? true;

        protected override bool ShouldSkipPruning(TestRecord record) => SkipFilter?.Invoke(record) ?? false;

        public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task<int> InvokePruneStaleAsync(IReadOnlyDictionary<string, TestRecord> existing, HashSet<string> seenKeys, CancellationToken cancellationToken)
            => PruneStaleAsync(existing, seenKeys, cancellationToken);
    }

    private static TestableRefreshTask CreateTask(out FakeManager manager)
    {
        manager = new FakeManager();
        return new TestableRefreshTask(manager, new FakeConfigManager(), new FakeClientFactory());
    }

    private static TestRecord Record(long id, string key, string scopeId = "scope-a") => new()
    {
        Id = id,
        Key = key,
        ScopeId = scopeId
    };

    private static IReadOnlyDictionary<string, TestRecord> Map(params TestRecord[] records)
    {
        var dict = new Dictionary<string, TestRecord>();
        foreach (var r in records)
        {
            dict[r.Key] = r;
        }

        return dict;
    }

    // -----------------------------------------------------------------------
    // PruneStaleAsync tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// An empty seen-set with default in-scope behavior prunes every row.
    /// True: stale rows are removed from tracking as intended.
    /// False: dead rows survive forever and the table grows unbounded.
    /// </summary>
    [Fact]
    public async Task PruneStaleAsync_EmptySeen_PrunesEveryInScopeRow()
    {
        var task = CreateTask(out var manager);
        var existing = Map(Record(1, "a"), Record(2, "b"), Record(3, "c"));

        var pruned = await task.InvokePruneStaleAsync(existing, new HashSet<string>(), CancellationToken.None);

        Assert.Equal(3, pruned);
        Assert.Equal(new[] { 1L, 2L, 3L }, manager.Deleted);
    }

    /// <summary>
    /// Every key in seenKeys is preserved.
    /// True: rows we observed this run keep their tracking state.
    /// False: every refresh would delete and reinsert rows, churning IDs and history.
    /// </summary>
    [Fact]
    public async Task PruneStaleAsync_AllSeen_PrunesNothing()
    {
        var task = CreateTask(out var manager);
        var existing = Map(Record(1, "a"), Record(2, "b"));
        var seen = new HashSet<string> { "a", "b" };

        var pruned = await task.InvokePruneStaleAsync(existing, seen, CancellationToken.None);

        Assert.Equal(0, pruned);
        Assert.Empty(manager.Deleted);
    }

    /// <summary>
    /// Out-of-scope rows are skipped even when not seen.
    /// True: disabling a mapping preserves its history (incl. Ignored overrides) instead of nuking it.
    /// False: a config toggle silently destroys the user's tracking state.
    /// </summary>
    [Fact]
    public async Task PruneStaleAsync_OutOfScope_NotPruned()
    {
        var task = CreateTask(out var manager);
        var inScope = Record(1, "a", scopeId: "active");
        var outOfScope = Record(2, "b", scopeId: "disabled");
        task.ScopeFilter = r => r.ScopeId == "active";

        var pruned = await task.InvokePruneStaleAsync(Map(inScope, outOfScope), new HashSet<string>(), CancellationToken.None);

        Assert.Equal(1, pruned);
        Assert.Equal(new[] { 1L }, manager.Deleted);
    }

    /// <summary>
    /// Rows in scopes flagged via <see cref="RefreshSyncTaskBase{TRecord, TSource, TKey}.ShouldSkipPruning"/>
    /// are preserved even when in-scope and unseen — this is the partial-discovery safety net.
    /// True: a transient source hiccup mid-discovery doesn't delete every unseen row.
    /// False: 4 of 5 sync modules lose tracking rows (Content loses local files) on any blip.
    /// </summary>
    [Fact]
    public async Task PruneStaleAsync_ShouldSkipPruning_PreservesRowAcrossRuns()
    {
        var task = CreateTask(out var manager);
        var row = Record(7, "fragile");
        task.SkipFilter = r => r.Key == "fragile";

        var pruned = await task.InvokePruneStaleAsync(Map(row), new HashSet<string>(), CancellationToken.None);

        Assert.Equal(0, pruned);
        Assert.Empty(manager.Deleted);
    }

    /// <summary>
    /// Skip-pruning applies per-row: skipped rows survive while non-skipped stale rows are still deleted.
    /// True: a single failing library doesn't block pruning for the libraries that did finish.
    /// False: one bad library would freeze pruning for everything or nothing.
    /// </summary>
    [Fact]
    public async Task PruneStaleAsync_ShouldSkipPruning_PartitionsPerRow()
    {
        var task = CreateTask(out var manager);
        var safe = Record(10, "safe", scopeId: "lib-a");
        var fragile = Record(20, "fragile", scopeId: "lib-b");
        task.SkipFilter = r => r.ScopeId == "lib-b";

        var pruned = await task.InvokePruneStaleAsync(Map(safe, fragile), new HashSet<string>(), CancellationToken.None);

        Assert.Equal(1, pruned);
        Assert.Equal(new[] { 10L }, manager.Deleted);
    }

    /// <summary>
    /// Seen-key check fires before skip-pruning check.
    /// True: skip-pruning only matters for unseen rows; observed rows take the normal path.
    /// False: a row that was both observed AND in a skipped scope would never run its observed-path side-effects.
    /// </summary>
    [Fact]
    public async Task PruneStaleAsync_SeenTakesPrecedenceOverSkipPruning()
    {
        var task = CreateTask(out var manager);
        var observed = Record(1, "seen");
        task.SkipFilter = _ => true;
        var seen = new HashSet<string> { "seen" };

        var pruned = await task.InvokePruneStaleAsync(Map(observed), seen, CancellationToken.None);

        Assert.Equal(0, pruned);
        Assert.Empty(manager.Deleted);
    }

    /// <summary>
    /// Out-of-scope rows are skipped before <see cref="RefreshSyncTaskBase{TRecord, TSource, TKey}.ShouldSkipPruning"/>
    /// even gets consulted. Order matters because the two guards have different
    /// recovery semantics — out-of-scope is persistent (a config toggle), skip-
    /// pruning is per-run (a transient discovery failure).
    /// True: an out-of-scope row in a fragile scope is treated as out-of-scope.
    /// False: the two flags blur and operators can't reason about behavior.
    /// </summary>
    [Fact]
    public async Task PruneStaleAsync_OutOfScopeTakesPrecedenceOverSkipPruning()
    {
        var task = CreateTask(out var manager);
        task.ScopeFilter = _ => false;
        task.SkipFilter = _ => true;

        var pruned = await task.InvokePruneStaleAsync(Map(Record(1, "a")), new HashSet<string>(), CancellationToken.None);

        Assert.Equal(0, pruned);
        Assert.Empty(manager.Deleted);
    }

    /// <summary>
    /// Default <see cref="RefreshSyncTaskBase{TRecord, TSource, TKey}.ShouldSkipPruning"/>
    /// returns false — subclasses that don't override get the historical
    /// always-prune behavior.
    /// True: existing modules that don't opt in keep working unchanged.
    /// False: adding the hook silently changes pruning behavior for every subclass.
    /// </summary>
    [Fact]
    public async Task PruneStaleAsync_DefaultShouldSkipPruning_IsFalse_AndPrunes()
    {
        var task = CreateTask(out var manager);
        // No SkipFilter assigned — falls through to the base default (false).
        task.SkipFilter = null;

        var pruned = await task.InvokePruneStaleAsync(Map(Record(1, "a")), new HashSet<string>(), CancellationToken.None);

        Assert.Equal(1, pruned);
        Assert.Equal(new[] { 1L }, manager.Deleted);
    }

    /// <summary>
    /// Cancellation propagates out of the prune loop.
    /// True: a Stop request from Jellyfin's task runner is honored mid-prune.
    /// False: cancellation would be ignored and the user can't actually stop a runaway prune.
    /// </summary>
    [Fact]
    public async Task PruneStaleAsync_CancellationRequested_Throws()
    {
        var task = CreateTask(out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var existing = Map(Record(1, "a"), Record(2, "b"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => task.InvokePruneStaleAsync(existing, new HashSet<string>(), cts.Token));
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Common;

/// <summary>
/// Focused tests for <see cref="RefreshSyncTaskBase{TRecord, TSource, TKey}"/>'s
/// pruning contract. The base owns the "any row not seen this run gets
/// deleted" sweep — its scoping and the source-unavailable guard must hold or
/// every module silently loses tracking rows on a transient source-side error.
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
            => new(Array.Empty<TestRecord>(), 0, 0, 50);
    }

    private sealed class FakeConfigManager : IPluginConfigurationManager
    {
        public PluginConfiguration Configuration { get; } = new();

        public string DecryptedSourceServerApiKey => Configuration.SourceServerApiKey;

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

        public bool MarkUnavailableDuringDiscovery { get; set; }

        public IList<string> Sources { get; set; } = Array.Empty<string>();

        public override string Name => "Test Refresh";

        public override string Key => "TestRefresh";

        public override string Description => "Test";

        public override string Category => "Test";

        protected override string ModuleMutexKey => "Test";

        protected override bool IsEnabled() => true;

        protected override Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
            => Task.FromResult(true);

        protected override Task<IList<string>> GetListAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (MarkUnavailableDuringDiscovery)
            {
                MarkSourceUnavailable("test: source server unavailable");
            }

            return Task.FromResult(Sources);
        }

        public bool ThrowDuringBuild { get; set; }

        protected override Task<TestRecord?> BuildRecordAsync(string source, IReadOnlyDictionary<string, TestRecord> existing, CancellationToken cancellationToken)
        {
            if (ThrowDuringBuild)
            {
                throw new InvalidOperationException("test: build failed");
            }

            return Task.FromResult<TestRecord?>(null);
        }

        protected override string ExtractKey(TestRecord record) => record.Key;

        protected override bool IsInScope(TestRecord record) => ScopeFilter?.Invoke(record) ?? true;

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
    /// Any source error during discovery (signalled via MarkSourceUnavailable)
    /// skips pruning for the ENTIRE run, not just the failed scope.
    /// True: a source hiccup never deletes tracking rows for items that may
    /// still exist on the source.
    /// False: a transient 4xx/5xx/timeout silently deletes unseen rows (Content
    /// would remove local files).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SourceUnavailableDuringDiscovery_SkipsAllPruning()
    {
        var task = CreateTask(out var manager);
        manager.All = new List<TestRecord> { Record(1, "a"), Record(2, "b") };
        task.MarkUnavailableDuringDiscovery = true;

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Empty(manager.Deleted);
    }

    /// <summary>
    /// A clean discovery (no source error) prunes the rows it didn't see — the
    /// "source answered cleanly with fewer/no items, remove the stale rows" path.
    /// True: genuinely-removed items get reconciled.
    /// False: dead rows survive forever and the table grows unbounded.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CleanDiscovery_PrunesUnseenRows()
    {
        var task = CreateTask(out var manager);
        manager.All = new List<TestRecord> { Record(1, "a"), Record(2, "b") };
        task.MarkUnavailableDuringDiscovery = false;

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(2, manager.Deleted.Count);
        Assert.Contains(1L, manager.Deleted);
        Assert.Contains(2L, manager.Deleted);
    }

    /// <summary>
    /// A per-item build failure (e.g. a 4xx/5xx fetching that item's detail)
    /// also skips pruning for the whole run — the failed item is still on the
    /// source, just not in the seen set.
    /// True: a per-item source error never deletes that item's row.
    /// False: a transient detail-fetch error deletes rows for items that exist.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BuildFailureDuringRun_SkipsAllPruning()
    {
        var task = CreateTask(out var manager);
        manager.All = new List<TestRecord> { Record(1, "a") };
        task.Sources = new List<string> { "x" };
        task.ThrowDuringBuild = true;

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Empty(manager.Deleted);
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

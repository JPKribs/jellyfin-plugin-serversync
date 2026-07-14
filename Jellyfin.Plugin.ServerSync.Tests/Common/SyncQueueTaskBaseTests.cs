using System;
using System.Collections.Generic;
using System.Linq;
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
/// Run-state contract of <see cref="SyncQueueTaskBase{TRecord, TKey}"/>: a
/// run that didn't actually finish must never look finished (no completion
/// stamp, no failure-record clear), failures must persist as Errored rows,
/// and apply groups must be real barriers — parents fully applied before
/// children start.
/// </summary>
public class SyncQueueTaskBaseTests
{
    private sealed class TestRecord : SyncRecord
    {
        public string Key { get; set; } = string.Empty;

        public int Group { get; set; }

        public override bool HasChanges => true;

        public bool MarkedSynced { get; private set; }

        public override void MarkSynced() => MarkedSynced = true;
    }

    private sealed class FakeManager : ISyncTableManager<TestRecord, string>
    {
        public List<TestRecord> Queued { get; } = new();

        public bool ThrowOnStrictRead { get; set; }

        public List<TestRecord> Upserted { get; } = new();

        public TestRecord? GetById(long id) => null;

        public TestRecord? GetByKey(string key) => null;

        public IList<TestRecord> GetByStatus(SyncStatus status, int? limit = null) => Array.Empty<TestRecord>();

        public IList<TestRecord> GetByStatusStrict(SyncStatus status)
            => ThrowOnStrictRead
                ? throw new InvalidOperationException("test: database unavailable")
                : Queued.ToList();

        public IList<TestRecord> GetAllStrict() => Queued.ToList();

        public int Count() => Queued.Count;

        public int CountByStatus(SyncStatus status) => 0;

        public void Upsert(TestRecord record)
        {
            lock (Upserted)
            {
                Upserted.Add(record);
            }
        }

        public void DeleteById(long id)
        {
        }

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
            => throw new NotSupportedException("Client creation isn't exercised by these tests.");
    }

    private sealed class TestableQueueTask : SyncQueueTaskBase<TestRecord, string>
    {
        public TestableQueueTask(FakeManager manager)
            : base(NullLogger.Instance, manager, new FakeClientFactory(), new FakeConfigManager())
        {
        }

        public bool RunCompletedRecorded { get; private set; }

        public int Parallelism { get; set; } = 1;

        public bool SplitIntoGroups { get; set; }

        public Func<TestRecord, CancellationToken, Task>? ApplyBody { get; set; }

        public List<TestRecord> ApplyOrder { get; } = new();

        public override string Name => "Test Sync";

        public override string Key => "TestSync";

        public override string Description => "Test";

        public override string Category => "Test";

        protected override string ModuleMutexKey => "TestQueue";

        protected override bool IsEnabled() => true;

        protected override int MaxDegreeOfParallelism => Parallelism;

        protected override Task<bool> BeforeRunAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        protected override IEnumerable<IList<TestRecord>> GetApplyGroups(IList<TestRecord> items)
        {
            if (!SplitIntoGroups)
            {
                return base.GetApplyGroups(items);
            }

            return items.GroupBy(i => i.Group).OrderBy(g => g.Key).Select(g => (IList<TestRecord>)g.ToList());
        }

        protected override async Task ApplyAsync(TestRecord record, CancellationToken cancellationToken)
        {
            lock (ApplyOrder)
            {
                ApplyOrder.Add(record);
            }

            if (ApplyBody != null)
            {
                await ApplyBody(record, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override void RecordRunCompleted(PluginConfiguration config, DateTime utcNow)
            => RunCompletedRecorded = true;

        public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }

    private static TestRecord Record(long id, string key, int group = 0) => new()
    {
        Id = id,
        Key = key,
        Group = group,
        Status = SyncStatus.Queued
    };

    /// <summary>
    /// A clean run stamps completion and marks rows Synced with MarkSynced
    /// called — the baseline the negative tests contrast against.
    /// </summary>
    [Fact]
    public async Task CleanRun_StampsCompletionAndMarksSynced()
    {
        var manager = new FakeManager();
        manager.Queued.AddRange(new[] { Record(1, "a"), Record(2, "b") });
        var task = new TestableQueueTask(manager);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(task.RunCompletedRecorded);
        Assert.Equal(2, manager.Upserted.Count);
        Assert.All(manager.Upserted, r => Assert.Equal(SyncStatus.Synced, r.Status));
        Assert.All(manager.Upserted, r => Assert.True(r.MarkedSynced));
    }

    /// <summary>
    /// Cancellation mid-run must propagate and must NOT stamp completion —
    /// a cancelled run with items still queued looking "finished and clean"
    /// on the dashboard was one of the audited bugs.
    /// </summary>
    [Fact]
    public async Task CancelledRun_ThrowsAndDoesNotStampCompletion()
    {
        var manager = new FakeManager();
        manager.Queued.AddRange(new[] { Record(1, "a"), Record(2, "b"), Record(3, "c") });
        var task = new TestableQueueTask(manager);
        using var cts = new CancellationTokenSource();
        task.ApplyBody = (_, _) =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.ExecuteAsync(new Progress<double>(), cts.Token));

        Assert.False(task.RunCompletedRecorded);
    }

    /// <summary>
    /// An apply failure persists the row as Errored with the exception
    /// message, and the run still completes for the other rows.
    /// </summary>
    [Fact]
    public async Task ApplyFailure_PersistsErroredRowAndContinues()
    {
        var manager = new FakeManager();
        manager.Queued.AddRange(new[] { Record(1, "bad"), Record(2, "good") });
        var task = new TestableQueueTask(manager);
        task.ApplyBody = (record, _) => record.Key == "bad"
            ? throw new InvalidOperationException("test: apply exploded")
            : Task.CompletedTask;

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var bad = manager.Upserted.Single(r => r.Key == "bad");
        Assert.Equal(SyncStatus.Errored, bad.Status);
        Assert.Equal("test: apply exploded", bad.Reason);
        Assert.Equal(SyncStatus.Synced, manager.Upserted.Single(r => r.Key == "good").Status);
        Assert.True(task.RunCompletedRecorded);
    }

    /// <summary>
    /// Apply groups are barriers: with parallelism enabled, every group-0
    /// item must finish before any group-1 item starts. This is what makes
    /// Metadata's parent-before-children ordering real instead of a sort
    /// that parallelism immediately defeats.
    /// </summary>
    [Fact]
    public async Task ApplyGroups_AreBarriersUnderParallelism()
    {
        var manager = new FakeManager();
        for (var i = 0; i < 8; i++)
        {
            manager.Queued.Add(Record(i + 1, $"parent-{i}", group: 0));
        }

        for (var i = 0; i < 8; i++)
        {
            manager.Queued.Add(Record(i + 100, $"child-{i}", group: 1));
        }

        var task = new TestableQueueTask(manager)
        {
            Parallelism = 4,
            SplitIntoGroups = true
        };
        task.ApplyBody = (_, _) => Task.Delay(5);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var lastParent = task.ApplyOrder.FindLastIndex(r => r.Group == 0);
        var firstChild = task.ApplyOrder.FindIndex(r => r.Group == 1);
        Assert.True(firstChild > lastParent, $"a group-1 item started at {firstChild} before group-0 finished at {lastParent}");
        Assert.Equal(16, task.ApplyOrder.Count);
    }

    /// <summary>
    /// A database error reading the queue must fail the run loudly. The old
    /// lenient read returned an empty list, which stamped a clean completion
    /// while the queue silently went unprocessed.
    /// </summary>
    [Fact]
    public async Task StrictReadFailure_PropagatesAndDoesNotStampCompletion()
    {
        var manager = new FakeManager { ThrowOnStrictRead = true };
        manager.Queued.Add(Record(1, "a"));
        var task = new TestableQueueTask(manager);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));

        Assert.False(task.RunCompletedRecorded);
        Assert.Empty(manager.Upserted);
    }
}

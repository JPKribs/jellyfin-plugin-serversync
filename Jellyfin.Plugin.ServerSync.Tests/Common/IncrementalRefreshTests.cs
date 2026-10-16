using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Common;

public sealed class IncrementalRefreshTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly ScanWatermarkStore _watermarks;

    public IncrementalRefreshTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _watermarks = new ScanWatermarkStore(new FixedProvider(_database), NullLogger<ScanWatermarkStore>.Instance);
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

    [Fact]
    public async Task FirstRun_IsFull_AndRecordsTheListing()
    {
        var (task, manager) = Task("a");
        manager.All = new List<TestRecord> { Row(1, "stale", "a") };
        task.Offered = new List<string> { "kept" };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Null(task.LastChangedSince);
        Assert.Contains(1L, manager.Deleted);
        var mark = _watermarks.Get(task.Key, "a");
        Assert.NotNull(mark);
        Assert.True(DateTime.UtcNow - mark!.FullAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RunAfterAFullScan_ListsOnlyChanges_AndNeverPrunes()
    {
        var (task, manager) = Task("a");
        var listedFrom = DateTime.UtcNow.AddHours(-2);
        _watermarks.Set(task.Key, "a", new ScanWatermark(task.ScopeFor("a"), listedFrom, DateTime.UtcNow.AddHours(-3)));
        manager.All = new List<TestRecord> { Row(1, "unchanged", "a") };
        task.Offered = new List<string> { "changed" };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.NotNull(task.LastChangedSince);
        Assert.Equal(listedFrom - RefreshSyncTaskBase<TestRecord, string, string>.IncrementalOverlap, task.LastChangedSince!.Value, TimeSpan.FromSeconds(1));
        Assert.Empty(manager.Deleted);
        Assert.Single(manager.Upserted, r => r.Key == "changed");
        var mark = _watermarks.Get(task.Key, "a")!;
        Assert.True(mark.ListedFrom > listedFrom);
        Assert.True(DateTime.UtcNow - mark.FullAt > TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task ChangedMappings_ForceAFullScan()
    {
        var (task, manager) = Task("a");
        _watermarks.Set(task.Key, "a", new ScanWatermark("stale-scope", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-1)));
        manager.All = new List<TestRecord> { Row(1, "gone", "a") };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Null(task.LastChangedSince);
        Assert.Contains(1L, manager.Deleted);
    }

    [Fact]
    public async Task OldFullScan_ForcesAFullScan()
    {
        var (task, manager) = Task("a");
        _watermarks.Set(task.Key, "a", new ScanWatermark(task.ScopeFor("a"), DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-25)));
        manager.All = new List<TestRecord> { Row(1, "x", "a") };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Null(task.LastChangedSince);
    }

    [Fact]
    public async Task ZeroInterval_MakesEveryScanFull()
    {
        var (task, manager) = Task("a");
        task.Config.FullScanIntervalHours = 0;
        _watermarks.Set(task.Key, "a", new ScanWatermark(task.ScopeFor("a"), DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-5)));
        manager.All = new List<TestRecord> { Row(1, "x", "a") };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Null(task.LastChangedSince);
    }

    [Fact]
    public async Task EmptyTable_ForcesAFullScan()
    {
        var (task, _) = Task("a");
        _watermarks.Set(task.Key, "a", new ScanWatermark(task.ScopeFor("a"), DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-5)));

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Null(task.LastChangedSince);
    }

    [Fact]
    public async Task UncleanRun_DoesNotMoveTheListingForward()
    {
        var (task, manager) = Task("a");
        var listedFrom = DateTime.UtcNow.AddHours(-2);
        _watermarks.Set(task.Key, "a", new ScanWatermark(task.ScopeFor("a"), listedFrom, DateTime.UtcNow.AddHours(-3)));
        manager.All = new List<TestRecord> { Row(1, "x", "a") };
        task.Offered = new List<string> { "boom" };
        task.FailBuildOf = "boom";

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(listedFrom, _watermarks.Get(task.Key, "a")!.ListedFrom, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task IncrementalRun_LeavesAnItemWithTheHigherServerThatProvidesIt()
    {
        var (task, manager) = Task("high", "low");
        foreach (var key in new[] { "high", "low" })
        {
            _watermarks.Set(task.Key, key, new ScanWatermark(task.ScopeFor(key), DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-2)));
        }

        manager.All = new List<TestRecord> { Row(1, "high|film", "high", "film") };
        task.PerServer = new Dictionary<string, IList<string>> { ["high"] = new List<string>(), ["low"] = new List<string> { "film" } };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.NotNull(task.LastChangedSince);
        Assert.DoesNotContain(manager.Upserted, r => r.ServerKey == "low");
    }

    [Fact]
    public async Task IncrementalRun_RetiresARowAHigherServerNowCovers()
    {
        var (task, manager) = Task("high", "low");
        foreach (var key in new[] { "high", "low" })
        {
            _watermarks.Set(task.Key, key, new ScanWatermark(task.ScopeFor(key), DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-2)));
        }

        manager.All = new List<TestRecord> { Row(1, "low|film", "low", "film") };
        task.PerServer = new Dictionary<string, IList<string>> { ["high"] = new List<string> { "film" }, ["low"] = new List<string>() };

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.NotNull(task.LastChangedSince);
        Assert.Contains(manager.Upserted, r => r.ServerKey == "high");
        Assert.Contains(1L, manager.Deleted);
    }

    private (IncrementalTask Task, FakeManager Manager) Task(params string[] serverKeys)
    {
        var manager = new FakeManager();
        var config = new FakeConfigManager();
        foreach (var key in serverKeys)
        {
            config.Configuration.Servers.Add(new SourceServer
            {
                Key = key,
                Name = key,
                Url = "http://" + key + ":8096",
                ApiKey = "k",
                LibraryMappings = { new LibraryMapping { SourceLibraryId = "lib", SourceRootPath = "/media", LocalLibraryId = "local", LocalRootPath = "/local" } }
            });
        }

        return (new IncrementalTask(manager, config, _watermarks), manager);
    }

    private static TestRecord Row(long id, string key, string serverKey, string? priorityKey = null) => new()
    {
        Id = id,
        Key = key,
        ServerKey = serverKey,
        PriorityKey = priorityKey
    };

    public sealed class TestRecord : SyncRecord
    {
        public string Key { get; set; } = string.Empty;

        public string? PriorityKey { get; set; }

        public override bool HasChanges => false;

        public override void MarkSynced()
        {
        }
    }

    private sealed class IncrementalTask : RefreshSyncTaskBase<TestRecord, (string Server, string Item), string>
    {
        public IncrementalTask(FakeManager manager, FakeConfigManager config, ScanWatermarkStore watermarks)
            : base(NullLogger.Instance, manager, new FakeClientFactory(), config, watermarks)
        {
        }

        public IList<string> Offered { get; set; } = new List<string>();

        public Dictionary<string, IList<string>>? PerServer { get; set; }

        public string? FailBuildOf { get; set; }

        public DateTime? LastChangedSince { get; private set; }

        public PluginConfiguration Config => ConfigManager.Configuration;

        public override string Name => "Incremental Test";

        public override string Key => "IncrementalTest";

        public override string Description => "Test";

        public override string Category => "Test";

        protected override string ModuleMutexKey => "IncrementalTest";

        protected override bool SupportsIncremental => true;

        public string ScopeFor(string serverKey)
        {
            var server = Config.Servers.First(s => s.Key == serverKey);
            var shape = System.Text.Json.JsonSerializer.Serialize(new
            {
                server.Url,
                Libraries = server.GetEnabledLibraryMappings(),
                Users = server.GetEnabledUserMappings(),
                Settings = string.Empty
            });
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(shape)))[..16];
        }

        protected override bool IsEnabled() => true;

        protected override Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
        {
            var sources = new List<ScanSource>();
            var servers = Config.GetPullServers();
            for (var i = 0; i < servers.Count; i++)
            {
                sources.Add(new ScanSource(servers[i], new SourceServerClient(NullLogger<SourceServerClient>.Instance, new System.Net.Http.HttpClient(), servers[i].Url, "k", "test", "0"), i));
            }

            Sources = sources;
            return System.Threading.Tasks.Task.FromResult(true);
        }

        protected override Task<IList<(string Server, string Item)>> GetListAsync(ScanSource source, IProgress<double> progress, CancellationToken cancellationToken)
        {
            LastChangedSince = ChangedSince(source);
            var items = PerServer is not null && PerServer.TryGetValue(source.Key, out var perServer) ? perServer : Offered;
            return System.Threading.Tasks.Task.FromResult<IList<(string Server, string Item)>>(items.Select(i => (source.Key, i)).ToList());
        }

        protected override string? PriorityKeyOf((string Server, string Item) source) => PerServer is null ? null : source.Item;

        protected override string? PriorityKeyOf(TestRecord record) => record.PriorityKey;

        protected override Task<TestRecord?> BuildRecordAsync((string Server, string Item) source, IReadOnlyDictionary<string, TestRecord> existing, CancellationToken cancellationToken)
        {
            if (source.Item == FailBuildOf)
            {
                throw new InvalidOperationException("test: build failed");
            }

            return System.Threading.Tasks.Task.FromResult<TestRecord?>(new TestRecord
            {
                Key = PerServer is null ? source.Item : source.Server + "|" + source.Item,
                ServerKey = source.Server,
                PriorityKey = PerServer is null ? null : source.Item
            });
        }

        protected override string ExtractKey(TestRecord record) => record.Key;
    }

    private sealed class FakeManager : ISyncTableManager<TestRecord, string>
    {
        public List<long> Deleted { get; } = new();

        public List<TestRecord> Upserted { get; } = new();

        public IList<TestRecord> All { get; set; } = new List<TestRecord>();

        public TestRecord? GetById(long id) => null;

        public TestRecord? GetByKey(string key) => null;

        public IList<TestRecord> GetByStatus(SyncStatus status, int? limit = null) => Array.Empty<TestRecord>();

        public IList<TestRecord> GetByStatusStrict(SyncStatus status) => Array.Empty<TestRecord>();

        public IList<TestRecord> GetAllStrict() => All;

        public int Count() => All.Count;

        public int CountByStatus(SyncStatus status) => 0;

        public void Upsert(TestRecord record) => Upserted.Add(record);

        public void DeleteById(long id) => Deleted.Add(id);

        public void DeleteByKey(string key)
        {
        }

        public int ResetTable() => 0;

        public void UpdateStatus(long id, SyncStatus status, string? reason = null)
        {
        }

        public void UpdateStatusByKey(string key, SyncStatus status, string? reason = null)
        {
        }

        public PagedResult<TestRecord> Paginate(PaginationRequest request) => new(Array.Empty<TestRecord>(), 0, 0, 50);
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
        public SourceServerClient Create(string serverUrl, string apiKey) => throw new NotSupportedException();

        public SourceServerClient Create(SourceServer server) => throw new NotSupportedException();
    }

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }
}

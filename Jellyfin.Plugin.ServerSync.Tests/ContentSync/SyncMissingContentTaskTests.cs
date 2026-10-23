using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Tasks;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.ContentSync;

/// <summary>
/// Sync Content's per-row bookkeeping, driven through the public single-row apply against a real
/// <see cref="ContentSyncTableManager"/>: what a failure, a circuit-breaker skip, a pre-flight abort and
/// a success each do to the row's retry count. The retry count is what eventually stops a download
/// that can never succeed, so getting it wrong either retries forever or gives up on a row that was
/// never tried.
/// </summary>
public sealed class SyncMissingContentTaskTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _localRoot;
    private readonly SyncDatabase _database;
    private readonly ContentSyncTableManager _manager;
    private readonly FakeConfigManager _config;
    private readonly InterfaceStub _monitor;
    private readonly SourceServer _server;
    private readonly Probe _task;

    public SyncMissingContentTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-sync-content-" + Guid.NewGuid().ToString("N"));
        _localRoot = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_localRoot);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _manager = new ContentSyncTableManager(new FixedProvider(_database), NullLogger<ContentSyncTableManager>.Instance);
        _config = new FakeConfigManager(Path.Combine(_tempDir, "tmp"));
        _config.Configuration.MinimumFreeDiskSpaceGb = 0;

        // A server URL of its own per test: the task keeps its circuit breakers per URL across runs.
        _server = new SourceServer
        {
            Key = "server-a",
            Url = "http://peer-" + Guid.NewGuid().ToString("N") + ":8096",
            ApiKey = "k",
            Mode = ServerMode.Pull,
            LibraryMappings = { new LibraryMapping { SourceLibraryId = "lib-1", SourceRootPath = "/src", LocalRootPath = _localRoot } }
        };
        _config.Configuration.Servers.Add(_server);

        var (monitor, monitorStub) = InterfaceStub.Create<ILibraryMonitor>();
        _monitor = monitorStub;
        _task = new Probe(monitor, _config, _manager);
    }

    public void Dispose()
    {
        RemoveBreaker();
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
        private readonly string _tempPath;

        public FakeConfigManager(string tempPath) => _tempPath = tempPath;

        public PluginConfiguration Configuration { get; } = new();

        public string DecryptApiKey(string protectedKey) => protectedKey;

        public string ResolveRequestApiKey(string? requestApiKey, string? serverKey, string? requestUrl) => requestApiKey ?? string.Empty;

        public void SaveConfiguration()
        {
        }

        public string GetTempDownloadPath() => _tempPath;

        public string LocalServerName => string.Empty;

        public string PluginVersion => "0.0.0-test";
    }

    private sealed class FakeClientFactory : ISourceServerClientFactory
    {
        public SourceServerClient Create(SourceServer server)
            => throw new NotSupportedException("The single-row apply is handed its source.");
    }

    /// <summary>
    /// The real task with only what these paths touch: the library monitor (told about changed folders)
    /// and the table. The library manager and download service are reached only by a full run's
    /// finalize and by an actual download, which these tests never make.
    /// </summary>
    private sealed class Probe : SyncMissingContentTask
    {
        public Probe(ILibraryMonitor monitor, IPluginConfigurationManager config, ContentSyncTableManager manager)
            : base(NullLogger<SyncMissingContentTask>.Instance, null!, monitor, config, manager, new FakeClientFactory(), null!)
        {
        }

        public IList<SyncItem> ItemsToApply() => GetItemsToApply();
    }

    // The breakers live in a private static map keyed by server URL, with no seam to reach them, so the
    // tests that need an open breaker plant one there.
    private static readonly FieldInfo BreakersField = typeof(SyncMissingContentTask).GetField("_circuitBreakers", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo BreakerLockField = typeof(SyncMissingContentTask).GetField("_circuitBreakerLock", BindingFlags.NonPublic | BindingFlags.Static)!;

    private CircuitBreaker OpenBreaker()
    {
        var breaker = new CircuitBreaker(NullLogger.Instance, "SourceServer", failureThreshold: 5, cooldownPeriod: TimeSpan.FromMinutes(5));
        for (var i = 0; i < 5; i++)
        {
            breaker.RecordFailure("download failed");
        }

        lock (BreakerLockField.GetValue(null)!)
        {
            ((Dictionary<string, CircuitBreaker>)BreakersField.GetValue(null)!)[_server.Url] = breaker;
        }

        Assert.True(breaker.IsOpen);
        return breaker;
    }

    private void RemoveBreaker()
    {
        lock (BreakerLockField.GetValue(null)!)
        {
            ((Dictionary<string, CircuitBreaker>)BreakersField.GetValue(null)!).Remove(_server.Url);
        }
    }

    private ScanSource Source() => new(_server, new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(), _server.Url, "k", "test", "0.0.0-test"), 0);

    private SyncItem Track(string id, SyncStatus status = SyncStatus.Queued, int retryCount = 0, string? localPath = "", long size = 4)
    {
        _manager.Upsert(new SyncItem
        {
            SourceLibraryId = "lib-1",
            LocalLibraryId = "local-1",
            SourceItemId = id,
            SourcePath = "/src/" + id + ".mkv",
            SourceSize = size,
            SourceCreateDate = DateTime.UtcNow,
            LocalPath = localPath == string.Empty ? Path.Combine(_localRoot, "Title", id + ".mkv") : localPath,
            Status = status,
            StatusDate = DateTime.UtcNow,
            RetryCount = retryCount,
            ServerKey = _server.Key
        });
        return _manager.GetByKey(id)!;
    }

    private async Task<(bool Ok, SyncItem Stored)> ApplyRow(SyncItem row)
    {
        using var source = Source();
        var ok = await _task.ApplyRowAsync(row, source, CancellationToken.None);
        return (ok, _manager.GetByKey(row.SourceItemId)!);
    }

    /// <summary>
    /// A failed apply stores the row as Errored with the reason and one more retry used.
    /// True: the retry ceiling counts real failures, and the dashboard says what went wrong.
    /// False: a broken row is retried forever, or errors with no explanation.
    /// </summary>
    [Fact]
    public async Task ApplyFailure_IncrementsRetryCount_AndStoresReason()
    {
        var row = Track("a", retryCount: 1, localPath: null);

        var (ok, stored) = await ApplyRow(row);

        Assert.False(ok);
        Assert.Equal(SyncStatus.Errored, stored.Status);
        Assert.Equal(2, stored.RetryCount);
        Assert.Contains("no local path", stored.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A row skipped because its server's circuit breaker is open is stored as Errored with the skip as
    /// its reason, but keeps its retry count, since it was never tried. Once the breaker closes, the
    /// next real failure counts again.
    /// True: a server outage pauses its rows without using up their retries.
    /// False: an hour-long outage exhausts every queued row's retries and they are never downloaded.
    /// </summary>
    [Fact]
    public async Task OpenBreaker_SkipsWithoutUsingARetry_AndTheNextRealFailureCounts()
    {
        var breaker = OpenBreaker();
        var row = Track("a", retryCount: 1, localPath: null);

        var (skipped, afterSkip) = await ApplyRow(row);

        Assert.False(skipped);
        Assert.Equal(SyncStatus.Errored, afterSkip.Status);
        Assert.Equal(1, afterSkip.RetryCount);
        Assert.StartsWith("Skipped:", afterSkip.Reason, StringComparison.Ordinal);

        var (skippedAgain, afterSecondSkip) = await ApplyRow(afterSkip);
        Assert.False(skippedAgain);
        Assert.Equal(1, afterSecondSkip.RetryCount);

        breaker.RecordSuccess();
        var (failed, afterFailure) = await ApplyRow(afterSecondSkip);

        Assert.False(failed);
        Assert.Equal(2, afterFailure.RetryCount);
        Assert.Contains("no local path", afterFailure.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A success, here a file already on disk at the right size, stores the row as Synced with its retry
    /// count back to zero and no reason, and tells the library which folder changed.
    /// True: a row that finally downloads gets its full allowance for any later failure, and appears in the library.
    /// False: an old failure count carries over and a single later failure exhausts it, or the file never shows up.
    /// </summary>
    [Fact]
    public async Task ApplySuccess_ResetsRetryCount_AndReportsTheFolder()
    {
        var localPath = Path.Combine(_localRoot, "Title", "a.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.WriteAllText(localPath, "abcd");
        var row = Track("a", SyncStatus.Errored, retryCount: 2, localPath: localPath, size: 4);

        var (ok, stored) = await ApplyRow(row);

        Assert.True(ok);
        Assert.Equal(SyncStatus.Synced, stored.Status);
        Assert.Equal(0, stored.RetryCount);
        Assert.Null(stored.Reason);
        var reported = Assert.Single(_monitor.Calls, c => c.Name == nameof(ILibraryMonitor.ReportFileSystemChanged));
        Assert.Equal(Path.GetDirectoryName(localPath), (string)reported.Args[0]!);
    }

    /// <summary>
    /// A pre-flight abort, here too little free disk space, stores the row as Errored with the reason
    /// but does not count as an attempt.
    /// True: a full disk pauses downloads without spending their retries.
    /// False: a full disk overnight exhausts the retries of everything queued.
    /// </summary>
    [Fact]
    public async Task PreflightFailure_ErrorsWithoutUsingARetry()
    {
        _config.Configuration.MinimumFreeDiskSpaceGb = int.MaxValue;
        var row = Track("a", retryCount: 1);

        var (ok, stored) = await ApplyRow(row);

        Assert.False(ok);
        Assert.Equal(SyncStatus.Errored, stored.Status);
        Assert.Equal(1, stored.RetryCount);
        Assert.Contains("disk space", stored.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A run applies every Queued row plus the Errored rows still under the retry ceiling, and a ceiling
    /// of zero falls back to the default of three.
    /// True: failed downloads get their retries and then stop.
    /// False: exhausted rows are retried every run, or errored rows are never retried.
    /// </summary>
    [Fact]
    public void GetItemsToApply_QueuedPlusErroredUnderTheCeiling()
    {
        Track("queued");
        Track("errored-1", SyncStatus.Errored, retryCount: 1);
        Track("errored-2", SyncStatus.Errored, retryCount: 2);
        Track("exhausted", SyncStatus.Errored, retryCount: 3);
        Track("synced", SyncStatus.Synced);

        _config.Configuration.MaxRetryCount = 2;
        Assert.Equal(new[] { "errored-1", "queued" }, _task.ItemsToApply().Select(r => r.SourceItemId).OrderBy(k => k, StringComparer.Ordinal));

        _config.Configuration.MaxRetryCount = 0;
        Assert.Equal(new[] { "errored-1", "errored-2", "queued" }, _task.ItemsToApply().Select(r => r.SourceItemId).OrderBy(k => k, StringComparer.Ordinal));
    }
}

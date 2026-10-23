using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Database;

/// <summary>
/// Batched writes and the read connections, against a real SQLite file. A scan writes its rows in
/// batches, and the dashboard reads beside it.
/// </summary>
public sealed class BatchWriteTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SyncDatabase _database;
    private readonly PeopleSyncTableManager _people;

    public BatchWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir);
        _people = new PeopleSyncTableManager(new FixedProvider(_database), NullLogger<PeopleSyncTableManager>.Instance);
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

    private static PeopleSyncItem Person(string? name) => new()
    {
        PersonName = name!,
        Status = SyncStatus.Queued,
        StatusDate = DateTime.UtcNow
    };

    [Fact]
    public void UpsertMany_WritesEveryRecord()
    {
        var written = _people.UpsertMany(Enumerable.Range(0, 600).Select(i => Person("Person " + i)).ToList(), (_, ex) => throw ex);

        Assert.Equal(600, written);
        Assert.Equal(600, _people.Count());
    }

    /// <summary>
    /// One record that cannot be written costs only itself.
    /// True: the rest of the batch lands and the bad record is reported.
    /// False: a whole batch of a scan is lost to one bad row.
    /// </summary>
    [Fact]
    public void UpsertMany_ABadRecord_FailsAlone()
    {
        var failed = new List<PeopleSyncItem>();
        var batch = new List<PeopleSyncItem> { Person("A"), Person(null), Person("B") };

        var written = _people.UpsertMany(batch, (record, _) => failed.Add(record));

        Assert.Equal(2, written);
        Assert.Single(failed);
        Assert.Equal(2, _people.Count());
    }

    [Fact]
    public void DeleteByIds_RemovesThoseRowsOnly()
    {
        _people.UpsertMany(new List<PeopleSyncItem> { Person("A"), Person("B"), Person("C") }, (_, ex) => throw ex);
        var rows = _people.GetAllStrict();

        var deleted = _people.DeleteByIds(rows.Where(r => r.PersonName != "B").Select(r => r.Id).ToList());

        Assert.Equal(2, deleted);
        Assert.Equal("B", Assert.Single(_people.GetAllStrict()).PersonName);
    }

    /// <summary>
    /// Reads from many threads at once, while batches are written, all succeed and see whole batches.
    /// True: the read connections are safe to share across the dashboard and the workers.
    /// False: reads fail, or see a batch half written.
    /// </summary>
    [Fact]
    public async Task ReadsBesideWrites_SeeWholeBatches()
    {
        var writer = Task.Run(() =>
        {
            for (var b = 0; b < 20; b++)
            {
                _people.UpsertMany(Enumerable.Range(0, 50).Select(i => Person($"P{b}-{i}")).ToList(), (_, ex) => throw ex);
            }
        });

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var counts = new List<int>();
            while (!writer.IsCompleted)
            {
                counts.Add(_people.Count());
            }

            return counts;
        })).ToList();

        await writer;
        foreach (var counts in await Task.WhenAll(readers))
        {
            Assert.All(counts, c => Assert.Equal(0, c % 50));
        }

        Assert.Equal(1000, _people.Count());
    }

    /// <summary>
    /// A reset with read connections open replaces the file and later reads see the new one.
    /// </summary>
    [Fact]
    public void Reset_WithReadConnectionsOpen_StartsClean()
    {
        _people.UpsertMany(new List<PeopleSyncItem> { Person("A") }, (_, ex) => throw ex);
        Assert.Equal(1, _people.Count());

        _database.ResetDatabase();

        Assert.Equal(0, _people.Count());
    }
}

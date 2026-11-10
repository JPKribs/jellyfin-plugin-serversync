using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Services.Links;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Links;

/// <summary>
/// Many threads at once against the link pieces Jellyfin calls from its request threads.
/// </summary>
public sealed class LinkConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serversync-linkrace-" + Guid.NewGuid().ToString("N"));

    public LinkConcurrencyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Sixty-four threads opening and closing streams for many users and items against a limit of five
    /// streams for the server: at no moment are more than five distinct streams open.
    /// True: the limit holds however requests race.
    /// False: requests arriving together slip past the limit.
    /// </summary>
    [Fact]
    public async Task TryOpen_NeverPassesTheLimit_UnderRaces()
    {
        var sessions = new LinkSessions();
        var users = Enumerable.Range(0, 64).Select(_ => Guid.NewGuid()).ToArray();
        var items = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var peak = 0;
        var violations = 0;
        using var start = new Barrier(64);

        await Task.WhenAll(Enumerable.Range(0, 64).Select(t => Task.Factory.StartNew(
            () =>
            {
                var random = new Random(t);
                start.SignalAndWait();
                for (var i = 0; i < 1500; i++)
                {
                    var handle = sessions.TryOpen("peer", users[random.Next(users.Length)], items[random.Next(items.Length)], maxForPeer: 5, maxForUser: 0, userElsewhere: 0);
                    if (handle is null)
                    {
                        Thread.Yield();
                        continue;
                    }

                    var distinct = sessions.CountForPeer("peer");
                    InterlockedMax(ref peak, distinct);
                    if (distinct > 5)
                    {
                        Interlocked.Increment(ref violations);
                    }

                    Thread.SpinWait(random.Next(200, 2000));
                    handle.Dispose();
                }
            },
            TaskCreationOptions.LongRunning)));

        Assert.Equal(0, violations);
        Assert.InRange(peak, 1, 5);
    }

    /// <summary>
    /// Thirty-two threads granting, refreshing, probing, and using grants on the same few links at
    /// once: nothing throws, and every grant handed out names a user who was granted that link.
    /// True: the link route can run on many request threads safely.
    /// False: a race corrupts the grants or hands a link to someone never granted it.
    /// </summary>
    [Fact]
    public async Task Grants_AreSafeUnderRaces()
    {
        var grants = new LinkGrants();
        var links = new[] { "a", "b", "c" };
        var users = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var granted = new ConcurrentDictionary<(string, Guid), byte>();
        var strangers = 0;

        await Task.WhenAll(Enumerable.Range(0, 32).Select(t => Task.Run(() =>
        {
            var random = new Random(100 + t);
            for (var i = 0; i < 2000; i++)
            {
                var link = links[random.Next(links.Length)];
                var user = users[random.Next(users.Length)];
                switch (random.Next(4))
                {
                    case 0:
                        granted[(link, user)] = 0;
                        grants.Grant(link, user);
                        break;
                    case 1:
                        grants.Refresh(link, user);
                        break;
                    case 2:
                        grants.GrantProbe(link);
                        break;
                    default:
                        if (grants.Use(link) is { Probe: false, UserId: { } who } && !granted.ContainsKey((link, who)))
                        {
                            Interlocked.Increment(ref strangers);
                        }

                        break;
                }
            }
        })));

        Assert.Equal(0, strangers);
    }

    /// <summary>
    /// Lookups on many threads while links are written to the table and the registry is told to read
    /// again: nothing throws, and every link written is found once the writers are done.
    /// True: Jellyfin's request threads can look links up while a sync writes them.
    /// False: a lookup during a reload throws, or a written link is lost.
    /// </summary>
    [Fact]
    public async Task Registry_IsSafeWhileLinksAreWritten()
    {
        var database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _dir);
        try
        {
            var table = new ContentSyncTableManager(new FixedProvider(database), NullLogger<ContentSyncTableManager>.Instance);
            var registry = new TableLinkRegistry(new ServiceCollection().AddSingleton(table).BuildServiceProvider());
            using var stop = new CancellationTokenSource();
            var failures = 0;

            var readers = Enumerable.Range(0, 16).Select(t => Task.Run(() =>
            {
                var random = new Random(t);
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        var n = random.Next(200);
                        registry.Find($"/m/F{n}/F{n}.strm");
                        registry.LinkPathFor($"/m/F{n}/F{n}.mkv");
                        if (random.Next(20) == 0)
                        {
                            registry.Invalidate();
                        }
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            })).ToList();

            await Task.Run(() =>
            {
                for (var n = 0; n < 200; n++)
                {
                    table.Upsert(new SyncItem
                    {
                        SourceItemId = $"id{n}",
                        SourceLibraryId = "s",
                        LocalLibraryId = "l",
                        SourcePath = $"/src/F{n}/F{n}.mkv",
                        SourceSize = 1,
                        SourceCreateDate = DateTime.UtcNow,
                        LocalPath = $"/m/F{n}/F{n}.strm",
                        StorageMode = StorageMode.Link,
                        Status = SyncStatus.Synced,
                        StatusDate = DateTime.UtcNow
                    });
                    registry.Invalidate();
                }
            });

            stop.Cancel();
            await Task.WhenAll(readers);
            registry.Invalidate();

            Assert.Equal(0, failures);
            Assert.All(Enumerable.Range(0, 200), n => Assert.Equal($"/m/F{n}/F{n}.strm", registry.LinkPathFor($"/m/F{n}/F{n}.mkv")));
        }
        finally
        {
            database.Dispose();
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Providers;
using MediaBrowser.Model.MediaSegments;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.MetadataSync;

/// <summary>
/// Media segments must settle across a pool of three or more servers in rings and meshes, whatever order the servers scan in:
/// a change made on one server flows out and is never undone by an older copy coming back around, no server takes back a segment
/// it made, and once nothing changes the pool stays still.
/// Each simulated server runs the real reader tagging, pool choice, consolidation, segment table row, and provider plan.
/// </summary>
public sealed class SegmentPoolSimulationTests
{
    private const long RunTime = 10_000_000;
    private const int MaxRounds = 30;

    /// <summary>The settings each scenario runs under: replace, remove, and consolidate.</summary>
    public static TheoryData<bool, bool, bool> Settings()
    {
        var data = new TheoryData<bool, bool, bool>();
        foreach (var replace in new[] { false, true })
        {
            foreach (var remove in new[] { false, true })
            {
                foreach (var consolidate in new[] { false, true })
                {
                    data.Add(replace, remove, consolidate);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// In a ring where B pulls from A, C from B and A from C, A's intro reaches every server, A never takes its own back, and the ring stays still.
    /// True: a ring passes a segment around once and stops.
    /// False: A's intro would return to A as a copy, or the servers would keep rewriting each other.
    /// </summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void Ring_OneChange_FlowsOutAndSettles(bool replace, bool remove, bool consolidate)
    {
        foreach (var seed in Enumerable.Range(0, 25))
        {
            var pool = Pool.Ring(3, replace, remove, consolidate);
            pool["A"].Own(Seg("Intro", 0, 300));

            pool.Settle(new Random(seed));

            foreach (var server in pool.Servers)
            {
                Assert.Equal(new[] { ("Intro", 0L, 300L) }, server.Visible());
            }

            Assert.Empty(pool["A"].Copies);
            pool.AssertNoSelfCopies();
        }
    }

    /// <summary>
    /// A changes its intro while copies of the old one still sit around the ring, in every scan order. A keeps the new one, and every server ends with it.
    /// True: a change flows out instead of being undone by the old copy coming back.
    /// False: A would be rewritten with its own old intro, the loop the pool must never make.
    /// </summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void Ring_ChangeWhileOldCopiesCirculate_IsNeverUndone(bool replace, bool remove, bool consolidate)
    {
        foreach (var size in new[] { 3, 4, 5 })
        {
            foreach (var seed in Enumerable.Range(0, 20))
            {
                var random = new Random(seed);
                var pool = Pool.Ring(size, replace, remove, consolidate);
                pool["A"].Own(Seg("Intro", 0, 300));
                pool.Settle(random);

                pool["A"].Own(Seg("Intro", 50, 400));
                pool.Settle(random, afterEachScan: () => Assert.Equal(new[] { ("Intro", 50L, 400L) }, pool["A"].Visible()));

                foreach (var server in pool.Servers)
                {
                    Assert.Equal(new[] { ("Intro", 50L, 400L) }, server.Visible());
                }

                pool.AssertNoSelfCopies();
            }
        }
    }

    /// <summary>
    /// A changes its intro several times in a row, each before the last has gone around, in rings of three to five servers. It ends the same everywhere.
    /// True: quick edits settle to the last one.
    /// False: an earlier edit could come back and win.
    /// </summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void Ring_QuickEdits_SettleToTheLast(bool replace, bool remove, bool consolidate)
    {
        foreach (var size in new[] { 3, 4, 5 })
        {
            foreach (var seed in Enumerable.Range(0, 10))
            {
                var random = new Random(seed);
                var pool = Pool.Ring(size, replace, remove, consolidate);
                for (var edit = 0; edit < 5; edit++)
                {
                    pool["A"].Own(Seg("Intro", edit * 10, 300 + (edit * 10)));
                    pool.ScanSome(random, random.Next(1, size + 1));
                }

                pool.Settle(random);

                foreach (var server in pool.Servers)
                {
                    Assert.Equal(new[] { ("Intro", 40L, 340L) }, server.Visible());
                }

                pool.AssertNoSelfCopies();
            }
        }
    }

    /// <summary>
    /// In a full mesh of three and four servers, each with its own type, every server ends with every type, none takes its own back, and the mesh stays still.
    /// True: a server that copied another's intro still passes on its own outro.
    /// False: one copied segment would hide a server's own segments, the bug this fixes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void Mesh_EachServerOwnType_EveryServerGetsEveryType(bool replace, bool remove, bool consolidate)
    {
        var types = new[] { "Intro", "Outro", "Recap", "Preview" };
        foreach (var size in new[] { 3, 4 })
        {
            foreach (var seed in Enumerable.Range(0, 60))
            {
                var pool = Pool.Mesh(size, replace, remove, consolidate, new Random(seed + 1000));
                for (var i = 0; i < size; i++)
                {
                    pool.Servers[i].Own(Seg(types[i], i * 1000, (i * 1000) + 500));
                }

                pool.Settle(new Random(seed));

                pool.AssertNoSelfCopies();
                foreach (var server in pool.Servers)
                {
                    Assert.Equal(
                        Enumerable.Range(0, size).Select(i => (types[i], i * 1000L, (i * 1000L) + 500)).OrderBy(s => s.Item2),
                        server.Visible());
                }
            }
        }
    }

    /// <summary>
    /// The case seen in use: MEDIA made an intro, Littleton copied it and has its own outro. MEDIA takes the outro and keeps its own intro.
    /// True: Littleton's own segments reach MEDIA.
    /// False: MEDIA would drop Littleton's whole answer because one segment started on MEDIA.
    /// </summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void TwoWay_CopiedIntroAndOwnOutro_OutroComesBack(bool replace, bool remove, bool consolidate)
    {
        var pool = new Pool(replace, remove, consolidate);
        var media = pool.Add("MEDIA");
        var littleton = pool.Add("Littleton");
        media.Sources.Add(littleton);
        littleton.Sources.Add(media);
        media.Own(Seg("Intro", 0, 300));
        pool.Settle(new Random(1), order: new[] { littleton });

        littleton.Own(Seg("Outro", 9000, 9900));
        pool.Settle(new Random(2));

        pool.AssertNoSelfCopies();
        Assert.Contains(("Intro", 0L, 300L), media.Visible());
        if (replace)
        {
            Assert.Equal(new[] { ("Intro", 0L, 300L), ("Outro", 9000L, 9900L) }, media.Visible());
            Assert.Equal(new[] { ("Intro", 0L, 300L), ("Outro", 9000L, 9900L) }, littleton.Visible());
        }
    }

    /// <summary>
    /// Two servers that both make their own intro, in rings and meshes of three and four, scanning in any order including all at once, end still.
    /// True: a disagreement settles instead of flipping back and forth forever.
    /// False: two intros would chase each other around the pool.
    /// </summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void Conflict_TwoMakersOfOneType_Settles(bool replace, bool remove, bool consolidate)
    {
        foreach (var size in new[] { 3, 4 })
        {
            foreach (var seed in Enumerable.Range(0, 15))
            {
                foreach (var mesh in new[] { false, true })
                {
                    var random = new Random(seed);
                    var pool = mesh ? Pool.Mesh(size, replace, remove, consolidate, new Random(seed + 77)) : Pool.Ring(size, replace, remove, consolidate);
                    pool.Servers[0].Own(Seg("Intro", 0, 300));
                    pool.Servers[1].Own(Seg("Intro", 20, 280));

                    pool.Settle(random, allAtOnce: seed % 3 == 0);

                    pool.AssertNoSelfCopies();
                }
            }
        }
    }

    /// <summary>
    /// A removes its intro after it went around. Nothing ever brings it back to A, and with Remove on it leaves every server.
    /// True: a removed segment stays removed where it was made.
    /// False: the copies around the pool would resurrect it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void Removal_NeverComesBackToTheMaker(bool replace, bool remove, bool consolidate)
    {
        foreach (var mesh in new[] { false, true })
        {
            foreach (var seed in Enumerable.Range(0, 15))
            {
                var random = new Random(seed);
                var pool = mesh ? Pool.Mesh(4, replace, remove, consolidate, new Random(seed + 5)) : Pool.Ring(4, replace, remove, consolidate);
                pool["A"].Own(Seg("Intro", 0, 300));
                pool.Settle(random);

                pool["A"].Own();
                pool.Settle(random, afterEachScan: () => Assert.Empty(pool["A"].Visible()));

                pool.AssertNoSelfCopies();
                if (remove && !mesh)
                {
                    Assert.All(pool.Servers, s => Assert.Empty(s.Visible()));
                }
            }
        }
    }

    /// <summary>
    /// Overlapping intros from two servers are consolidated on a third. The consolidated one never goes back to undo either maker, and the pool stays still.
    /// True: a server's own view of the pool never feeds back into the servers it came from.
    /// False: the shrunk intro would replace the makers' own, and their next scans would fight over it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void Consolidated_NeverUndoesTheMakers(bool replace, bool remove, bool consolidate)
    {
        foreach (var seed in Enumerable.Range(0, 20))
        {
            var pool = Pool.Mesh(3, replace, remove, consolidate, new Random(seed + 9));
            pool["A"].Own(Seg("Intro", 0, 300), Seg("Intro", 50, 450));
            pool["B"].Own(Seg("Outro", 9000, 9500));

            pool.Settle(new Random(seed));

            Assert.Equal(new[] { ("Intro", 0L, 300L), ("Intro", 50L, 450L) }, pool["A"].Visible().Where(s => s.Type == "Intro"));
            pool.AssertNoSelfCopies();
        }
    }

    /// <summary>
    /// A pool larger than any seen in use, six servers each pulling from two others at random ranks, with edits landing mid-scan, settles every time.
    /// True: no shape of pool makes segments go around forever.
    /// False: some pool shape would keep rewriting segments on every scan.
    /// </summary>
    [Fact]
    public void RandomPools_AlwaysSettle()
    {
        var types = new[] { "Intro", "Outro", "Recap", "Preview", "Commercial" };
        for (var seed = 0; seed < 1000; seed++)
        {
            var random = new Random(seed);
            var pool = new Pool(random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0);
            var names = Enumerable.Range(0, 6).Select(i => ((char)('A' + i)).ToString()).ToList();
            foreach (var name in names)
            {
                pool.Add(name);
            }

            foreach (var server in pool.Servers)
            {
                foreach (var other in pool.Servers.Where(o => o != server).OrderBy(_ => random.Next()).Take(2))
                {
                    server.Sources.Add(other);
                }
            }

            for (var step = 0; step < 12; step++)
            {
                var maker = pool.Servers[random.Next(pool.Servers.Count)];
                var type = types[random.Next(types.Length)];
                var start = random.Next(0, 50) * 1000;
                maker.Own(maker.Others.Where(o => o.Type != type).Append(Seg(type, start, start + random.Next(1, 9) * 100)).ToArray());
                pool.ScanSome(random, random.Next(0, 6));
            }

            pool.Settle(random, allAtOnce: seed % 4 == 0);
            pool.AssertNoSelfCopies();
        }
    }

    private static PeerSegment Seg(string type, long start, long end) => new() { Type = type, StartTicks = start, EndTicks = end };

    /// <summary>One simulated server: the segments its own providers made, the ones Server Sync gave it, its segment table row, and whom it pulls from.</summary>
    private sealed class Server
    {
        public Server(string id, Pool pool)
        {
            Id = id;
            Pool = pool;
        }

        public string Id { get; }

        public Pool Pool { get; }

        public List<Server> Sources { get; } = new();

        public List<PeerSegment> Others { get; private set; } = new();

        public List<PeerSegment> Copies { get; private set; } = new();

        public SourceMediaSegments? Row { get; private set; }

        public void Own(params PeerSegment[] segments) => Others = segments.ToList();

        public List<(string Type, long Start, long End)> Visible()
            => Others.Concat(Copies).Select(s => (s.Type, s.StartTicks, s.EndTicks)).Distinct().OrderBy(s => s.StartTicks).ThenBy(s => s.EndTicks).ToList();

        /// <summary>Answers a peer as <see cref="PeerSegmentReader"/> does.</summary>
        public PeerSegmentsItem Answer()
        {
            var all = Others.Concat(Copies).ToList();
            var tagged = PeerSegmentReader.TagOrigins(all, Others, Copies.Count > 0 ? Row : null);
            var answer = new PeerSegmentsItem { ItemId = Id, Found = true, Size = 1, RunTimeTicks = RunTime, SegmentOrigins = true };
            answer.OriginServerId = tagged.Where(s => s.OriginServerId is not null).Select(s => s.OriginServerId).FirstOrDefault();
            answer.Segments.AddRange(tagged);
            return answer;
        }

        /// <summary>Picks from the answers, keeps the row as the scan and apply do, then runs the provider as Jellyfin would.</summary>
        public void Apply(IReadOnlyList<(Server Source, PeerSegmentsItem Answer)> answers)
        {
            var local = MediaSegmentsValue.FromRow(Row);
            var chosen = MediaSegmentsValue.Choose(answers.Select(a => (a.Source.Id, (string?)a.Source.Id, a.Source.Id + "-item", a.Answer)), Id)
                ?.Consolidate(Pool.Consolidate, Pool.Consolidate, Pool.Consolidate);
            var worth = MediaSegmentsValue.Worth(chosen, local, Pool.Remove);
            if (worth is not null)
            {
                // The row goes through JSON both ways, as the Metadata table and the segment table keep it.
                Row = MediaSegmentsValue.FromJson(worth.ToJson())!.ToRow(Id, DateTime.UnixEpoch);
            }

            RunProvider();
        }

        /// <summary>Does what <see cref="PeerSegmentProvider.GetMediaSegments"/> does with the row.</summary>
        private void RunProvider()
        {
            if (Row is null)
            {
                return;
            }

            var itemId = Guid.Empty;
            var held = Row.HeldTypes ?? (IReadOnlyList<string>)Array.Empty<string>();
            var fitted = PeerSegmentProvider.Fit(Row.Segments, itemId, RunTime).Where(f => !PeerSegmentProvider.IsHeld(f.Type, held)).ToList();
            var others = Others.Where(o => !PeerSegmentProvider.IsHeld(Parse(o.Type), held)).ToList();
            var kept = Copies.Where(c => !PeerSegmentProvider.IsHeld(Parse(c.Type), held)).Select(c => Dto(c, itemId)).ToList();
            var plan = PeerSegmentProvider.Plan(fitted, others.Select(o => Dto(o, itemId)).ToList(), kept, Pool.Replace, Pool.Remove);
            var gone = plan.Remove.Select(r => (r.Type.ToString(), r.StartTicks, r.EndTicks)).ToList();
            Others = Others.Where(o => !gone.Contains((Parse(o.Type).ToString(), o.StartTicks, o.EndTicks))).ToList();

            Copies = plan.Ours.Select(o => new PeerSegment { Type = o.Type.ToString(), StartTicks = o.StartTicks, EndTicks = o.EndTicks }).ToList();
        }

        public string State()
            => string.Join(";", Others.Select(s => $"o{s.Type}{s.StartTicks}-{s.EndTicks}").OrderBy(x => x, StringComparer.Ordinal))
                + "|" + string.Join(";", Copies.Select(s => $"c{s.Type}{s.StartTicks}-{s.EndTicks}").OrderBy(x => x, StringComparer.Ordinal))
                + "|" + (Row is null ? "-" : string.Join(";", Row.Segments.Select(s => $"r{s.Type}{s.StartTicks}-{s.EndTicks}@{s.OriginServerId}").OrderBy(x => x, StringComparer.Ordinal)) + "h" + string.Join(",", Row.HeldTypes ?? Array.Empty<string>()));

        private static MediaSegmentType Parse(string type) => Enum.Parse<MediaSegmentType>(type, ignoreCase: true);

        private static MediaSegmentDto Dto(PeerSegment s, Guid itemId) => new() { Id = Guid.NewGuid(), ItemId = itemId, Type = Parse(s.Type), StartTicks = s.StartTicks, EndTicks = s.EndTicks };
    }

    /// <summary>A pool of simulated servers and the settings they share.</summary>
    private sealed class Pool
    {
        public Pool(bool replace, bool remove, bool consolidate)
        {
            Replace = replace;
            Remove = remove;
            Consolidate = consolidate;
        }

        public bool Replace { get; }

        public bool Remove { get; }

        public bool Consolidate { get; }

        public List<Server> Servers { get; } = new();

        public Server this[string id] => Servers.Single(s => s.Id == id);

        /// <summary>A ring where each server pulls from the one before it, so a change on A goes A to B to C and back to A.</summary>
        public static Pool Ring(int size, bool replace, bool remove, bool consolidate)
        {
            var pool = new Pool(replace, remove, consolidate);
            for (var i = 0; i < size; i++)
            {
                pool.Add(((char)('A' + i)).ToString());
            }

            for (var i = 0; i < size; i++)
            {
                pool.Servers[i].Sources.Add(pool.Servers[(i + size - 1) % size]);
            }

            return pool;
        }

        /// <summary>A full mesh where every server pulls from every other, each in its own random rank order.</summary>
        public static Pool Mesh(int size, bool replace, bool remove, bool consolidate, Random random)
        {
            var pool = new Pool(replace, remove, consolidate);
            for (var i = 0; i < size; i++)
            {
                pool.Add(((char)('A' + i)).ToString());
            }

            foreach (var server in pool.Servers)
            {
                server.Sources.AddRange(pool.Servers.Where(o => o != server).OrderBy(_ => random.Next()));
            }

            return pool;
        }

        public Server Add(string id)
        {
            var server = new Server(id, this);
            Servers.Add(server);
            return server;
        }

        public void Scan(Server server) => server.Apply(server.Sources.Select(s => (s, s.Answer())).ToList());

        public void ScanSome(Random random, int count)
        {
            for (var i = 0; i < count; i++)
            {
                Scan(Servers[random.Next(Servers.Count)]);
            }
        }

        public string State() => string.Join(" / ", Servers.Select(s => s.Id + ":" + s.State()));

        /// <summary>
        /// Scans rounds in random orders until a whole round changes nothing, then scans more rounds to prove it stays still.
        /// With all at once, every server reads its peers before any applies, the worst case for two servers swapping.
        /// </summary>
        public void Settle(Random random, Action? afterEachScan = null, bool allAtOnce = false, IEnumerable<Server>? order = null)
        {
            foreach (var server in order ?? Enumerable.Empty<Server>())
            {
                Scan(server);
                afterEachScan?.Invoke();
            }

            var history = new List<string>();
            for (var round = 0; round < MaxRounds; round++)
            {
                var before = State();
                if (allAtOnce && round % 2 == 0)
                {
                    var answers = Servers.ToDictionary(s => s, s => s.Sources.Select(p => (p, p.Answer())).ToList());
                    foreach (var server in Servers)
                    {
                        server.Apply(answers[server]);
                        afterEachScan?.Invoke();
                    }
                }
                else
                {
                    foreach (var server in Servers.OrderBy(_ => random.Next()))
                    {
                        Scan(server);
                        afterEachScan?.Invoke();
                    }
                }

                var after = State();
                if (before == after && !(allAtOnce && round % 2 == 0))
                {
                    // Still for a full round in random order: prove it with more rounds in other orders.
                    for (var check = 0; check < 5; check++)
                    {
                        foreach (var server in Servers.OrderBy(_ => random.Next()))
                        {
                            Scan(server);
                        }

                        Assert.True(after == State(), "The pool changed again after it had settled: " + after + " became " + State());
                    }

                    return;
                }

                history.Add(after);
            }

            Assert.Fail("The pool never settled in " + MaxRounds + " rounds. Last states: " + string.Join("  ||  ", history.TakeLast(4)));
        }

        public void AssertNoSelfCopies()
        {
            foreach (var server in Servers)
            {
                Assert.True(
                    server.Row is null || server.Row.Segments.All(s => !string.Equals(s.OriginServerId, server.Id, StringComparison.OrdinalIgnoreCase)),
                    server.Id + " took back a segment it made: " + server.State());
            }
        }
    }
}

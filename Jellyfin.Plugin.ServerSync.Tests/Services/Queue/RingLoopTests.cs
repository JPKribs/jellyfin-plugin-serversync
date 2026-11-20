using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// Metadata edits in rings and meshes of three and four servers settle: one change flows out and goes quiet, a stale value
/// arriving late never overwrites a newer one, and concurrent edits end on the same value and stamp everywhere.
/// </summary>
[Collection(QueueStateCollection.Name)]
public sealed class RingLoopTests : IDisposable
{
    private readonly List<RingNetwork> _nets = new();

    public RingLoopTests() => LoopBreaker.Reset();

    public static TheoryData<string> Topologies => new() { "ring3", "ring4", "biring4", "mesh3", "mesh4" };

    public static TheoryData<string, string> TopologyAndServer
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var t in new[] { "ring3", "ring4", "biring4", "mesh3", "mesh4" })
            {
                foreach (var s in t.EndsWith('3') ? new[] { "A", "B", "C" } : new[] { "A", "B", "C", "D" })
                {
                    data.Add(t, s);
                }
            }

            return data;
        }
    }

    public static TheoryData<string, string, string, string> ConcurrentCases
    {
        get
        {
            var data = new TheoryData<string, string, string, string>();
            foreach (var t in new[] { "ring3", "ring4", "biring4", "mesh3", "mesh4" })
            {
                foreach (var (first, second) in new[] { ("A", "B"), ("B", "A"), ("A", "C"), ("C", "B") })
                {
                    foreach (var order in new[] { "fifo", "lifo", "7", "42" })
                    {
                        data.Add(t, first, second, order);
                    }
                }
            }

            return data;
        }
    }

    public void Dispose()
    {
        foreach (var net in _nets)
        {
            net.Dispose();
        }
    }

    private RingNetwork Net(string topology)
    {
        var net = RingNetwork.Build(topology);
        _nets.Add(net);
        return net;
    }

    private static int EdgeCount(RingNetwork net) => net.Servers.Sum(s => s.Config.Configuration.Servers.Count(e => e.Pushes));

    /// <summary>
    /// Every server holds the value. In a mesh every server also holds the winning stamp. Elsewhere a server may hold an older
    /// stamp on the right value (see the skipped understated-stamp test), but never a newer one.
    /// </summary>
    private static void AssertConverged(RingNetwork net, string path, string value, ObjectVersion stamp, bool exact = true)
    {
        Assert.Equal(new[] { value }, net.ValuesOf(path));
        Assert.All(net.Servers, s =>
        {
            var held = s.Stamp(path);
            if (!exact && (held is null || VersionDecider.IsNewer(stamp, held)))
            {
                return;
            }

            Assert.NotNull(held);
            Assert.True(VersionDecider.SameVersion(held!, stamp), $"{s.Name} holds {held!.ServerId}@{held.Timestamp:O}");
        });
    }

    private static bool Mesh(string topology) => topology.StartsWith("mesh", StringComparison.Ordinal);

    private static void AssertQuiet(RingNetwork net, string path)
    {
        var values = net.Servers.Select(s => s.Values[path]).ToList();
        var stamps = net.StampsOf(path);
        var writes = net.Writes;
        Assert.Equal(0, net.DeliverAll());
        Assert.Equal(0, net.ScanAll());
        Assert.Equal(0, net.ScanAllAtOnce());
        Assert.Empty(net.Waiting());
        Assert.Equal(writes, net.Writes);
        Assert.Equal(values, net.Servers.Select(s => s.Values[path]).ToList());
        Assert.Equal(stamps, net.StampsOf(path));
    }

    /// <summary>
    /// One hand edit reaches every server through hints and scans, carrying the editor's stamp, and the network goes quiet.
    /// True: no server re-announces a value it was given, so the number of hints is at most one per link.
    /// False: a change bounces around the ring, or a server ends with an older value or its own stamp on someone else's edit.
    /// </summary>
    [Theory]
    [MemberData(nameof(TopologyAndServer))]
    public void OneEdit_FlowsOutCarryingTheEditorsStamp_AndGoesQuiet(string topology, string editor)
    {
        var net = Net(topology);
        var path = net.AddItem();
        var stamp = net.Edit(editor, path, "edited");

        net.Settle();

        AssertConverged(net, path, "edited", stamp);
        Assert.True(net.Delivered <= EdgeCount(net), $"{net.Delivered} hints for {EdgeCount(net)} links");
        Assert.Equal(net.Servers.Count - 1, net.Writes);
        AssertQuiet(net, path);
    }

    /// <summary>
    /// The receivers of a hint never re-announce it, since only the editor may announce its own stamp.
    /// True: in a one-way ring an edit reaches only the next server by hint, and the rest by scans.
    /// False: a receiver forwards it and the hint comes back to the editor.
    /// </summary>
    [Theory]
    [InlineData("ring3")]
    [InlineData("ring4")]
    public void OneWayRing_HintReachesOnlyTheNextServer_AndNeverComesBack(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Edit("A", path, "edited");

        Assert.Equal(1, net.DeliverAll());
        Assert.Equal("edited", net["B"].Values[path]);
        Assert.Empty(net.Waiting());
        Assert.Empty(net["A"].Answers);
        Assert.All(net.Servers.Where(s => s.Name is not ("A" or "B")), s => Assert.Equal("v0", s.Values[path]));
    }

    /// <summary>
    /// A value that travels by scans alone keeps the editor's stamp at every hop, and no hop announces it.
    /// True: a server two or three hops away holds the same stamp as the editor, and the only hints sent are the editor's own,
    /// when its scan keeps its edit against a peer that has not taken it yet.
    /// False: a hop records the copy as its own edit, which then looks newer than real edits made elsewhere.
    /// </summary>
    [Theory]
    [MemberData(nameof(Topologies))]
    public void ValueCarriedOnlyByScans_KeepsTheEditorsStamp_AndIsNotAnnounced(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        var stamp = net.Edit("A", path, "edited");
        foreach (var peer in net["A"].Config.Configuration.Servers.Where(e => e.Pushes))
        {
            net.Lose("A", peer.Key);
        }

        for (var i = 0; i < 4; i++)
        {
            net.ScanAll();
            Assert.All(net.Waiting(), w => Assert.Equal("A", w.From.Name));
        }

        net.Settle();
        AssertConverged(net, path, "edited", stamp);
        AssertQuiet(net, path);
    }

    /// <summary>
    /// Two servers edit the same item close together. Every delivery order and topology ends on the newer edit everywhere.
    /// True: the stamps form one order every server agrees on.
    /// False: servers disagree forever, or the older edit wins somewhere.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConcurrentCases))]
    public void TwoConcurrentEdits_ConvergeOnTheNewer(string topology, string first, string second, string order)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Edit(first, path, "from-" + first);
        var newer = net.Edit(second, path, "from-" + second);

        net.Settle(order);

        AssertConverged(net, path, "from-" + second, newer, Mesh(topology));
        AssertQuiet(net, path);
    }

    /// <summary>
    /// Two edits with the very same time are told apart by server id, so both sides pick the same one.
    /// True: the server with the larger id wins on every server.
    /// False: each server keeps its own edit and they never agree.
    /// </summary>
    [Theory]
    [InlineData("mesh3", "fifo")]
    [InlineData("mesh3", "lifo")]
    [InlineData("mesh4", "fifo")]
    [InlineData("mesh4", "3")]
    [InlineData("ring3", "fifo")]
    [InlineData("ring4", "lifo")]
    [InlineData("biring4", "11")]
    public void SameInstantEdits_ServerIdBreaksTheTie_Everywhere(string topology, string order)
    {
        var net = Net(topology);
        var path = net.AddItem();
        var at = net.Tick();
        net.Edit("A", path, "from-A", at);
        var winner = net.Edit("C", path, "from-C", at);

        net.Settle(order);

        AssertConverged(net, path, "from-C", winner, Mesh(topology));
        AssertQuiet(net, path);
    }

    /// <summary>
    /// Every server of a mesh edits the same item at once.
    /// True: all end on the last edit, and the number of hints stays bounded.
    /// False: the edits chase each other around the mesh.
    /// </summary>
    [Theory]
    [InlineData("mesh4", "fifo")]
    [InlineData("mesh4", "lifo")]
    [InlineData("mesh4", "5")]
    [InlineData("mesh4", "99")]
    [InlineData("biring4", "fifo")]
    [InlineData("ring4", "fifo")]
    [InlineData("ring4", "17")]
    public void EveryServerEditsAtOnce_ConvergesOnTheLast_WithBoundedHints(string topology, string order)
    {
        var net = Net(topology);
        var path = net.AddItem();
        ObjectVersion last = null!;
        foreach (var s in net.Servers)
        {
            last = net.Edit(s.Name, path, "from-" + s.Name);
        }

        net.Settle(order);

        AssertConverged(net, path, "from-" + net.Servers[^1].Name, last, Mesh(topology));
        Assert.True(net.Delivered <= 3 * EdgeCount(net), $"{net.Delivered} hints for {EdgeCount(net)} links");
        AssertQuiet(net, path);
    }

    /// <summary>
    /// An older edit's hint reaches a server after a newer edit already applied there.
    /// True: it is turned away at triage or kept by the handler, and the newer value stays.
    /// False: the late hint overwrites the newer value.
    /// </summary>
    [Theory]
    [InlineData("mesh3")]
    [InlineData("mesh4")]
    [InlineData("biring4")]
    public void StaleHintArrivingLate_DoesNotOverwriteTheNewerValue(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Edit("B", path, "old");
        var newer = net.Edit("A", path, "new");

        // A's newer edit reaches everyone first, B's older one last.
        while (net.Waiting().FirstOrDefault(w => w.From.Name == "A") is { From: not null } w)
        {
            net.DeliverOne("A", w.Row.PeerKey);
        }

        var linked = net["A"].Config.Configuration.Servers.Where(e => e.Pushes).Select(e => e.Key).Append("A").ToList();
        Assert.All(net.Servers.Where(s => linked.Contains(s.Name)), s => Assert.Equal("new", s.Values[path]));
        net.Settle();

        AssertConverged(net, path, "new", newer, Mesh(topology));
        AssertQuiet(net, path);
    }

    /// <summary>
    /// A server that holds its own newer edit turns an older hint away at triage. The sender is not told the newer value
    /// by that answer, so the scan carries it.
    /// True: the sender takes the newer value by scan, and the network goes quiet.
    /// False: the two servers trade values, or the older value wins.
    /// </summary>
    [Theory]
    [InlineData("mesh3")]
    [InlineData("mesh4")]
    public void OlderHintMeetsNewerOwnEdit_KeepsAndReannouncesOnce(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Edit("A", path, "old");
        net.Lose("B", "A");
        var newer = net.Edit("B", path, "new");
        foreach (var w in net.Waiting().Where(w => w.From.Name == "B").ToList())
        {
            net.Lose("B", w.Row.PeerKey);
        }

        // Only B holds the newer edit, and A's older hint now reaches it.
        Assert.Equal(HintCode.Superseded, net.DeliverOne("A", "B"));
        Assert.Equal("new", net["B"].Values[path]);

        net.Settle();

        AssertConverged(net, path, "new", newer);
        AssertQuiet(net, path);
    }

    /// <summary>
    /// A server keeps a value it took from a third server, but does not announce it as its own.
    /// True: the hint the receiver would send carries nobody else's stamp.
    /// False: a server re-announces another's edit, which triage turns away or, worse, records under the wrong server.
    /// </summary>
    [Fact]
    public void KeptValueFromAThirdServer_IsNotReannounced()
    {
        var net = Net("mesh3");
        var path = net.AddItem();
        net.Edit("B", path, "old");
        net.Lose("B", "C");
        net.Edit("A", path, "new");
        Assert.Equal(HintCode.Applied, net.DeliverOne("A", "C"));

        net.DeliverOne("B", "A");

        Assert.Equal("new", net["A"].Values[path]);
        Assert.DoesNotContain(net.Waiting(), w => w.Row.VersionServerId != w.From.Id);
    }

    /// <summary>
    /// A hand edit stamped while its hint gathers loses to a peer's newer edit that applies during the wait, and is not announced.
    /// True: the gathered change is dropped and everyone ends on the newer edit.
    /// False: the older gathered edit goes out after the newer one and overwrites it somewhere.
    /// </summary>
    [Theory]
    [InlineData("mesh3")]
    [InlineData("mesh4")]
    [InlineData("biring4")]
    public void GatheringEditOvertakenByNewerPeerEdit_IsNotAnnounced(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        var gathering = net.Note("A", path, "a-old");
        var newer = net.Edit("B", path, "b-new");
        net.DeliverOne("B", "A");

        Assert.Equal(0, net.Raise(gathering));
        net.Settle();

        AssertConverged(net, path, "b-new", newer);
        AssertQuiet(net, path);
    }

    /// <summary>
    /// A provider's change after a hand edit is announced unrecorded. Servers holding the edit's stamp keep the edit, and its
    /// author announces it once more, so the provider echo stops after one round.
    /// True: a bounded number of hints, and every server but the provider's keeps the hand edit.
    /// False: unstamped provider work bounces around and replaces hand edits over hints.
    /// </summary>
    [Theory]
    [InlineData("mesh3")]
    [InlineData("mesh4")]
    public void ProviderWorkAfterAnEdit_DoesNotReplaceTheEditOverHints(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Edit("A", path, "hand");
        net.DeliverAll();

        net.ProviderChange("B", path, "provider");
        var delivered = net.DeliverAll();

        Assert.True(delivered <= 2 * (net.Servers.Count - 1), $"{delivered} hints");
        Assert.All(net.Servers.Where(s => s.Name != "B"), s => Assert.Equal("hand", s.Values[path]));
        Assert.Empty(net.Waiting());
    }

    /// <summary>
    /// The provider's value on B still sits under A's stamp, since provider work records none, and A's re-announcement of that
    /// same stamp is answered AlreadyCurrent. Today the scans then spread it: same stamp, different value, arrival wins.
    /// Pinned as today's behaviour; the skipped test below states what should happen.
    /// </summary>
    [Theory]
    [InlineData("mesh3")]
    [InlineData("mesh4")]
    public void ProviderWorkAfterAnEdit_SpreadsByScansToday(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Edit("A", path, "hand");
        net.DeliverAll();
        net.ProviderChange("B", path, "provider");
        net.DeliverAll();

        Assert.Equal("provider", net["B"].Values[path]);
        Assert.Contains(net["B"].Answers, a => a.From == "A" && a.Code == HintCode.AlreadyCurrent);

        net.Settle();
        Assert.Single(net.ValuesOf(path));
    }

    /// <summary>
    /// Fixed: only the server that made an edit sets right a drifted copy of it. Before, a provider refresh on B after B took A's hand edit left B with a different value under A's stamp. Hints keep the
    /// hand edit on A and C, but the next scans see "same stamp, different value", which VersionDecider.Decide lets the arrival win
    /// (VersionDecider.cs:34-37, reached through ScanConflictSettler via VersionConflictResolver.Decide, VersionConflictResolver.cs:204).
    /// Whichever server scans B first takes the provider value, and the scans carry it on until it replaces the hand edit on its author.
    /// The outcome depends on which server's scan runs first, and within one scan a server can flip twice (once per peer).
    /// </summary>
    [Theory]
    [InlineData("mesh3")]
    [InlineData("ring3")]
    public void ProviderWorkAfterAnEdit_NeverReplacesTheHandEditByScans(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Edit("A", path, "hand");
        net.Settle();
        net.ProviderChange("B", path, "provider");
        net.DeliverAll();

        // C's scheduled scan happens to run first, then A's, then B's.
        net.Scan("C");
        net.Scan("A");
        net.Scan("B");

        Assert.Equal(new[] { "hand" }, net.ValuesOf(path));
    }

    /// <summary>
    /// Fixed: the value read live is recorded under the origin's stamp at the read. Before, ItemHintHandler read the origin's item live but recorded the hint's stamp (ItemHintHandler.cs:243 and 302-305).
    /// When the origin took a newer edit from a third server between sending and the read, the receiver gets the newer value
    /// under the older stamp, and an edit made between the two then overwrites it there. Here C edits last; A takes it before
    /// its own older hint is read by B; B then holds C's value under A's stamp and gives it up to D's edit, which is older than C's.
    /// Scans repair it later, but until then an older value has replaced a newer one.
    /// </summary>
    [Fact]
    public void LiveReadUnderAnOlderStamp_IsNotOverwrittenByAMiddleEdit()
    {
        var net = RingNetwork.Create("A", "B", "C", "D");
        _nets.Add(net);
        net.Link("C", "A");
        net.Link("A", "B");
        net.Link("D", "B");
        var path = net.AddItem();
        net.Edit("A", path, "from-A");
        net.Edit("D", path, "from-D");
        net.Edit("C", path, "from-C");

        net.DeliverOne("C", "A");
        net.DeliverOne("A", "B");
        Assert.Equal("from-C", net["B"].Values[path]);
        net.DeliverOne("D", "B");

        Assert.Equal("from-C", net["B"].Values[path]);
    }

    /// <summary>
    /// A save that slips past the guard after an apply looks like provider work, which is announced unrecorded and dies out.
    /// True: the echo stops after one hop on each server that holds a stamp.
    /// False: an apply's own save keeps the object bouncing.
    /// </summary>
    [Theory]
    [InlineData("mesh3")]
    [InlineData("mesh4")]
    [InlineData("ring4")]
    public void LateProviderEchoAfterEachApply_DiesOut(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        foreach (var s in net.Servers)
        {
            s.AfterApply = (server, p) => server.Publisher.PublishMetadata(server.ItemId(p), p, new ObjectVersion { ServerId = server.Id, Timestamp = net.Tick() }, null, recorded: false);
        }

        var stamp = net.Edit("A", path, "edited");
        net.Settle();

        AssertConverged(net, path, "edited", stamp, Mesh(topology));
        Assert.True(net.Delivered <= 2 * EdgeCount(net), $"{net.Delivered} hints for {EdgeCount(net)} links");
    }

    /// <summary>
    /// The worst echo: every apply raises a hand-edit-like save with a fresh stamp of its own. The loop breaker holds it back.
    /// True: each server announces the object at most the loop breaker's limit, so the storm ends.
    /// False: the servers bounce the object forever.
    /// </summary>
    [Theory]
    [InlineData("mesh3")]
    [InlineData("ring3")]
    [InlineData("mesh4")]
    public void RecordedEchoAfterEachApply_IsCutOffByTheLoopBreaker(string topology)
    {
        LoopBreaker.Reset();
        var net = Net(topology);
        var path = net.AddItem();
        foreach (var s in net.Servers)
        {
            s.AfterApply = (server, p) =>
            {
                var at = net.Tick();
                server.Versions.Set(new ObjectVersion { Kind = HintKind.Metadata, Key = server.MetadataKey(p), ServerId = server.Id, Timestamp = at });
                server.Publisher.PublishMetadata(server.ItemId(p), p, new ObjectVersion { ServerId = server.Id, Timestamp = at }, null);
            };
        }

        net.Edit("A", path, "edited");
        var delivered = net.DeliverAll(max: 2000);

        Assert.True(delivered <= net.Servers.Count * (LoopBreaker.Limit + 1) * (net.Servers.Count - 1), $"{delivered} hints");
        Assert.Empty(net.Waiting());
        Assert.Equal(new[] { "edited" }, net.ValuesOf(path));
    }

    /// <summary>
    /// A stale stamp held by a server that missed the hints never wins a scan against the newer stamp.
    /// True: the server that missed the edit takes it, and the others keep it.
    /// False: the stale value travels back over the scan and replaces the edit.
    /// </summary>
    [Theory]
    [MemberData(nameof(Topologies))]
    public void ServerThatMissedTheEdit_TakesItByScan_AndNeverPushesItsOldValueBack(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Edit("C", path, "old");
        net.Settle();
        var newer = net.Edit("A", path, "new");
        foreach (var w in net.Waiting().Where(w => w.Row.PeerKey == "C").ToList())
        {
            net.Lose(w.From.Name, "C");
        }

        net.DeliverAll();
        net.ScanAll();
        net.Settle();

        AssertConverged(net, path, "new", newer, Mesh(topology));
        AssertQuiet(net, path);
    }

    /// <summary>
    /// A deleted-and-recreated pattern for metadata: an edit back to the starting value is still a newer edit, so the older middle value never returns.
    /// True: edits v0 to x to v0 settle on v0 everywhere.
    /// False: a server that only saw x pushes it back.
    /// </summary>
    [Theory]
    [MemberData(nameof(Topologies))]
    public void EditBackToTheOldValue_DoesNotLetTheMiddleValueReturn(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Edit("A", path, "x");
        net.Settle();
        var back = net.Edit("B", path, "v0");

        net.Settle("lifo");

        AssertConverged(net, path, "v0", back, Mesh(topology));
        AssertQuiet(net, path);
    }

    /// <summary>
    /// Settles after many rounds of random edits on random servers, each followed by a partial delivery.
    /// True: the last edit wins everywhere, every time.
    /// False: some interleaving loops or leaves the servers apart.
    /// </summary>
    [Theory]
    [InlineData("mesh3", 1)]
    [InlineData("mesh3", 2)]
    [InlineData("mesh4", 3)]
    [InlineData("mesh4", 4)]
    [InlineData("ring3", 5)]
    [InlineData("ring4", 6)]
    [InlineData("biring4", 7)]
    [InlineData("biring4", 8)]
    public void RandomEditsAndPartialDelivery_ConvergeOnTheLastEdit(string topology, int seed)
    {
        var net = Net(topology);
        var path = net.AddItem();
        var random = new Random(seed);
        ObjectVersion last = null!;
        string lastValue = string.Empty;
        for (var i = 0; i < 12; i++)
        {
            var server = net.Servers[random.Next(net.Servers.Count)].Name;
            lastValue = "e" + i + "-" + server;
            last = net.Edit(server, path, lastValue);
            for (var d = random.Next(4); d > 0 && net.Waiting().Count > 0; d--)
            {
                var w = net.Waiting()[random.Next(net.Waiting().Count)];
                net.DeliverOne(w.From.Name, w.Row.PeerKey);
            }

            if (random.Next(3) == 0)
            {
                net.Scan(net.Servers[random.Next(net.Servers.Count)].Name);
            }
        }

        net.Settle(seed.ToString(System.Globalization.CultureInfo.InvariantCulture));

        AssertConverged(net, path, lastValue, last, Mesh(topology));
        AssertQuiet(net, path);
    }

    /// <summary>
    /// Fixed: only the server that made an edit sets right a drifted copy of it. Before, two servers holding the same stamp but different values (a provider refresh on one changed the value after it took the edit,
    /// and provider work records no stamp). VersionDecider.Decide treats "same stamp, different value" as drift and lets the
    /// arrival win (VersionDecider.cs:34-37), and VersionConflictResolver.Decide always passes valuesEqual: false for a queued scan row,
    /// so whenever scans overlap each server takes its neighbour's value and the values rotate around the ring forever.
    /// Run sequentially they settle, but on the provider's value, which overwrites the hand edit everywhere, including on its author.
    /// </summary>
    [Fact]
    public void SameStampDifferentValues_OverlappingScans_Settle()
    {
        var net = Net("ring3");
        var path = net.AddItem();
        var stamp = net.Edit("A", path, "hand");
        net.Settle();

        // A provider refresh on B rewrites the value and records no stamp, so B still holds A's stamp.
        net.ProviderChange("B", path, "provider", announce: false);

        for (var round = 0; round < 6; round++)
        {
            net.ScanAllAtOnce();
        }

        Assert.Single(net.ValuesOf(path));
        Assert.Equal("hand", net["A"].Values[path]);
        Assert.True(VersionDecider.SameVersion(net["A"].Stamp(path)!, stamp));
    }
}

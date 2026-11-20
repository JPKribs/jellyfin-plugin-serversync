using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Queue;

/// <summary>
/// Playlists, user history, and content files in meshes of three and four servers settle without loops or resurrection.
/// </summary>
[Collection(QueueStateCollection.Name)]
public sealed class RingKindsTests : IDisposable
{
    private const string List = "0123456789abcdef0123456789abcdef";
    private readonly List<RingNetwork> _nets = new();

    public RingKindsTests() => LoopBreaker.Reset();

    public static TheoryData<string, string> MeshOrders
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var t in new[] { "mesh3", "mesh4" })
            {
                foreach (var o in new[] { "fifo", "lifo", "3", "8", "21" })
                {
                    data.Add(t, o);
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

    private static int Links(RingNetwork net) => net.Servers.Sum(s => s.Config.Configuration.Servers.Count(e => e.Pushes));

    private static IReadOnlyList<string> PlaylistStates(RingNetwork net)
        => net.Servers.Select(s => s.Playlists.TryGetValue(List, out var v) ? v : "<none>").Distinct().ToList();

    // ---------------------------------------------------------------- playlists

    /// <summary>
    /// A new playlist reaches every server of a mesh once, and nobody announces the copy back.
    /// True: one hint per link from the author, then quiet.
    /// False: copies are re-announced and bounce.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void Playlist_NewOnOneServer_ReachesAll_AndGoesQuiet(string topology, string order)
    {
        var net = Net(topology);
        net.EditPlaylist("B", List, "1,2,3");

        var delivered = net.DeliverAll(order);

        Assert.Equal(net.Servers.Count - 1, delivered);
        Assert.Equal(new[] { "1,2,3" }, PlaylistStates(net));
        Assert.Equal(0, net.DeliverAll());
    }

    /// <summary>
    /// Two servers edit the same playlist close together. Every order ends on the newer edit.
    /// True: one order of stamps everywhere.
    /// False: the servers keep each other's edits, or trade them back and forth.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void Playlist_ConcurrentEdits_ConvergeOnTheNewer(string topology, string order)
    {
        var net = Net(topology);
        net.EditPlaylist("A", List, "base");
        net.DeliverAll();
        net.EditPlaylist("C", List, "from-C");
        net.EditPlaylist("A", List, "from-A");

        net.DeliverAll(order);

        Assert.Equal(new[] { "from-A" }, PlaylistStates(net));
        Assert.Empty(net.Waiting());
    }

    /// <summary>
    /// A deletion reaches every server, and an older edit's hint arriving after it does not bring the playlist back.
    /// True: the playlist stays gone everywhere.
    /// False: a late update resurrects a deleted playlist.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void Playlist_DeletionThenLateOlderEdit_DoesNotResurrect(string topology, string order)
    {
        var net = Net(topology);
        net.EditPlaylist("A", List, "base");
        net.DeliverAll();
        net.EditPlaylist("C", List, "older-edit");
        foreach (var w in net.Waiting().Where(w => w.From.Name == "C").ToList())
        {
            net.Lose("C", w.Row.PeerKey);
        }

        net.DeletePlaylist("A", List);
        net.DeliverAll(order);
        Assert.Equal(new[] { "<none>" }, PlaylistStates(net));

        // C's older edit is announced again late, as a retry would.
        var c = net["C"];
        c.Playlists[List] = "older-edit";
        c.Publisher.PublishPlaylist(c.PlaylistIds[List], List, "List", RingNetwork.User, c.Versions.Get(HintKind.Playlists, List)!, null, true, false, false);
        net.DeliverAll(order);

        Assert.DoesNotContain(net.Servers.Where(s => s.Name != "C"), s => s.Playlists.ContainsKey(List));
        Assert.Empty(net.Waiting());
    }

    /// <summary>
    /// A deletion and a newer edit race. The newer edit wins everywhere, whatever the order.
    /// True: the playlist exists with the edit on every server.
    /// False: some servers keep it deleted and others not.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void Playlist_DeletionRacingANewerEdit_NewerEditWins(string topology, string order)
    {
        var net = Net(topology);
        net.EditPlaylist("A", List, "base");
        net.DeliverAll();
        net.DeletePlaylist("A", List);
        net.EditPlaylist("B", List, "edited-after");

        net.DeliverAll(order);

        Assert.Equal(new[] { "edited-after" }, PlaylistStates(net));
    }

    /// <summary>
    /// A deletion newer than an edit wins everywhere, whatever the order.
    /// True: gone on every server.
    /// False: the older edit brings it back somewhere.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void Playlist_NewerDeletionRacingAnEdit_DeletionWins(string topology, string order)
    {
        var net = Net(topology);
        net.EditPlaylist("A", List, "base");
        net.DeliverAll();
        net.EditPlaylist("B", List, "edited-before");
        net.DeletePlaylist("A", List);

        net.DeliverAll(order);

        Assert.Equal(new[] { "<none>" }, PlaylistStates(net));
        Assert.Empty(net.Waiting());
    }

    // ---------------------------------------------------------------- history

    /// <summary>
    /// One play reaches every server of a mesh with no re-announcement.
    /// True: one hint per link, the favorite is untouched, then quiet.
    /// False: receivers announce the play again.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void History_OnePlay_ReachesAll_AndGoesQuiet(string topology, string order)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Play("C", path, HistoryParts.Played, "watched");

        var delivered = net.DeliverAll(order);

        Assert.Equal(net.Servers.Count - 1, delivered);
        Assert.All(net.Servers, s => Assert.Equal(new HistoryValue("watched", "no"), s.History[path]));
        Assert.Equal(0, net.DeliverAll());
    }

    /// <summary>
    /// Changes to different parts made one after another, each delivered before the next, both survive everywhere.
    /// True: the play and the favorite are on every server.
    /// False: the second change undoes the first.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void History_PartsChangedInTurn_BothSurvive(string topology, string order)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Play("A", path, HistoryParts.Played, "watched");
        net.DeliverAll(order);
        net.Play("B", path, HistoryParts.Favorite, "yes");
        net.DeliverAll(order);

        Assert.All(net.Servers, s => Assert.Equal(new HistoryValue("watched", "yes"), s.History[path]));
        Assert.Empty(net.Waiting());
    }

    /// <summary>
    /// Two servers change the same part close together. History hints always end, however the merge re-announces.
    /// True: a bounded number of hints and one value everywhere.
    /// False: merges keep re-announcing with fresh stamps forever.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void History_SamePartChangedConcurrently_Settles(string topology, string order)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Play("A", path, HistoryParts.Played, "pos-100");
        net.Play("B", path, HistoryParts.Played, "pos-200");

        var delivered = net.DeliverAll(order);

        Assert.True(delivered <= 3 * Links(net), $"{delivered} hints");
        Assert.Single(net.Servers.Select(s => s.History[path]).Distinct());
    }

    /// <summary>
    /// Every server changes the played part at once. The hints end on one value.
    /// True: a bounded number of hints.
    /// False: the merge re-announcements chase each other.
    /// Mixed parts are left out: the network models only the handler's version path, not the negotiated-base merge that
    /// takes over after first contact, and without it a favorite that differs keeps being re-announced under the played part.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void History_EveryServerChangesAtOnce_HintsEnd(string topology, string order)
    {
        var net = Net(topology);
        var path = net.AddItem();
        foreach (var s in net.Servers)
        {
            net.Play(s.Name, path, HistoryParts.Played, "v-" + s.Name);
        }

        var delivered = net.DeliverAll(order);

        Assert.True(delivered <= 3 * Links(net), $"{delivered} hints");
        Assert.Empty(net.Waiting());
        Assert.Single(net.Servers.Select(s => s.History[path]).Distinct());
    }

    /// <summary>
    /// A plays at t1 and B favorites at t2 before A's hint arrives. Triage weighs the whole stamp, so B turns A's play away
    /// (see the next test), but B's own hint then reaches A, A keeps its play, and the negotiation writes the merge back to B.
    /// True: the play and the favorite survive on every server.
    /// False: the servers end apart on the played part.
    /// </summary>
    [Theory]
    [InlineData("mesh3")]
    [InlineData("mesh4")]
    public void History_DifferentPartsChangedConcurrently_BothSurvive(string topology)
    {
        var net = Net(topology);
        var path = net.AddItem();
        net.Play("A", path, HistoryParts.Played, "watched");
        net.Play("B", path, HistoryParts.Favorite, "yes");

        net.DeliverAll();

        Assert.All(net.Servers, s => Assert.Equal(new HistoryValue("watched", "yes"), s.History[path]));
    }

    /// <summary>
    /// Against the real triage alone: a played hint older than a favorite made here is answered Superseded, though the
    /// played part here is older still. Only the negotiation of the reverse hint brings the play across.
    /// </summary>
    [Fact]
    public void History_TriageWeighsTheWholeStampToday()
    {
        var net = Net("mesh3");
        var path = net.AddItem();
        net.Play("A", path, HistoryParts.Played, "watched");
        net.Play("B", path, HistoryParts.Favorite, "yes");

        Assert.Equal(HintCode.Superseded, net.DeliverOne("A", "B"));
        Assert.Equal("unplayed", net["B"].History[path].Played);
    }

    /// <summary>
    /// A, B, and C change the played part at t5, t6, and t8. B keeps its t6 value against A's older hint and re-announces the merge
    /// under a fresh stamp of its own (HistoryHintHandler.cs:271-275), yet C's real t8 edit still wins everywhere.
    /// True: every server ends on C's value.
    /// False: the re-announced older value beats the newer edit.
    /// </summary>
    [Fact]
    public void History_ReannouncedMerge_DoesNotBeatANewerEdit()
    {
        var net = Net("mesh3");
        var path = net.AddItem();
        net.Play("A", path, HistoryParts.Played, "a5");
        net.Play("B", path, HistoryParts.Played, "b6");
        net.Play("C", path, HistoryParts.Played, "c8");

        // A's older hint reaches B first. B keeps b6, A takes it by the negotiation, and B announces b6 to C under a new stamp.
        net.DeliverOne("A", "B");
        net.DeliverOne("B", "C");
        net.DeliverAll();

        Assert.All(net.Servers, s => Assert.Equal("c8", s.History[path].Played));
    }

    // ---------------------------------------------------------------- content

    /// <summary>
    /// A new file reaches every server, and a copy is never announced, so only its author's hints go out.
    /// True: one hint per peer of the author.
    /// False: copies are re-announced and offered back to the author.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void Content_NewFile_ReachesAll_CopiesAreNotAnnounced(string topology, string order)
    {
        var net = Net(topology);
        var path = net.AddFile("A", "new.mkv");

        var delivered = net.DeliverAll(order);

        Assert.Equal(net.Servers.Count - 1, delivered);
        Assert.All(net.Servers, s => Assert.Contains(path, s.Files));
        Assert.Equal(0, net.DeliverAll());
    }

    /// <summary>
    /// The author removes a file. Its copies follow, each copy announces its removal once, and nothing brings the file back.
    /// True: no server holds the file, and the number of hints is bounded by one per link.
    /// False: removals echo forever, or a copy is offered back and the file returns.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void Content_AuthorRemovesTheFile_CopiesFollow_AndItDoesNotReturn(string topology, string order)
    {
        var net = Net(topology);
        var path = net.AddFile("A", "gone.mkv");
        net.DeliverAll(order);

        net.RemoveFile("A", path);
        var delivered = net.DeliverAll(order);

        Assert.All(net.Servers, s => Assert.DoesNotContain(path, s.Files));
        Assert.True(delivered <= Links(net), $"{delivered} hints for {Links(net)} links");
        Assert.Equal(0, net.DeliverAll());
    }

    /// <summary>
    /// A server removes its own copy. The removal is announced, but no other server holds a copy from it, so nothing else changes.
    /// True: the author and the other copies keep the file.
    /// False: removing one copy removes the file everywhere.
    /// </summary>
    [Theory]
    [MemberData(nameof(MeshOrders))]
    public void Content_ACopyIsRemoved_OthersKeepTheFile(string topology, string order)
    {
        var net = Net(topology);
        var path = net.AddFile("A", "kept.mkv");
        net.DeliverAll(order);

        net.RemoveFile("C", path);
        var delivered = net.DeliverAll(order);

        Assert.Equal(net.Servers.Count - 1, delivered);
        Assert.All(net.Servers.Where(s => s.Name != "C"), s => Assert.Contains(path, s.Files));
        Assert.Equal(0, net.DeliverAll());
    }
}

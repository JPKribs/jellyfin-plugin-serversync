using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The Change Queue routes show both queues as stored, act only on the rows named, and judge a server entry by how the other server lists this one.
/// </summary>
[Collection(PlaylistCopyStoreCollection.Name)]
public sealed class HintsControllerTests : IDisposable
{
    private const string Url = "http://127.0.0.1:8096";
    private static readonly Guid ItemId = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222222222222222222222222222");

    private readonly ControllerTestHarness _harness = new();
    private PeerCapabilities? _capabilities = new() { PluginVersion = "0.0.0-test", ProtocolVersion = HintProtocol.ProtocolVersion, MinProtocolVersion = HintProtocol.MinProtocolVersion, Accepts = Enum.GetNames<HintKind>().ToList() };
    private PeerLinkResponse? _link;

    /// <summary>
    /// Initializes a new instance of the <see cref="HintsControllerTests"/> class, with a peer that answers as a Jellyfin server whose Server Sync a test shapes.
    /// </summary>
    public HintsControllerTests()
    {
        _harness.Peer = request => request.RequestUri!.AbsolutePath switch
        {
            "/System/Info" => ControllerTestHarness.Json("{\"ServerName\":\"Peer\",\"Id\":\"" + ControllerTestHarness.PeerId + "\"}"),
            "/Users/Me" => new HttpResponseMessage(HttpStatusCode.BadRequest),
            "/Sync/Peer/Capabilities" => _capabilities is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : ControllerTestHarness.Json(JsonSerializer.Serialize(_capabilities)),
            "/Sync/Peer/Link" => _link is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : ControllerTestHarness.Json(JsonSerializer.Serialize(_link)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// A check with no Server URL or no API Key asks for them and calls nothing.
    /// True: the page hears what to fill in.
    /// False: a blank entry would be checked against an empty address.
    /// </summary>
    [Theory]
    [InlineData("", "key")]
    [InlineData("  ", "key")]
    [InlineData(Url, "")]
    [InlineData(Url, " ")]
    public async Task CheckPeer_BlankUrlOrKey_AsksForThemWithoutCalling(string url, string apiKey)
    {
        var result = await Check(new TestConnectionRequest { ServerUrl = url, ApiKey = apiKey });

        Assert.Equal("Enter the Server URL and API Key.", result.Message);
        Assert.False(result.Reachable);
        Assert.Empty(_harness.Clients.Created);
    }

    /// <summary>
    /// The client is built for the trimmed URL under the entry's key, and a refusal from the factory is the message.
    /// True: the check reaches the address the entry will use, and the page shows why an address is refused.
    /// False: stray spaces would break the check, or a refusal would surface as an unhandled error.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("that address is not allowed")]
    public async Task CheckPeer_Client_IsBuiltForTheEntryOrRefusedWithTheReason(string? refusal)
    {
        _harness.Clients.Refusal = refusal;

        var result = await Check(new TestConnectionRequest { ServerUrl = "  " + Url + "  ", ApiKey = "key", ServerKey = "entry-1", AllowPrivateNetwork = false });

        var built = Assert.Single(_harness.Clients.Created);
        Assert.Equal((Url, "key", "entry-1", false), (built.Url, built.ApiKey, built.Key, built.AllowPrivateNetwork));
        Assert.Equal(refusal is null, result.Reachable);
        Assert.Equal(refusal is null, result.Message != refusal);
    }

    /// <summary>
    /// A server that answers with an error is reported as not reachable, and its Server Sync is not asked anything.
    /// True: the page shows the connection failure.
    /// False: a broken server would be judged as if it had answered.
    /// </summary>
    [Fact]
    public async Task CheckPeer_ServerAnswersWithAnError_IsNotReachable()
    {
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await Check(Request("Sync"));

        Assert.False(result.Reachable);
        Assert.False(string.IsNullOrEmpty(result.Message));
        Assert.Equal("error", result.Severity);
        Assert.DoesNotContain("/Sync/Peer/Capabilities", _harness.PeerPaths);
    }

    /// <summary>
    /// A server without Server Sync is a warning for Pull and an error for Push and Sync, and any other Direction is read as Pull.
    /// True: Pull still works on a schedule against a plain Jellyfin server, and Push and Sync are refused at setup.
    /// False: a Push entry would be saved against a server that can never take its changes.
    /// </summary>
    [Theory]
    [InlineData("Pull", "warn")]
    [InlineData(null, "warn")]
    [InlineData("Whatever", "warn")]
    [InlineData("Push", "error")]
    [InlineData("push", "error")]
    [InlineData("SYNC", "error")]
    public async Task CheckPeer_NoServerSyncThere_WarnsForPullAndRefusesTheRest(string? mode, string severity)
    {
        _capabilities = null;

        var result = await Check(Request(mode));

        Assert.Equal((true, false, severity), (result.Reachable, result.HasPlugin, result.Severity));
        Assert.Equal(("Peer", ControllerTestHarness.PeerId), (result.ServerName, result.ServerId));
        Assert.Contains("not installed", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Server Sync that refuses this key is reported with its reason and a pointer to its Whitelist.
    /// True: the operator learns the other server must allow this one.
    /// False: a refused key would look like a missing plugin.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task CheckPeer_ServerSyncRefusesTheKey_PointsToTheWhitelist(HttpStatusCode status)
    {
        var answer = _harness.Peer;
        _harness.Peer = request => request.RequestUri!.AbsolutePath == "/Sync/Peer/Capabilities"
            ? ControllerTestHarness.Json("{\"Code\":\"IpNotWhitelisted\",\"Message\":\"that address is not listed\"}", status)
            : answer(request);

        var result = await Check(Request("Pull"));

        Assert.True(result.Reachable);
        Assert.False(result.HasPlugin);
        Assert.Contains("refused this call", result.Message, StringComparison.Ordinal);
        Assert.Contains("Whitelist", result.Message, StringComparison.Ordinal);
        Assert.Equal(status == HttpStatusCode.Forbidden, result.Message.Contains("that address is not listed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A Server Sync whose protocol does not overlap this one's is an error marked incompatible, and its listing is not asked for.
    /// True: the page tells the operator to update before the entry is saved.
    /// False: an entry would be saved against a server whose hints are all refused.
    /// </summary>
    [Fact]
    public async Task CheckPeer_ProtocolDoesNotOverlap_IsIncompatible()
    {
        _capabilities!.ProtocolVersion = HintProtocol.ProtocolVersion + 5;
        _capabilities.MinProtocolVersion = HintProtocol.ProtocolVersion + 5;

        var result = await Check(Request("Sync"));

        Assert.Equal((true, true, "error"), (result.HasPlugin, result.Incompatible, result.Severity));
        Assert.StartsWith("Connected, but ", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/Sync/Peer/Link", _harness.PeerPaths);
    }

    /// <summary>
    /// The verdict follows the Direction and how the other server lists this one.
    /// True: Pull is ready on a schedule and live when the peer sends, Push needs the peer to pull, and Sync needs the peer to be Sync.
    /// False: a pairing that can only fail in the queue would be shown as ready.
    /// </summary>
    [Theory]
    [InlineData("Pull", false, null, false, false, false, "warn", false, false)]
    [InlineData("Pull", true, "Push", true, false, true, "ok", false, true)]
    [InlineData("Pull", true, "Pull", true, true, false, "warn", true, false)]
    [InlineData("Push", true, "Pull", true, true, false, "ok", true, false)]
    [InlineData("Push", false, null, false, false, false, "error", false, false)]
    [InlineData("Push", true, "Pull", false, true, false, "error", false, false)]
    [InlineData("Push", true, "Push", true, false, true, "error", false, true)]
    [InlineData("Sync", true, "Sync", true, true, true, "ok", true, true)]
    [InlineData("Sync", true, "sync", true, true, true, "ok", true, true)]
    [InlineData("Sync", true, "Pull", true, true, false, "error", true, false)]
    [InlineData("Sync", true, "Sync", false, true, true, "error", false, false)]
    [InlineData("Sync", false, null, false, false, false, "error", false, false)]
    public async Task CheckPeer_DirectionAndListing_DecideTheVerdict(string mode, bool listed, string? peerMode, bool enabled, bool pulls, bool sends, string severity, bool listsThisServer, bool sendsToThisServer)
    {
        _link = new PeerLinkResponse { Listed = listed, Mode = peerMode, Enabled = enabled, PullsFromYou = pulls, SendsToYou = sends, Paired = true };

        var result = await Check(Request(mode));

        Assert.Equal(severity, result.Severity);
        Assert.Equal((listsThisServer, sendsToThisServer, peerMode), (result.ListsThisServer, result.SendsToThisServer, result.PeerMode));
        Assert.Equal(severity == "ok", result.Headline.StartsWith("Ready.", StringComparison.Ordinal));
        Assert.Equal(severity == "error", result.Headline.StartsWith("Not ready.", StringComparison.Ordinal));
        Assert.Equal(!listed ? "Not listed" : enabled ? peerMode : "Disabled", result.Details[0].Badge);
    }

    /// <summary>
    /// A server that lists this one but could not pair turns a ready verdict into a warning that names why.
    /// True: the operator sees that hints will be refused until the two pair.
    /// False: a link that cannot prove itself would be shown as ready.
    /// </summary>
    [Fact]
    public async Task CheckPeer_ListedButNotPaired_WarnsWithTheReason()
    {
        _link = new PeerLinkResponse { Listed = true, Mode = "Sync", Enabled = true, PullsFromYou = true, SendsToYou = true, Paired = false, PairingError = "it refused the key" };

        var result = await Check(Request("Sync"));

        var pairing = Assert.Single(result.Details, d => d.Title == "Pairing");
        Assert.Equal(("warn", false, "it refused the key"), (result.Severity, result.Paired, result.PairingError));
        Assert.Contains("It could not pair with this server: it refused the key", result.Message, StringComparison.Ordinal);
        Assert.Equal(("Not paired", "no"), (pairing.Badge, pairing.State));
    }

    /// <summary>
    /// A clock more than thirty seconds off turns a ready verdict into a warning, and one within it does not.
    /// True: the operator is told to fix the clocks before edits are settled the wrong way round.
    /// False: two servers minutes apart would be shown as ready with no caveat.
    /// </summary>
    [Theory]
    [InlineData(0, "ok", false)]
    [InlineData(-600, "warn", true)]
    [InlineData(600, "warn", true)]
    public async Task CheckPeer_ClockSkew_WarnsOnlyPastTheLimit(int peerClockOffsetSeconds, string severity, bool clockRow)
    {
        _capabilities!.ServerTime = DateTime.UtcNow.AddSeconds(peerClockOffsetSeconds);
        _link = new PeerLinkResponse { Listed = true, Mode = "Sync", Enabled = true, PullsFromYou = true, SendsToYou = true, Paired = true };

        var result = await Check(Request("Sync"));

        Assert.Equal(severity, result.Severity);
        Assert.InRange(result.ClockSkewSeconds, Math.Abs(peerClockOffsetSeconds), Math.Abs(peerClockOffsetSeconds) + 30);
        Assert.Equal(clockRow, result.Details.Any(d => d.Title == "Clocks"));
        Assert.Equal(clockRow, result.Message.Contains("NTP", StringComparison.Ordinal));
    }

    /// <summary>
    /// What the other server applies is listed in words, with a badge for all, some, or none.
    /// True: the operator sees which kinds of change the other server takes from this one.
    /// False: a server with every module off would look like it applies everything.
    /// </summary>
    [Theory]
    [InlineData("History,Users", "It applies watch history and user settings from this server.", "limited")]
    [InlineData("Content", "It applies files from this server.", "limited")]
    [InlineData("", "Every module is off there, so it applies no changes from this server.", "no")]
    public async Task CheckPeer_Accepts_AreListedInWords(string accepts, string expected, string state)
    {
        _capabilities!.Accepts = accepts.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        _link = new PeerLinkResponse { Listed = true, Mode = "Sync", Enabled = true, PullsFromYou = true, SendsToYou = true, Paired = true };

        var result = await Check(Request("Sync"));

        var applies = Assert.Single(result.Details, d => d.Title == "What it applies from this server");
        Assert.Contains(expected, result.Message, StringComparison.Ordinal);
        Assert.Equal(state, applies.State);
        Assert.Equal(_capabilities.Accepts, result.Accepts);
    }

    /// <summary>
    /// A Pull entry the other server does not send to says nothing of what that server applies.
    /// True: the verdict stays about what this server pulls.
    /// False: a Pull entry would carry a line about changes it never sends.
    /// </summary>
    [Fact]
    public async Task CheckPeer_PullWithoutLiveChanges_LeavesOutWhatThePeerApplies()
    {
        _link = new PeerLinkResponse { Listed = false };

        var result = await Check(Request("Pull"));

        Assert.DoesNotContain("It applies", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Details, d => d.Title == "What it applies from this server");
        Assert.Equal(new[] { "Its entry for this server", "Changes made there" }, result.Details.Select(d => d.Title).ToArray());
    }

    /// <summary>
    /// The other server's access here is reported, with a blocked line when its account may not download.
    /// True: the operator sees that a standard user's key reads only that user's libraries and copies no files.
    /// False: a limited key would look like full access.
    /// </summary>
    [Theory]
    [InlineData("User", "bob", false, "Standard user", "limited", true)]
    [InlineData("Administrator", null, true, "Administrator", "ok", false)]
    [InlineData(null, null, null, "Unknown", "none", false)]
    public async Task CheckPeer_PeerAccess_IsReported(string? level, string? user, bool? canDownload, string badge, string state, bool blocked)
    {
        _link = new PeerLinkResponse { Listed = true, Mode = "Sync", Enabled = true, PullsFromYou = true, SendsToYou = true, Paired = true, AccessLevel = level, AccessUser = user, CanDownload = canDownload };

        var result = await Check(Request("Sync"));

        var reads = Assert.Single(result.Details, d => d.Title == "What it reads here");
        Assert.Equal((badge, state), (reads.Badge, reads.State));
        Assert.Equal(blocked, result.Details.Any(d => d.Title == "File downloads from here"));
        Assert.Equal((level, user, canDownload), (result.PeerAccessLevel, result.PeerAccessUser, result.PeerCanDownload));
        Assert.Equal(user is not null, result.Message.Contains("the standard user bob", StringComparison.Ordinal));
    }

    /// <summary>
    /// A Server Sync that cannot be asked how it lists this server is reported with the failure.
    /// True: the page says the listing is unknown.
    /// False: an unreachable listing would be read as not listed, and a working link shown as broken.
    /// </summary>
    [Fact]
    public async Task CheckPeer_ListingCannotBeRead_SaysSo()
    {
        var answer = _harness.Peer;
        _harness.Peer = request => request.RequestUri!.AbsolutePath == "/Sync/Peer/Link" ? throw new HttpRequestException("connection reset") : answer(request);

        var result = await Check(Request("Sync"));

        Assert.True(result.HasPlugin);
        Assert.Contains("did not say whether it lists this server", result.Message, StringComparison.Ordinal);
        Assert.Contains("connection reset", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The check asks the other server about this server by this server's own ID.
    /// True: the other server looks up the right entry.
    /// False: the other server would answer for some other server, or for none.
    /// </summary>
    [Fact]
    public async Task CheckPeer_Listing_IsAskedForThisServersId()
    {
        string? query = null;
        var answer = _harness.Peer;
        _harness.Peer = request =>
        {
            query = request.RequestUri!.AbsolutePath == "/Sync/Peer/Link" ? request.RequestUri.Query : query;
            return answer(request);
        };

        await Check(Request("Sync"));

        Assert.Equal("?serverId=" + ControllerTestHarness.ThisServerId, query);
    }

    /// <summary>
    /// A check without a request body is refused.
    /// True: the mistake is named at once.
    /// False: a missing body would surface as an unrelated null reference.
    /// </summary>
    [Fact]
    public async Task CheckPeer_NullRequest_Throws()
    {
        var (controller, _) = _harness.Hints();

        await Assert.ThrowsAsync<ArgumentNullException>(() => controller.CheckPeer(null!, CancellationToken.None));
    }

    /// <summary>
    /// An empty queue reads as zeroes with a line for each server hints go to.
    /// True: the Change Queue tab shows each Push or Sync server, idle.
    /// False: the tab would fail on an empty queue, or list servers that are only pulled from.
    /// </summary>
    [Fact]
    public void Get_EmptyQueues_AnswersZeroesAndThePushServers()
    {
        _harness.AddPeer(ServerMode.Push, "push");
        _harness.AddPeer(ServerMode.Pull, "pull");
        _harness.AddPeer(ServerMode.Sync, "sync");

        var overview = Overview();

        Assert.Equal((0, 0, null, 0, 0), (overview.Pending, overview.Unmatched, overview.LastUnmatched, overview.InboundCount, overview.InboundFailingCount));
        Assert.Empty(overview.Gathering);
        Assert.Empty(overview.Outbound);
        Assert.Empty(overview.OutboundCounts);
        Assert.Equal(new[] { "push", "sync" }, overview.Peers.Select(p => p.Key).ToArray());
        Assert.All(overview.Peers, p => Assert.Equal(("Peer", null, null, null), (p.Name, p.LastAttempt, p.PausedUntil, p.Reason)));
    }

    /// <summary>
    /// An outbound row is shown with its peer's name, its state counted, and the title of the item it is about.
    /// True: the tab names the film and the server each row is owed to.
    /// False: rows would show bare IDs and keys.
    /// </summary>
    [Fact]
    public void Get_OutboundRows_CarryThePeerNameAndTheItemsTitle()
    {
        _harness.AddPeer(ServerMode.Push);
        _harness.LibraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => new Movie { Id = ItemId, Name = "Film", ProductionYear = 2020 });
        Enqueue("peer", "a", ItemId.ToString("N"));
        Enqueue("removed-entry", "b", itemId: null, userName: "alice");
        Fail(_harness.Outbound.GetAll().Single(r => r.Key == "b"));

        var overview = Overview();

        var known = Assert.Single(overview.Outbound, r => r.Key == "a");
        var orphan = Assert.Single(overview.Outbound, r => r.Key == "b");
        Assert.Equal(("Film", "2020", "Movie", "Peer", OutboundState.Pending), (known.Title, known.Subtitle, known.ItemType, known.PeerName, known.State));
        Assert.Equal(("alice", null, "removed-entry", OutboundState.Failed, "refused"), (orphan.Title, orphan.Subtitle, orphan.PeerName, orphan.State, orphan.LastError));
        Assert.Equal((1, 1), (overview.OutboundCounts["Pending"], overview.OutboundCounts["Failed"]));
    }

    /// <summary>
    /// A row's title follows the kind of item, with an episode under its series and no year repeated.
    /// True: an episode reads as its series with the episode below, and a film titled with its year shows the year once.
    /// False: every episode of a series would look the same, or a year would be shown twice.
    /// </summary>
    [Theory]
    [InlineData("episode", "Show", "Pilot")]
    [InlineData("season", "Show", "Season 1")]
    [InlineData("titled-with-year", "Film (2020)", null)]
    [InlineData("no-year", "Film", null)]
    public void Get_OutboundRow_TitleFollowsTheItem(string kind, string title, string? subtitle)
    {
        BaseItem item = kind switch
        {
            "episode" => new Episode { Id = ItemId, Name = "Pilot", SeriesName = "Show" },
            "season" => new Season { Id = ItemId, Name = "Season 1", SeriesName = "Show" },
            "titled-with-year" => new Movie { Id = ItemId, Name = "Film (2020)", ProductionYear = 2020 },
            _ => new Movie { Id = ItemId, Name = "Film" }
        };
        _harness.LibraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => item);
        Enqueue("peer", "a", ItemId.ToString("N"));

        var row = Assert.Single(Overview().Outbound);

        Assert.Equal((title, subtitle), (row.Title, row.Subtitle));
    }

    /// <summary>
    /// Inbound rows are listed and counted, with the failing ones counted apart.
    /// True: the tab shows what peers sent and how much of it is failing.
    /// False: a failing apply would be hidden among waiting rows.
    /// </summary>
    [Fact]
    public void Get_InboundRows_AreListedAndTheFailingCounted()
    {
        Receive("1");
        Receive("2");
        _harness.Inbound.Defer(_harness.Inbound.GetAll().First(), DateTime.UtcNow.AddHours(1), "the source refused");

        var overview = Overview();

        Assert.Equal((2, 2, 1), (overview.Inbound.Count, overview.InboundCount, overview.InboundFailingCount));
    }

    /// <summary>
    /// A Stamp lookup without the IDs its kind needs answers 400.
    /// True: the page hears that the lookup was malformed.
    /// False: a lookup with a missing or malformed ID would read some other object's Stamp.
    /// </summary>
    [Theory]
    [InlineData(HintKind.History, "11111111111111111111111111111111", null, null)]
    [InlineData(HintKind.History, null, "22222222222222222222222222222222", null)]
    [InlineData(HintKind.Metadata, "not-an-id", null, null)]
    [InlineData(HintKind.Content, null, "22222222222222222222222222222222", null)]
    [InlineData(HintKind.Users, "11111111111111111111111111111111", null, null)]
    [InlineData(HintKind.People, "11111111111111111111111111111111", null, " ")]
    [InlineData(HintKind.Playlists, "11111111111111111111111111111111", null, null)]
    [InlineData((HintKind)999, "11111111111111111111111111111111", "22222222222222222222222222222222", "name")]
    public void GetVersion_IdsForTheKindMissing_Answers400(HintKind kind, string? itemId, string? userId, string? name)
    {
        var (controller, _) = _harness.Hints();

        var result = controller.GetVersion(kind, itemId, userId, name);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// An object nobody has edited answers 204.
    /// True: the page shows no Stamp.
    /// False: an unedited object would be shown as edited by an unnamed server.
    /// </summary>
    [Fact]
    public void GetVersion_NoStampRecorded_Answers204()
    {
        var (controller, _) = _harness.Hints();

        var result = controller.GetVersion(HintKind.Metadata, ItemId.ToString("N"));

        Assert.IsType<NoContentResult>(result.Result);
    }

    /// <summary>
    /// A Stamp names the server that made the edit, as this server, a listed server, or a bare ID.
    /// True: the page shows who last edited the object, by name where it knows one.
    /// False: an edit made here would be credited to another server, or a known server shown as an ID.
    /// </summary>
    [Theory]
    [InlineData(ControllerTestHarness.ThisServerId, "This Server", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "This Server", true)]
    [InlineData(ControllerTestHarness.PeerId, "Peer", false)]
    [InlineData("ffffffffffffffffffffffffffffffff", "ffffffffffffffffffffffffffffffff", false)]
    public void GetVersion_Stamp_NamesTheServerThatEdited(string serverId, string serverName, bool isThisServer)
    {
        _harness.AddPeer();
        var at = new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        _harness.Versions.Set(new ObjectVersion { Kind = HintKind.Metadata, Key = HintProtocol.MetadataKey(ItemId), ServerId = serverId, Timestamp = at });
        var (controller, _) = _harness.Hints();

        var result = controller.GetVersion(HintKind.Metadata, ItemId.ToString("D"));

        var stamp = Assert.IsType<ObjectVersionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((serverId, serverName, isThisServer, at), (stamp.ServerId, stamp.ServerName, stamp.IsThisServer, stamp.Timestamp.ToUniversalTime()));
    }

    /// <summary>
    /// Each kind reads the Stamp stored under its own key.
    /// True: a History lookup needs the user and the item, a Users lookup the user, and a People lookup the name in any case.
    /// False: a lookup would read another kind's Stamp, or miss one over letter case.
    /// </summary>
    [Theory]
    [InlineData(HintKind.History)]
    [InlineData(HintKind.Users)]
    [InlineData(HintKind.People)]
    [InlineData(HintKind.Content)]
    public void GetVersion_EachKind_ReadsItsOwnKey(HintKind kind)
    {
        var key = kind switch
        {
            HintKind.History => HintProtocol.HistoryKey(UserId, ItemId),
            HintKind.Users => HintProtocol.UsersKey(UserId),
            HintKind.People => HintProtocol.PeopleKey("Some Actor"),
            _ => HintProtocol.MetadataKey(ItemId)
        };
        _harness.Versions.Set(new ObjectVersion { Kind = kind, Key = key, ServerId = "someone", Timestamp = DateTime.UtcNow });
        var (controller, _) = _harness.Hints();

        var result = controller.GetVersion(kind, ItemId.ToString("N"), UserId.ToString("N"), " some actor ");

        Assert.Equal("someone", Assert.IsType<ObjectVersionDto>(Assert.IsType<OkObjectResult>(result.Result).Value).ServerId);
    }

    /// <summary>
    /// Run and the unmatched dismissal answer 204 on an idle queue.
    /// True: the buttons work when there is nothing to do.
    /// False: pressing Run on an empty queue would show an error.
    /// </summary>
    [Fact]
    public async Task RunAndDismissUnmatched_IdleQueue_Answer204()
    {
        var (controller, observer) = _harness.Hints();

        var ran = await controller.Run(CancellationToken.None);
        var dismissed = controller.DismissUnmatched();

        Assert.IsType<NoContentResult>(ran);
        Assert.IsType<NoContentResult>(dismissed);
        Assert.Equal(0, observer.UnmatchedCount);
    }

    /// <summary>
    /// Run on a cancelled request stops.
    /// True: the cancellation reaches the caller.
    /// False: a pass would run on after the operator left the page.
    /// </summary>
    [Fact]
    public async Task Run_Cancelled_Throws()
    {
        var (controller, _) = _harness.Hints();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Run(new CancellationToken(canceled: true)));
    }

    /// <summary>
    /// Retry puts every failed row back and runs a pass each way, and reports how many went back.
    /// True: a failed outbound row is delivered again and a failing inbound row is applied again, at once.
    /// False: Retry would only reset counters and leave the rows waiting for the schedule.
    /// </summary>
    [Fact]
    public async Task RetryFailed_FailedRowsBothWays_PutsThemBackAndRunsBothPasses()
    {
        Enqueue("removed-entry", "a", null);
        Fail(_harness.Outbound.GetAll().Single());
        Receive("1");
        _harness.Inbound.Defer(_harness.Inbound.GetAll().Single(), DateTime.UtcNow.AddHours(1), "the source refused");
        var (controller, _) = _harness.Hints();

        var result = await controller.RetryFailed(CancellationToken.None);

        Assert.Equal((2, 1, 1), (Read(result, "Retried"), Read(result, "Outbound"), Read(result, "Inbound")));
        Assert.Empty(_harness.Outbound.GetAll());
        Assert.Equal(1, Assert.Single(_harness.Inbound.GetAll()).Attempts);
    }

    /// <summary>
    /// Retry with nothing failed reports zero and runs no pass.
    /// True: rows that are only waiting are left to their schedule.
    /// False: Retry would force a delivery and drop or resend rows that had not failed.
    /// </summary>
    [Fact]
    public async Task RetryFailed_NothingFailed_ReportsZeroAndLeavesWaitingRows()
    {
        Enqueue("removed-entry", "a", null);
        Receive("1");
        var (controller, _) = _harness.Hints();

        var result = await controller.RetryFailed(CancellationToken.None);

        Assert.Equal((0, 0, 0), (Read(result, "Retried"), Read(result, "Outbound"), Read(result, "Inbound")));
        Assert.Single(_harness.Outbound.GetAll());
        Assert.Equal(0, Assert.Single(_harness.Inbound.GetAll()).Attempts);
    }

    /// <summary>
    /// Dismissing the failed rows removes them both ways and leaves the rows that have not failed.
    /// True: the failed list is cleared, and waiting rows still go.
    /// False: clearing failures would empty the whole queue.
    /// </summary>
    [Fact]
    public void DismissFailed_MixedRows_RemovesOnlyTheFailed()
    {
        Enqueue("peer", "failed", null);
        Enqueue("peer", "waiting", null);
        Fail(_harness.Outbound.GetAll().Single(r => r.Key == "failed"));
        Receive("1");
        Receive("2");
        _harness.Inbound.Defer(_harness.Inbound.GetAll().First(r => r.HintId.EndsWith(":1", StringComparison.Ordinal)), DateTime.UtcNow.AddHours(1), "the source refused");
        var (controller, _) = _harness.Hints();

        var result = controller.DismissFailed();

        Assert.Equal((2, 1, 1), (Read(result, "Removed"), Read(result, "Outbound"), Read(result, "Inbound")));
        Assert.Equal("waiting", Assert.Single(_harness.Outbound.GetAll()).Key);
        Assert.EndsWith(":2", Assert.Single(_harness.Inbound.GetAll()).HintId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing one row by ID removes that row and answers 204, and an ID that names no row answers 404.
    /// True: the operator discards exactly the row they picked.
    /// False: a stale ID would report success, or another row would go.
    /// </summary>
    [Fact]
    public void RemoveOutboundAndInbound_ByRowId_RemoveThatRowOr404()
    {
        Enqueue("peer", "a", null);
        Enqueue("peer", "b", null);
        Receive("1");
        Receive("2");
        var outbound = _harness.Outbound.GetAll().Single(r => r.Key == "a");
        var inbound = _harness.Inbound.GetAll().First();
        var (controller, _) = _harness.Hints();

        Assert.IsType<NoContentResult>(controller.RemoveOutbound(outbound.Id));
        Assert.IsType<NotFoundResult>(controller.RemoveOutbound(outbound.Id));
        Assert.IsType<NotFoundResult>(controller.RemoveOutbound(long.MaxValue));
        Assert.IsType<NoContentResult>(controller.RemoveInbound(inbound.Id));
        Assert.IsType<NotFoundResult>(controller.RemoveInbound(inbound.Id));
        Assert.IsType<NotFoundResult>(controller.RemoveInbound(-1));
        Assert.Equal("b", Assert.Single(_harness.Outbound.GetAll()).Key);
        Assert.NotEqual(inbound.Id, Assert.Single(_harness.Inbound.GetAll()).Id);
    }

    /// <summary>
    /// A dismissed inbound row is remembered as given up, so its sender stops waiting for it.
    /// True: the sender's status check hears the hint is finished.
    /// False: the sender would resend a hint the operator discarded.
    /// </summary>
    [Fact]
    public void RemoveInbound_DismissedRow_IsRememberedAsGivenUp()
    {
        Receive("1");
        var row = _harness.Inbound.GetAll().Single();
        var (controller, _) = _harness.Hints();

        controller.RemoveInbound(row.Id);

        var done = Assert.Single(_harness.InboundWorker.RecentlyCompleted);
        Assert.Equal((row.HintId, HintCode.GaveUp), (done.HintId, done.Code));
    }

    /// <summary>
    /// The controller refuses to be built without its parts.
    /// True: a missing service is named when the controller is built.
    /// False: the first route to use it would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingArgument_Throws()
    {
        var h = _harness;
        var (_, observer) = h.Hints();

        Assert.Throws<ArgumentNullException>(() => new HintsController(null!, h.Inbound, h.OutboundWorker, h.InboundWorker, observer, h.Config, h.Clients, h.Host, h.Versions, h.Library, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, null!, h.OutboundWorker, h.InboundWorker, observer, h.Config, h.Clients, h.Host, h.Versions, h.Library, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, h.Inbound, null!, h.InboundWorker, observer, h.Config, h.Clients, h.Host, h.Versions, h.Library, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, h.Inbound, h.OutboundWorker, null!, observer, h.Config, h.Clients, h.Host, h.Versions, h.Library, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, h.Inbound, h.OutboundWorker, h.InboundWorker, null!, h.Config, h.Clients, h.Host, h.Versions, h.Library, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, h.Inbound, h.OutboundWorker, h.InboundWorker, observer, null!, h.Clients, h.Host, h.Versions, h.Library, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, h.Inbound, h.OutboundWorker, h.InboundWorker, observer, h.Config, null!, h.Host, h.Versions, h.Library, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, h.Inbound, h.OutboundWorker, h.InboundWorker, observer, h.Config, h.Clients, null!, h.Versions, h.Library, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, h.Inbound, h.OutboundWorker, h.InboundWorker, observer, h.Config, h.Clients, h.Host, null!, h.Library, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, h.Inbound, h.OutboundWorker, h.InboundWorker, observer, h.Config, h.Clients, h.Host, h.Versions, null!, h.UserManager));
        Assert.Throws<ArgumentNullException>(() => new HintsController(h.Outbound, h.Inbound, h.OutboundWorker, h.InboundWorker, observer, h.Config, h.Clients, h.Host, h.Versions, h.Library, null!));
    }

    /// <summary>
    /// Each Change Queue route requires elevation and answers one verb at its own path under Sync/Hints.
    /// True: only an administrator can read the queues, discard a row, or check a server.
    /// False: any signed in user could empty the queue or probe another server through this one.
    /// </summary>
    [Theory]
    [InlineData(nameof(HintsController.CheckPeer), "POST", "CheckPeer")]
    [InlineData(nameof(HintsController.Get), "GET", null)]
    [InlineData(nameof(HintsController.GetVersion), "GET", "Version")]
    [InlineData(nameof(HintsController.Run), "POST", "Run")]
    [InlineData(nameof(HintsController.DismissUnmatched), "DELETE", "Unmatched")]
    [InlineData(nameof(HintsController.RetryFailed), "POST", "Failed/Retry")]
    [InlineData(nameof(HintsController.DismissFailed), "DELETE", "Failed")]
    [InlineData(nameof(HintsController.RemoveOutbound), "DELETE", "Outbound/{id}")]
    [InlineData(nameof(HintsController.RemoveInbound), "DELETE", "Inbound/{id}")]
    public void Route_RequiresElevation(string action, string verb, string? template)
    {
        ControllerRouteAssert.Elevated(typeof(HintsController), action, verb, template);
        Assert.Equal("Sync/Hints", Assert.Single(typeof(HintsController).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>()).Template);
    }

    private static TestConnectionRequest Request(string? mode) => new() { ServerUrl = Url, ApiKey = "key", Mode = mode };

    private static int Read(ActionResult result, string property)
    {
        var body = Assert.IsType<OkObjectResult>(result).Value!;
        return (int)body.GetType().GetProperty(property)!.GetValue(body)!;
    }

    private async Task<PeerCheckResult> Check(TestConnectionRequest request)
    {
        var (controller, _) = _harness.Hints();
        var result = await controller.CheckPeer(request, CancellationToken.None);
        return result.Value ?? Assert.IsType<PeerCheckResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private HintsOverview Overview()
    {
        var (controller, _) = _harness.Hints();
        return Assert.IsType<HintsOverview>(Assert.IsType<OkObjectResult>(controller.Get().Result).Value);
    }

    private void Enqueue(string peerKey, string key, string? itemId, string? userName = null)
        => _harness.Outbound.Enqueue(new OutboundHint { PeerKey = peerKey, Kind = HintKind.Metadata, Key = key, ItemId = itemId, UserName = userName, VersionServerId = "me", VersionTimestamp = DateTime.UtcNow.AddMinutes(-1) }, "me");

    private void Fail(OutboundHint row) => _harness.Outbound.MarkFailed(row, "refused");

    private void Receive(string id)
        => _harness.Inbound.Enqueue(new InboundHint { HintId = ControllerTestHarness.PeerId + ":" + id, OriginServerId = ControllerTestHarness.PeerId, Kind = HintKind.Metadata, Key = "key-" + id, VersionServerId = ControllerTestHarness.PeerId, VersionTimestamp = DateTime.UtcNow.AddMinutes(-1), ReceivedAt = DateTime.UtcNow, NextAttempt = DateTime.UtcNow.AddMinutes(-1) });
}

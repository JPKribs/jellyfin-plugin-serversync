using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// Calls to a peer's Server Sync must tell a peer without the plugin, a refusal, and a peer that is down apart, and must send the pairing secret only as the protocol allows.
/// </summary>
public sealed class SourceServerClientPeerTests : IDisposable
{
    private const string ThisServer = "this-server-id";
    private const string PeerKey = "peer-key";
    private readonly List<string> _tempDirs = new();
    private readonly List<SyncDatabase> _databases = new();

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }

        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A 404 means the source has no Server Sync, a success is parsed in either casing, and any other answer throws with its status.
    /// True: a caller can tell "not installed" from a refused key and from a peer that is down.
    /// False: a peer that is down would read as one without the plugin, and live changes would be turned off for it.
    /// </summary>
    [Fact]
    public async Task GetPeerCapabilitiesAsync_Answers_AreToldApart()
    {
        using var missing = new ClientStubServer().Client();
        using var running = new ClientStubServer().OnJson("/Sync/Peer/Capabilities", "{\"serverId\":\"abc\",\"PluginVersion\":\"9.9\",\"protocolVersion\":3,\"Accepts\":[\"History\"]}").Client();
        using var refusing = new ClientStubServer().OnStatus("/Sync/Peer/Capabilities", HttpStatusCode.Unauthorized, "bad key").Client();

        var none = await missing.GetPeerCapabilitiesAsync();
        var capabilities = await running.GetPeerCapabilitiesAsync();
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => refusing.GetPeerCapabilitiesAsync());

        Assert.Null(none);
        Assert.Equal("abc", capabilities!.ServerId);
        Assert.Equal("9.9", capabilities.PluginVersion);
        Assert.Equal(3, capabilities.ProtocolVersion);
        Assert.Equal(new[] { "History" }, capabilities.Accepts);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("bad key", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each answer to the access check maps to one result code.
    /// True: the Servers tab shows whether this key may sync there, and why not.
    /// False: a refusal would be shown as allowed, or a missing plugin as a refusal.
    /// </summary>
    [Theory]
    [InlineData(200, "{}", "Allowed", "This key may use the sync endpoints there")]
    [InlineData(204, null, "Allowed", "This key may use the sync endpoints there")]
    [InlineData(404, null, "NotInstalled", "Server Sync is not installed there")]
    [InlineData(426, "upgrade first", "VersionMismatch", "upgrade first")]
    [InlineData(426, null, "VersionMismatch", "")]
    [InlineData(403, "{\"Code\":\"IpNotWhitelisted\",\"Message\":\"Your address is not on the whitelist\"}", "IpNotWhitelisted", "Your address is not on the whitelist")]
    [InlineData(403, "{\"Code\":null,\"Message\":null}", "Refused", "Refused")]
    [InlineData(403, "<html>forbidden</html>", "Refused", "Server Sync there refused this key (403)")]
    [InlineData(403, "[1]", "Refused", "Server Sync there refused this key (403)")]
    [InlineData(403, null, "Refused", "Server Sync there refused this key (403)")]
    [InlineData(500, "boom", "Refused", "Server Sync there refused this key (500)")]
    public async Task CheckSyncAccessAsync_Answer_MapsToACode(int status, string? body, string expectedCode, string expectedMessage)
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/Capabilities", (HttpStatusCode)status, body).Client();

        var (code, message) = await client.CheckSyncAccessAsync();

        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedMessage, message);
    }

    /// <summary>
    /// A peer that cannot be reached is an Unknown result, and a cancelled check throws.
    /// True: the page shows the fault as a result and a closed page stops the check.
    /// False: an unreachable peer would fail the page's request.
    /// </summary>
    [Fact]
    public async Task CheckSyncAccessAsync_Unreachable_IsUnknown()
    {
        using var broken = new ClientStubServer().OnThrow("/Sync/Peer/Capabilities", new HttpRequestException("no route")).Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (code, message) = await broken.CheckSyncAccessAsync();

        Assert.Equal("Unknown", code);
        Assert.Equal("Could not reach Server Sync there: no route", message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broken.CheckSyncAccessAsync(cancelled.Token));
    }

    /// <summary>
    /// The playlist request names this server and the one playlist when asked for one.
    /// True: the peer lists what it offers this server, or just the playlist a hint named.
    /// False: the peer could not tell who asks, or one hint would list every playlist.
    /// </summary>
    [Fact]
    public async Task GetPeerPlaylistsAsync_Request_NamesThisServerAndThePlaylist()
    {
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");
        var server = new ClientStubServer().OnJson("/Sync/Peer/Playlists", "{\"Playlists\":[]}");
        using var client = server.Client();

        await client.GetPeerPlaylistsAsync("this server&1");
        await client.GetPeerPlaylistsAsync(ThisServer, id);

        Assert.Equal("?serverId=this server&1", server.Seen[0].Query);
        Assert.Equal("?serverId=" + ThisServer + "&id=0123456789abcdef0123456789abcdef", server.Seen[1].Query);
    }

    /// <summary>
    /// A peer that refuses or lacks the route lists nothing, another version or a server error throws, and an empty success is an empty list.
    /// True: Playlists waits on a mismatch or an error, and skips a peer that does not offer them.
    /// False: an error would read as no playlists and remove the copies here.
    /// </summary>
    [Theory]
    [InlineData(401, true, false)]
    [InlineData(403, true, false)]
    [InlineData(404, true, false)]
    [InlineData(426, false, true)]
    [InlineData(500, false, true)]
    [InlineData(200, false, false)]
    public async Task GetPeerPlaylistsAsync_Answer_IsNullEmptyOrThrown(int status, bool expectNull, bool expectThrow)
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/Playlists", (HttpStatusCode)status).Client();

        if (expectThrow)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetPeerPlaylistsAsync(ThisServer));
            Assert.Equal((HttpStatusCode)status, failure.StatusCode);
            return;
        }

        var result = await client.GetPeerPlaylistsAsync(ThisServer);

        Assert.Equal(expectNull, result is null);
    }

    /// <summary>
    /// The visible playlists are listed as the user, and an answer with no list throws.
    /// True: Playlists mirrors what that user can see.
    /// False: a missing answer would read as a user with no playlists and remove them here.
    /// </summary>
    [Fact]
    public async Task GetVisiblePlaylistsAsync_ListOrNoAnswer_ReturnsOrThrows()
    {
        var user = Guid.NewGuid();
        var server = new ClientStubServer().OnJson("/Items", ClientJson.Page(1, ClientJson.Item(Guid.NewGuid(), "Playlist", name: "Mix")));
        using var client = server.Client();
        using var silent = new ClientStubServer().OnJson("/Items", "{}").Client();

        var playlists = await client.GetVisiblePlaylistsAsync(user);

        Assert.Equal("Mix", Assert.Single(playlists).Name);
        var query = Assert.Single(server.Seen).Query;
        Assert.Contains("userId=" + user, query, StringComparison.Ordinal);
        Assert.Contains("includeItemTypes=Playlist", query, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => silent.GetVisiblePlaylistsAsync(user));
    }

    /// <summary>
    /// A playlist's entries keep their order and their repeats.
    /// True: a playlist that holds a song twice has it twice here.
    /// False: the mirrored playlist would lose every repeat.
    /// </summary>
    [Fact]
    public async Task GetPlaylistEntriesAsync_RepeatedItem_IsKept()
    {
        var playlist = Guid.NewGuid();
        var song = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var client = new ClientStubServer()
            .OnJson("/Playlists/" + playlist + "/Items", ClientJson.Page(null, ClientJson.Item(song, "Audio"), ClientJson.Item(other, "Audio"), ClientJson.Item(song, "Audio")))
            .Client();

        var entries = await client.GetPlaylistEntriesAsync(playlist, Guid.NewGuid());

        Assert.Equal(new[] { song, other, song }, entries.Select(e => e.Id!.Value));
    }

    /// <summary>
    /// A history negotiation posts the entries and returns the peer's results, with enums read as names.
    /// True: both servers end on the same history state.
    /// False: the peer's outcome would be misread and the row left unresolved.
    /// </summary>
    [Fact]
    public async Task NegotiateHistoryAsync_Accepted_ReturnsThePeersResults()
    {
        var server = new ClientStubServer().OnJson("/Sync/Peer/History", "{\"Items\":[{\"UserId\":\"u\",\"ItemId\":\"i\",\"Outcome\":\"Stale\",\"Current\":{\"Played\":true,\"PlayCount\":2}}]}");
        using var client = server.Client();
        var request = new PeerHistoryRequest { SenderServerId = ThisServer, Items = { new PeerHistoryEntry { UserId = "u", ItemId = "i", Proposed = new PeerHistoryState { Played = true } } } };

        var response = await client.NegotiateHistoryAsync(request);

        var result = Assert.Single(response.Items);
        Assert.Equal(PeerHistoryOutcome.Stale, result.Outcome);
        Assert.True(result.Current!.Played);
        Assert.Equal(2, result.Current.PlayCount);
        var sent = Assert.Single(server.Seen);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Contains("\"SenderServerId\":\"" + ThisServer + "\"", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"Proposed\":{\"Played\":true", sent.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused negotiation throws with the status, which says whether the key itself was refused.
    /// True: History reports a refused key apart from a peer that is down.
    /// False: a refusal would read as an empty set of results, and every row would look settled.
    /// </summary>
    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, false)]
    [InlineData(503, false)]
    public async Task NegotiateHistoryAsync_Refused_ThrowsWithTheStatus(int status, bool keyRefused)
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/History", (HttpStatusCode)status, "why").Client();

        var failure = await Assert.ThrowsAsync<PeerRefusedException>(() => client.NegotiateHistoryAsync(new PeerHistoryRequest()));

        Assert.Equal((HttpStatusCode)status, failure.StatusCode);
        Assert.Equal(keyRefused, failure.KeyRefused);
        Assert.EndsWith(": why", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An accepted negotiation with no body is an empty set of results.
    /// True: a peer with nothing to say does not fail the run.
    /// False: an empty answer would throw a null reference.
    /// </summary>
    [Fact]
    public async Task NegotiateHistoryAsync_EmptyAnswer_IsEmpty()
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/History", HttpStatusCode.OK).Client();

        var response = await client.NegotiateHistoryAsync(new PeerHistoryRequest());

        Assert.Empty(response.Items);
    }

    /// <summary>
    /// Offering hints returns the status for every answer and never throws on one.
    /// True: delivery decides between done, paused, and retry from the status.
    /// False: a 503 would escape as an exception and stop the delivery loop.
    /// </summary>
    [Theory]
    [InlineData(200, "{\"Items\":[{\"HintId\":\"h1\",\"Code\":\"Queued\"}]}", 1, null)]
    [InlineData(200, null, 0, null)]
    [InlineData(204, null, -1, null)]
    [InlineData(426, "another version", -1, "another version")]
    [InlineData(503, "busy", -1, "busy")]
    public async Task SendHintsAsync_AnyAnswer_ReturnsItsStatus(int status, string? body, int expectedItems, string? expectedBody)
    {
        var server = new ClientStubServer().OnStatus("/Sync/Peer/Queue", (HttpStatusCode)status, body);
        using var client = server.Client();
        var request = new QueueRequest { SenderServerId = ThisServer, Items = { new SyncHint() } };

        var (statusCode, response, text) = await client.SendHintsAsync(request);

        Assert.Equal(status, statusCode);
        Assert.Equal(expectedItems, response?.Items.Count ?? -1);
        Assert.Equal(expectedBody, text);
        Assert.Contains("\"SenderServerId\":\"" + ThisServer + "\"", Assert.Single(server.Seen).Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// While the link question is open, the pairing store expects a secret from this peer, and not after.
    /// True: the peer can hand over its secret only during a call this server made.
    /// False: any caller could plant a pairing secret at any time, or the real one would be refused.
    /// </summary>
    [Fact]
    public async Task GetPeerLinkAsync_WhileOpen_ExpectsAPairingFromThePeer()
    {
        var store = NewPairings();
        var expectingDuringCall = false;
        var server = new ClientStubServer().On("/Sync/Peer/Link", _ =>
        {
            expectingDuringCall = store.Expecting(PeerKey);
            return ClientStubServer.Json("{\"ServerId\":\"peer\",\"Listed\":true,\"Paired\":true}");
        });
        using var client = server.Client();
        client.Pairing = new PeerPairing(store, PeerKey, ThisServer);

        var link = await client.GetPeerLinkAsync(ThisServer);

        Assert.True(expectingDuringCall);
        Assert.False(store.Expecting(PeerKey));
        Assert.True(link!.Listed);
        Assert.True(link.Paired);
        Assert.Equal("?serverId=" + ThisServer, Assert.Single(server.Seen).Query);
    }

    /// <summary>
    /// A peer with no Server Sync to answer the link question gives null.
    /// True: the Servers tab shows the peer as not running Server Sync.
    /// False: the page would fail on a plain Jellyfin server.
    /// </summary>
    [Fact]
    public async Task GetPeerLinkAsync_NoServerSync_IsNull()
    {
        using var client = new ClientStubServer().Client();

        Assert.Null(await client.GetPeerLinkAsync(ThisServer));
    }

    /// <summary>
    /// Pairing posts this server's id and secret and returns what the peer answered.
    /// True: the caller sees a refusal and its reason.
    /// False: a refused pairing would look stored.
    /// </summary>
    [Theory]
    [InlineData(204, null, null)]
    [InlineData(403, "not listed", "not listed")]
    public async Task PairAsync_Answer_ReturnsStatusAndBody(int status, string? body, string? expectedBody)
    {
        var server = new ClientStubServer().OnStatus("/Sync/Peer/Pair", (HttpStatusCode)status, body);
        using var client = server.Client();

        var (statusCode, text) = await client.PairAsync(new PairRequest { ServerId = ThisServer, Secret = "s3cret" });

        Assert.Equal(status, statusCode);
        Assert.Equal(expectedBody, text);
        Assert.Equal("{\"ServerId\":\"" + ThisServer + "\",\"Secret\":\"s3cret\"}", Assert.Single(server.Seen).Body);
    }

    /// <summary>
    /// A person is read by name with the name escaped, a 404 is null, and another error throws.
    /// True: a person the peer does not have is skipped, and a failing peer is retried.
    /// False: a name with a slash would ask for another route, or a server error would read as no such person.
    /// </summary>
    [Fact]
    public async Task GetPersonByNameAsync_FoundMissingOrFailed_AreToldApart()
    {
        var server = new ClientStubServer().OnJson("/Persons/AC%2FDC%20Jr.", ClientJson.Item(Guid.NewGuid(), "Person", name: "AC/DC Jr."));
        using var client = server.Client();
        using var refusing = new ClientStubServer().OnStatus("/Persons/Ada", HttpStatusCode.InternalServerError).Client();

        var found = await client.GetPersonByNameAsync("AC/DC Jr.");
        var missing = await client.GetPersonByNameAsync("Nobody");

        Assert.Equal("AC/DC Jr.", found!.Name);
        Assert.Null(missing);
        await Assert.ThrowsAnyAsync<ApiException>(() => refusing.GetPersonByNameAsync("Ada"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetPersonByNameAsync(" "));
    }

    /// <summary>
    /// The peer's versions come back by key, and a refusal answers null.
    /// True: a conflict is settled against the peer's real stamp.
    /// False: a refusal would read as "the peer holds no versions" and this server's value would always win.
    /// </summary>
    [Fact]
    public async Task GetPeerVersionsAsync_AnswerOrRefusal_IsAMapOrNull()
    {
        var server = new ClientStubServer().OnJson(
            "/Sync/Peer/Versions",
            "{\"Items\":[{\"Kind\":\"Metadata\",\"Key\":\"a\",\"ServerId\":\"s1\",\"Timestamp\":\"2026-01-02T03:04:05Z\"},{\"Kind\":\"Metadata\",\"Key\":\"a\",\"ServerId\":\"s2\",\"Timestamp\":\"2026-01-03T00:00:00Z\"}]}");
        using var client = server.Client();
        using var refusing = new ClientStubServer().OnStatus("/Sync/Peer/Versions", HttpStatusCode.Forbidden).Client();
        using var silent = new ClientStubServer().OnStatus("/Sync/Peer/Versions", HttpStatusCode.OK).Client();
        var request = new VersionsRequest { Kind = HintKind.Metadata, Keys = { "a" } };

        var versions = await client.GetPeerVersionsAsync(request);

        Assert.Equal("s2", Assert.Single(versions!).Value.ServerId);
        Assert.Equal("{\"Kind\":\"Metadata\",\"Keys\":[\"a\"]}", Assert.Single(server.Seen).Body);
        Assert.Null(await refusing.GetPeerVersionsAsync(request));
        Assert.Empty((await silent.GetPeerVersionsAsync(request))!);
    }

    /// <summary>
    /// A completion report is accepted only on a success status.
    /// True: a refused report is sent again later.
    /// False: the origin would keep hints as waiting that this server already finished.
    /// </summary>
    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(400, false)]
    [InlineData(500, false)]
    public async Task CompleteHintsAsync_Status_SaysWhetherItWasAccepted(int status, bool expected)
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/Complete", (HttpStatusCode)status).Client();

        var accepted = await client.CompleteHintsAsync(new CompleteRequest { SenderServerId = ThisServer });

        Assert.Equal(expected, accepted);
    }

    /// <summary>
    /// The peer's interest is read with kinds as names, and a refusal throws with its status.
    /// True: this server sends the peer only the hints it wants.
    /// False: a refusal would read as "wants nothing" and stop every hint to that peer.
    /// </summary>
    [Fact]
    public async Task GetPeerInterestAsync_AnswerOrRefusal_ParsesOrThrows()
    {
        var server = new ClientStubServer().OnJson("/Sync/Peer/Interest", "{\"Kinds\":[\"History\",\"Playlists\"],\"Users\":[\"u1\"],\"Revision\":\"r1\"}");
        using var client = server.Client();
        using var refusing = new ClientStubServer().OnStatus("/Sync/Peer/Interest", HttpStatusCode.Forbidden, "no").Client();

        var interest = await client.GetPeerInterestAsync("this/server");
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => refusing.GetPeerInterestAsync(ThisServer));

        Assert.Equal(new[] { HintKind.History, HintKind.Playlists }, interest!.Kinds);
        Assert.Equal("r1", interest.Revision);
        Assert.Equal("?serverId=this/server", Assert.Single(server.Seen).Query);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetPeerInterestAsync(" "));
    }

    /// <summary>
    /// A lookup returns the peer's answer, and a refusal throws with its status.
    /// True: the metadata provider can tell "not there" from "could not ask".
    /// False: a peer that is down would read as an item that does not exist there.
    /// </summary>
    [Fact]
    public async Task LookupPeerItemAsync_AnswerOrRefusal_ParsesOrThrows()
    {
        var server = new ClientStubServer().OnJson("/Sync/Peer/Lookup", "{\"Found\":true,\"ItemId\":\"abc\",\"ItemType\":\"Movie\"}");
        using var client = server.Client();
        using var refusing = new ClientStubServer().OnStatus("/Sync/Peer/Lookup", HttpStatusCode.BadGateway).Client();

        var found = await client.LookupPeerItemAsync(new PeerLookupRequest { Path = "/m/a.mkv", ItemType = "Movie" });
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => refusing.LookupPeerItemAsync(new PeerLookupRequest()));

        Assert.True(found!.Found);
        Assert.Equal("abc", found.ItemId);
        Assert.Contains("\"Path\":\"/m/a.mkv\"", Assert.Single(server.Seen).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
    }

    /// <summary>
    /// A media segment read sends the item ids and returns the peer's answer, and a refusal, such as a peer on a release without the route, throws with its status.
    /// True: the metadata scan keeps what it held for a peer that cannot answer.
    /// False: a peer without the route would read as one whose files have no segments, and every segment copied from it would be removed.
    /// </summary>
    [Fact]
    public async Task GetPeerSegmentsAsync_AnswerOrRefusal_ParsesOrThrows()
    {
        var server = new ClientStubServer().OnJson("/Sync/Peer/Segments", "{\"Items\":[{\"ItemId\":\"abc\",\"Found\":true,\"Size\":5,\"RunTimeTicks\":9,\"Segments\":[{\"Type\":\"Intro\",\"StartTicks\":1,\"EndTicks\":2}]}]}");
        using var client = server.Client();
        using var refusing = new ClientStubServer().OnStatus("/Sync/Peer/Segments", HttpStatusCode.NotFound).Client();

        var answer = await client.GetPeerSegmentsAsync(new[] { "abc" });
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => refusing.GetPeerSegmentsAsync(new[] { "abc" }));

        var item = Assert.Single(answer.Items);
        Assert.True(item.Found);
        Assert.Equal(5, item.Size);
        Assert.Equal("Intro", Assert.Single(item.Segments).Type);
        Assert.Contains("\"ItemIds\":[\"abc\"]", Assert.Single(server.Seen).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetPeerSegmentsAsync(null!));
    }

    /// <summary>
    /// An authorized GET only goes to an address under this entry's Server URL.
    /// True: the API Key is never sent to another host.
    /// False: an image address a peer supplied could collect the key on any server it names.
    /// </summary>
    [Theory]
    [InlineData("https://elsewhere.example/Items/1/Images/Primary")]
    [InlineData("https://peer.example.evil.test/Items/1")]
    [InlineData("https://peer.example")]
    [InlineData("http://peer.example/Items/1")]
    public async Task GetAuthorizedAsync_AddressOffThisServer_IsRefusedWithoutARequest(string url)
    {
        var server = new ClientStubServer();
        using var client = server.Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAuthorizedAsync(url));

        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// An address under the Server URL is fetched with the key, whatever the case of its host.
    /// True: the provider can fetch a source's images.
    /// False: no image of a source could ever be read.
    /// </summary>
    [Fact]
    public async Task GetAuthorizedAsync_AddressOnThisServer_IsFetchedWithTheKey()
    {
        var server = new ClientStubServer().OnStatus("/Items/1/Images/Primary", HttpStatusCode.OK, "img");
        using var client = server.Client();

        using var response = await client.GetAuthorizedAsync("https://PEER.example/Items/1/Images/Primary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Token=\"key\"", Assert.Single(server.Seen).Authorization, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetAuthorizedAsync(" "));
    }

    /// <summary>
    /// The queue status is read for this server, and a peer that gives none answers null.
    /// True: the Change queue shows what the peer still holds.
    /// False: a peer without an answer would fail the queue view.
    /// </summary>
    [Fact]
    public async Task GetPeerQueueStatusAsync_AnswerOrNone_ParsesOrNull()
    {
        var server = new ClientStubServer().OnJson("/Sync/Peer/Status", "{\"ServerId\":\"peer\",\"CompletedHints\":[{}]}");
        using var client = server.Client();
        using var refusing = new ClientStubServer().OnStatus("/Sync/Peer/Status", HttpStatusCode.Forbidden).Client();

        var status = await client.GetPeerQueueStatusAsync(ThisServer, CancellationToken.None);

        Assert.Equal("peer", status!.ServerId);
        Assert.Single(status.CompletedHints);
        Assert.Equal("?serverId=" + ThisServer, Assert.Single(server.Seen).Query);
        Assert.Null(await refusing.GetPeerQueueStatusAsync(ThisServer, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetPeerQueueStatusAsync(string.Empty, CancellationToken.None));
    }

    /// <summary>
    /// A stream request names the caller, the user, and its purpose, forwards the range, and is never retried.
    /// True: the peer can check who streams and serve a seek.
    /// False: the peer could not apply its user limits, a seek would restart the film, or a failed stream would be replayed.
    /// </summary>
    [Fact]
    public async Task OpenPeerStreamAsync_Request_CarriesCallerUserRangeAndNoRetry()
    {
        var user = Guid.Parse("0123456789abcdef0123456789abcdef");
        var server = new ClientStubServer().OnStatus("/Sync/Peer/Stream/item%201", HttpStatusCode.PartialContent, "bytes");
        using var client = server.Client(pluginVersion: "7.7");

        using var response = await client.OpenPeerStreamAsync("item 1", ThisServer, new LinkGrant(user, true), "src user", "bytes=10-", head: false, fill: true);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var sent = Assert.Single(server.Seen);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("?serverId=" + ThisServer + "&userId=0123456789abcdef0123456789abcdef&sourceUserId=src user&probe=true&fill=true", sent.Query);
        Assert.Equal("bytes=10-", sent.Header("Range"));
        Assert.Equal("7.7", sent.Header(HintProtocol.VersionHeader));
        Assert.True(sent.NoRetry);
        Assert.Null(sent.Header(HintProtocol.PairingHeader));
    }

    /// <summary>
    /// A plain stream check sends HEAD with only the caller named.
    /// True: a link check reads the headers without pulling the file.
    /// False: checking a link would download it.
    /// </summary>
    [Fact]
    public async Task OpenPeerStreamAsync_HeadWithoutUser_SendsTheBareRequest()
    {
        var server = new ClientStubServer();
        using var client = server.Client();

        using var response = await client.OpenPeerStreamAsync("abc", ThisServer, new LinkGrant(null, false), null, null, head: true, fill: false);

        var sent = Assert.Single(server.Seen);
        Assert.Equal(HttpMethod.Head, sent.Method);
        Assert.Equal("?serverId=" + ThisServer, sent.Query);
        Assert.Null(sent.Header("Range"));
    }

    /// <summary>
    /// A stream the peer answers "not paired" is paired once and asked again, only when this client holds a pairing.
    /// True: a peer that lost the pairing heals on the next play.
    /// False: every stream would fail until an operator paired by hand, or an unsaved entry would start pairing.
    /// </summary>
    [Theory]
    [InlineData(true, 2, 1, 200)]
    [InlineData(false, 1, 0, 428)]
    public async Task OpenPeerStreamAsync_Unpaired_PairsOnceWhenPaired(bool hasPairing, int expectedStreamCalls, int expectedLinkCalls, int expectedStatus)
    {
        var server = new ClientStubServer().OnJson("/Sync/Peer/Link", "{\"Listed\":true}");
        server.On("/Sync/Peer/Stream/abc", _ => new HttpResponseMessage(server.To("/Sync/Peer/Link").Count == 0 ? (HttpStatusCode)HintProtocol.UnpairedStatus : HttpStatusCode.OK));
        using var client = server.Client();
        if (hasPairing)
        {
            client.Pairing = new PeerPairing(NewPairings(), PeerKey, ThisServer);
        }

        using var response = await client.OpenPeerStreamAsync("abc", ThisServer, new LinkGrant(null, false), null, null, head: false, fill: false);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(expectedStreamCalls, server.To("/Sync/Peer/Stream/abc").Count);
        Assert.Equal(expectedLinkCalls, server.To("/Sync/Peer/Link").Count);
    }

    /// <summary>
    /// A stream check the peer refuses, or answers with nothing, is not allowed and says why.
    /// True: a refused user sees the peer's reason before playback starts.
    /// False: a failed check would read as allowed and playback would break partway.
    /// </summary>
    [Theory]
    [InlineData(200, "{\"Allowed\":true,\"Size\":42}", true, null)]
    [InlineData(403, "Allow linking is off", false, "the source answered 403: Allow linking is off")]
    [InlineData(200, null, false, "the source answered 200: ")]
    public async Task CheckLinkStreamAsync_Answer_IsAllowedOnlyOnAParsedSuccess(int status, string? body, bool expectedAllowed, string? expectedReason)
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/Stream/Check", (HttpStatusCode)status, body).Client();

        var result = await client.CheckLinkStreamAsync(new LinkStreamCheckRequest { ServerId = ThisServer, ItemId = "abc" });

        Assert.Equal(expectedAllowed, result.Allowed);
        Assert.Equal(expectedReason, result.Reason);
    }

    /// <summary>
    /// A playback report or a remote control command the peer refuses throws, and an accepted one does not.
    /// True: a refused report is retried or logged by its caller.
    /// False: the peer's session would silently go stale while this server believes it reported.
    /// </summary>
    [Theory]
    [InlineData(204, false)]
    [InlineData(403, true)]
    [InlineData(500, true)]
    public async Task ReportAndControl_Answer_ThrowOnlyWhenRefused(int status, bool expectThrow)
    {
        var server = new ClientStubServer()
            .OnStatus("/Sync/Peer/Playback", (HttpStatusCode)status, "p")
            .OnStatus("/Sync/Peer/Control", (HttpStatusCode)status, "c");
        using var client = server.Client();
        var report = new PeerPlaybackReport { ServerId = ThisServer, Event = LinkPlaybackEvent.Stop, ItemId = "abc" };
        var control = new PeerControlRequest { ServerId = ThisServer, Command = "Pause" };

        var reportFailure = await Record.ExceptionAsync(() => client.ReportLinkPlaybackAsync(report));
        var controlFailure = await Record.ExceptionAsync(() => client.SendLinkControlAsync(control));

        Assert.Equal(expectThrow, reportFailure is HttpRequestException);
        Assert.Equal(expectThrow, controlFailure is HttpRequestException);
        Assert.Contains("\"Event\":\"Stop\"", server.To("/Sync/Peer/Playback")[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"Command\":\"Pause\"", server.To("/Sync/Peer/Control")[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Describing links answers null when the peer has no such route, the peer's own reason when it refuses, and an empty list for an empty success.
    /// True: the link check shows "Allow linking is off" as the peer wrote it.
    /// False: a refusal would read as links with no details, or a missing route as a refusal.
    /// </summary>
    [Theory]
    [InlineData(404, null, true, null)]
    [InlineData(405, null, true, null)]
    [InlineData(200, null, false, null)]
    [InlineData(403, "Allow linking is off for this server", false, "Allow linking is off for this server")]
    [InlineData(500, null, false, "the source answered 500")]
    public async Task DescribeLinksAsync_Answer_IsNullEmptyOrThePeersReason(int status, string? body, bool expectNull, string? expectedFailure)
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/Links/Describe", (HttpStatusCode)status, body).Client();
        var request = new PeerLinkDescribeRequest { ServerId = ThisServer, ItemIds = { "abc" } };

        if (expectedFailure is not null)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.DescribeLinksAsync(request));
            Assert.Equal(expectedFailure, failure.Message);
            return;
        }

        var result = await client.DescribeLinksAsync(request);

        Assert.Equal(expectNull, result is null);
    }

    /// <summary>
    /// A link image is asked for by item, kind, index, and width, as this server.
    /// True: chapter images and trickplay tiles of a link come from the peer that made them.
    /// False: the wrong tile would be shown, or the peer could not tell who asks.
    /// </summary>
    [Fact]
    public async Task OpenLinkImageAsync_Request_NamesTheImage()
    {
        var server = new ClientStubServer();
        using var client = server.Client();

        using var response = await client.OpenLinkImageAsync("abc", "Trickplay", 12, 320, ThisServer);

        var sent = Assert.Single(server.Seen);
        Assert.Equal("/Sync/Peer/Links/abc/Images/Trickplay/12", sent.Path);
        Assert.Equal("?serverId=" + ThisServer + "&width=320", sent.Query);
        Assert.True(sent.NoRetry);
    }

    /// <summary>
    /// Only a missing route says the peer has no links. A refusal or an error throws.
    /// True: a refusal never reads as "no links".
    /// False: a refused listing would let this server copy the peer's .strm files as media.
    /// </summary>
    [Theory]
    [InlineData(404, true, false)]
    [InlineData(200, false, false)]
    [InlineData(403, false, true)]
    [InlineData(500, false, true)]
    public async Task GetPeerLinksAsync_Answer_IsNullEmptyOrThrown(int status, bool expectNull, bool expectThrow)
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/Links", (HttpStatusCode)status).Client();

        if (expectThrow)
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetPeerLinksAsync());
            Assert.Equal((HttpStatusCode)status, failure.StatusCode);
            return;
        }

        var result = await client.GetPeerLinksAsync();

        Assert.Equal(expectNull, result is null);
    }

    /// <summary>
    /// The peer's removals are asked for from a UTC time. A client error answers null and a server error throws.
    /// True: a peer without the route falls back to a full listing, and a failing peer is asked again.
    /// False: a server error would read as "nothing was removed" and removed files would stay here.
    /// </summary>
    [Theory]
    [InlineData(200, false, false)]
    [InlineData(404, true, false)]
    [InlineData(403, true, false)]
    [InlineData(500, false, true)]
    public async Task GetPeerChangesAsync_Answer_IsParsedNullOrThrown(int status, bool expectNull, bool expectThrow)
    {
        var server = new ClientStubServer().OnStatus("/Sync/Peer/Changes", (HttpStatusCode)status, status == 200 ? "{\"TooOld\":true,\"RemovedItemIds\":[\"a\",\"b\"]}" : null);
        using var client = server.Client();
        var since = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        if (expectThrow)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetPeerChangesAsync(since));
            return;
        }

        var changes = await client.GetPeerChangesAsync(since);

        Assert.Equal(expectNull, changes is null);
        Assert.Equal("?since=2026-05-06T07:08:09.0000000Z", Assert.Single(server.Seen).Query);
        if (!expectNull)
        {
            Assert.True(changes!.TooOld);
            Assert.Equal(new[] { "a", "b" }, changes.RemovedItemIds);
        }
    }

    /// <summary>
    /// The peer's copies are parsed. A client error answers null and a server error throws.
    /// True: a peer without the route has every file taken as its own, and a failing peer is asked again.
    /// False: a server error would read as "no copies" and a copy of a copy would be made.
    /// </summary>
    [Theory]
    [InlineData(200, false, false)]
    [InlineData(404, true, false)]
    [InlineData(401, true, false)]
    [InlineData(502, false, true)]
    public async Task GetPeerCopiesAsync_Answer_IsParsedNullOrThrown(int status, bool expectNull, bool expectThrow)
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/Copies", (HttpStatusCode)status).Client();

        if (expectThrow)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetPeerCopiesAsync());
            return;
        }

        var copies = await client.GetPeerCopiesAsync();

        Assert.Equal(expectNull, copies is null);
        Assert.Equal(expectNull, copies?.Copies is null);
    }

    /// <summary>
    /// A peer call carries the secret the peer issued, and none when there is no pairing or no secret yet.
    /// True: the peer can verify the sender of every request.
    /// False: every paired request would be refused as unpaired, or an empty header would be sent.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PeerCalls_PairingSecret_IsSentOnlyWhenHeld(bool hasPairing, bool hasSecret)
    {
        var secret = PeerPairingStore.NewSecret();
        var server = new ClientStubServer().OnJson("/Sync/Peer/Capabilities", "{}");
        using var client = server.Client();
        if (hasPairing)
        {
            var store = NewPairings();
            if (hasSecret)
            {
                store.SetOutbound(PeerKey, secret);
            }

            client.Pairing = new PeerPairing(store, PeerKey, ThisServer);
        }

        await client.GetPeerCapabilitiesAsync();

        Assert.Equal(hasSecret ? secret : null, Assert.Single(server.Seen).Header(HintProtocol.PairingHeader));
    }

    /// <summary>
    /// A call the peer answers "not paired" asks the peer to pair and goes once more.
    /// True: a peer that lost the pairing heals on the next request.
    /// False: hints would fail until an operator paired by hand, or the call would loop forever on a peer that stays unpaired.
    /// </summary>
    [Fact]
    public async Task PeerCalls_Unpaired_PairAndRetryOnce()
    {
        var server = new ClientStubServer()
            .OnJson("/Sync/Peer/Link", "{\"Listed\":true}")
            .OnStatus("/Sync/Peer/Complete", (HttpStatusCode)HintProtocol.UnpairedStatus, "not paired");
        using var client = server.Client();
        client.Pairing = new PeerPairing(NewPairings(), PeerKey, ThisServer);

        var accepted = await client.CompleteHintsAsync(new CompleteRequest());

        Assert.False(accepted);
        Assert.Equal(2, server.To("/Sync/Peer/Complete").Count);
        Assert.Equal("?serverId=" + ThisServer, Assert.Single(server.To("/Sync/Peer/Link")).Query);
    }

    /// <summary>
    /// The request is not sent again when there is no pairing, when the peer cannot answer the link question, or for a call that must not pair.
    /// True: an unsaved entry, a peer without the route, and the pairing calls themselves never start a pairing loop.
    /// False: asking to pair would itself ask to pair, without end.
    /// </summary>
    [Theory]
    [InlineData("no-pairing")]
    [InlineData("no-link-answer")]
    [InlineData("call-does-not-pair")]
    public async Task PeerCalls_UnpairedWithoutAWayToPair_AnswerOnce(string scenario)
    {
        var path = scenario == "call-does-not-pair" ? "/Sync/Peer/Lookup" : "/Sync/Peer/Complete";
        var server = new ClientStubServer().OnStatus(path, (HttpStatusCode)HintProtocol.UnpairedStatus);
        if (scenario == "call-does-not-pair")
        {
            server.OnJson("/Sync/Peer/Link", "{\"Listed\":true}");
        }

        using var client = server.Client();
        if (scenario != "no-pairing")
        {
            client.Pairing = new PeerPairing(NewPairings(), PeerKey, ThisServer);
        }

        if (scenario == "call-does-not-pair")
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.LookupPeerItemAsync(new PeerLookupRequest()));
        }
        else
        {
            Assert.False(await client.CompleteHintsAsync(new CompleteRequest()));
        }

        Assert.Single(server.To(path));
        Assert.Equal(scenario == "no-link-answer" ? 1 : 0, server.To("/Sync/Peer/Link").Count);
    }

    /// <summary>
    /// A refusal's body is cut to one short line with control characters turned into spaces.
    /// True: a peer's reason is safe to show in a log line, a row, and the dashboard.
    /// False: a hostile peer could forge log lines or flood the dashboard with megabytes of text.
    /// </summary>
    [Fact]
    public async Task PeerCalls_RefusalBody_IsCutToOneShortLine()
    {
        using var client = new ClientStubServer().OnStatus("/Sync/Peer/Queue", HttpStatusCode.BadRequest, "line one\r\nline\ttwo" + new string('x', 5000)).Client();

        var (_, _, body) = await client.SendHintsAsync(new QueueRequest());

        Assert.StartsWith("line one  line two", body, StringComparison.Ordinal);
        Assert.Equal(2048, body!.Length);
        Assert.DoesNotContain(body, c => char.IsControl(c));
    }

    /// <summary>
    /// An answer that declares more than the cap is refused before its body is read.
    /// True: a peer cannot make this server hold an answer of any size it likes.
    /// False: one hostile or broken peer could exhaust this server's memory.
    /// </summary>
    [Fact]
    public async Task PeerCalls_DeclaredLengthOverTheCap_IsRefusedUnread()
    {
        var body = new ClientEndlessStream(long.MaxValue);
        using var client = new ClientStubServer().On("/Sync/Peer/Capabilities", _ =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = HintProtocol.MaxPeerResponseBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }).Client();

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetPeerCapabilitiesAsync());

        Assert.Contains("more than the " + HintProtocol.MaxPeerResponseBytes, failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, body.BytesRead);
    }

    /// <summary>
    /// An answer with no declared length is cut off once it passes the cap.
    /// True: a peer that streams without end is stopped just past the cap.
    /// False: a chunked answer would be read until memory ran out.
    /// </summary>
    [Fact]
    public async Task PeerCalls_UndeclaredLengthPastTheCap_IsCutOff()
    {
        var body = new ClientEndlessStream(long.MaxValue);
        using var client = new ClientStubServer().On("/Sync/Peer/Capabilities", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }).Client();

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetPeerCapabilitiesAsync());

        Assert.Contains("passed " + HintProtocol.MaxPeerResponseBytes + " bytes", failure.Message, StringComparison.Ordinal);
        Assert.InRange(body.BytesRead, HintProtocol.MaxPeerResponseBytes + 1, HintProtocol.MaxPeerResponseBytes + (128 * 1024));
    }

    /// <summary>
    /// A peer that never answers is abandoned when the caller cancels, as a cancellation.
    /// True: a hint worker told to stop is not held by a silent peer, and sees a shutdown, not a failed request.
    /// False: shutdown would hang on a silent peer, or be recorded as a delivery failure.
    /// </summary>
    [Fact]
    public async Task PeerCalls_PeerNeverAnswersAndCallerCancels_ThrowCancellation()
    {
        var reached = new TaskCompletionSource();
        using var handler = new ScriptedHandler(async token =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new SourceServerClient(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), ClientStubServer.Url, "key", "test", "1.0");
        using var stop = new CancellationTokenSource();

        var call = client.GetPeerCapabilitiesAsync(stop.Token);
        await reached.Task;
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    /// <summary>
    /// Each peer call refuses a missing request.
    /// True: a bad call fails at once with the argument's name.
    /// False: a null would be posted to the peer as the word "null".
    /// </summary>
    [Fact]
    public async Task PeerCalls_NullRequest_Throw()
    {
        using var client = new ClientStubServer().Client();

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.NegotiateHistoryAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SendHintsAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.PairAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetPeerVersionsAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.CompleteHintsAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.LookupPeerItemAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.CheckLinkStreamAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ReportLinkPlaybackAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SendLinkControlAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.DescribeLinksAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenPeerStreamAsync(null!, ThisServer, new LinkGrant(null, false), null, null, false, false));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenPeerStreamAsync("abc", ThisServer, null!, null, null, false, false));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenLinkImageAsync(null!, "Chapter", 0, 0, ThisServer));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenLinkImageAsync("abc", null!, 0, 0, ThisServer));
    }

    private PeerPairingStore NewPairings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "serversync-clientpeer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var database = new SyncDatabase(NullLogger<SyncDatabase>.Instance, dir);
        _databases.Add(database);
        return new PeerPairingStore(new FixedProvider(database), new SecretProtector("tests", NullLogger.Instance), NullLogger<PeerPairingStore>.Instance);
    }
}

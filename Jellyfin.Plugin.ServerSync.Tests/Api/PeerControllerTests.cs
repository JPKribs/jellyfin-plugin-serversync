using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Api;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Services.Playlists;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Api;

/// <summary>
/// The peer routes answer a server only as this server lists it, take nothing from a sender that has not proven itself, and hold a standard user's key to that user's own data.
/// </summary>
public sealed class PeerControllerTests : IDisposable
{
    private const string Secret = "issued-to-the-peer";
    private const string PeerId = ControllerTestHarness.PeerId;
    private static readonly Guid Own = Guid.Parse("22222222222222222222222222222222");
    private static readonly Guid Other = Guid.Parse("33333333333333333333333333333333");
    private static readonly Guid ItemId = Guid.Parse("11111111111111111111111111111111");

    private readonly ControllerTestHarness _harness = new();

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// Capabilities name this server, its plugin and protocol versions, and the kinds whose sync type is on.
    /// True: a caller learns what this server speaks and applies before it queues work.
    /// False: a caller would send hints this server drops, or could not tell its protocol.
    /// </summary>
    [Fact]
    public void GetCapabilities_ModulesOn_NamesThisServerAndWhatItApplies()
    {
        var config = _harness.Config.Configuration;
        (config.EnableHistorySync, config.EnableMetadataSync, config.EnablePeopleSync, config.EnableContentSync, config.EnablePlaylistSync) = (true, false, true, false, true);

        var result = _harness.PeerRoutes().GetCapabilities();

        var body = Assert.IsType<PeerCapabilities>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((ControllerTestHarness.ThisServerId, "0.0.0-test"), (body.ServerId, body.PluginVersion));
        Assert.Equal((HintProtocol.ProtocolVersion, HintProtocol.MinProtocolVersion), (body.ProtocolVersion, body.MinProtocolVersion));
        Assert.Equal(new[] { "History", "People", "Playlists" }, body.Accepts);
        Assert.Empty(body.Features);
        Assert.NotNull(body.ServerTime);
    }

    /// <summary>
    /// The link feature is offered only while some Push or Sync entry allows linking.
    /// True: a peer learns it may link here only when an entry lets it.
    /// False: every peer would try to link to a server that refuses them all.
    /// </summary>
    [Theory]
    [InlineData(ServerMode.Sync, true, true)]
    [InlineData(ServerMode.Push, true, true)]
    [InlineData(ServerMode.Pull, true, false)]
    [InlineData(ServerMode.Sync, false, false)]
    public void GetCapabilities_Linking_IsOfferedOnlyWhenAnEntryAllowsIt(ServerMode mode, bool allowLinking, bool offered)
    {
        _harness.AddPeer(mode).AllowLinking = allowLinking;

        var result = _harness.PeerRoutes().GetCapabilities();

        var body = Assert.IsType<PeerCapabilities>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(offered ? 1 : 0, body.Features.Count);
    }

    /// <summary>
    /// A caller this server does not list is told only who this server is.
    /// True: an unlisted or unnamed caller reads Listed as false and nothing about any entry.
    /// False: any caller could read how this server lists others.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ffffffffffffffffffffffffffffffff")]
    public async Task Link_CallerNotListed_AnswersNotListed(string? serverId)
    {
        _harness.AddPeer();

        var body = await Link(serverId);

        Assert.Equal((ControllerTestHarness.ThisServerId, "This server", false, false, null), (body.ServerId, body.ServerName, body.Listed, body.Paired, body.Mode));
        Assert.Empty(_harness.PeerPaths);
    }

    /// <summary>
    /// A listed caller is told its Direction here, which way changes travel, and the access this server has there.
    /// True: the caller's check shows exactly how this server lists it.
    /// False: the caller would judge its entry against the wrong Direction.
    /// </summary>
    [Theory]
    [InlineData(ServerMode.Pull, true, false)]
    [InlineData(ServerMode.Push, false, true)]
    [InlineData(ServerMode.Sync, true, true)]
    public async Task Link_ListedCaller_IsToldHowItIsListed(ServerMode mode, bool pulls, bool sends)
    {
        var entry = _harness.AddPeer(mode);
        (entry.AccessLevel, entry.AuthenticatedUser, entry.CanDownload) = (SourceServer.StandardUserAccess, "bob", false);
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var body = await Link(PeerId.ToUpperInvariant());

        Assert.Equal((true, mode.ToString(), true, pulls, sends), (body.Listed, body.Mode, body.Enabled, body.PullsFromYou, body.SendsToYou));
        Assert.Equal((SourceServer.StandardUserAccess, "bob", false), (body.AccessLevel, body.AccessUser, body.CanDownload));
    }

    /// <summary>
    /// A listed entry that is disabled or not set up is not paired with, and says why.
    /// True: no secret is sent to an entry this server would not call.
    /// False: a disabled entry would still be handed a secret that proves it.
    /// </summary>
    [Theory]
    [InlineData(false, "key")]
    [InlineData(true, "")]
    public async Task Link_EntryDisabledOrNotSetUp_DoesNotPair(bool enabled, string apiKey)
    {
        var entry = _harness.AddPeer();
        (entry.IsEnabled, entry.ApiKey) = (enabled, apiKey);

        var body = await Link(PeerId);

        Assert.Equal((true, false, false, "the entry for this server is disabled or has no URL and key"), (body.Listed, body.Enabled, body.Paired, body.PairingError));
        Assert.Empty(_harness.Clients.Created);
        Assert.Null(_harness.Pairings.GetInbound(entry.Key));
    }

    /// <summary>
    /// Pairing sends a well formed secret to the entry's own address and stores it once the entry accepts it.
    /// True: the caller is paired, and this server will recognize the secret it issued.
    /// False: the two would report paired while this server holds no secret to check a sender against.
    /// </summary>
    [Fact]
    public async Task Link_EntryAcceptsTheSecret_PairsAndStoresWhatWasSent()
    {
        var entry = _harness.AddPeer();
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var body = await Link(PeerId);

        var sent = JsonSerializer.Deserialize<PairRequest>(Assert.Single(_harness.PeerHandler.Bodies))!;
        Assert.Equal((true, null), (body.Paired, body.PairingError));
        Assert.Equal(new[] { "/Sync/Peer/Pair" }, _harness.PeerPaths);
        Assert.Equal(ControllerTestHarness.ThisServerId, sent.ServerId);
        Assert.True(PeerPairingStore.WellFormed(sent.Secret));
        Assert.Equal(sent.Secret, _harness.Pairings.GetInbound(entry.Key));
    }

    /// <summary>
    /// A second check sends the secret already issued, not a new one.
    /// True: a repeated check never invalidates the secret the peer already presents.
    /// False: every check would rotate the secret and cut off hints in flight.
    /// </summary>
    [Fact]
    public async Task Link_SecretAlreadyIssued_SendsTheSameSecretAgain()
    {
        var entry = _harness.AddPeer();
        _harness.Pairings.SetInbound(entry.Key, Secret);
        _harness.Peer = _ => new HttpResponseMessage(HttpStatusCode.OK);

        await Link(PeerId);

        Assert.Equal(Secret, JsonSerializer.Deserialize<PairRequest>(Assert.Single(_harness.PeerHandler.Bodies))!.Secret);
        Assert.Equal(Secret, _harness.Pairings.GetInbound(entry.Key));
    }

    /// <summary>
    /// An entry that refuses the secret is not paired, the reason says why, and no secret is stored.
    /// True: each refusal reads as its own cause, from a bad key to a missing plugin.
    /// False: a refused pairing would store a secret the peer never took.
    /// </summary>
    [Theory]
    [InlineData(401, "it refused the key this server holds for it")]
    [InlineData(403, "it refused the key this server holds for it")]
    [InlineData(404, "Server Sync is not installed there")]
    [InlineData(409, "it does not list this server: the reason it gave")]
    [InlineData(426, "the reason it gave")]
    [InlineData(500, "it answered 500: the reason it gave")]
    public async Task Link_EntryRefusesTheSecret_IsNotPairedAndSaysWhy(int status, string reason)
    {
        var entry = _harness.AddPeer();
        _harness.Peer = _ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("the reason it gave") };

        var body = await Link(PeerId);

        Assert.False(body.Paired);
        Assert.StartsWith(reason, body.PairingError, StringComparison.Ordinal);
        Assert.Null(_harness.Pairings.GetInbound(entry.Key));
    }

    /// <summary>
    /// An entry that cannot be reached, or whose client the factory refuses, is not paired and names the address.
    /// True: the caller's check shows this server could not call it back.
    /// False: an unreachable entry would fail the whole check with an error.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Link_EntryCannotBeReached_IsNotPairedAndNamesTheAddress(bool factoryRefuses)
    {
        var entry = _harness.AddPeer();
        _harness.Clients.Refusal = factoryRefuses ? "that address is not allowed" : null;
        _harness.Peer = _ => throw new HttpRequestException("connection refused");

        var body = await Link(PeerId);

        Assert.False(body.Paired);
        Assert.StartsWith("this server could not reach it at " + entry.Url + ": ", body.PairingError, StringComparison.Ordinal);
        Assert.Contains(factoryRefuses ? "that address is not allowed" : "connection refused", body.PairingError, StringComparison.Ordinal);
        Assert.Null(_harness.Pairings.GetInbound(entry.Key));
    }

    /// <summary>
    /// A cancelled check stops before it pairs.
    /// True: the cancellation reaches the caller and nothing is sent.
    /// False: a check the caller gave up on would still issue a secret.
    /// </summary>
    [Fact]
    public async Task Link_Cancelled_ThrowsWithoutPairing()
    {
        _harness.AddPeer();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _harness.PeerRoutes().Link(PeerId, new CancellationToken(canceled: true)));

        Assert.Empty(_harness.PeerPaths);
    }

    /// <summary>
    /// A sender is taken only when it names itself, is listed and enabled, and presents the secret this server issued to it.
    /// True: each failure has its own status, so the sender knows whether to pair, wait, or stop.
    /// False: a caller could act as another server by naming its ID.
    /// </summary>
    [Theory]
    [InlineData(null, true, Secret, StatusCodes.Status400BadRequest)]
    [InlineData(" ", true, Secret, StatusCodes.Status400BadRequest)]
    [InlineData("ffffffffffffffffffffffffffffffff", true, Secret, StatusCodes.Status409Conflict)]
    [InlineData(PeerId, false, Secret, StatusCodes.Status409Conflict)]
    [InlineData(PeerId, true, null, HintProtocol.UnpairedStatus)]
    [InlineData(PeerId, true, "issued-to-the-peer ", HintProtocol.UnpairedStatus)]
    [InlineData(PeerId, true, "", HintProtocol.UnpairedStatus)]
    [InlineData(PeerId, true, Secret, StatusCodes.Status200OK)]
    [InlineData("0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0F", true, Secret, StatusCodes.Status200OK)]
    public void Status_Sender_IsTakenOnlyWhenListedEnabledAndProven(string? serverId, bool enabled, string? presented, int status)
    {
        var entry = _harness.AddPeer();
        entry.IsEnabled = enabled;
        _harness.Pairings.SetInbound(entry.Key, Secret);

        var result = _harness.PeerRoutes(presented).Status(serverId);

        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
    }

    /// <summary>
    /// An entry that never paired takes no secret, not even an empty one.
    /// True: a sender is refused until this server has issued it a secret.
    /// False: an unpaired entry would accept a caller that presents nothing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anything")]
    public void Status_EntryNeverPaired_IsRefusedWhateverIsPresented(string? presented)
    {
        _harness.AddPeer();

        var result = _harness.PeerRoutes(presented).Status(PeerId);

        Assert.Equal(HintProtocol.UnpairedStatus, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    /// <summary>
    /// Status answers with the hints this server still holds from the caller and those of the caller's it finished, and no other server's.
    /// True: a sender can settle its own rows against what the receiver holds.
    /// False: a sender would read, and settle against, another server's hints.
    /// </summary>
    [Fact]
    public void Status_ProvenSender_ReadsOnlyItsOwnHints()
    {
        var controller = PairedRoutes();
        Receive(PeerId, "1");
        Receive(PeerId, "2");
        Receive("ffffffffffffffffffffffffffffffff", "3");
        _harness.InboundWorker.Dismiss(_harness.Inbound.GetAll().Single(r => r.HintId == PeerId + ":2").Id);
        _harness.InboundWorker.Dismiss(_harness.Inbound.GetAll().Single(r => r.OriginServerId != PeerId).Id);

        var result = controller.Status(PeerId);

        var body = Assert.IsType<QueueStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(ControllerTestHarness.ThisServerId, body.ServerId);
        Assert.Equal(PeerId + ":1", Assert.Single(body.Inbound).HintId);
        Assert.Equal(PeerId + ":2", Assert.Single(body.CompletedHints).HintId);
    }

    /// <summary>
    /// A hint batch without a body, a sender, or a list, or with too many hints, is refused with 400 before the sender is checked.
    /// True: a malformed batch stores nothing.
    /// False: a malformed batch would reach triage, or a huge one would be read in full.
    /// </summary>
    [Theory]
    [InlineData("no-body")]
    [InlineData("no-items")]
    [InlineData("no-sender")]
    [InlineData("too-many")]
    public void Queue_MalformedBatch_Answers400(string scenario)
    {
        var controller = PairedRoutes();
        var request = scenario switch
        {
            "no-body" => null,
            "no-items" => new QueueRequest { SenderServerId = PeerId, Items = null! },
            "no-sender" => new QueueRequest { SenderServerId = " ", Items = { Hint("1") } },
            _ => new QueueRequest { SenderServerId = PeerId, Items = Enumerable.Range(0, HintProtocol.MaxHintsPerRequest + 1).Select(i => Hint(i.ToString())).ToList() }
        };

        var result = controller.Queue(request!);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, _harness.Inbound.Count());
    }

    /// <summary>
    /// A batch with a hint that has no ID, an ID that is too long, or a null entry is refused whole.
    /// True: nothing of the batch is stored, since the sender matches answers by ID.
    /// False: part of a batch would be stored with answers the sender cannot match.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("too-long")]
    [InlineData("null-entry")]
    public void Queue_HintWithoutAUsableId_RefusesTheWholeBatch(string scenario)
    {
        var controller = PairedRoutes();
        var bad = scenario == "null-entry" ? null : Hint("x");
        if (bad is not null)
        {
            bad.HintId = scenario == "too-long" ? new string('a', HintTriage.MaxIdLength + 1) : scenario;
        }

        var result = controller.Queue(new QueueRequest { SenderServerId = PeerId, Items = { Hint("1"), bad! } });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, _harness.Inbound.Count());
    }

    /// <summary>
    /// A full batch of the largest size, and an empty one, are both answered 200 with one result per hint.
    /// True: the size limit holds at its edge, and an empty batch is harmless.
    /// False: the largest allowed batch would be refused, or an empty one would fail.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(HintProtocol.MaxHintsPerRequest)]
    public void Queue_BatchWithinTheLimit_AnswersOneResultPerHint(int count)
    {
        var controller = PairedRoutes();
        var request = new QueueRequest { SenderServerId = PeerId, Items = Enumerable.Range(0, count).Select(i => Hint(i.ToString())).ToList() };

        var result = controller.Queue(request);

        var body = Assert.IsType<QueueResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(request.Items.Select(h => h.HintId), body.Items.Select(r => r.HintId));
    }

    /// <summary>
    /// A completion batch is refused with 400 when it has no body or list, holds a null, or is too large, and by the sender check when the sender is unknown.
    /// True: only a proven sender's well formed completions settle rows.
    /// False: any caller could mark another server's hints as finished.
    /// </summary>
    [Theory]
    [InlineData("no-body", StatusCodes.Status400BadRequest)]
    [InlineData("no-items", StatusCodes.Status400BadRequest)]
    [InlineData("null-entry", StatusCodes.Status400BadRequest)]
    [InlineData("too-many", StatusCodes.Status400BadRequest)]
    [InlineData("unknown-sender", StatusCodes.Status409Conflict)]
    [InlineData("valid", StatusCodes.Status200OK)]
    public void Complete_Batch_IsTakenOnlyWhenWellFormedAndProven(string scenario, int status)
    {
        var controller = PairedRoutes();
        var request = scenario switch
        {
            "no-body" => null,
            "no-items" => new CompleteRequest { SenderServerId = PeerId, Items = null! },
            "null-entry" => new CompleteRequest { SenderServerId = PeerId, Items = { null! } },
            "too-many" => new CompleteRequest { SenderServerId = PeerId, Items = Enumerable.Range(0, HintProtocol.MaxHintsPerRequest + 1).Select(i => new CompletedHint { HintId = i.ToString() }).ToList() },
            "unknown-sender" => new CompleteRequest { SenderServerId = "ffffffffffffffffffffffffffffffff" },
            _ => new CompleteRequest { SenderServerId = PeerId, Items = { new CompletedHint { HintId = "never-sent" } } }
        };

        var result = controller.Complete(request!);

        var answer = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(status, answer.StatusCode);
        if (scenario == "valid")
        {
            Assert.Equal(0, answer.Value!.GetType().GetProperty("Removed")!.GetValue(answer.Value));
        }
    }

    /// <summary>
    /// Interest tells a server this one pulls from what to announce, and tells a Push only entry to announce nothing.
    /// True: a peer this server only sends to does not waste hints on it.
    /// False: every listed server would announce every change here, whichever way the entry points.
    /// </summary>
    [Theory]
    [InlineData(ServerMode.Sync, false)]
    [InlineData(ServerMode.Pull, false)]
    [InlineData(ServerMode.Push, true)]
    public void Interest_ByDirection_IsNoneForAnEntryThisServerDoesNotPull(ServerMode mode, bool none)
    {
        _harness.Config.Configuration.EnableMetadataSync = true;
        var controller = PairedRoutes(mode);

        var result = controller.Interest(PeerId);

        var body = Assert.IsType<PeerInterest>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(none, body.Revision == "none");
        Assert.Equal(none, body.Kinds.Count == 0);
    }

    /// <summary>
    /// Interest is refused to a sender that has not proven itself.
    /// True: only a paired server learns what this one wants announced.
    /// False: any caller could read which libraries and users this server syncs.
    /// </summary>
    [Fact]
    public void Interest_UnprovenSender_IsRefused()
    {
        _harness.AddPeer();

        var result = _harness.PeerRoutes("wrong").Interest(PeerId);

        Assert.Equal(HintProtocol.UnpairedStatus, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    /// <summary>
    /// A standard user's key whose user no longer exists reads no playlists, and the library is not asked.
    /// True: a key left behind by a removed user lists nothing.
    /// False: a key with no user would read every user's playlists.
    /// </summary>
    [Fact]
    public void Playlists_ScopedUserGone_AnswersNoneWithoutReadingTheLibrary()
    {
        var reader = new PeerPlaylistReader(_harness.Library, _harness.UserManager);

        var result = _harness.PeerRoutes(scopedUser: Own).Playlists(reader, id: null, serverId: PeerId);

        Assert.Empty(Assert.IsType<PeerPlaylistsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Playlists);
        Assert.DoesNotContain(_harness.LibraryStub.Calls, c => c.Name == nameof(ILibraryManager.GetItemList));
    }

    /// <summary>
    /// A full caller on a server with no playlists reads an empty list.
    /// True: the caller's playlist sync finds nothing to copy.
    /// False: an empty library would fail the caller's run.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(PeerId)]
    public void Playlists_NoPlaylistsHere_AnswersAnEmptyList(string? serverId)
    {
        _harness.AddPeer();
        _harness.LibraryStub.Answer(nameof(ILibraryManager.GetItemList), _ => new List<BaseItem>());
        var reader = new PeerPlaylistReader(_harness.Library, _harness.UserManager);

        var result = _harness.PeerRoutes().Playlists(reader, id: null, serverId: serverId);

        Assert.Empty(Assert.IsType<PeerPlaylistsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Playlists);
    }

    /// <summary>
    /// A lookup that names nothing, or whose fields are too long or too many, is refused with 400.
    /// True: a malformed lookup never reaches the library.
    /// False: an oversized lookup would run an unbounded query.
    /// </summary>
    [Theory]
    [InlineData("no-body")]
    [InlineData("empty")]
    [InlineData("blank-path")]
    [InlineData("path-too-long")]
    [InlineData("name-too-long")]
    [InlineData("type-too-long")]
    [InlineData("too-many-ids")]
    [InlineData("too-many-series-ids")]
    public void Lookup_MalformedRequest_Answers400(string scenario)
    {
        var ids = Enumerable.Range(0, PeerItemLookup.MaxProviderIds + 1).ToDictionary(i => "p" + i, i => "v");
        var request = scenario switch
        {
            "no-body" => null,
            "empty" => new PeerLookupRequest { ProviderIds = new(), SeriesProviderIds = new() },
            "blank-path" => new PeerLookupRequest { Path = " ", Name = " " },
            "path-too-long" => new PeerLookupRequest { Path = new string('a', 4097) },
            "name-too-long" => new PeerLookupRequest { Name = new string('a', 513) },
            "type-too-long" => new PeerLookupRequest { Name = "x", ItemType = new string('a', 129) },
            "too-many-ids" => new PeerLookupRequest { Name = "x", ProviderIds = ids },
            _ => new PeerLookupRequest { Name = "x", SeriesProviderIds = ids }
        };

        var result = _harness.PeerRoutes().Lookup(request!, _harness.UserManager, _harness.Library);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(_harness.LocalItemsStub.Calls);
    }

    /// <summary>
    /// A media segment read with no body, no ids, too many ids, or an id too long to be one is refused with 400.
    /// True: a malformed request reads nothing.
    /// False: a huge batch would read every file's segments in one request.
    /// </summary>
    [Theory]
    [InlineData("no-body")]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("too-long")]
    public async Task Segments_MalformedRequest_Answers400(string scenario)
    {
        var request = scenario switch
        {
            "no-body" => null,
            "empty" => new PeerSegmentsRequest(),
            "too-many" => new PeerSegmentsRequest { ItemIds = Enumerable.Range(0, PeerSegmentsRequest.MaxItems + 1).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList() },
            _ => new PeerSegmentsRequest { ItemIds = { new string('a', 65) } }
        };
        var (segments, segmentsStub) = InterfaceStub.Create<MediaBrowser.Controller.MediaSegments.IMediaSegmentManager>();

        var result = await _harness.PeerRoutes().Segments(request!, new Jellyfin.Plugin.ServerSync.Services.Peer.PeerSegmentReader(_harness.Library, _harness.UserManager, segments, new Jellyfin.Plugin.ServerSync.Services.MetadataSync.MediaSegmentStore(_harness.Provider, Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.ServerSync.Services.MetadataSync.MediaSegmentStore>.Instance)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(segmentsStub.Calls);
    }

    /// <summary>
    /// A media segment read of the most ids allowed is answered, one entry for each id.
    /// True: the limit holds at its edge, and an item this server lacks is a plain answer.
    /// False: a full batch would be refused, or a miss would fail the batch.
    /// </summary>
    [Fact]
    public async Task Segments_FullBatch_AnswersEachId()
    {
        var ids = Enumerable.Range(0, PeerSegmentsRequest.MaxItems).Select(_ => Guid.NewGuid().ToString("N")).ToList();
        var segments = InterfaceStub.Create<MediaBrowser.Controller.MediaSegments.IMediaSegmentManager>().Proxy;

        var result = await _harness.PeerRoutes().Segments(new PeerSegmentsRequest { ItemIds = ids }, new Jellyfin.Plugin.ServerSync.Services.Peer.PeerSegmentReader(_harness.Library, _harness.UserManager, segments, new Jellyfin.Plugin.ServerSync.Services.MetadataSync.MediaSegmentStore(_harness.Provider, Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.ServerSync.Services.MetadataSync.MediaSegmentStore>.Instance)));

        var body = Assert.IsType<PeerSegmentsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(ids, body.Items.Select(i => i.ItemId));
        Assert.All(body.Items, i => Assert.False(i.Found));
    }

    /// <summary>
    /// A lookup at the longest allowed path is answered, and a path this server does not hold is not found.
    /// True: the length limit holds at its edge, and a miss is a plain answer.
    /// False: a long but valid path would be refused, or a miss would be an error.
    /// </summary>
    [Fact]
    public void Lookup_PathNotHere_AnswersNotFound()
    {
        var result = _harness.PeerRoutes().Lookup(new PeerLookupRequest { Path = new string('a', 4096) }, _harness.UserManager, _harness.Library);

        var body = Assert.IsType<PeerLookupResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((false, null), (body.Found, body.ItemId));
    }

    /// <summary>
    /// A full caller is told the item a path names, and a standard user's key whose user is gone is told nothing.
    /// True: a scoped key cannot learn that an item exists unless its user can see it.
    /// False: a standard user's key could probe the whole library by path.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Lookup_ItemFound_IsToldOnlyToACallerThatMaySeeIt(bool scoped, bool found)
    {
        _harness.LocalItemsStub.Answer(nameof(ILocalItems.FindByPath), _ => new Movie { Id = ItemId, Path = "/m/Film.mkv" });

        var result = _harness.PeerRoutes(scopedUser: scoped ? Own : null).Lookup(new PeerLookupRequest { Path = "/m/Film.mkv" }, _harness.UserManager, _harness.Library);

        var body = Assert.IsType<PeerLookupResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((found, found ? ItemId.ToString("N") : null, found ? "Movie" : null), (body.Found, body.ItemId, body.ItemType));
    }

    /// <summary>
    /// A Stamp request without a body or a list, with a null key, or with too many keys is refused with 400.
    /// True: a malformed request reads nothing.
    /// False: a huge key list would be run against the table.
    /// </summary>
    [Theory]
    [InlineData("no-body")]
    [InlineData("no-keys")]
    [InlineData("null-key")]
    [InlineData("too-many")]
    public void Versions_MalformedRequest_Answers400(string scenario)
    {
        var request = scenario switch
        {
            "no-body" => null,
            "no-keys" => new VersionsRequest { Keys = null! },
            "null-key" => new VersionsRequest { Keys = { "a", null! } },
            _ => new VersionsRequest { Keys = Enumerable.Range(0, HintProtocol.MaxHintsPerRequest + 1).Select(i => i.ToString()).ToList() }
        };

        var result = _harness.PeerRoutes().Versions(request!);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// A full caller reads the Stamp of each key that has one, in the kind it asked for.
    /// True: keys with no Stamp, and Stamps of another kind, are left out.
    /// False: a caller would settle a conflict against the wrong object's Stamp.
    /// </summary>
    [Fact]
    public void Versions_FullCaller_ReadsTheStampsOfTheKeysThatHaveOne()
    {
        Stamp(HintKind.Metadata, "a");
        Stamp(HintKind.Metadata, "b");
        Stamp(HintKind.People, "c");

        var result = _harness.PeerRoutes().Versions(new VersionsRequest { Kind = HintKind.Metadata, Keys = { "a", "c", "missing" } });

        var body = Assert.IsType<VersionsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("a", Assert.Single(body.Items).Key);
    }

    /// <summary>
    /// A standard user's key reads History and Users Stamps for its own user only, and other kinds as asked.
    /// True: a scoped key cannot learn when another user watched or changed anything.
    /// False: a standard user's key could read every user's activity times.
    /// </summary>
    [Theory]
    [InlineData(HintKind.History, 1)]
    [InlineData(HintKind.Users, 1)]
    [InlineData(HintKind.Metadata, 2)]
    public void Versions_ScopedCaller_ReadsOnlyItsOwnUsersStamps(HintKind kind, int expected)
    {
        var (own, other) = kind switch
        {
            HintKind.History => (HintProtocol.HistoryKey(Own, ItemId), HintProtocol.HistoryKey(Other, ItemId)),
            HintKind.Users => (HintProtocol.UsersKey(Own), HintProtocol.UsersKey(Other)),
            _ => ("a", "b")
        };
        Stamp(kind, own);
        Stamp(kind, other);

        var result = _harness.PeerRoutes(scopedUser: Own).Versions(new VersionsRequest { Kind = kind, Keys = { own, other, "not-a-key" } });

        var body = Assert.IsType<VersionsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(expected, body.Items.Count);
        Assert.Contains(body.Items, v => v.Key == own);
        Assert.Equal(expected == 2, body.Items.Any(v => v.Key == other));
    }

    /// <summary>
    /// A History batch without a body or a list, with a null entry, or with too many entries is refused with 400.
    /// True: a malformed batch writes nothing.
    /// False: an oversized batch would be negotiated in full.
    /// </summary>
    [Theory]
    [InlineData("no-body")]
    [InlineData("no-items")]
    [InlineData("null-entry")]
    [InlineData("too-many")]
    public void NegotiateHistory_MalformedBatch_Answers400(string scenario)
    {
        var request = scenario switch
        {
            "no-body" => null,
            "no-items" => new PeerHistoryRequest { Items = null! },
            "null-entry" => new PeerHistoryRequest { Items = { null! } },
            _ => new PeerHistoryRequest { Items = Enumerable.Range(0, PeerHistoryNegotiator.MaxEntriesPerRequest + 1).Select(_ => Entry(Own)).ToList() }
        };

        var result = _harness.PeerRoutes().NegotiateHistory(request!);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// A sender this server lists must prove itself, and one it does not list is answered without proof.
    /// True: a listed server's rows cannot be written by a caller using its ID, while a one way caller still negotiates.
    /// False: a caller could write History as a listed server, or a server that only pulls could never negotiate.
    /// </summary>
    [Theory]
    [InlineData(PeerId, "wrong", HintProtocol.UnpairedStatus)]
    [InlineData(PeerId, Secret, StatusCodes.Status200OK)]
    [InlineData("ffffffffffffffffffffffffffffffff", null, StatusCodes.Status200OK)]
    [InlineData("", null, StatusCodes.Status200OK)]
    public void NegotiateHistory_Sender_MustProveItselfOnlyWhenListed(string senderId, string? presented, int status)
    {
        var entry = _harness.AddPeer();
        _harness.Pairings.SetInbound(entry.Key, Secret);

        var result = _harness.PeerRoutes(presented).NegotiateHistory(new PeerHistoryRequest { SenderServerId = senderId, Items = { Entry(Own) } });

        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
    }

    /// <summary>
    /// A standard user's key gets one answer per entry in order, and every entry for another user is failed with the reason.
    /// True: the key negotiates only its own user's history, and the caller can still match answers by position.
    /// False: a standard user's key could write another user's watch history, or the answers would shift out of order.
    /// </summary>
    [Fact]
    public void NegotiateHistory_ScopedCaller_FailsEveryEntryForAnotherUser()
    {
        var request = new PeerHistoryRequest { Items = { Entry(Other), Entry(Own), Entry(Other), new PeerHistoryEntry { UserId = "not-an-id", ItemId = "x" } } };

        var result = _harness.PeerRoutes(scopedUser: Own).NegotiateHistory(request);

        var body = Assert.IsType<PeerHistoryResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { Other.ToString("N"), Own.ToString("N"), Other.ToString("N"), "not-an-id" }, body.Items.Select(i => i.UserId).ToArray());
        Assert.All(new[] { body.Items[0], body.Items[2], body.Items[3] }, i => Assert.Equal((PeerHistoryOutcome.Failed, "a standard user may only negotiate its own history"), (i.Outcome, i.Reason)));
        Assert.NotEqual("a standard user may only negotiate its own history", body.Items[1].Reason);
    }

    /// <summary>
    /// Every peer route sits behind a signed in caller, the Whitelist guard, and a body size limit, and none allows an anonymous caller.
    /// True: no route under Sync/Peer can be reached without a token and a place on the Whitelist.
    /// False: a peer route would be open to any caller that can reach the server.
    /// </summary>
    [Fact]
    public void Controller_Always_CarriesTheSignInAndWhitelistGuards()
    {
        var controller = typeof(PeerController);

        Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(controller.GetCustomAttribute<PeerCallerGuardAttribute>());
        Assert.NotNull(controller.GetCustomAttribute<ApiControllerAttribute>());
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("Sync/Peer", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.NotNull(controller.GetCustomAttribute<RequestSizeLimitAttribute>());
        Assert.Equal(4L * 1024 * 1024, PeerController.MaxRequestBytes);
    }

    /// <summary>
    /// Each route answers its own verb and path, and every route but Capabilities checks the caller's protocol.
    /// True: a peer on another protocol can still read Capabilities to learn why, and nothing else.
    /// False: a mismatched peer would exchange data this server cannot read, or could not learn the mismatch.
    /// </summary>
    [Theory]
    [InlineData(nameof(PeerController.GetCapabilities), "GET", "Capabilities", false)]
    [InlineData(nameof(PeerController.Link), "GET", "Link", true)]
    [InlineData(nameof(PeerController.Queue), "POST", "Queue", true)]
    [InlineData(nameof(PeerController.Complete), "POST", "Complete", true)]
    [InlineData(nameof(PeerController.Status), "GET", "Status", true)]
    [InlineData(nameof(PeerController.Interest), "GET", "Interest", true)]
    [InlineData(nameof(PeerController.Playlists), "GET", "Playlists", true)]
    [InlineData(nameof(PeerController.Lookup), "POST", "Lookup", true)]
    [InlineData(nameof(PeerController.Versions), "POST", "Versions", true)]
    [InlineData(nameof(PeerController.NegotiateHistory), "POST", "History", true)]
    public void Route_CarriesItsVerbPathAndProtocolCheck(string action, string verb, string template, bool checksProtocol)
    {
        var method = ControllerRouteAssert.Route(typeof(PeerController), action, verb, template);

        Assert.Equal(checksProtocol, method.GetCustomAttribute<RequireCompatibleProtocolAttribute>() is not null);
    }

    private static SyncHint Hint(string id) => new()
    {
        HintId = PeerId + ":" + id,
        OriginServerId = PeerId,
        Kind = HintKind.Metadata,
        Key = "key-" + id,
        ItemPath = "/elsewhere/" + id + ".mkv",
        VersionServerId = PeerId,
        VersionTimestamp = DateTime.UtcNow.AddMinutes(-1)
    };

    private static PeerHistoryEntry Entry(Guid user) => new() { UserId = user.ToString("N"), ItemId = ItemId.ToString("N") };

    private async Task<PeerLinkResponse> Link(string? serverId)
    {
        var result = await _harness.PeerRoutes().Link(serverId, CancellationToken.None);
        return Assert.IsType<PeerLinkResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private PeerController PairedRoutes(ServerMode mode = ServerMode.Sync)
    {
        var entry = _harness.AddPeer(mode);
        _harness.Pairings.SetInbound(entry.Key, Secret);
        return _harness.PeerRoutes(Secret);
    }

    private void Stamp(HintKind kind, string key)
        => _harness.Versions.Set(new ObjectVersion { Kind = kind, Key = key, ServerId = PeerId, Timestamp = DateTime.UtcNow });

    private void Receive(string origin, string id)
        => _harness.Inbound.Enqueue(new InboundHint { HintId = origin + ":" + id, OriginServerId = origin, Kind = HintKind.Metadata, Key = "key-" + id, VersionServerId = origin, VersionTimestamp = DateTime.UtcNow.AddMinutes(-1), ReceivedAt = DateTime.UtcNow, NextAttempt = DateTime.UtcNow });
}

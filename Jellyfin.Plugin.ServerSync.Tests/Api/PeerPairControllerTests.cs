using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.ServerSync.Api;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Api;

/// <summary>
/// The pairing route stores a secret only for a listed server this server is asking to pair, and refuses every other offer.
/// </summary>
public sealed class PeerPairControllerTests : IDisposable
{
    private readonly ControllerTestHarness _harness = new();

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// A request with no body, no server ID, or no secret is malformed.
    /// True: the caller hears 400 and nothing is stored.
    /// False: a blank request would reach the store, or throw.
    /// </summary>
    [Theory]
    [InlineData(false, null, null)]
    [InlineData(true, null, "secret")]
    [InlineData(true, "", "secret")]
    [InlineData(true, "   ", "secret")]
    [InlineData(true, ControllerTestHarness.PeerId, null)]
    [InlineData(true, ControllerTestHarness.PeerId, "")]
    public void Pair_MissingBodyServerOrSecret_Answers400(bool hasBody, string? serverId, string? secret)
    {
        var peer = _harness.AddPeer();
        using var asking = _harness.Pairings.Expect(peer.Key);
        var request = hasBody ? new PairRequest { ServerId = serverId!, Secret = secret == "secret" ? PeerPairingStore.NewSecret() : secret! } : null;

        var result = Controller().Pair(request!);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(_harness.Pairings.GetOutbound(peer.Key));
    }

    /// <summary>
    /// A secret at the longest length is taken, and one character more is refused.
    /// True: the length limit holds at its edge.
    /// False: an oversized secret would be stored, or a full length one turned away.
    /// </summary>
    [Theory]
    [InlineData(32, true)]
    [InlineData(256, true)]
    [InlineData(31, false)]
    [InlineData(257, false)]
    public void Pair_SecretAtTheLengthLimits_IsTakenOnlyInsideThem(int length, bool taken)
    {
        var peer = _harness.AddPeer();
        using var asking = _harness.Pairings.Expect(peer.Key);
        var secret = new string('a', length);

        var result = Controller().Pair(new PairRequest { ServerId = ControllerTestHarness.PeerId, Secret = secret });

        Assert.Equal(taken, result.Result is OkObjectResult);
        Assert.Equal(taken ? secret : null, _harness.Pairings.GetOutbound(peer.Key));
    }

    /// <summary>
    /// An accepted offer answers that the two are paired.
    /// True: the offering server reads Paired as true and starts presenting the secret.
    /// False: the offering server could not tell a stored secret from a dropped one.
    /// </summary>
    [Fact]
    public void Pair_AskedForByAListedServer_AnswersPaired()
    {
        var peer = _harness.AddPeer();
        using var asking = _harness.Pairings.Expect(peer.Key);

        var result = Controller().Pair(new PairRequest { ServerId = ControllerTestHarness.PeerId.ToUpperInvariant(), Secret = PeerPairingStore.NewSecret() });

        var body = Assert.IsType<OkObjectResult>(result.Result).Value!;
        Assert.Equal(true, body.GetType().GetProperty("Paired")!.GetValue(body));
    }

    /// <summary>
    /// A second offer while this server is still asking replaces the first secret.
    /// True: the newest secret the peer issued is the one this server presents.
    /// False: a retried pairing would leave this server presenting a secret the peer dropped.
    /// </summary>
    [Fact]
    public void Pair_OfferedTwiceWhileAsking_KeepsTheNewestSecret()
    {
        var peer = _harness.AddPeer();
        using var asking = _harness.Pairings.Expect(peer.Key);
        var first = PeerPairingStore.NewSecret();
        var second = PeerPairingStore.NewSecret();
        var controller = Controller();

        controller.Pair(new PairRequest { ServerId = ControllerTestHarness.PeerId, Secret = first });
        controller.Pair(new PairRequest { ServerId = ControllerTestHarness.PeerId, Secret = second });

        Assert.Equal(second, _harness.Pairings.GetOutbound(peer.Key));
    }

    /// <summary>
    /// A wait opened for one entry does not let another entry's offer in.
    /// True: only the server this one is checking can hand it a secret.
    /// False: any listed server could replace another entry's secret while a check runs.
    /// </summary>
    [Fact]
    public void Pair_WhileAskingAnotherEntry_Answers409AndStoresNothing()
    {
        var peer = _harness.AddPeer();
        _harness.Config.Configuration.Servers.Add(new SourceServer { Key = "other", Name = "Other", Url = "http://127.0.0.2:8096", ApiKey = "k", ServerId = "1111111111111111aaaaaaaaaaaaaaaa" });
        using var asking = _harness.Pairings.Expect("other");

        var result = Controller().Pair(new PairRequest { ServerId = ControllerTestHarness.PeerId, Secret = PeerPairingStore.NewSecret() });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Null(_harness.Pairings.GetOutbound(peer.Key));
    }

    /// <summary>
    /// The controller refuses to be built without its parts.
    /// True: a missing service is named when the controller is built.
    /// False: the first pairing would fail with a null reference.
    /// </summary>
    [Fact]
    public void Constructor_MissingArgument_Throws()
    {
        var logger = NullLogger<PeerPairController>.Instance;

        Assert.Throws<ArgumentNullException>(() => new PeerPairController(null!, _harness.Pairings, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerPairController(_harness.Config, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new PeerPairController(_harness.Config, _harness.Pairings, null!));
    }

    /// <summary>
    /// The pairing route is a POST under Sync/Peer behind the Whitelist guard, the protocol check, and a small body limit.
    /// True: only a signed in caller on the Whitelist that speaks this protocol can offer a secret, in a body of at most 64 KiB.
    /// False: an anonymous or unlisted caller could offer a secret, or post a body of any size.
    /// </summary>
    [Fact]
    public void Pair_Route_CarriesItsGuards()
    {
        var controller = typeof(PeerPairController);
        var action = controller.GetMethod(nameof(PeerPairController.Pair))!;

        Assert.NotNull(controller.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        Assert.NotNull(controller.GetCustomAttribute<PeerCallerGuardAttribute>());
        Assert.NotNull(controller.GetCustomAttribute<ApiControllerAttribute>());
        Assert.Equal("Sync/Peer", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.NotNull(controller.GetCustomAttribute<RequestSizeLimitAttribute>());
        Assert.NotNull(action.GetCustomAttribute<RequireCompatibleProtocolAttribute>());
        Assert.Equal("Pair", action.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal(new[] { "POST" }, action.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).ToArray());
    }

    private PeerPairController Controller() => new(_harness.Config, _harness.Pairings, NullLogger<PeerPairController>.Instance);
}

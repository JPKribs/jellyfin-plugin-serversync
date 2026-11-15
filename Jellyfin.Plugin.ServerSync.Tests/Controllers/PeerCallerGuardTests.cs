using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Peer;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The guard runs a peer route only for a caller the Whitelist allows, and answers every other caller with a 403 that names why.
/// </summary>
public sealed class PeerCallerGuardTests
{
    private const string GrantedUser = "0f8fad5bd9cb469fa16570867728950e";
    private const string OtherUser = "7c9e6679742540de944be07fc1f90ae7";
    private const string PeerAddress = "198.51.100.7";

    /// <summary>
    /// An administrator calling from a listed server's address reaches the action.
    /// True: the action runs, no refusal is set, and no user scope is attached.
    /// False: a listed peer would be refused, or held to one user's data.
    /// </summary>
    [Fact]
    public async Task OnActionExecutionAsync_AdministratorFromAListedAddress_RunsTheAction()
    {
        var context = Context(PeerAddress, Principal("Administrator", OtherUser));
        var ran = false;

        await Guard(Listed()).OnActionExecutionAsync(context, Next(() => ran = true));

        Assert.True(ran);
        Assert.Null(context.Result);
        Assert.Null(PeerCallerGuard.ScopedUser(context.HttpContext));
    }

    /// <summary>
    /// An administrator calling from an address no entry names is answered 403 with the refusal code and reason.
    /// True: the action never runs and the body tells the caller it is not on the Whitelist.
    /// False: any signed in administrator on any machine could use the peer routes.
    /// </summary>
    [Fact]
    public async Task OnActionExecutionAsync_AddressNotListed_Answers403WithTheCode()
    {
        var context = Context("203.0.113.9", Principal("Administrator", OtherUser));
        var ran = false;

        await Guard(Listed()).OnActionExecutionAsync(context, Next(() => ran = true));

        var result = Assert.IsType<ObjectResult>(context.Result);
        var refusal = Assert.IsType<PeerCallerRefusal>(result.Value);
        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(nameof(PeerCallerCode.IpNotWhitelisted), refusal.Code);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
    }

    /// <summary>
    /// A standard user without a grant is answered 403 even from a listed address.
    /// True: the action never runs and the code names the user as the reason.
    /// False: any account on a listed server could read through the peer routes.
    /// </summary>
    [Fact]
    public async Task OnActionExecutionAsync_StandardUserWithoutAGrant_Answers403()
    {
        var context = Context(PeerAddress, Principal("User", GrantedUser));
        var ran = false;

        await Guard(Listed()).OnActionExecutionAsync(context, Next(() => ran = true));

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(nameof(PeerCallerCode.UserNotWhitelisted), Assert.IsType<PeerCallerRefusal>(result.Value).Code);
    }

    /// <summary>
    /// A granted standard user reaches the action with its own ID attached to the request.
    /// True: the action runs and reads the user it must hold the call to.
    /// False: a standard user's key would read every user's data.
    /// </summary>
    [Fact]
    public async Task OnActionExecutionAsync_GrantedStandardUser_RunsTheActionScopedToThatUser()
    {
        var config = Listed();
        config.PeerCallerGrants.Add(new PeerCallerGrant { UserId = GrantedUser });
        var context = Context("203.0.113.9", Principal("User", GrantedUser));
        var ran = false;

        await Guard(config).OnActionExecutionAsync(context, Next(() => ran = true));

        Assert.True(ran);
        Assert.Null(context.Result);
        Assert.Equal(Guid.Parse(GrantedUser), PeerCallerGuard.ScopedUser(context.HttpContext));
    }

    /// <summary>
    /// The scoped user reads as null when the guard set none, when the item is not an ID, and when there is no request.
    /// True: only an ID the guard attached scopes a call.
    /// False: a stray request item could scope a call to a user, or a missing request would throw.
    /// </summary>
    [Fact]
    public void ScopedUser_NoneSetWrongTypeOrNoRequest_IsNull()
    {
        var unset = new DefaultHttpContext();
        var wrongType = new DefaultHttpContext();
        wrongType.Items[PeerCallerGuard.ScopedUserKey] = GrantedUser;

        Assert.Null(PeerCallerGuard.ScopedUser(unset));
        Assert.Null(PeerCallerGuard.ScopedUser(wrongType));
        Assert.Null(PeerCallerGuard.ScopedUser(null!));
    }

    /// <summary>
    /// The guard refuses to be built or run without its parts.
    /// True: a missing policy, logger, context, or action is named at once.
    /// False: a guard without a policy would fail on the first peer call instead of at start.
    /// </summary>
    [Fact]
    public async Task Guard_MissingArgument_Throws()
    {
        var policy = new PeerCallerPolicy(new FakeConfigManager(), NullLogger<PeerCallerPolicy>.Instance);
        var guard = new PeerCallerGuard(policy, NullLogger<PeerCallerGuard>.Instance);
        var context = Context(PeerAddress, Principal("Administrator", OtherUser));

        Assert.Throws<ArgumentNullException>(() => new PeerCallerGuard(null!, NullLogger<PeerCallerGuard>.Instance));
        Assert.Throws<ArgumentNullException>(() => new PeerCallerGuard(policy, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => guard.OnActionExecutionAsync(null!, Next(() => { })));
        await Assert.ThrowsAsync<ArgumentNullException>(() => guard.OnActionExecutionAsync(context, null!));
    }

    private static PeerCallerGuard Guard(PluginConfiguration config)
        => new(new PeerCallerPolicy(new FakeConfigManager(config), NullLogger<PeerCallerPolicy>.Instance), NullLogger<PeerCallerGuard>.Instance);

    private static PluginConfiguration Listed() => new()
    {
        Servers = new List<SourceServer> { new() { Name = "peer", Url = "http://" + PeerAddress + ":8096", ApiKey = "k" } }
    };

    private static ClaimsPrincipal Principal(string role, string userId)
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "caller"),
                new Claim(ClaimTypes.Role, role),
                new Claim("Jellyfin-UserId", userId),
                new Claim("Jellyfin-IsApiKey", bool.FalseString)
            },
            "Custom"));

    private static ActionExecutingContext Context(string remote, ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user };
        http.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        http.Request.Path = "/Sync/Peer/Status";
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
    }

    private static ActionExecutionDelegate Next(Action ran) => () =>
    {
        ran();
        return Task.FromResult<ActionExecutedContext>(null!);
    };
}

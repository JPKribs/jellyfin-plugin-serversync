using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// A link stream result must stream the link for the user it was granted to, and answer with the streamer's status when nothing was written.
/// </summary>
public sealed class LinkStreamResultTests
{
    private static readonly LinkEntry Link = new("/media/Film.strm", "/media/Film.mkv", "aaaa", "gone-server", 100);

    /// <summary>
    /// The result needs its streamer, link, and grant, and a context to run in.
    /// True: a missing part shows when the result is built.
    /// False: the failure would surface mid response, after headers were sent.
    /// </summary>
    [Fact]
    public async Task Members_NullArguments_Throw()
    {
        var streamer = Streamer(out _);
        var grant = new LinkGrant(Guid.NewGuid(), false);

        Assert.Throws<ArgumentNullException>(() => new LinkStreamResult(null!, Link, grant));
        Assert.Throws<ArgumentNullException>(() => new LinkStreamResult(streamer, null!, grant));
        Assert.Throws<ArgumentNullException>(() => new LinkStreamResult(streamer, Link, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => new LinkStreamResult(streamer, Link, grant).ExecuteResultAsync(null!));
    }

    /// <summary>
    /// The result exposes the grant it was built with.
    /// True: what is streamed can be traced to the user and route it was allowed for.
    /// False: a direct play could not be checked against its own user.
    /// </summary>
    [Fact]
    public void Grant_IsTheOneGiven()
    {
        var grant = new LinkGrant(Guid.NewGuid(), false, new LinkRoute("b", "bbbb"));

        var result = new LinkStreamResult(Streamer(out _), Link, grant);

        Assert.Same(grant, result.Grant);
    }

    /// <summary>
    /// When no server can stream the link, the response takes the streamer's failure status, and other servers were asked for the granted user.
    /// True: the player sees a clear failure for a link whose server is gone.
    /// False: the response would go out as an empty 200 and the player would treat it as a finished film.
    /// </summary>
    [Fact]
    public async Task ExecuteResultAsync_NothingStreamed_SetsTheFailureStatus()
    {
        var user = Guid.NewGuid();
        var streamer = Streamer(out var access);
        var http = new DefaultHttpContext();
        var context = new ActionContext(http, new RouteData(), new ActionDescriptor());

        await new LinkStreamResult(streamer, Link, new LinkGrant(user, false)).ExecuteResultAsync(context);

        var asked = Assert.Single(access.Calls, c => c.Name == nameof(ILinkAccess.ChooseAsync)).Args;
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
        Assert.Same(Link, asked[0]);
        Assert.Equal(user, asked[1]);
    }

    private static LinkStreamer Streamer(out InterfaceStub access)
    {
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "this-server");
        var (chooser, chooserStub) = InterfaceStub.Create<ILinkAccess>();
        chooserStub.Answer(nameof(ILinkAccess.ChooseAsync), _ => Task.FromResult(new LinkChoice(null, "no server holds the file")));
        access = chooserStub;
        return new LinkStreamer(new FakeConfigManager(), new FakeClientFactory(), host, chooser, new LinkGrants(), new LinkHealth(), NullLogger<LinkStreamer>.Instance, Timeout.InfiniteTimeSpan);
    }
}

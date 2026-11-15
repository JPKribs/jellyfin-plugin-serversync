using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// The handler must stop at every redirect and name where it pointed, so credentials never follow one to another host.
/// </summary>
public sealed class RedirectRefusingHandlerTests
{
    /// <summary>
    /// Every redirect status with a location is refused, and the error carries that status.
    /// True: a permanent, temporary, or method keeping redirect are all stopped.
    /// False: a 307 or 308, which resends the body and headers, would carry the API Key to the new host.
    /// </summary>
    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(399)]
    public async Task SendAsync_AnyRedirectStatus_Throws(int status)
    {
        using var invoker = Invoker(_ => Redirect(status, "https://elsewhere.example/landing"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://peer.example/Sync/Peer/Queue");

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));

        Assert.Equal((HttpStatusCode)status, failure.StatusCode);
    }

    /// <summary>
    /// A relative location is resolved against the request, and the message names both ends and how to fix it.
    /// True: the operator reads the address to put in the Server URL.
    /// False: a relative redirect would be reported as a bare path that names no server.
    /// </summary>
    [Fact]
    public async Task SendAsync_RelativeLocation_IsResolvedAgainstTheRequest()
    {
        using var invoker = Invoker(_ => Redirect(302, "/jellyfin/System/Info?token=abc"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://peer.example:8920/System/Info");

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));

        Assert.Contains("The server at https://peer.example:8920 redirected to https://peer.example:8920/jellyfin/System/Info.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token=abc", failure.Message, StringComparison.Ordinal);
        Assert.Contains("set the server URL to the address it redirects to", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer that is not a redirect, or a 3xx without a location, is passed through as it is.
    /// True: a 304, a 400, and a success all reach the caller.
    /// False: ordinary answers would be turned into redirect errors.
    /// </summary>
    [Theory]
    [InlineData(200, false)]
    [InlineData(299, true)]
    [InlineData(302, false)]
    [InlineData(400, true)]
    [InlineData(500, true)]
    public async Task SendAsync_NotARedirectToFollow_PassesTheAnswerThrough(int status, bool withLocation)
    {
        using var invoker = Invoker(_ => withLocation ? Redirect(status, "https://elsewhere.example/") : new HttpResponseMessage((HttpStatusCode)status));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://peer.example/");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
    }

    /// <summary>
    /// A refused redirect is sent once, and nothing goes to the address it pointed to.
    /// True: the other host never sees the request.
    /// False: the handler would follow the redirect itself before refusing it.
    /// </summary>
    [Fact]
    public async Task SendAsync_Redirect_SendsNothingFurther()
    {
        var inner = new StubHandler(_ => Redirect(302, "https://elsewhere.example/"));
        using var handler = new RedirectRefusingHandler { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://peer.example/");

        await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));

        Assert.Equal(1, inner.Requests);
    }

    private static HttpMessageInvoker Invoker(Func<HttpRequestMessage, HttpResponseMessage> answer)
        => new(new RedirectRefusingHandler { InnerHandler = new StubHandler(answer) });

    private static HttpResponseMessage Redirect(int status, string location)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }
}

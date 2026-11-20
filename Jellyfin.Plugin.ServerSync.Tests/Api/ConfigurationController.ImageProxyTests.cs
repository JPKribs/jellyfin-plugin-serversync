using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Api;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Api;

/// <summary>
/// The image proxy hands the dashboard nothing but a small image from a configured source, and answers 404 for everything else.
/// </summary>
public sealed class ConfigurationControllerImageProxyTests : IDisposable
{
    private static readonly Guid ItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

    private readonly ControllerTestHarness _harness = new();
    private readonly List<string> _requests = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationControllerImageProxyTests"/> class, with a peer that answers every request with a small PNG.
    /// </summary>
    public ConfigurationControllerImageProxyTests()
    {
        _harness.Peer = request =>
        {
            _requests.Add(request.RequestUri!.PathAndQuery);
            return Image(Png, "image/png");
        };
    }

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// An image from the source is passed on with its type, a private cache header, and sniffing off.
    /// True: the dashboard shows the poster, and the browser can neither share it nor guess another type.
    /// False: posters would not show, or a proxied body could be run as something else.
    /// </summary>
    [Fact]
    public async Task GetSourceImage_SourceSendsAnImage_PassesItOn()
    {
        _harness.AddPeer();
        var controller = _harness.Configuration();

        var result = await controller.GetSourceImage(ItemId);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(Png, file.FileContents);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal("private, max-age=300", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
    }

    /// <summary>
    /// The request goes to the item's or the user's primary image, with the height held between 1 and 600.
    /// True: a user's avatar and an item's poster each come from their own path, at a thumbnail's size.
    /// False: avatars would load posters, or a caller could ask the source for a full size image.
    /// </summary>
    [Theory]
    [InlineData(false, 80, "/Items/11111111-1111-1111-1111-111111111111/Images/Primary?maxHeight=80")]
    [InlineData(true, 80, "/Users/11111111-1111-1111-1111-111111111111/Images/Primary?maxHeight=80")]
    [InlineData(false, 5000, "/Items/11111111-1111-1111-1111-111111111111/Images/Primary?maxHeight=600")]
    [InlineData(false, 0, "/Items/11111111-1111-1111-1111-111111111111/Images/Primary?maxHeight=1")]
    [InlineData(false, int.MinValue, "/Items/11111111-1111-1111-1111-111111111111/Images/Primary?maxHeight=1")]
    public async Task GetSourceImage_UserFlagAndHeight_ShapeTheRequest(bool user, int maxHeight, string expected)
    {
        _harness.AddPeer();

        await _harness.Configuration().GetSourceImage(ItemId, user, maxHeight);

        Assert.Equal(new[] { expected }, _requests);
    }

    /// <summary>
    /// The image comes from the entry the key names, and from the first source when no key is given.
    /// True: a row's image is fetched from the server the row came from.
    /// False: every image would be asked of the first server, and most would be missing.
    /// </summary>
    [Theory]
    [InlineData(null, "first")]
    [InlineData("second", "second")]
    [InlineData("SECOND", "second")]
    [InlineData("no-such-entry", "first")]
    public async Task GetSourceImage_ServerKey_PicksTheEntry(string? serverKey, string expectedKey)
    {
        _harness.AddPeer(key: "first");
        _harness.AddPeer(key: "second");

        await _harness.Configuration().GetSourceImage(ItemId, serverKey: serverKey);

        Assert.Equal(expectedKey, Assert.Single(_harness.Clients.Created).Key);
    }

    /// <summary>
    /// No source, an entry that is not set up, or an entry whose URL the SSRF guard refuses answers 404 without a call.
    /// True: the proxy only ever calls an address the guard passes.
    /// False: the image route could be used to reach an address this server must not call.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("no-key")]
    [InlineData("link-local")]
    [InlineData("plain-http-public")]
    [InlineData("push-only")]
    public async Task GetSourceImage_NoUsableSource_Answers404WithoutCalling(string scenario)
    {
        if (scenario != "none")
        {
            var entry = _harness.AddPeer(scenario == "push-only" ? ServerMode.Push : ServerMode.Pull);
            entry.ApiKey = scenario == "no-key" ? string.Empty : "key";
            entry.Url = scenario switch { "link-local" => "http://169.254.169.254", "plain-http-public" => "http://203.0.113.10:8096", _ => entry.Url };
        }

        var result = await _harness.Configuration().GetSourceImage(ItemId);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_harness.Clients.Created);
        Assert.Empty(_requests);
    }

    /// <summary>
    /// A body that is not an image answers 404, whatever the source says it is.
    /// True: HTML, a script, or a body with no type is never served from this server's origin.
    /// False: a source could run a page in the dashboard's origin through the proxy.
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public async Task GetSourceImage_BodyIsNotAnImage_Answers404(string? contentType)
    {
        _harness.AddPeer();
        _harness.Peer = _ => Image(Png, contentType);

        var result = await _harness.Configuration().GetSourceImage(ItemId);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// An image of exactly the largest size is passed on, and one byte more answers 404.
    /// True: the size cap holds at its edge.
    /// False: a source could make this server buffer a body of any size.
    /// </summary>
    [Theory]
    [InlineData(8 * 1024 * 1024, true)]
    [InlineData((8 * 1024 * 1024) + 1, false)]
    public async Task GetSourceImage_AtTheSizeCap_IsServedOnlyUpToIt(int bytes, bool served)
    {
        _harness.AddPeer();
        _harness.Peer = _ => Image(new byte[bytes], "image/jpeg");

        var result = await _harness.Configuration().GetSourceImage(ItemId);

        Assert.Equal(served, result is FileContentResult);
        Assert.Equal(!served, result is NotFoundResult);
    }

    /// <summary>
    /// A source that answers with an error, or cannot be reached, answers 404.
    /// True: a missing image or an unreachable source shows as no image.
    /// False: a failing source would break the dashboard's tables.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(null)]
    public async Task GetSourceImage_SourceFails_Answers404(HttpStatusCode? status)
    {
        _harness.AddPeer();
        _harness.Peer = _ => status is { } code ? new HttpResponseMessage(code) : throw new HttpRequestException("connection refused");

        var result = await _harness.Configuration().GetSourceImage(ItemId);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// A server entry the client factory refuses answers 404.
    /// True: an entry that cannot be called shows as no image.
    /// False: the refusal would escape as an unhandled error on every thumbnail.
    /// </summary>
    [Fact]
    public async Task GetSourceImage_FactoryRefusesTheEntry_Answers404()
    {
        _harness.AddPeer();
        _harness.Clients.Refusal = "the address is not allowed";

        var result = await _harness.Configuration().GetSourceImage(ItemId);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// The image route requires elevation and answers GET only.
    /// True: only an administrator can fetch a source's images through this server's key.
    /// False: any caller could use this server's key to read another server's images.
    /// </summary>
    [Fact]
    public void Route_RequiresElevation()
    {
        ControllerRouteAssert.Elevated(typeof(ConfigurationController), nameof(ConfigurationController.GetSourceImage), "GET", "ImageProxy");
    }

    private static HttpResponseMessage Image(byte[] bytes, string? contentType)
    {
        var content = new ByteArrayContent(bytes);
        if (contentType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

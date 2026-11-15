using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// User and image reads must tell "the source has none" from "the source could not be read", since callers clear what is here on the first.
/// </summary>
public sealed class SourceServerClientUsersTests
{
    private static readonly Guid Id = Guid.Parse("cccccccccccccccccccccccccccccccc");
    private static readonly string ImagePath = "/Users/" + Id + "/Images/Primary";

    /// <summary>
    /// A user's details are read by id.
    /// True: the Users scan reads the policy and settings it syncs.
    /// False: the scan would have nothing to compare.
    /// </summary>
    [Fact]
    public async Task GetUserAsync_Found_ReturnsTheUser()
    {
        using var client = new ClientStubServer().OnJson("/Users/" + Id, "{\"Id\":\"" + Id.ToString("N") + "\",\"Name\":\"alice\",\"Policy\":{\"IsAdministrator\":true}}").Client();

        var user = await client.GetUserAsync(Id);

        Assert.Equal(Id, user!.Id);
        Assert.Equal("alice", user.Name);
        Assert.True(user.Policy!.IsAdministrator);
    }

    /// <summary>
    /// An error status is rethrown, and a failure without a status returns null.
    /// True: the Users scan skips pruning when the source refuses.
    /// False: a refused read would look like a user who is gone, and the row would be pruned.
    /// </summary>
    [Fact]
    public async Task GetUserAsync_Failures_ThrowOnStatusAndReturnNullOtherwise()
    {
        using var refusing = new ClientStubServer().Client();
        using var broken = new ClientStubServer().OnThrow("/Users/" + Id, new HttpRequestException("down")).Client();

        var thrown = await Assert.ThrowsAnyAsync<ApiException>(() => refusing.GetUserAsync(Id));
        var missing = await broken.GetUserAsync(Id);

        Assert.Equal(404, thrown.ResponseStatusCode);
        Assert.Null(missing);
    }

    /// <summary>
    /// A profile image is returned as a stream of the bytes the source sent.
    /// True: the user's image here matches the source's.
    /// False: the image would be saved cut short or empty.
    /// </summary>
    [Fact]
    public async Task GetUserImageAsync_Found_StreamsTheImage()
    {
        var server = new ClientStubServer().On(ImagePath, _ => Bytes(new byte[] { 1, 2, 3 }));
        using var client = server.Client();

        await using var stream = await client.GetUserImageAsync(Id);

        using var copy = new MemoryStream();
        await stream!.CopyToAsync(copy);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        Assert.Contains("Token=\"key\"", Assert.Single(server.Seen).Authorization, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a 404 means the user has no image. Every other failure throws.
    /// True: the image here is cleared only when the source says it has none.
    /// False: a source that is down would clear every user's image.
    /// </summary>
    [Fact]
    public async Task GetUserImageAsync_NotFoundIsNull_OtherFailuresThrow()
    {
        using var none = new ClientStubServer().Client();
        using var refusing = new ClientStubServer().OnStatus(ImagePath, HttpStatusCode.InternalServerError).Client();
        using var broken = new ClientStubServer().OnThrow(ImagePath, new HttpRequestException("down")).Client();

        var missing = await none.GetUserImageAsync(Id);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.GetUserImageAsync(Id));
        var down = await Assert.ThrowsAsync<InvalidOperationException>(() => broken.GetUserImageAsync(Id));

        Assert.Null(missing);
        Assert.Contains(Id.ToString(), refused.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(down.InnerException);
    }

    /// <summary>
    /// The hash is the first 32 hex characters of the image's SHA-256 in lowercase, with the byte count beside it.
    /// True: the same image on both servers compares equal, and a changed one does not.
    /// False: an unchanged image would be downloaded on every scan, or a changed one never.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("profile image bytes")]
    public async Task GetUserImageHashAndSizeAsync_Image_ReturnsHashAndSize(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var client = new ClientStubServer().On(ImagePath, _ => Bytes(bytes)).Client();

        var (hash, size) = await client.GetUserImageHashAndSizeAsync(Id);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..32], hash);
        Assert.Equal(bytes.Length, size);
    }

    /// <summary>
    /// A user with no image answers two nulls, and a failed read throws.
    /// True: "no image" and "could not read" stay apart.
    /// False: a failed read would be taken for a removed image.
    /// </summary>
    [Fact]
    public async Task GetUserImageHashAndSizeAsync_NoImageIsNull_FailureThrows()
    {
        using var none = new ClientStubServer().Client();
        using var refusing = new ClientStubServer().OnStatus(ImagePath, HttpStatusCode.BadGateway).Client();

        var (hash, size) = await none.GetUserImageHashAndSizeAsync(Id);

        Assert.Null(hash);
        Assert.Null(size);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.GetUserImageHashAndSizeAsync(Id));
    }

    /// <summary>
    /// An item's images are listed, and any failure returns null.
    /// True: a failed listing is told apart from an item with no images.
    /// False: a failed listing would read as every image removed.
    /// </summary>
    [Fact]
    public async Task GetItemImageInfoAsync_ListOrFailure_ReturnsImagesOrNull()
    {
        var path = "/Items/" + Id + "/Images";
        using var answering = new ClientStubServer().OnJson(path, "[{\"ImageType\":\"Primary\",\"Width\":10,\"Height\":20,\"Size\":99},{\"ImageType\":\"Backdrop\",\"ImageIndex\":1}]").Client();
        using var refusing = new ClientStubServer().OnStatus(path, HttpStatusCode.InternalServerError).Client();

        var images = await answering.GetItemImageInfoAsync(Id);
        var failed = await refusing.GetItemImageInfoAsync(Id);

        Assert.Equal(2, images!.Count);
        Assert.Equal(10, images[0].Width);
        Assert.Equal(1, images[1].ImageIndex);
        Assert.Null(failed);
    }

    /// <summary>
    /// The image size comes from a HEAD request to the image, with its index when it has one.
    /// True: a replaced image is seen by its new size without downloading it.
    /// False: the wrong image of a type would be measured, or the whole image downloaded to measure it.
    /// </summary>
    [Theory]
    [InlineData(null, "/Images/Backdrop")]
    [InlineData(0, "/Images/Backdrop/0")]
    [InlineData(3, "/Images/Backdrop/3")]
    public async Task GetItemImageContentLengthAsync_Image_SendsHeadAndReturnsTheLength(int? index, string expectedSuffix)
    {
        var server = new ClientStubServer().On("/Items/" + Id + expectedSuffix, _ => Bytes(new byte[1234]));
        using var client = server.Client();

        var length = await client.GetItemImageContentLengthAsync(Id, "Backdrop", index);

        Assert.Equal(1234, length);
        Assert.Equal(HttpMethod.Head, Assert.Single(server.Seen).Method);
    }

    /// <summary>
    /// A refused or failed HEAD answers null, and a missing image type is refused.
    /// True: an unreadable size is unknown, not zero.
    /// False: a failed request would read as an empty image.
    /// </summary>
    [Fact]
    public async Task GetItemImageContentLengthAsync_Failures_ReturnNull()
    {
        using var none = new ClientStubServer().Client();
        using var broken = new ClientStubServer().OnThrow("/Items/" + Id + "/Images/Primary", new HttpRequestException("down")).Client();

        Assert.Null(await none.GetItemImageContentLengthAsync(Id, "Primary", null));
        Assert.Null(await broken.GetItemImageContentLengthAsync(Id, "Primary", null));
        await Assert.ThrowsAsync<ArgumentNullException>(() => none.GetItemImageContentLengthAsync(Id, null!, null));
    }

    /// <summary>
    /// An item image comes back with its content type, and "image/jpeg" when the source names none.
    /// True: the image is saved with the right extension.
    /// False: an image without a declared type could not be saved.
    /// </summary>
    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData(null, "image/jpeg")]
    public async Task DownloadItemImageAsync_Found_ReturnsStreamAndContentType(string? sentType, string expectedType)
    {
        var server = new ClientStubServer().On("/Items/" + Id + "/Images/Primary/2", _ => Bytes(new byte[] { 9, 8 }, sentType));
        using var client = server.Client();

        var (stream, contentType) = await client.DownloadItemImageAsync(Id, "Primary", 2);

        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream!.CopyToAsync(copy);
            Assert.Equal(new byte[] { 9, 8 }, copy.ToArray());
        }

        Assert.Equal(expectedType, contentType);
    }

    /// <summary>
    /// A refused or failed image download answers two nulls, and a cancelled one throws.
    /// True: one bad image is skipped and the rest of the item still syncs.
    /// False: one missing image would fail the whole item.
    /// </summary>
    [Fact]
    public async Task DownloadItemImageAsync_Failures_ReturnNullsAndCancellationThrows()
    {
        using var none = new ClientStubServer().Client();
        using var broken = new ClientStubServer().OnThrow("/Items/" + Id + "/Images/Logo", new HttpRequestException("down")).Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Equal((null, null), await none.DownloadItemImageAsync(Id, "Logo", null));
        Assert.Equal((null, null), await broken.DownloadItemImageAsync(Id, "Logo", null));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => none.DownloadItemImageAsync(Id, "Logo", null, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => none.DownloadItemImageAsync(Id, null!, null));
    }

    /// <summary>
    /// The primary image is asked from the user route or the item route, scaled to the height.
    /// True: the dashboard shows a user's picture for a user and a poster for an item.
    /// False: a user id would be sent to the item route and answer 404.
    /// </summary>
    [Theory]
    [InlineData(true, "/Users/")]
    [InlineData(false, "/Items/")]
    public async Task OpenPrimaryImageAsync_UserOrItem_AsksTheMatchingRoute(bool user, string route)
    {
        var server = new ClientStubServer();
        using var client = server.Client();

        using var response = await client.OpenPrimaryImageAsync(Id, user, 120);

        var sent = Assert.Single(server.Seen);
        Assert.Equal(route + Id + "/Images/Primary", sent.Path);
        Assert.Equal("?maxHeight=120", sent.Query);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpResponseMessage Bytes(byte[] bytes, string? contentType = null)
    {
        var content = new ByteArrayContent(bytes);
        if (contentType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

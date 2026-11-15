using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// The client must know which of a peer's items are links and where a peer's file first came from, and must never guess either when the peer cannot be read.
/// </summary>
public sealed class SourceServerClientLinksTests
{
    private const string LinksPath = "/Sync/Peer/Links";
    private const string CopiesPath = "/Sync/Peer/Copies";
    private static readonly Guid Library = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly BaseItemKind[] Movies = { BaseItemKind.Movie };

    /// <summary>
    /// An item never read, and a missing id, are not links.
    /// True: Content treats an unknown item as a file it may copy.
    /// False: a null id would throw in the middle of a scan.
    /// </summary>
    [Fact]
    public void IsPeerLink_UnknownOrMissingId_IsFalse()
    {
        using var client = new ClientStubServer().Client();

        Assert.False(client.IsPeerLink(null));
        Assert.False(client.IsPeerLink(Guid.NewGuid()));
        Assert.False(client.PeerLinksUnknown);
    }

    /// <summary>
    /// A listed link is matched whatever the case of its path, and takes the path its copy would have.
    /// True: a link on a case preserving file system is still matched to its file.
    /// False: the link would be copied as a tiny .strm file in place of the film.
    /// </summary>
    [Fact]
    public async Task ApplyPeerLinks_LinkPathInAnotherCase_IsStillMatched()
    {
        var id = Guid.NewGuid();
        var server = new ClientStubServer()
            .OnJson("/Items", ClientJson.Page(1, ClientJson.Item(id, "Movie", "/P/Film.STRM")))
            .OnJson(LinksPath, "{\"Links\":[{\"LinkPath\":\"/p/film.strm\",\"CopiedPath\":\"/p/Film.mkv\"},{\"LinkPath\":\"\",\"CopiedPath\":\"/x\"}]}");
        using var client = server.Client();

        var page = await client.GetLibraryItemPathsAsync(Library, Movies);

        Assert.Equal("/p/Film.mkv", Assert.Single(page.Items!).Path);
        Assert.True(client.IsPeerLink(id));
        Assert.False(client.PeerLinksUnknown);
    }

    /// <summary>
    /// A peer with no links route has no links, and its .strm files are its own.
    /// True: a peer on an older Server Sync is scanned as before.
    /// False: its .strm files would wait forever for an answer that never comes.
    /// </summary>
    [Fact]
    public async Task ApplyPeerLinks_PeerWithoutTheRoute_LeavesPathsAndKnowsThereAreNone()
    {
        var id = Guid.NewGuid();
        var server = new ClientStubServer().OnJson("/Items", ClientJson.Page(1, ClientJson.Item(id, "Movie", "/p/own.strm")));
        using var client = server.Client();

        var page = await client.GetLibraryItemPathsAsync(Library, Movies);
        await client.GetLibraryItemPathsAsync(Library, Movies);

        Assert.Equal("/p/own.strm", Assert.Single(page.Items!).Path);
        Assert.False(client.IsPeerLink(id));
        Assert.False(client.PeerLinksUnknown);
        Assert.Single(server.To(LinksPath));
    }

    /// <summary>
    /// A peer that fails to list its links leaves them unknown, and is not asked again straight away.
    /// True: Content leaves the peer's .strm files alone until the peer answers, without a request per page.
    /// False: a link would be copied as a plain file, or a failing peer would be asked on every page of every scan.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ApplyPeerLinks_PeerFailsToAnswer_MarksLinksUnknownAndWaits(HttpStatusCode status)
    {
        var id = Guid.NewGuid();
        var server = new ClientStubServer()
            .OnJson("/Items", ClientJson.Page(1, ClientJson.Item(id, "Movie", "/p/film.strm")))
            .OnStatus(LinksPath, status);
        using var client = server.Client();

        var page = await client.GetLibraryItemPathsAsync(Library, Movies);
        await client.GetLibraryItemPathsAsync(Library, Movies);

        Assert.Equal("/p/film.strm", Assert.Single(page.Items!).Path);
        Assert.True(client.PeerLinksUnknown);
        Assert.False(client.IsPeerLink(id));
        Assert.Single(server.To(LinksPath));
    }

    /// <summary>
    /// A file with no path has no origin, and the peer is not asked.
    /// True: an item without a file costs no request.
    /// False: a null path would throw, or every pathless item would read the peer's copies.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OriginOfAsync_NoPath_IsNullWithoutARequest(string? path)
    {
        var server = new ClientStubServer();
        using var client = server.Client();

        var origin = await client.OriginOfAsync(path, CancellationToken.None);

        Assert.Null(origin);
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// A copy's origin is found by its path in any case, the peer's own files have none, and the copies are read once.
    /// True: a file is only ever copied from the server it first came from.
    /// False: a copy of a copy would be taken as an original, or every lookup would list the peer's copies again.
    /// </summary>
    [Fact]
    public async Task OriginOfAsync_KnownCopy_ReturnsItsOriginFromOneRead()
    {
        var server = new ClientStubServer().OnJson(
            CopiesPath,
            "{\"Copies\":[{\"Path\":\"/m/Copy.mkv\",\"OriginServerId\":\"origin-1\"},{\"Path\":\"/m/blank.mkv\",\"OriginServerId\":\"\"}]}");
        using var client = server.Client();

        var copy = await client.OriginOfAsync("/M/COPY.MKV", CancellationToken.None);
        var own = await client.OriginOfAsync("/m/own.mkv", CancellationToken.None);
        var blank = await client.OriginOfAsync("/m/blank.mkv", CancellationToken.None);

        Assert.Equal("origin-1", copy);
        Assert.Null(own);
        Assert.Null(blank);
        Assert.Single(server.Seen);
    }

    /// <summary>
    /// A peer with no copies route, or one that refuses, makes every file its own.
    /// True: a peer on an older Server Sync still syncs.
    /// False: a peer without the route would fail every Content scan.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task OriginOfAsync_PeerHasNoCopiesRoute_TreatsEveryFileAsItsOwn(HttpStatusCode status)
    {
        var server = new ClientStubServer().OnStatus(CopiesPath, status);
        using var client = server.Client();

        var origin = await client.OriginOfAsync("/m/a.mkv", CancellationToken.None);
        await client.OriginOfAsync("/m/b.mkv", CancellationToken.None);

        Assert.Null(origin);
        Assert.Single(server.Seen);
    }

    /// <summary>
    /// A peer that fails to list its copies throws, and is asked again on the next lookup.
    /// True: a file of unknown origin is never taken for the peer's own.
    /// False: one server error would make every copy look like an original for the life of the client.
    /// </summary>
    [Fact]
    public async Task OriginOfAsync_PeerFails_ThrowsAndAsksAgain()
    {
        var failing = true;
        var server = new ClientStubServer().On(CopiesPath, _ => failing
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : ClientStubServer.Json("{\"Copies\":[{\"Path\":\"/m/a.mkv\",\"OriginServerId\":\"origin-1\"}]}"));
        using var client = server.Client();

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.OriginOfAsync("/m/a.mkv", CancellationToken.None));
        failing = false;
        var origin = await client.OriginOfAsync("/m/a.mkv", CancellationToken.None);

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Equal("origin-1", origin);
    }

    /// <summary>
    /// A cancelled origin lookup throws the cancellation.
    /// True: a stopped scan ends at once.
    /// False: a cancelled lookup would read as a file with no origin.
    /// </summary>
    [Fact]
    public async Task OriginOfAsync_Cancelled_Throws()
    {
        using var client = new ClientStubServer().Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OriginOfAsync("/m/a.mkv", cancelled.Token));
    }
}

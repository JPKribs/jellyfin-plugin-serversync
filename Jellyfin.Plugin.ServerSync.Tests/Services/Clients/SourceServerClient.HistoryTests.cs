using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// History reads must return every changed or played item, and must fail loudly when the source cannot answer, so a failure is never read as nothing played.
/// </summary>
public sealed class SourceServerClientHistoryTests
{
    private static readonly Guid User = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Guid Library = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    /// <summary>
    /// The changed item listing asks for the user, the library, the kinds, and the time, and returns each id once.
    /// True: an incremental History scan reads only what changed for that user.
    /// False: the scan would read another user's changes, or everything on every run.
    /// </summary>
    [Fact]
    public async Task GetItemIdsChangedForUserAsync_OnePage_ReturnsTheIdsAndAsksForTheChangeWindow()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var server = new ClientStubServer().OnJson("/Items", ClientJson.Page(3, ClientJson.Item(first), ClientJson.Item(second), ClientJson.Item(first)));
        using var client = server.Client();

        var ids = await client.GetItemIdsChangedForUserAsync(User, Library, new[] { BaseItemKind.Movie, BaseItemKind.Episode }, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        Assert.Equal(new[] { first, second }.OrderBy(g => g), ids.OrderBy(g => g));
        var query = Assert.Single(server.Seen).Query;
        Assert.Contains("userId=" + User, query, StringComparison.Ordinal);
        Assert.Contains("parentId=" + Library, query, StringComparison.Ordinal);
        Assert.Contains("includeItemTypes=Movie&includeItemTypes=Episode", query, StringComparison.Ordinal);
        Assert.Contains("minDateLastSavedForUser=2026-01-02T03:04:05.0000000+00:00", query, StringComparison.Ordinal);
        Assert.Contains("startIndex=0", query, StringComparison.Ordinal);
        Assert.Contains("limit=1000", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A full page is followed by a request for the next one, until the reported total is reached.
    /// True: a user with more changes than one page holds has all of them read.
    /// False: changes past the first thousand would never sync.
    /// </summary>
    [Fact]
    public async Task GetItemIdsChangedForUserAsync_FullPage_ReadsTheNextPage()
    {
        var server = new ClientStubServer().On("/Items", request => ClientStubServer.Json(
            request.Query.Contains("startIndex=0", StringComparison.Ordinal) ? ClientJson.PageOf(1000, 1002) : ClientJson.PageOf(2, 1002)));
        using var client = server.Client();

        var ids = await client.GetItemIdsChangedForUserAsync(User, Library, new[] { BaseItemKind.Movie }, DateTime.UnixEpoch);

        Assert.Equal(1002, ids.Count);
        Assert.Equal(2, server.Seen.Count);
        Assert.Contains("startIndex=1000", server.Seen[1].Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source that answers with an error, cannot be reached, or gives no list fails the listing.
    /// True: the scan stops and keeps its rows.
    /// False: a failed listing would read as no changes, and real changes would be skipped for good.
    /// </summary>
    [Theory]
    [InlineData("error")]
    [InlineData("unreachable")]
    [InlineData("no-list")]
    public async Task GetItemIdsChangedForUserAsync_NoAnswer_Throws(string failure)
    {
        var server = new ClientStubServer();
        _ = failure switch
        {
            "error" => server.OnStatus("/Items", HttpStatusCode.InternalServerError),
            "unreachable" => server.OnThrow("/Items", new HttpRequestException("down")),
            _ => server.OnJson("/Items", "{}")
        };
        using var client = server.Client();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetItemIdsChangedForUserAsync(User, Library, new[] { BaseItemKind.Movie }, DateTime.UnixEpoch));
    }

    /// <summary>
    /// The changed item listing refuses a missing list of kinds, and a cancelled token stops it.
    /// True: a bad call fails at once and a cancelled scan ends.
    /// False: a null would reach the source as a query for every kind, or a cancelled scan would be wrapped as a failure.
    /// </summary>
    [Fact]
    public async Task GetItemIdsChangedForUserAsync_NullKindsOrCancelled_Throws()
    {
        using var client = new ClientStubServer().Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetItemIdsChangedForUserAsync(User, Library, null!, DateTime.UnixEpoch));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetItemIdsChangedForUserAsync(User, Library, new[] { BaseItemKind.Movie }, DateTime.UnixEpoch, cancelled.Token));
    }

    /// <summary>
    /// A batch read asks for the ids with user data, and an empty batch asks for nothing.
    /// True: the scan reads the history of exactly the items it holds.
    /// False: an empty batch would query the whole library, or the answer would carry no history.
    /// </summary>
    [Fact]
    public async Task GetItemsWithUserDataByIdsAsync_Ids_AreAskedForWithUserData()
    {
        var id = Guid.NewGuid();
        var server = new ClientStubServer().OnJson("/Items", ClientJson.Page(1, ClientJson.Item(id, path: "/m/a.mkv")));
        using var client = server.Client();

        var none = await client.GetItemsWithUserDataByIdsAsync(User, Array.Empty<Guid?>());
        var result = await client.GetItemsWithUserDataByIdsAsync(User, new Guid?[] { id });

        Assert.Null(none);
        Assert.Equal(id, Assert.Single(result!.Items!).Id);
        var query = Assert.Single(server.Seen).Query;
        Assert.Contains("ids=" + id, query, StringComparison.Ordinal);
        Assert.Contains("enableUserData=true", query, StringComparison.Ordinal);
        Assert.Contains("userId=" + User, query, StringComparison.Ordinal);
    }

    /// <summary>
    /// An error status is rethrown, and a failure without a status returns null.
    /// True: the scan stops before it prunes on an HTTP error, and skips one batch on a network fault.
    /// False: an HTTP error would read as items with no history, and their rows would be pruned.
    /// </summary>
    [Fact]
    public async Task GetItemsWithUserDataByIdsAsync_Failures_ThrowOnStatusAndReturnNullOtherwise()
    {
        var refusing = new ClientStubServer().OnStatus("/Items", HttpStatusCode.InternalServerError);
        var broken = new ClientStubServer().OnThrow("/Items", new HttpRequestException("down"));
        using var refusingClient = refusing.Client();
        using var brokenClient = broken.Client();

        var thrown = await Assert.ThrowsAnyAsync<ApiException>(() => refusingClient.GetItemsWithUserDataByIdsAsync(User, new Guid?[] { Guid.NewGuid() }));
        var missing = await brokenClient.GetItemsWithUserDataByIdsAsync(User, new Guid?[] { Guid.NewGuid() });

        Assert.Equal(500, thrown.ResponseStatusCode);
        Assert.Null(missing);
        await Assert.ThrowsAsync<ArgumentNullException>(() => refusingClient.GetItemsWithUserDataByIdsAsync(User, null!));
    }

    /// <summary>
    /// One page of played items asks for played items of the four media kinds from an index.
    /// True: the History scan pages through what the user has played.
    /// False: unplayed items or folders would come back as played.
    /// </summary>
    [Fact]
    public async Task GetUserPlayedItemsAsync_Page_AsksForPlayedMedia()
    {
        var server = new ClientStubServer().OnJson("/Items", ClientJson.PageOf(2, 2));
        using var client = server.Client();

        var page = await client.GetUserPlayedItemsAsync(User, Library, startIndex: 40, limit: 20);

        Assert.Equal(2, page!.Items!.Count);
        var query = Assert.Single(server.Seen).Query;
        Assert.Contains("isPlayed=true", query, StringComparison.Ordinal);
        Assert.Contains("includeItemTypes=Movie&includeItemTypes=Episode&includeItemTypes=Audio&includeItemTypes=Video", query, StringComparison.Ordinal);
        Assert.Contains("startIndex=40", query, StringComparison.Ordinal);
        Assert.Contains("limit=20", query, StringComparison.Ordinal);
        Assert.Contains("enableUserData=true", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// An error status on a played page is rethrown, and a failure without a status returns null.
    /// True: the scan stops before it prunes on an HTTP error.
    /// False: an HTTP error would read as a user who played nothing.
    /// </summary>
    [Fact]
    public async Task GetUserPlayedItemsAsync_Failures_ThrowOnStatusAndReturnNullOtherwise()
    {
        using var refusing = new ClientStubServer().OnStatus("/Items", HttpStatusCode.Forbidden).Client();
        using var broken = new ClientStubServer().OnThrow("/Items", new HttpRequestException("down")).Client();

        var thrown = await Assert.ThrowsAnyAsync<ApiException>(() => refusing.GetUserPlayedItemsAsync(User, Library));
        var missing = await broken.GetUserPlayedItemsAsync(User, Library);

        Assert.Equal(403, thrown.ResponseStatusCode);
        Assert.Null(missing);
    }

    /// <summary>
    /// The played id listing returns every played id, and a page that cannot be read fails it.
    /// True: a source that drops the connection stops the scan.
    /// False: the failed page would read as the end, and plays past it would be marked unplayed here.
    /// </summary>
    [Fact]
    public async Task GetUserPlayedItemIdsAsync_PagesThenFailure_ReturnsIdsOrThrows()
    {
        var first = Guid.NewGuid();
        using var answering = new ClientStubServer().OnJson("/Items", ClientJson.Page(1, ClientJson.Item(first))).Client();
        using var broken = new ClientStubServer().OnThrow("/Items", new HttpRequestException("down")).Client();

        var ids = await answering.GetUserPlayedItemIdsAsync(User, Library);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => broken.GetUserPlayedItemIdsAsync(User, Library));

        Assert.Equal(first, Assert.Single(ids));
        Assert.Contains("at index 0", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A user with nothing played gets an empty set.
    /// True: an empty answer is a valid answer.
    /// False: a user who played nothing would fail every History scan.
    /// </summary>
    [Fact]
    public async Task GetUserPlayedItemIdsAsync_NothingPlayed_ReturnsEmpty()
    {
        using var client = new ClientStubServer().OnJson("/Items", ClientJson.Page(0)).Client();

        var ids = await client.GetUserPlayedItemIdsAsync(User, Library);

        Assert.Empty(ids);
    }
}

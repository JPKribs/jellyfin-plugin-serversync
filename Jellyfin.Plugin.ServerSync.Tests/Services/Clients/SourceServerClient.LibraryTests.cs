using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// Library reads must return a whole listing or fail, since a list cut short reads as files removed, and a download must say where its bytes start.
/// </summary>
public sealed class SourceServerClientLibraryTests
{
    private static readonly Guid Root = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Guid Library = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly string DownloadPath = "/Items/" + Root + "/Download";

    /// <summary>
    /// A whitelisted media file stands for itself.
    /// True: whitelisting one film syncs that film.
    /// False: a whitelisted film would sync nothing.
    /// </summary>
    [Fact]
    public async Task GetWhitelistedItemLeavesAsync_LeafItem_ReturnsItself()
    {
        var server = new ClientStubServer().OnJson("/Items", ClientJson.Page(1, ClientJson.Item(Root, "Movie", "/m/film.mkv")));
        using var client = server.Client();

        var leaves = await client.GetWhitelistedItemLeavesAsync(Root);

        Assert.Equal(Root, Assert.Single(leaves).Id);
        Assert.Contains("ids=" + Root, Assert.Single(server.Seen).Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A whitelisted folder is walked one level at a time down to its media files.
    /// True: whitelisting a series syncs every episode of every season.
    /// False: a series would sync nothing, since a recursive query by parent returns none.
    /// </summary>
    [Fact]
    public async Task GetWhitelistedItemLeavesAsync_Series_WalksEachFolderToItsEpisodes()
    {
        var season = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var server = new ClientStubServer().On("/Items", request => ClientStubServer.Json(
            request.Query.Contains("ids=" + Root, StringComparison.Ordinal) ? ClientJson.Page(1, ClientJson.Item(Root, "Series"))
            : request.Query.Contains("parentId=" + Root, StringComparison.Ordinal) ? ClientJson.Page(1, ClientJson.Item(season, "Season"))
            : request.Query.Contains("parentId=" + season, StringComparison.Ordinal) ? ClientJson.Page(2, ClientJson.Item(first, "Episode"), ClientJson.Item(second, "Episode"))
            : ClientJson.Page(0)));
        using var client = server.Client();

        var leaves = await client.GetWhitelistedItemLeavesAsync(Root);

        Assert.Equal(new[] { first, second }, leaves.Select(l => l.Id!.Value));
        Assert.Equal(3, server.Seen.Count);
        Assert.DoesNotContain("recursive", server.Seen[1].Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A whitelisted playlist is read through the playlist route as the given user, and each member counts once.
    /// True: a whitelisted playlist syncs its films, even one it holds twice.
    /// False: a playlist would sync nothing, since it answers no child query.
    /// </summary>
    [Fact]
    public async Task GetWhitelistedItemLeavesAsync_Playlist_ReadsItsMembersAsTheUser()
    {
        var user = Guid.NewGuid();
        var film = Guid.NewGuid();
        var server = new ClientStubServer()
            .OnJson("/Items", ClientJson.Page(1, ClientJson.Item(Root, "Playlist")))
            .OnJson("/Playlists/" + Root + "/Items", ClientJson.Page(null, ClientJson.Item(film, "Movie"), ClientJson.Item(film, "Movie")));
        using var client = server.Client();

        var leaves = await client.GetWhitelistedItemLeavesAsync(Root, user);

        Assert.Equal(film, Assert.Single(leaves).Id);
        Assert.Contains("userId=" + user, Assert.Single(server.To("/Playlists/" + Root + "/Items")).Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A whitelist entry the source no longer holds fails the walk.
    /// True: the files it stood for are kept until the entry is removed from the whitelist.
    /// False: a deleted entry would read as every file under it removed.
    /// </summary>
    [Fact]
    public async Task GetWhitelistedItemLeavesAsync_EntryGone_Throws()
    {
        using var client = new ClientStubServer().OnJson("/Items", ClientJson.Page(0)).Client();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWhitelistedItemLeavesAsync(Root));

        Assert.Contains("no longer exists", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder whose listing repeats an item, or holds fewer items than the source reported, fails the walk.
    /// True: a library that changed mid scan is scanned again later.
    /// False: an item skipped by a shifted page would read as removed.
    /// </summary>
    [Theory]
    [InlineData(true, "duplicate item across pages")]
    [InlineData(false, "expected 5, fetched 2")]
    public async Task GetWhitelistedItemLeavesAsync_ListingShifted_Throws(bool repeat, string expected)
    {
        var child = Guid.NewGuid();
        var server = new ClientStubServer().On("/Items", request => ClientStubServer.Json(
            request.Query.Contains("ids=", StringComparison.Ordinal) ? ClientJson.Page(1, ClientJson.Item(Root, "Folder"))
            : repeat ? ClientJson.Page(2, ClientJson.Item(child), ClientJson.Item(child))
            : ClientJson.PageOf(2, 5)));
        using var client = server.Client();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWhitelistedItemLeavesAsync(Root));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source error on the root or on a folder is rethrown.
    /// True: the scan stops and keeps its rows.
    /// False: a failed walk would return what it had and the rest would read as removed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetWhitelistedItemLeavesAsync_SourceError_Rethrows(bool failOnRoot)
    {
        var server = new ClientStubServer().On("/Items", request =>
            request.Query.Contains("ids=", StringComparison.Ordinal) && !failOnRoot
                ? ClientStubServer.Json(ClientJson.Page(1, ClientJson.Item(Root, "Folder")))
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = server.Client();

        var failure = await Assert.ThrowsAnyAsync<ApiException>(() => client.GetWhitelistedItemLeavesAsync(Root));

        Assert.Equal(500, failure.ResponseStatusCode);
    }

    /// <summary>
    /// One library page asks for the four media kinds recursively, in a stable order, from a time when one is given.
    /// True: a Content scan pages through a library without skipping on ties.
    /// False: folders would come back as files, or an incremental scan would list everything.
    /// </summary>
    [Fact]
    public async Task GetLibraryItemsAsync_Page_AsksForMediaInAStableOrder()
    {
        var server = new ClientStubServer().OnJson("/Items", ClientJson.PageOf(2, 2));
        using var client = server.Client();

        var page = await client.GetLibraryItemsAsync(Library, 10, 5, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await client.GetLibraryItemsAsync(Library);

        Assert.Equal(2, page!.Items!.Count);
        var query = server.Seen[0].Query;
        Assert.Contains("parentId=" + Library, query, StringComparison.Ordinal);
        Assert.Contains("recursive=true", query, StringComparison.Ordinal);
        Assert.Contains("includeItemTypes=Movie&includeItemTypes=Episode&includeItemTypes=Audio&includeItemTypes=Video", query, StringComparison.Ordinal);
        Assert.Contains("sortBy=DateCreated&sortBy=SortName&sortBy=IndexNumber", query, StringComparison.Ordinal);
        Assert.Contains("minDateLastSaved=2026-03-01T00:00:00.0000000+00:00", query, StringComparison.Ordinal);
        Assert.Contains("startIndex=10", query, StringComparison.Ordinal);
        Assert.Contains("limit=5", query, StringComparison.Ordinal);
        Assert.DoesNotContain("minDateLastSaved", server.Seen[1].Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// An error status on a library page is rethrown, and a failure without a status returns null.
    /// True: the scan stops before it prunes on an HTTP error.
    /// False: an HTTP error would read as the end of the library.
    /// </summary>
    [Fact]
    public async Task GetLibraryItemsAsync_Failures_ThrowOnStatusAndReturnNullOtherwise()
    {
        using var refusing = new ClientStubServer().OnStatus("/Items", HttpStatusCode.Unauthorized).Client();
        using var broken = new ClientStubServer().OnThrow("/Items", new HttpRequestException("down")).Client();

        var thrown = await Assert.ThrowsAnyAsync<ApiException>(() => refusing.GetLibraryItemsAsync(Library));
        var missing = await broken.GetLibraryItemsAsync(Library);

        Assert.Equal(401, thrown.ResponseStatusCode);
        Assert.Null(missing);
    }

    /// <summary>
    /// The collection and playlist pickers search the whole server, and send a search term only when one is typed.
    /// True: the Library filter picker lists every collection or playlist, narrowed as the operator types.
    /// False: a blank search box would filter on an empty term and list nothing.
    /// </summary>
    [Theory]
    [InlineData(true, null, "BoxSet")]
    [InlineData(true, "  ", "BoxSet")]
    [InlineData(true, "star", "BoxSet")]
    [InlineData(false, null, "Playlist")]
    [InlineData(false, "star", "Playlist")]
    public async Task GetCollectionsAndPlaylistsAsync_SearchTerm_IsSentOnlyWhenTyped(bool collections, string? term, string expectedKind)
    {
        var server = new ClientStubServer().OnJson("/Items", ClientJson.PageOf(1, 1));
        using var client = server.Client();

        var page = collections ? await client.GetCollectionsAsync(term, 5, 25) : await client.GetPlaylistsAsync(term, 5, 25);

        Assert.Single(page!.Items!);
        var query = Assert.Single(server.Seen).Query;
        Assert.Contains("includeItemTypes=" + expectedKind, query, StringComparison.Ordinal);
        Assert.Contains("recursive=true", query, StringComparison.Ordinal);
        Assert.DoesNotContain("parentId", query, StringComparison.Ordinal);
        Assert.Contains("startIndex=5", query, StringComparison.Ordinal);
        Assert.Contains("limit=25", query, StringComparison.Ordinal);
        Assert.Equal(!string.IsNullOrWhiteSpace(term), query.Contains("searchTerm=star", StringComparison.Ordinal));
    }

    /// <summary>
    /// A picker listing that fails for any reason answers null.
    /// True: the picker shows that it could not load.
    /// False: the settings page's request would fail outright.
    /// </summary>
    [Fact]
    public async Task PickerListings_SourceFails_ReturnNull()
    {
        using var client = new ClientStubServer().OnStatus("/Items", HttpStatusCode.InternalServerError).Client();

        Assert.Null(await client.GetCollectionsAsync());
        Assert.Null(await client.GetPlaylistsAsync());
        Assert.Null(await client.GetTopLevelLibraryItemsAsync(Library));
    }

    /// <summary>
    /// Browsing a library lists its top level without recursion, and a search goes recursive over top level kinds only.
    /// True: the picker shows series and films, and a search finds them by name.
    /// False: browsing would list every episode, or a search would return single episodes and songs.
    /// </summary>
    [Theory]
    [InlineData(null, "recursive=false", true)]
    [InlineData("alien", "recursive=true", false)]
    public async Task GetTopLevelLibraryItemsAsync_BrowseOrSearch_SetsRecursionAndKinds(string? term, string expectedRecursion, bool listsFiles)
    {
        var server = new ClientStubServer().OnJson("/Items", ClientJson.PageOf(1, 1));
        using var client = server.Client();

        await client.GetTopLevelLibraryItemsAsync(Library, term);

        var query = Assert.Single(server.Seen).Query;
        Assert.Contains("parentId=" + Library, query, StringComparison.Ordinal);
        Assert.Contains(expectedRecursion, query, StringComparison.Ordinal);
        Assert.Contains("includeItemTypes=Movie&includeItemTypes=Series&includeItemTypes=BoxSet&includeItemTypes=MusicAlbum&includeItemTypes=MusicArtist", query, StringComparison.Ordinal);
        Assert.Equal(listsFiles, query.Contains("includeItemTypes=Audio&includeItemTypes=Video", StringComparison.Ordinal));
        Assert.Equal(term is not null, query.Contains("searchTerm=alien", StringComparison.Ordinal));
    }

    /// <summary>
    /// The count is the source's total from a query that reads no items.
    /// True: the scan knows how many files to expect before it lists them.
    /// False: counting would download the whole library.
    /// </summary>
    [Fact]
    public async Task GetLibraryItemCountAsync_Answer_ReturnsTheTotal()
    {
        var server = new ClientStubServer().OnJson("/Items", "{\"Items\":[],\"TotalRecordCount\":4321}");
        using var client = server.Client();

        var count = await client.GetLibraryItemCountAsync(Library);

        Assert.Equal(4321, count);
        Assert.Contains("limit=0", Assert.Single(server.Seen).Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// An error status on the count is rethrown, and a failure without a status counts as 0.
    /// True: the scan stops before it prunes on an HTTP error.
    /// False: a refused count would read as an empty library.
    /// </summary>
    [Fact]
    public async Task GetLibraryItemCountAsync_Failures_ThrowOnStatusAndReturnZeroOtherwise()
    {
        using var refusing = new ClientStubServer().OnStatus("/Items", HttpStatusCode.InternalServerError).Client();
        using var broken = new ClientStubServer().OnThrow("/Items", new HttpRequestException("down")).Client();

        await Assert.ThrowsAnyAsync<ApiException>(() => refusing.GetLibraryItemCountAsync(Library));
        Assert.Equal(0, await broken.GetLibraryItemCountAsync(Library));
    }

    /// <summary>
    /// The path listing asks only for paths of the given kinds and returns the page.
    /// True: a scan finds what this server holds with a cheap listing.
    /// False: the listing would carry every field of every item.
    /// </summary>
    [Fact]
    public async Task GetLibraryItemPathsAsync_Page_AsksOnlyForPaths()
    {
        var id = Guid.NewGuid();
        var server = new ClientStubServer().OnJson("/Items", ClientJson.Page(1, ClientJson.Item(id, "Movie", "/m/a.mkv")));
        using var client = server.Client();

        var page = await client.GetLibraryItemPathsAsync(Library, new[] { BaseItemKind.Series }, 7, 9);

        Assert.Equal("/m/a.mkv", Assert.Single(page.Items!).Path);
        var query = Assert.Single(server.Seen).Query;
        Assert.Contains("fields=Path", query, StringComparison.Ordinal);
        Assert.DoesNotContain("fields=DateCreated", query, StringComparison.Ordinal);
        Assert.Contains("includeItemTypes=Series", query, StringComparison.Ordinal);
        Assert.Contains("startIndex=7", query, StringComparison.Ordinal);
        Assert.Contains("limit=9", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every failure of the path listing throws, including an answer with no page.
    /// True: the scan stops, since a null read as the end would make later items look removed.
    /// False: a dropped connection mid listing would prune every item past it.
    /// </summary>
    [Theory]
    [InlineData("error", typeof(ApiException))]
    [InlineData("unreachable", typeof(InvalidOperationException))]
    [InlineData("no-content", typeof(InvalidOperationException))]
    public async Task GetLibraryItemPathsAsync_NoAnswer_Throws(string failure, Type expected)
    {
        var server = new ClientStubServer();
        _ = failure switch
        {
            "error" => server.OnStatus("/Items", HttpStatusCode.InternalServerError),
            "unreachable" => server.OnThrow("/Items", new HttpRequestException("down")),
            _ => server.OnStatus("/Items", HttpStatusCode.NoContent)
        };
        using var client = server.Client();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => client.GetLibraryItemPathsAsync(Library, new[] { BaseItemKind.Movie }));

        Assert.IsAssignableFrom(expected, thrown);
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetLibraryItemPathsAsync(Library, null!));
    }

    /// <summary>
    /// Ids are read in batches of the given size, and no ids means no request.
    /// True: a long list of ids never makes one URL too long for the source.
    /// False: a large scan would send every id in one request and be refused.
    /// </summary>
    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(2, 2, 1)]
    [InlineData(5, 2, 3)]
    [InlineData(3, 50, 1)]
    public async Task GetItemsByIdsAsync_Ids_AreReadInBatches(int idCount, int batchSize, int expectedRequests)
    {
        var ids = Enumerable.Range(0, idCount).Select(_ => Guid.NewGuid()).ToArray();
        var server = new ClientStubServer().On("/Items", request => ClientStubServer.Json(
            ClientJson.Page(null, ids.Where(id => request.Query.Contains("ids=" + id, StringComparison.Ordinal)).Select(id => ClientJson.Item(id)).ToArray())));
        using var client = server.Client();

        var items = await client.GetItemsByIdsAsync(ids, new[] { ItemFields.Path, ItemFields.Overview }, batchSize);

        Assert.Equal(ids, items.Select(i => i.Id!.Value));
        Assert.Equal(expectedRequests, server.Seen.Count);
        Assert.All(server.Seen, r => Assert.Contains("fields=Path&fields=Overview", r.Query, StringComparison.Ordinal));
    }

    /// <summary>
    /// A failed batch throws, whatever the cause, and a cancelled read stops before the next batch.
    /// True: the caller never gets a partial answer.
    /// False: the items of the failed batch would look removed from the source.
    /// </summary>
    [Fact]
    public async Task GetItemsByIdsAsync_BatchFails_Throws()
    {
        var ids = new[] { Guid.NewGuid() };
        var fields = new[] { ItemFields.Path };
        using var refusing = new ClientStubServer().OnStatus("/Items", HttpStatusCode.InternalServerError).Client();
        using var broken = new ClientStubServer().OnThrow("/Items", new HttpRequestException("down")).Client();
        var idle = new ClientStubServer();
        using var idleClient = idle.Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<ApiException>(() => refusing.GetItemsByIdsAsync(ids, fields));
        await Assert.ThrowsAsync<HttpRequestException>(() => broken.GetItemsByIdsAsync(ids, fields));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idleClient.GetItemsByIdsAsync(ids, fields, cancellationToken: cancelled.Token));
        Assert.Empty(idle.Seen);
        await Assert.ThrowsAsync<ArgumentNullException>(() => idleClient.GetItemsByIdsAsync(null!, fields));
        await Assert.ThrowsAsync<ArgumentNullException>(() => idleClient.GetItemsByIdsAsync(ids, null!));
    }

    /// <summary>
    /// Null means the source holds no such item, and a source that cannot be asked throws.
    /// True: a caller can tell a gone item from an unreachable source.
    /// False: an unreachable source would read as the item being gone.
    /// </summary>
    [Fact]
    public async Task FindItemDetailsAsync_GoneIsNull_UnreachableThrows()
    {
        using var holding = new ClientStubServer().OnJson("/Items", ClientJson.Page(1, ClientJson.Item(Root, "Movie", "/m/a.mkv"))).Client();
        using var empty = new ClientStubServer().OnJson("/Items", ClientJson.Page(0)).Client();
        using var refusing = new ClientStubServer().OnStatus("/Items", HttpStatusCode.InternalServerError).Client();

        Assert.Equal(Root, (await holding.FindItemDetailsAsync(Root))!.Id);
        Assert.Null(await empty.FindItemDetailsAsync(Root));
        await Assert.ThrowsAnyAsync<ApiException>(() => refusing.FindItemDetailsAsync(Root));
    }

    /// <summary>
    /// A whole file download carries the key, no range, and reports the file's length and validator.
    /// True: a first download saves the whole file and can be resumed later.
    /// False: the file would be saved without knowing its size, or a later resume could not be tied to it.
    /// </summary>
    [Fact]
    public async Task DownloadFileFromAsync_WholeFile_StartsAtZeroWithAValidator()
    {
        var server = new ClientStubServer().On(DownloadPath, _ => File("0123456789", etag: "\"v1\""));
        using var client = server.Client();

        var download = await client.DownloadFileFromAsync(Root, 0, null);

        await using (download.Stream)
        {
            Assert.Equal("0123456789", await ReadAsync(download.Stream));
        }

        Assert.Equal(0, download.Offset);
        Assert.Equal(10, download.ContentLength);
        Assert.Equal(10, download.TotalLength);
        Assert.Equal("\"v1\"", download.Validator);
        var sent = Assert.Single(server.Seen);
        Assert.Null(sent.Header("Range"));
        Assert.Contains("Token=\"key\"", sent.Authorization, StringComparison.Ordinal);
    }

    /// <summary>
    /// A resume sends the range with the earlier validator, and a partial answer from that byte reports the offset and the whole length.
    /// True: an interrupted download continues where it stopped.
    /// False: every interruption would start a large file over, or resumed bytes would be written at the wrong place.
    /// </summary>
    [Fact]
    public async Task DownloadFileFromAsync_ResumeAccepted_ContinuesFromTheOffset()
    {
        var server = new ClientStubServer().On(DownloadPath, _ => File("456789", HttpStatusCode.PartialContent, new ContentRangeHeaderValue(4, 9, 10)));
        using var client = server.Client();

        var download = await client.DownloadFileFromAsync(Root, 4, "\"v1\"");

        await using (download.Stream)
        {
            Assert.Equal("456789", await ReadAsync(download.Stream));
        }

        Assert.Equal(4, download.Offset);
        Assert.Equal(6, download.ContentLength);
        Assert.Equal(10, download.TotalLength);
        var sent = Assert.Single(server.Seen);
        Assert.Equal("bytes=4-", sent.Header("Range"));
        Assert.Equal("\"v1\"", sent.Header("If-Range"));
    }

    /// <summary>
    /// A resume the source answers with the whole file starts at zero.
    /// True: a file that changed since the first attempt is saved whole.
    /// False: the new file's start would be appended after the old file's bytes.
    /// </summary>
    [Fact]
    public async Task DownloadFileFromAsync_ResumeAnsweredWhole_StartsAtZero()
    {
        using var client = new ClientStubServer().On(DownloadPath, _ => File("0123456789")).Client();

        var download = await client.DownloadFileFromAsync(Root, 4, "\"v1\"");

        await using (download.Stream)
        {
            Assert.Equal(0, download.Offset);
            Assert.Equal(10, download.TotalLength);
        }
    }

    /// <summary>
    /// A resume without a usable validator asks for the whole file.
    /// True: bytes are only appended when the source can prove the file is the same.
    /// False: a changed file would be stitched onto the old one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("W/\"weak\"")]
    [InlineData("not a date")]
    public async Task DownloadFileFromAsync_NoUsableValidator_SendsNoRange(string? validator)
    {
        var server = new ClientStubServer().On(DownloadPath, _ => File("0123456789"));
        using var client = server.Client();

        var download = await client.DownloadFileFromAsync(Root, 4, validator);

        await using (download.Stream)
        {
            Assert.Null(Assert.Single(server.Seen).Header("Range"));
            Assert.Equal(0, download.Offset);
        }
    }

    /// <summary>
    /// A resume past the end of the file starts over with a plain request.
    /// True: a file that got shorter is downloaded whole.
    /// False: the download would fail on every attempt and never recover.
    /// </summary>
    [Fact]
    public async Task DownloadFileFromAsync_RangeNotSatisfiable_StartsOver()
    {
        var server = new ClientStubServer().On(DownloadPath, request =>
            request.Header("Range") is null ? File("abc") : new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        using var client = server.Client();

        var download = await client.DownloadFileFromAsync(Root, 50, "\"v1\"");

        await using (download.Stream)
        {
            Assert.Equal("abc", await ReadAsync(download.Stream));
        }

        Assert.Equal(0, download.Offset);
        Assert.Equal(2, server.Seen.Count);
        Assert.Null(server.Seen[1].Header("Range"));
    }

    /// <summary>
    /// A partial answer this server did not ask for, or one from another byte, fails the download.
    /// True: bytes from an unknown place in the file are never written.
    /// False: a file would be saved with a hole or a shifted body and pass as complete.
    /// </summary>
    [Theory]
    [InlineData(0, null)]
    [InlineData(4, "\"v1\"")]
    public async Task DownloadFileFromAsync_UnaskedRange_Throws(long from, string? validator)
    {
        using var client = new ClientStubServer().On(DownloadPath, _ => File("789", HttpStatusCode.PartialContent, new ContentRangeHeaderValue(7, 9, 10))).Client();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadFileFromAsync(Root, from, validator));

        Assert.Contains("a range this server did not ask for", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused or failed download throws with the item and the cause, and a cancelled one throws the cancellation.
    /// True: the row records why the download failed, and a stopped task is not recorded as a failure.
    /// False: a failed download would return an empty stream and be saved as an empty file.
    /// </summary>
    [Fact]
    public async Task DownloadFileAsync_Failures_ThrowWithTheCause()
    {
        using var refusing = new ClientStubServer().Client();
        using var broken = new ClientStubServer().OnThrow(DownloadPath, new HttpRequestException("reset")).Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => refusing.DownloadFileAsync(Root));
        var down = await Assert.ThrowsAsync<InvalidOperationException>(() => broken.DownloadFileAsync(Root));

        Assert.StartsWith("Source download failed for item " + Root + ": HttpRequestException: ", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("reset", down.Message, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refusing.DownloadFileAsync(Root, cancelled.Token));
    }

    /// <summary>
    /// Closing a download's stream closes the body the source sent.
    /// True: a finished or abandoned download frees its connection.
    /// False: every download would leak a connection to the source.
    /// </summary>
    [Fact]
    public async Task DownloadFileAsync_StreamDisposed_ClosesTheResponseBody()
    {
        var body = new MemoryStream(Encoding.UTF8.GetBytes("abc"));
        using var client = new ClientStubServer().On(DownloadPath, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }).Client();

        var (stream, length) = await client.DownloadFileAsync(Root);
        await stream.DisposeAsync();

        Assert.Equal(3, length);
        Assert.False(body.CanRead);
    }

    /// <summary>
    /// Only external subtitle streams with a path are listed as companion files.
    /// True: a film's sidecar subtitles are downloaded with it.
    /// False: embedded tracks or external audio would be requested and write the main file into a subtitle.
    /// </summary>
    [Fact]
    public async Task GetCompanionFilesAsync_Streams_ListsOnlyExternalSubtitles()
    {
        var item = "{\"Id\":\"" + Root.ToString("N") + "\",\"Type\":\"Movie\",\"MediaSources\":[{\"Id\":\"ms1\",\"MediaStreams\":["
            + "{\"Type\":\"Subtitle\",\"IsExternal\":true,\"Path\":\"/m/film.en.srt\",\"Language\":\"eng\",\"Codec\":\"subrip\",\"Index\":3},"
            + "{\"Type\":\"Subtitle\",\"IsExternal\":false,\"Path\":\"/m/film.mkv\",\"Index\":2},"
            + "{\"Type\":\"Subtitle\",\"IsExternal\":true,\"Index\":4},"
            + "{\"Type\":\"Audio\",\"IsExternal\":true,\"Path\":\"/m/film.commentary.mka\",\"Index\":5}]}]}";
        using var client = new ClientStubServer().OnJson("/Items", ClientJson.Page(1, item)).Client();

        var companions = await client.GetCompanionFilesAsync(Root);

        var companion = Assert.Single(companions);
        Assert.Equal("/m/film.en.srt", companion.SourcePath);
        Assert.Equal("film.en.srt", companion.FileName);
        Assert.Equal("eng", companion.Language);
        Assert.Equal("subrip", companion.Codec);
        Assert.Equal(3, companion.StreamIndex);
        Assert.Equal("ms1", companion.MediaSourceId);
        Assert.True(companion.IsExternal);
    }

    /// <summary>
    /// An item that is gone, or has no media source or streams, has no companion files.
    /// True: an empty list always means the source has none.
    /// False: an item without streams would fail its download.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{\"Type\":\"Movie\"}")]
    [InlineData("{\"Type\":\"Movie\",\"MediaSources\":[]}")]
    [InlineData("{\"Type\":\"Movie\",\"MediaSources\":[{\"Id\":\"ms1\"}]}")]
    public async Task GetCompanionFilesAsync_NothingToList_ReturnsEmpty(string? item)
    {
        using var client = new ClientStubServer().OnJson("/Items", item is null ? ClientJson.Page(0) : ClientJson.Page(1, item)).Client();

        var companions = await client.GetCompanionFilesAsync(Root);

        Assert.Empty(companions);
    }

    /// <summary>
    /// A subtitle is asked for through the subtitle stream route in its own format, taken from its extension and else from its codec.
    /// True: an .ass file arrives as .ass, and a path without an extension still downloads.
    /// False: the source would convert the subtitle, or serve the main video in its place.
    /// </summary>
    [Theory]
    [InlineData("/m/film.en.SRT", "ass", "srt")]
    [InlineData("/m/film.en.ass", "subrip", "ass")]
    [InlineData("/m/subtitle", "subrip", "srt")]
    [InlineData("/m/subtitle", "WebVTT", "webvtt")]
    [InlineData("/m/subtitle", null, "srt")]
    public async Task DownloadCompanionFileAsync_Format_ComesFromExtensionThenCodec(string sourcePath, string? codec, string expectedFormat)
    {
        var path = "/Videos/" + Root + "/ms%201/Subtitles/3/Stream." + expectedFormat;
        var server = new ClientStubServer().On(path, _ => File("1\n00:00 --> 00:01\nhi"));
        using var client = server.Client();
        var companion = new CompanionFileInfo { SourcePath = sourcePath, Codec = codec, StreamIndex = 3, MediaSourceId = "ms 1" };

        await using var stream = await client.DownloadCompanionFileAsync(Root, companion);

        Assert.Equal("1\n00:00 --> 00:01\nhi", await ReadAsync(stream!));
        Assert.Equal(path, Assert.Single(server.Seen).Path);
    }

    /// <summary>
    /// A subtitle that cannot be downloaded answers null, and a missing companion is refused.
    /// True: a missing subtitle never fails the film it belongs to.
    /// False: one bad subtitle would mark the whole download as failed.
    /// </summary>
    [Fact]
    public async Task DownloadCompanionFileAsync_Failure_ReturnsNull()
    {
        using var client = new ClientStubServer().Client();
        var companion = new CompanionFileInfo { SourcePath = "/m/a.srt", StreamIndex = 1, MediaSourceId = "ms1" };

        Assert.Null(await client.DownloadCompanionFileAsync(Root, companion));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.DownloadCompanionFileAsync(Root, null!));
    }

    private static HttpResponseMessage File(string body, HttpStatusCode status = HttpStatusCode.OK, ContentRangeHeaderValue? range = null, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        response.Content.Headers.ContentRange = range;
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static async Task<string> ReadAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}

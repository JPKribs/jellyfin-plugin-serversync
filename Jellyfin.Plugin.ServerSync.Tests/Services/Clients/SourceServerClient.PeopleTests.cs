using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Kiota.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// The People read must return every person or fail, since a partial list would prune rows for people it never saw.
/// </summary>
public sealed class SourceServerClientPeopleTests
{
    /// <summary>
    /// Every person the source lists is returned from the paged list, which asks for Person items and the compared fields in a fixed order.
    /// True: the People scan compares overview, ids, tags, and lock state.
    /// False: fields left out of the request would read as blank and overwrite local values.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_Answer_ReturnsEveryPerson()
    {
        var server = new ClientStubServer().OnJson(
            "/Items",
            ClientJson.Page(2, ClientJson.Item(Guid.NewGuid(), "Person", name: "Ada"), ClientJson.Item(Guid.NewGuid(), "Person", name: "Grace")));
        using var client = server.Client();

        var people = await client.GetAllPersonsAsync();

        Assert.Equal(new[] { "Ada", "Grace" }, people.Select(p => p.Name));
        var sent = Assert.Single(server.Seen);
        Assert.Contains("includeItemTypes=Person", sent.Query, StringComparison.Ordinal);
        Assert.Contains("recursive=true", sent.Query, StringComparison.Ordinal);
        Assert.Contains("sortBy=SortName&sortBy=DateCreated", sent.Query, StringComparison.Ordinal);
        Assert.Contains("startIndex=0", sent.Query, StringComparison.Ordinal);
        Assert.Contains("limit=" + SourceServerClient.PersonPageSize.ToString(CultureInfo.InvariantCulture), sent.Query, StringComparison.Ordinal);
        Assert.Contains("fields=Overview&fields=ProviderIds&fields=Tags&fields=OriginalTitle&fields=SortName&fields=DateCreated&fields=ProductionLocations&fields=Settings", sent.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A catalog larger than one page is read page by page until the total.
    /// True: no one request carries a large source's whole catalog.
    /// False: only the first page of people would be listed, and the scan would prune the rest.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_CatalogLargerThanAPage_ReadsEveryPage()
    {
        var total = SourceServerClient.PersonPageSize + 3;
        var server = Paged(total, index => Person("Person " + index.ToString(CultureInfo.InvariantCulture)));
        using var client = server.Client();

        var people = await client.GetAllPersonsAsync();

        Assert.Equal(total, people.Count);
        Assert.Equal(total, people.Select(p => p.Name).Distinct().Count());
        Assert.Equal(2, server.To("/Items").Count);
        Assert.Empty(server.To("/Persons"));
    }

    /// <summary>
    /// A name the source holds under two people keeps the one the source's lookup by name answers with.
    /// True: the scan reads the same person the unpaged list gave it.
    /// False: the scan could take the other person's bio and picture for that name.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_NameHeldTwice_KeepsTheOneTheSourceUses()
    {
        var unused = Guid.NewGuid();
        var used = Guid.NewGuid();
        var server = new ClientStubServer()
            .OnJson("/Items", ClientJson.Page(3, ClientJson.Item(unused, "Person", name: "Ada"), ClientJson.Item(used, "Person", name: "Ada"), Person("Grace")))
            .OnJson("/Persons/Ada", ClientJson.Item(used, "Person", name: "Ada"));
        using var client = server.Client();

        var people = await client.GetAllPersonsAsync();

        Assert.Equal(new Guid?[] { used }, people.Where(p => p.Name == "Ada").Select(p => p.Id));
        Assert.Equal(2, people.Count);
        Assert.Single(server.To("/Persons/Ada"));
    }

    /// <summary>
    /// A doubled name the source's lookup does not know keeps the first person listed.
    /// True: the name is still offered once.
    /// False: the name would be offered twice, or a lookup that finds no one would fail the scan.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_NameHeldTwiceAndLookupFindsNoOne_KeepsTheFirst()
    {
        var first = Guid.NewGuid();
        var server = new ClientStubServer().OnJson("/Items", ClientJson.Page(2, ClientJson.Item(first, "Person", name: "Ada"), Person("Ada")));
        using var client = server.Client();

        var people = await client.GetAllPersonsAsync();

        Assert.Equal(first, Assert.Single(people).Id);
    }

    /// <summary>
    /// A lookup that fails for a doubled name fails the read.
    /// True: the scan stops and keeps its rows.
    /// False: a source that went down partway would decide which person a name means.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_LookupFails_Rethrows()
    {
        var server = new ClientStubServer()
            .OnJson("/Items", ClientJson.Page(2, Person("Ada"), Person("Ada")))
            .OnStatus("/Persons/Ada", HttpStatusCode.ServiceUnavailable);
        using var client = server.Client();

        var refused = await Assert.ThrowsAnyAsync<ApiException>(() => client.GetAllPersonsAsync());

        Assert.Equal(503, refused.ResponseStatusCode);
    }

    /// <summary>
    /// A list that moves while it is read fails the read, whether a person comes twice or the count ends short of the total.
    /// True: the scan stops and keeps its rows.
    /// False: a person skipped between two pages would be pruned.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAllPersonsAsync_ListMovesWhileRead_Throws(bool repeats)
    {
        var again = ClientJson.Item(Guid.NewGuid(), "Person", name: "Ada");
        var total = SourceServerClient.PersonPageSize + (repeats ? 1 : 5);
        var server = Paged(total, index => repeats && (index == 0 || index == SourceServerClient.PersonPageSize) ? again : Person("Person " + index.ToString(CultureInfo.InvariantCulture)), held: SourceServerClient.PersonPageSize + 1);
        using var client = server.Client();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAllPersonsAsync());

        Assert.Contains("changed while it was read", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source whose paged list holds no one is asked for the unpaged list, once and marked as slow to answer.
    /// True: an older source that hides Person items from a standard user's key still lists its people.
    /// False: such a source would list no one and its people would never sync.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_PagedListEmpty_AsksTheUnpagedList()
    {
        var server = new ClientStubServer().OnJson("/Items", ClientJson.Page(0)).OnJson("/Persons", ClientJson.Page(1, Person("Ada")));
        using var client = server.Client();

        var people = await client.GetAllPersonsAsync();

        Assert.Equal("Ada", Assert.Single(people).Name);
        Assert.Equal("?fields=Overview&fields=ProviderIds&fields=Tags&fields=OriginalTitle&fields=SortName&fields=DateCreated&fields=ProductionLocations&fields=Settings", Assert.Single(server.To("/Persons")).Query);
    }

    /// <summary>
    /// A source with no people answers an empty list both ways, which is not a failure.
    /// True: an empty catalog is read as empty.
    /// False: a source with no people would fail every People scan.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_EmptyCatalog_ReturnsEmpty()
    {
        using var client = new ClientStubServer().OnJson("/Items", ClientJson.Page(0)).OnJson("/Persons", ClientJson.Page(0)).Client();

        var people = await client.GetAllPersonsAsync();

        Assert.Empty(people);
    }

    /// <summary>
    /// An answer with no list fails the read, from the paged list and from the unpaged one.
    /// True: the scan stops and keeps its rows.
    /// False: a missing answer would read as no people and prune every row.
    /// </summary>
    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"TotalRecordCount\":5}", true)]
    [InlineData("{}", false)]
    [InlineData("{\"TotalRecordCount\":5}", false)]
    public async Task GetAllPersonsAsync_AnswerWithoutAList_Throws(string body, bool paged)
    {
        var server = paged
            ? new ClientStubServer().OnJson("/Items", body)
            : new ClientStubServer().OnJson("/Items", ClientJson.Page(0)).OnJson("/Persons", body);
        using var client = server.Client();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAllPersonsAsync());

        Assert.Contains("no response", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An error status and a broken connection both throw what went wrong, and the unpaged list is not asked.
    /// True: the scan stops before it prunes.
    /// False: a source that is down would look like one with no people.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_SourceFails_Rethrows()
    {
        var refusingServer = new ClientStubServer().OnStatus("/Items", HttpStatusCode.ServiceUnavailable);
        using var refusing = refusingServer.Client();
        using var broken = new ClientStubServer().OnThrow("/Items", new HttpRequestException("down")).Client();

        var refused = await Assert.ThrowsAnyAsync<ApiException>(() => refusing.GetAllPersonsAsync());
        var down = await Assert.ThrowsAsync<HttpRequestException>(() => broken.GetAllPersonsAsync());

        Assert.Equal(503, refused.ResponseStatusCode);
        Assert.Equal("down", down.Message);
        Assert.Empty(refusingServer.To("/Persons"));
    }

    /// <summary>
    /// A cancelled read throws the cancellation.
    /// True: a stopped task ends the read.
    /// False: a cancelled read would be logged and handled as a failed source.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_Cancelled_Throws()
    {
        using var client = new ClientStubServer().Client();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAllPersonsAsync(cancelled.Token));
    }

    private static string Person(string name) => ClientJson.Item(Guid.NewGuid(), "Person", name: name);

    /// <summary>
    /// Builds a source that answers the paged list from the start index and limit it is asked for.
    /// </summary>
    /// <param name="total">The total every page reports.</param>
    /// <param name="person">Writes the person at an index.</param>
    /// <param name="held">How many people the source really holds, or null for the total.</param>
    private static ClientStubServer Paged(int total, Func<int, string> person, int? held = null)
        => new ClientStubServer().On("/Items", request =>
        {
            var start = int.Parse(Regex.Match(request.Query, "startIndex=(\\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var limit = int.Parse(Regex.Match(request.Query, "limit=(\\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
            var count = Math.Max(0, Math.Min(limit, (held ?? total) - start));
            return ClientStubServer.Json(ClientJson.Page(total, Enumerable.Range(start, count).Select(person).ToArray()));
        });
}

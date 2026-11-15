using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
    /// Every person the source lists is returned from one call that asks for the compared fields.
    /// True: the People scan compares overview, ids, tags, and lock state.
    /// False: fields left out of the request would read as blank and overwrite local values.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_Answer_ReturnsEveryPerson()
    {
        var server = new ClientStubServer().OnJson(
            "/Persons",
            ClientJson.Page(2, ClientJson.Item(Guid.NewGuid(), "Person", name: "Ada"), ClientJson.Item(Guid.NewGuid(), "Person", name: "Grace")));
        using var client = server.Client();

        var people = await client.GetAllPersonsAsync();

        Assert.Equal(new[] { "Ada", "Grace" }, people.Select(p => p.Name));
        var sent = Assert.Single(server.Seen);
        Assert.Equal("?fields=Overview&fields=ProviderIds&fields=Tags&fields=OriginalTitle&fields=SortName&fields=DateCreated&fields=ProductionLocations&fields=Settings", sent.Query);
    }

    /// <summary>
    /// A source with no people answers an empty list, which is not a failure.
    /// True: an empty catalog is read as empty.
    /// False: a source with no people would fail every People scan.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_EmptyCatalog_ReturnsEmpty()
    {
        using var client = new ClientStubServer().OnJson("/Persons", ClientJson.Page(0)).Client();

        var people = await client.GetAllPersonsAsync();

        Assert.Empty(people);
    }

    /// <summary>
    /// An answer with no list fails the read.
    /// True: the scan stops and keeps its rows.
    /// False: a missing answer would read as no people and prune every row.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"TotalRecordCount\":5}")]
    public async Task GetAllPersonsAsync_AnswerWithoutAList_Throws(string body)
    {
        using var client = new ClientStubServer().OnJson("/Persons", body).Client();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAllPersonsAsync());

        Assert.Contains("no response", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An error status and a broken connection both throw what went wrong.
    /// True: the scan stops before it prunes.
    /// False: a source that is down would look like one with no people.
    /// </summary>
    [Fact]
    public async Task GetAllPersonsAsync_SourceFails_Rethrows()
    {
        using var refusing = new ClientStubServer().OnStatus("/Persons", HttpStatusCode.ServiceUnavailable).Client();
        using var broken = new ClientStubServer().OnThrow("/Persons", new HttpRequestException("down")).Client();

        var refused = await Assert.ThrowsAnyAsync<ApiException>(() => refusing.GetAllPersonsAsync());
        var down = await Assert.ThrowsAsync<HttpRequestException>(() => broken.GetAllPersonsAsync());

        Assert.Equal(503, refused.ResponseStatusCode);
        Assert.Equal("down", down.Message);
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
}

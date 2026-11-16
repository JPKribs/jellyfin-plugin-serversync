using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Clients;

/// <summary>
/// A request marked as slow to answer is sent once with no attempt limit, and every other request keeps its retries.
/// </summary>
public sealed class SlowAnswerHandlerTests
{
    /// <summary>
    /// The list of every person reaches the source with the retry turned off.
    /// True: the mark set on the SDK request is read from the HTTP request.
    /// False: the list would still be cut off at the attempt limit on a large source.
    /// </summary>
    [Fact]
    public async Task SendAsync_PersonList_TurnsTheRetryOff()
    {
        var server = new ClientStubServer().OnJson("/Persons", ClientJson.Page(0));
        using var client = Client(new SlowAnswerHandler { InnerHandler = server });

        await client.GetAllPersonsAsync();

        Assert.True(Assert.Single(server.Seen).NoRetry);
    }

    /// <summary>
    /// A request with no mark keeps its retries.
    /// True: only a marked request is sent once.
    /// False: every call to a source would lose its retries.
    /// </summary>
    [Fact]
    public async Task SendAsync_UnmarkedRequest_KeepsTheRetry()
    {
        var server = new ClientStubServer().OnJson("/any", "{}");
        using var http = new HttpClient(new SlowAnswerHandler { InnerHandler = server });

        using var answer = await http.GetAsync(new Uri(ClientStubServer.Url + "/any"));

        Assert.False(Assert.Single(server.Seen).NoRetry);
    }

    /// <summary>
    /// A source that takes longer than the attempt limit still answers the list of every person, on the first try.
    /// True: the People refresh finishes against a large source.
    /// False: the refresh would fail three times on every run and never list a person.
    /// </summary>
    [Fact]
    public async Task SendAsync_PersonListSlowerThanTheAttemptLimit_AnswersOnce()
    {
        var server = new ClientStubServer().OnJson("/Persons", ClientJson.Page(1, ClientJson.Item(Guid.NewGuid(), "Person", name: "Ada")));
        using var client = Client(Chain(server));

        var people = await client.GetAllPersonsAsync();

        Assert.Equal("Ada", Assert.Single(people).Name);
        Assert.Single(server.Seen);
    }

    /// <summary>
    /// The same slow source fails an unmarked request after every try, which is what the mark is there to avoid.
    /// True: the attempt limit in this chain is real, so the test above proves the mark and not a loose limit.
    /// False: the slow answer test would pass with or without the handler.
    /// </summary>
    [Fact]
    public async Task SendAsync_UnmarkedRequestSlowerThanTheAttemptLimit_Fails()
    {
        var server = new ClientStubServer().OnJson("/any", "{}");
        using var http = new HttpClient(Chain(server));

        await Assert.ThrowsAsync<TaskCanceledException>(() => http.GetAsync(new Uri(ClientStubServer.Url + "/any")));

        Assert.Empty(server.Seen);
    }

    private static SourceServerClient Client(HttpMessageHandler handler)
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), ClientStubServer.Url, "key", "test", "1.2.3.4");

    /// <summary>Builds the plugin's handler order over a source that takes 300 ms to answer, with a 50 ms attempt limit.</summary>
    private static SlowAnswerHandler Chain(ClientStubServer server)
    {
        var retry = new JpkRetryHandler(new JpkRetryOptions
        {
            MaxAttempts = 2,
            AttemptTimeout = TimeSpan.FromMilliseconds(50),
            Delay = (_, _) => Task.CompletedTask
        })
        {
            InnerHandler = new Slow { InnerHandler = server }
        };
        return new SlowAnswerHandler { InnerHandler = retry };
    }

    /// <summary>Waits before passing a request on, as a source building a large answer does.</summary>
    private sealed class Slow : DelegatingHandler
    {
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

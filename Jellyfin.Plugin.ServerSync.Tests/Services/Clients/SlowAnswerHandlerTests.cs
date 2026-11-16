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
    /// <summary>How long the slow source takes when it does answer, well past the 50 ms limits used here.</summary>
    private static readonly TimeSpan Answering = TimeSpan.FromMilliseconds(300);

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
        using var client = Client(Chain(server, Answering));

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

    /// <summary>
    /// An unmarked request that takes longer than the ordinary limit is cut off, with the timeout the client itself would give.
    /// True: raising the client's limit for the people list leaves every other request held to its own limit.
    /// False: a source that never answers would hold every call for thirty minutes.
    /// </summary>
    [Fact]
    public async Task SendAsync_UnmarkedRequestPastTheLimit_TimesOut()
    {
        var server = new ClientStubServer().OnJson("/any", "{}");
        using var http = new HttpClient(new SlowAnswerHandler(TimeSpan.FromMilliseconds(50)) { InnerHandler = new Slow { InnerHandler = server } });

        var failure = await Assert.ThrowsAsync<TaskCanceledException>(() => http.GetAsync(new Uri(ClientStubServer.Url + "/any")));

        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// A marked request is not held to the ordinary limit.
    /// True: the people list may take longer than five minutes.
    /// False: the list would be cut off at five minutes on a source that needs longer.
    /// </summary>
    [Fact]
    public async Task SendAsync_PersonListPastTheOrdinaryLimit_Answers()
    {
        var server = new ClientStubServer().OnJson("/Persons", ClientJson.Page(0));
        using var client = Client(new SlowAnswerHandler(TimeSpan.FromMilliseconds(50)) { InnerHandler = new Slow(Answering) { InnerHandler = server } });

        var people = await client.GetAllPersonsAsync();

        Assert.Empty(people);
    }

    /// <summary>
    /// A caller that cancels gets its own cancellation, not a timeout.
    /// True: a stopped task ends as stopped.
    /// False: a stopped task would be reported as a source that did not answer.
    /// </summary>
    [Fact]
    public async Task SendAsync_CallerCancels_IsNotATimeout()
    {
        using var http = new HttpClient(new SlowAnswerHandler { InnerHandler = new Slow { InnerHandler = new ClientStubServer() } });
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync(new Uri(ClientStubServer.Url + "/any"), stop.Token));

        Assert.IsNotType<TimeoutException>(failure.InnerException);
    }

    /// <summary>
    /// A limit of zero or less is refused.
    /// True: a handler cannot be built that cuts every request off at once.
    /// False: a mistyped limit would fail every call to a source.
    /// </summary>
    [Fact]
    public void Constructor_LimitNotPositive_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SlowAnswerHandler(TimeSpan.Zero));

    private static SourceServerClient Client(HttpMessageHandler handler)
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(handler), ClientStubServer.Url, "key", "test", "1.2.3.4");

    /// <summary>Builds the plugin's handler order over a slow source, with a 50 ms attempt limit.</summary>
    private static SlowAnswerHandler Chain(ClientStubServer server, TimeSpan? wait = null)
    {
        var retry = new JpkRetryHandler(new JpkRetryOptions
        {
            MaxAttempts = 2,
            AttemptTimeout = TimeSpan.FromMilliseconds(50),
            Delay = (_, _) => Task.CompletedTask
        })
        {
            InnerHandler = new Slow(wait) { InnerHandler = server }
        };
        return new SlowAnswerHandler { InnerHandler = retry };
    }

    /// <summary>
    /// Waits before passing a request on, as a source building a large answer does.
    /// With no wait given it never answers, so only a cancellation ends the request and no test rests on which timer fires first.
    /// </summary>
    private sealed class Slow : DelegatingHandler
    {
        private readonly TimeSpan _wait;

        public Slow(TimeSpan? wait = null) => _wait = wait ?? Timeout.InfiniteTimeSpan;

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_wait, cancellationToken).ConfigureAwait(false);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

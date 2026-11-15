using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link check must flag only links the source says cannot play, and never take an outage for a dead link.
/// </summary>
public sealed class LinkVerifierTests : IDisposable
{
    private readonly LinkTestTable _store = new();

    /// <inheritdoc/>
    public void Dispose() => _store.Dispose();

    /// <summary>
    /// The verifier and its checks refuse missing arguments, though it runs without media info.
    /// True: the mistake is named where it was made.
    /// False: a null table or client would fail partway through a check.
    /// </summary>
    [Fact]
    public async Task Members_NullArguments_Throw()
    {
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        var (host, _) = InterfaceStub.Create<IServerApplicationHost>();
        var verifier = Verifier();
        using var client = Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var row = LinkTestTable.Row("aaaa", _store.Media("A.strm"));

        Assert.Throws<ArgumentNullException>(() => new LinkVerifier(null!, null, host, NullLogger<LinkVerifier>.Instance));
        Assert.Throws<ArgumentNullException>(() => new LinkVerifier(library, null, null!, NullLogger<LinkVerifier>.Instance));
        Assert.Throws<ArgumentNullException>(() => new LinkVerifier(library, null, host, null!));
        Assert.Throws<ArgumentNullException>(() => LinkVerifier.Flagged(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.VerifyAsync(null!, _ => null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.VerifyAsync(_store.Table, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.GoneFromSourceAsync(null!, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.GoneFromSourceAsync(row, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.RedescribeAsync(null!, client, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.RedescribeAsync(row, null!, CancellationToken.None));
    }

    /// <summary>
    /// A row is flagged only when it is errored with a reason the link check wrote.
    /// True: an ordinary failed write is retried, and only the link check settles its own flags.
    /// False: a write error would be mistaken for a dead link, or a dead link retried for ever.
    /// </summary>
    /// <param name="status">The row's status.</param>
    /// <param name="reason">The row's reason.</param>
    /// <param name="expected">Whether the row is flagged.</param>
    [Theory]
    [InlineData(SyncStatus.Errored, "Link check: the file is no longer on the source", true)]
    [InlineData(SyncStatus.Errored, "link check: lower case", false)]
    [InlineData(SyncStatus.Errored, "A .strm that Server Sync did not write is already there", false)]
    [InlineData(SyncStatus.Errored, null, false)]
    [InlineData(SyncStatus.Synced, "Link check: stale reason", false)]
    public void Flagged_NeedsErroredAndThePrefix(SyncStatus status, string? reason, bool expected)
    {
        var row = new SyncItem { Status = status, Reason = reason };

        var flagged = LinkVerifier.Flagged(row);

        Assert.Equal(expected, flagged);
    }

    /// <summary>
    /// Links whose server has no client here are not checked and not flagged.
    /// True: a disabled server's links wait as they are.
    /// False: disabling a server would flag every link to it as dead.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_NoClientForTheServer_ChecksNothing()
    {
        _store.Table.Upsert(LinkTestTable.Row("aaaa", _store.Media("A.strm")));

        var found = await Verifier().VerifyAsync(_store.Table, _ => null, CancellationToken.None);

        Assert.Equal(new LinkVerification(0, 0, 0, 0), found);
        Assert.Equal(SyncStatus.Synced, _store.Table.GetBySourceItemId("aaaa")!.Status);
    }

    /// <summary>
    /// A link the source's answer leaves out is flagged with the plain reason, and counted dead.
    /// True: a file the source no longer knows shows as a dead link.
    /// False: a link to a deleted file would look healthy until someone pressed play.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_ItemMissingFromTheAnswer_IsFlagged()
    {
        _store.Table.Upsert(LinkTestTable.Row("aaaa", _store.Media("A.strm")));
        using var handler = Answers(new PeerLinkDescribeResponse());
        using var client = Client(handler);

        var found = await Verifier().VerifyAsync(_store.Table, _ => client, CancellationToken.None);

        var row = _store.Table.GetBySourceItemId("aaaa")!;
        Assert.Equal(new LinkVerification(1, 1, 0, 0), found);
        Assert.True(LinkVerifier.Flagged(row));
        Assert.Equal(LinkVerifier.Prefix + "the file is no longer on the source", row.Reason);
        Assert.Contains("\"Details\":false", Assert.Single(handler.Bodies), StringComparison.Ordinal);
    }

    /// <summary>
    /// A check cancelled before it starts ends as cancelled and changes no row.
    /// True: stopping the task leaves every link as it was.
    /// False: a cancelled check would flag links it never asked about.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_Cancelled_ThrowsAndChangesNothing()
    {
        _store.Table.Upsert(LinkTestTable.Row("aaaa", _store.Media("A.strm")));
        using var handler = Answers(new PeerLinkDescribeResponse());
        using var client = Client(handler);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => Verifier().VerifyAsync(_store.Table, _ => client, cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(SyncStatus.Synced, _store.Table.GetBySourceItemId("aaaa")!.Status);
    }

    /// <summary>
    /// A file counts as gone only when the source says it does not exist, and not when it merely cannot read it.
    /// True: a link is re-homed or removed only for a file that really left the source.
    /// False: a source with a disk unmounted would lose every link to it.
    /// </summary>
    /// <param name="exists">Whether the source says the file exists.</param>
    /// <param name="reason">The source's reason.</param>
    /// <param name="expected">Whether the file is gone.</param>
    [Theory]
    [InlineData(false, "the file is no longer on the source", true)]
    [InlineData(false, null, true)]
    [InlineData(false, PeerLinkDescription.Unreadable, false)]
    [InlineData(true, null, false)]
    public async Task GoneFromSourceAsync_OnlyWhenTheSourceSaysSo(bool exists, string? reason, bool expected)
    {
        var answer = new PeerLinkDescribeResponse();
        answer.Items.Add(new PeerLinkDescription { ItemId = "AAAA", Exists = exists, Reason = reason });
        using var handler = Answers(answer);
        using var client = Client(handler);

        var gone = await Verifier().GoneFromSourceAsync(LinkTestTable.Row("aaaa", _store.Media("A.strm")), client, CancellationToken.None);

        Assert.Equal(expected, gone);
    }

    /// <summary>
    /// A source that does not list the file, does not know the check, or answers with an error does not make the file gone.
    /// True: only a clear answer removes a link.
    /// False: an older or failing source would have its links re-homed away.
    /// </summary>
    /// <param name="status">The status the source answers with.</param>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GoneFromSourceAsync_NoClearAnswer_IsFalse(HttpStatusCode status)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("{\"Items\":[]}", Encoding.UTF8, "application/json") });
        using var client = Client(handler);

        var gone = await Verifier().GoneFromSourceAsync(LinkTestTable.Row("aaaa", _store.Media("A.strm")), client, CancellationToken.None);

        Assert.False(gone);
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>
    /// A source that cannot be reached does not make the file gone.
    /// True: an outage leaves links in place.
    /// False: a network blip would remove links.
    /// </summary>
    [Fact]
    public async Task GoneFromSourceAsync_SourceUnreachable_IsFalse()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = Client(handler);

        var gone = await Verifier().GoneFromSourceAsync(LinkTestTable.Row("aaaa", _store.Media("A.strm")), client, CancellationToken.None);

        Assert.False(gone);
    }

    /// <summary>
    /// Without the media info service, or for a row with no local path, nothing is taken again and the source is not asked.
    /// True: a server that cannot save media info does not spend a call on it.
    /// False: every replaced file would cost a request whose answer is thrown away.
    /// </summary>
    /// <param name="hasPath">Whether the row has a local path.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedescribeAsync_NothingToApplyTo_IsFalseWithoutAsking(bool hasPath)
    {
        using var handler = Answers(new PeerLinkDescribeResponse());
        using var client = Client(handler);
        var row = LinkTestTable.Row("aaaa", hasPath ? _store.Media("A.strm") : null);

        var taken = await Verifier().RedescribeAsync(row, client, CancellationToken.None);

        Assert.False(taken);
        Assert.Equal(0, handler.Requests);
    }

    private static StubHandler Answers(PeerLinkDescribeResponse answer)
        => new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json") });

    private static SourceServerClient Client(HttpMessageHandler handler)
        => new(NullLogger<SourceServerClient>.Instance, new HttpClient(handler, disposeHandler: false), "http://peer", "k", "test", "0");

    private static LinkVerifier Verifier()
    {
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "local");
        return new LinkVerifier(library, null, host, NullLogger<LinkVerifier>.Instance);
    }
}

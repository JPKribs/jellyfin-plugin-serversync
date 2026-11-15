using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// A link whose server lost the file must move to another server that holds it, and go only when none does.
/// </summary>
public sealed class LinkRehomerTests : IDisposable
{
    private const string OldId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly LinkTestTable _store = new();

    /// <inheritdoc/>
    public void Dispose() => _store.Dispose();

    /// <summary>
    /// The rehomer refuses missing services and a missing row, though it runs without a writer.
    /// True: the mistake is named where it was made.
    /// False: a null would fail after a row had been half moved.
    /// </summary>
    [Fact]
    public async Task Members_NullArguments_Throw()
    {
        var config = new FakeConfigManager();
        var factory = new FakeClientFactory();
        var (host, _) = InterfaceStub.Create<IServerApplicationHost>();
        var logger = NullLogger<LinkRehomer>.Instance;

        Assert.Throws<ArgumentNullException>(() => new LinkRehomer(null!, factory, _store.Table, host, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkRehomer(config, null!, _store.Table, host, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkRehomer(config, factory, null!, host, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkRehomer(config, factory, _store.Table, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new LinkRehomer(config, factory, _store.Table, host, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => new LinkRehomer(config, factory, _store.Table, host, logger).SettleGoneAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// A link moves to the other server that holds the file, under that server's id, path, library, and size, and is queued to be written.
    /// True: a film removed from one source keeps playing from another that has it.
    /// False: the link would be deleted although a second server holds the same file.
    /// </summary>
    [Fact]
    public async Task SettleGoneAsync_AnotherServerHoldsTheFile_MovesTheLink()
    {
        var row = Link();
        using var handler = Answers(new LinkStreamCheckResponse { Allowed = true, ItemId = NewId, Size = 9000 });
        var factory = new LinkStubClientFactory(handler);

        var movedTo = await Rehomer(factory).SettleGoneAsync(row, CancellationToken.None);

        var moved = _store.Table.GetBySourceItemId(NewId)!;
        Assert.Equal("Server B", movedTo);
        Assert.Equal(new[] { "b" }, factory.Created);
        Assert.Null(_store.Table.GetBySourceItemId(OldId));
        Assert.Equal(("b", "lib-b", "/b/movies/Film.mkv", 9000L), (moved.ServerKey, moved.SourceLibraryId, moved.SourcePath, moved.SourceSize));
        Assert.Equal(StorageMode.Link, moved.StorageMode);
        Assert.Equal(SyncStatus.Queued, moved.Status);
        Assert.Null(moved.Reason);
        Assert.Equal(0, moved.RetryCount);
    }

    /// <summary>
    /// The other server is asked by the file's path there, as a probe, in this server's name.
    /// True: the other server matches its own copy of the file and counts no stream for the question.
    /// False: it would be asked for an id it never had, or charge a user's stream slot.
    /// </summary>
    [Fact]
    public async Task SettleGoneAsync_AsksByPathAsAProbe()
    {
        HttpRequestMessage? sent = null;
        using var handler = new StubHandler(request =>
        {
            sent = request;
            return Json(new LinkStreamCheckResponse { Allowed = false, Reason = "no" });
        });

        await Rehomer(new LinkStubClientFactory(handler)).SettleGoneAsync(Link(), CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("/Sync/Peer/Stream/Check", sent!.RequestUri!.AbsolutePath);
        Assert.Equal("this-server", body.RootElement.GetProperty("ServerId").GetString());
        Assert.Equal("/b/movies/Film.mkv", body.RootElement.GetProperty("Path").GetString());
        Assert.True(body.RootElement.GetProperty("Probe").GetBoolean());
    }

    /// <summary>
    /// A move that keeps the same id and reports no size keeps the row's own id and size.
    /// True: two servers that share an id for the file leave one row, with the size already known.
    /// False: the row would be deleted and lost, or its size wiped to 0.
    /// </summary>
    [Fact]
    public async Task SettleGoneAsync_SameIdAndNoSize_KeepsTheRowsOwn()
    {
        var row = Link();
        using var handler = Answers(new LinkStreamCheckResponse { Allowed = true, ItemId = OldId.ToUpperInvariant(), Size = 0 });

        await Rehomer(new LinkStubClientFactory(handler)).SettleGoneAsync(row, CancellationToken.None);

        var kept = _store.Table.GetBySourceItemId(OldId)!;
        Assert.Equal("b", kept.ServerKey);
        Assert.Equal(1, kept.SourceSize);
    }

    /// <summary>
    /// When the other server refuses, answers without a usable id, fails, or cannot be reached, the link is marked for removal.
    /// True: a link goes once no server holds its file, and one failing server never keeps a dead link alive.
    /// False: a dead link would stay in the library, or a throw would stop the whole run.
    /// </summary>
    /// <param name="answer">How the other server answers.</param>
    [Theory]
    [InlineData("refuses")]
    [InlineData("no-id")]
    [InlineData("error")]
    [InlineData("unreachable")]
    public async Task SettleGoneAsync_NoServerHoldsTheFile_MarksItForRemoval(string answer)
    {
        var row = Link();
        using var handler = new StubHandler(_ => answer switch
        {
            "refuses" => Json(new LinkStreamCheckResponse { Allowed = false, Reason = "the file is no longer on the source" }),
            "no-id" => Json(new LinkStreamCheckResponse { Allowed = true, ItemId = "not-an-id" }),
            "error" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => throw new HttpRequestException("connection refused")
        });

        var movedTo = await Rehomer(new LinkStubClientFactory(handler)).SettleGoneAsync(row, CancellationToken.None);

        var marked = _store.Table.GetBySourceItemId(OldId)!;
        Assert.Null(movedTo);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(SyncStatus.Deleting, marked.Status);
        Assert.Equal("a", marked.ServerKey);
    }

    /// <summary>
    /// With no other server set up for the file's folder, nobody is asked and the link is marked for removal.
    /// True: a link with one source goes when that source loses the file.
    /// False: the link would be kept for ever, or a server that never mapped the folder would be asked.
    /// </summary>
    [Fact]
    public async Task SettleGoneAsync_NoOtherServer_MarksItForRemovalWithoutAsking()
    {
        var row = Link();
        var factory = new FakeClientFactory();

        var movedTo = await Rehomer(factory, withOther: false).SettleGoneAsync(row, CancellationToken.None);

        Assert.Null(movedTo);
        Assert.Equal(0, factory.Requests);
        Assert.Equal(SyncStatus.Deleting, _store.Table.GetBySourceItemId(OldId)!.Status);
    }

    /// <summary>
    /// A settle cancelled before it asks ends as cancelled and leaves the row alone.
    /// True: stopping the task does not remove links.
    /// False: a cancelled run would mark links for deletion it never checked.
    /// </summary>
    [Fact]
    public async Task SettleGoneAsync_Cancelled_ThrowsAndKeepsTheRow()
    {
        var row = Link();
        using var handler = Answers(new LinkStreamCheckResponse { Allowed = true, ItemId = NewId });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => Rehomer(new LinkStubClientFactory(handler)).SettleGoneAsync(row, cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(SyncStatus.Synced, _store.Table.GetBySourceItemId(OldId)!.Status);
    }

    private static HttpResponseMessage Json(object body)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static StubHandler Answers(LinkStreamCheckResponse answer) => new(_ => Json(answer));

    private SyncItem Link()
    {
        var path = _store.Media("Film.strm");
        File.WriteAllText(path, "http://127.0.0.1:8096/Sync/Link/" + OldId + "?k=s");
        var row = LinkTestTable.Row(OldId, path, sourcePath: "/a/movies/Film.mkv", serverKey: "a");
        row.SourceLibraryId = "lib-a";
        row.Reason = "stale";
        row.RetryCount = 3;
        _store.Table.Upsert(row);
        return row;
    }

    private LinkRehomer Rehomer(ISourceServerClientFactory factory, bool withOther = true)
    {
        var config = new PluginConfiguration();
        config.Servers.Add(Server("a", "Server A", "/a/movies", "lib-a"));
        if (withOther)
        {
            config.Servers.Add(Server("b", "Server B", "/b/movies", "lib-b"));
        }

        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "this-server");
        return new LinkRehomer(new FakeConfigManager(config), factory, _store.Table, host, NullLogger<LinkRehomer>.Instance);
    }

    private SourceServer Server(string key, string name, string sourceRoot, string libraryId) => new()
    {
        Key = key,
        Name = name,
        Url = "http://" + key + ":8096",
        ApiKey = "k",
        Mode = ServerMode.Pull,
        LibraryMappings = { new LibraryMapping { SourceLibraryId = libraryId, LocalLibraryId = "l", SourceRootPath = sourceRoot, LocalRootPath = _store.MediaRoot } }
    };
}

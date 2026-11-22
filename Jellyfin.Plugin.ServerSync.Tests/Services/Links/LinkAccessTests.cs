using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Peer;
using Jellyfin.Plugin.ServerSync.Services.Clients;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// Link access must ask a link's server as the user this server maps the viewer to, and remember which servers answered.
/// </summary>
public sealed class LinkAccessTests
{
    private static readonly Guid LocalUser = Guid.Parse("11111111111111111111111111111111");
    private static readonly LinkEntry Link = new("/media/Movies/Film.strm", "/media/Movies/Film.mkv", "aaaa", "a", 1000);

    /// <summary>
    /// Link access and its members refuse missing services, servers, and links.
    /// True: the mistake is named where it was made.
    /// False: a null would fail in the middle of a press of play.
    /// </summary>
    [Fact]
    public async Task Members_NullArguments_Throw()
    {
        var config = new FakeConfigManager();
        var factory = new FakeClientFactory();
        var (host, _) = InterfaceStub.Create<IServerApplicationHost>();
        var logger = NullLogger<LinkAccess>.Instance;
        var health = new LinkHealth();
        var access = new LinkAccess(config, factory, host, logger, health);

        Assert.Throws<ArgumentNullException>(() => new LinkAccess(null!, factory, host, logger, health));
        Assert.Throws<ArgumentNullException>(() => new LinkAccess(config, null!, host, logger, health));
        Assert.Throws<ArgumentNullException>(() => new LinkAccess(config, factory, null!, logger, health));
        Assert.Throws<ArgumentNullException>(() => new LinkAccess(config, factory, host, null!, health));
        Assert.Throws<ArgumentNullException>(() => new LinkAccess(config, factory, host, logger, null!));
        Assert.Throws<ArgumentNullException>(() => LinkAccess.SourceUserFor(null!, LocalUser));
        Assert.Throws<ArgumentNullException>(() => access.ServerFor(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => access.ChooseAsync(null!, LocalUser, null, CancellationToken.None));
    }

    /// <summary>
    /// The user on the source is the one an enabled mapping pairs with the local user, and none for the probe or an unmapped user.
    /// True: the source is told which of its own users the viewer is, when this server knows.
    /// False: a disabled mapping would still speak for a user, or the probe would be sent as someone.
    /// </summary>
    /// <param name="local">The local user asked about, or null for the probe.</param>
    /// <param name="mappingEnabled">Whether the mapping is enabled.</param>
    /// <param name="expected">The user on the source, or null.</param>
    [Theory]
    [InlineData("11111111111111111111111111111111", true, "source-user")]
    [InlineData("11111111-1111-1111-1111-111111111111", true, "source-user")]
    [InlineData("11111111111111111111111111111111", false, null)]
    [InlineData("22222222222222222222222222222222", true, null)]
    [InlineData(null, true, null)]
    public void SourceUserFor_FollowsTheEnabledMapping(string? local, bool mappingEnabled, string? expected)
    {
        var server = Server("a");
        server.UserMappings.Add(new UserMapping { SourceUserId = "source-user", LocalUserId = LocalUser.ToString("N"), IsEnabled = mappingEnabled });
        server.UserMappings.Add(new UserMapping { SourceUserId = "broken", LocalUserId = "not-a-guid" });

        var sourceUser = LinkAccess.SourceUserFor(server, local is null ? null : Guid.Parse(local));

        Assert.Equal(expected, sourceUser);
    }

    /// <summary>
    /// A link's server is its entry here only while that entry is enabled and set up.
    /// True: a link to a server that was disabled or removed is not asked for.
    /// False: a disabled server would still be called on every play.
    /// </summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="enabled">Whether the entry is enabled.</param>
    /// <param name="apiKey">The entry's API key.</param>
    /// <param name="found">Whether the link has a server.</param>
    [Theory]
    [InlineData("a", true, "k", true)]
    [InlineData("A", true, "k", true)]
    [InlineData("a", false, "k", false)]
    [InlineData("a", true, "", false)]
    [InlineData("other", true, "k", false)]
    public void ServerFor_NeedsAnEnabledConfiguredEntry(string key, bool enabled, string apiKey, bool found)
    {
        var server = Server(key);
        server.IsEnabled = enabled;
        server.ApiKey = apiKey;
        var access = Access(new FakeClientFactory(), new LinkHealth(), server);

        var result = access.ServerFor(Link);

        Assert.Equal(found, result is not null);
    }

    /// <summary>
    /// A play is asked for by the link's id, as the viewer, naming this server and the viewer's user on the source.
    /// True: the source decides for the right file and the right one of its users.
    /// False: the source would decide for the wrong user, or count the play as a probe.
    /// </summary>
    [Fact]
    public async Task ChooseAsync_UserPlay_AsksAsThatUser()
    {
        using var handler = new StubHandler(_ => Json(new LinkStreamCheckResponse { Allowed = true, ItemId = "aaaa", Size = 1000 }));
        var server = Server("a");
        server.UserMappings.Add(new UserMapping { SourceUserId = "source-user", LocalUserId = LocalUser.ToString("N") });

        var choice = await Access(new LinkStubClientFactory(handler), new LinkHealth(), server).ChooseAsync(Link, LocalUser, null, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(new LinkRoute("a", "aaaa"), choice.Route);
        Assert.Null(choice.Refusal);
        Assert.Equal("this-server", body.RootElement.GetProperty("ServerId").GetString());
        Assert.Equal("aaaa", body.RootElement.GetProperty("ItemId").GetString());
        Assert.False(body.RootElement.GetProperty("Probe").GetBoolean());
        Assert.Equal(LocalUser.ToString("N"), body.RootElement.GetProperty("UserId").GetString());
        Assert.Equal("source-user", body.RootElement.GetProperty("SourceUserId").GetString());
    }

    /// <summary>
    /// A link whose own server is skipped, with no other server holding the file, is refused without asking anyone.
    /// True: a failover with nowhere to go ends at once with a reason.
    /// False: the server that just failed would be asked again, or the play would hang.
    /// </summary>
    [Fact]
    public async Task ChooseAsync_OnlyServerSkipped_RefusesWithoutAsking()
    {
        var factory = new FakeClientFactory();

        var choice = await Access(factory, new LinkHealth(), Server("a")).ChooseAsync(Link, LocalUser, new[] { "A" }, CancellationToken.None);

        Assert.Null(choice.Route);
        Assert.Equal("the server this link streams from failed", choice.Refusal);
        Assert.Equal(0, factory.Requests);
    }

    /// <summary>
    /// A server that cannot be reached is remembered as down, and one that answers, even with a refusal or an error, is not.
    /// True: only a server that did not answer is passed over on the next play.
    /// False: a server that refused one user would be skipped for everyone for a minute.
    /// </summary>
    /// <param name="answer">How the server answers.</param>
    /// <param name="down">Whether it is remembered as down.</param>
    /// <param name="refusal">The refusal the play ends with.</param>
    [Theory]
    [InlineData("unreachable", true, "Server a could not be reached")]
    [InlineData("refuses", false, "your account is not mapped to a user on the source")]
    [InlineData("error", false, "the source answered 500: ")]
    public async Task ChooseAsync_ServerDoesNotAllow_RemembersOnlyAnOutage(string answer, bool down, string refusal)
    {
        using var handler = new StubHandler(_ => answer switch
        {
            "unreachable" => throw new HttpRequestException("connection refused"),
            "refuses" => Json(new LinkStreamCheckResponse { Allowed = false, Reason = "your account is not mapped to a user on the source" }),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });
        var health = new LinkHealth();

        var choice = await Access(new LinkStubClientFactory(handler), health, Server("a")).ChooseAsync(Link, LocalUser, null, CancellationToken.None);

        Assert.Null(choice.Route);
        Assert.Equal(refusal, choice.Refusal);
        Assert.Equal(down, health.IsDown("a"));
    }

    /// <summary>
    /// A refusal with no reason is given a plain one.
    /// True: the viewer is always told something.
    /// False: the player would show a refusal with an empty reason.
    /// </summary>
    [Fact]
    public async Task ChooseAsync_RefusalWithoutReason_GetsAPlainOne()
    {
        using var handler = new StubHandler(_ => Json(new LinkStreamCheckResponse { Allowed = false }));

        var choice = await Access(new LinkStubClientFactory(handler), new LinkHealth(), Server("a")).ChooseAsync(Link, LocalUser, null, CancellationToken.None);

        Assert.Equal("the source refused", choice.Refusal);
    }

    /// <summary>
    /// A download is asked for with the download flag, and is allowed only when the source says it checked the download.
    /// True: a source on an older build, which ignores the flag, cannot let a user download.
    /// False: a user barred from downloading on the source could download through a link.
    /// </summary>
    /// <param name="checkedHere">Whether the source answers that it checked the download.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChooseForDownloadAsync_NeedsTheSourceToCheckTheDownload(bool checkedHere)
    {
        using var handler = new StubHandler(_ => Json(new LinkStreamCheckResponse { Allowed = true, ItemId = "aaaa", Size = 1000, DownloadAllowed = checkedHere }));

        var choice = await Access(new LinkStubClientFactory(handler), new LinkHealth(), Server("a")).ChooseForDownloadAsync(Link, LocalUser, null, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.True(body.RootElement.GetProperty("Download").GetBoolean());
        Assert.Equal(checkedHere ? new LinkRoute("a", "aaaa") : null, choice.Route);
        Assert.Equal(checkedHere ? null : "Server a needs a newer Server Sync to allow downloads", choice.Refusal);
    }

    /// <summary>
    /// A play is asked for without the download flag.
    /// True: the source applies only its playback checks to a play.
    /// False: a user who may play but not download would be refused playback.
    /// </summary>
    [Fact]
    public async Task ChooseAsync_Play_IsNotAskedAsADownload()
    {
        using var handler = new StubHandler(_ => Json(new LinkStreamCheckResponse { Allowed = true, ItemId = "aaaa", Size = 1000 }));

        var choice = await Access(new LinkStubClientFactory(handler), new LinkHealth(), Server("a")).ChooseAsync(Link, LocalUser, null, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.False(body.RootElement.GetProperty("Download").GetBoolean());
        Assert.NotNull(choice.Route);
    }

    private static HttpResponseMessage Json(object body)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static SourceServer Server(string key)
        => new() { Key = key, Name = "Server " + key, Url = "http://" + key + ":8096", ApiKey = "k", Mode = ServerMode.Pull };

    private static LinkAccess Access(ISourceServerClientFactory factory, LinkHealth health, params SourceServer[] servers)
    {
        var config = new PluginConfiguration();
        config.Servers.AddRange(servers);
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "this-server");
        return new LinkAccess(new FakeConfigManager(config), factory, host, NullLogger<LinkAccess>.Instance, health, LinkAccess.Hedge);
    }
}

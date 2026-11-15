using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// A link session's controller must pass the dashboard's commands for a viewer to the linking server, and say when the session has gone quiet.
/// </summary>
public sealed class LinkSessionControllerTests
{
    private static readonly DateTime Reported = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A session is active for ten minutes after its last report, to the tick.
    /// True: the dashboard shows a viewer as playing only while reports arrive.
    /// False: a viewer whose stop was lost would show as playing for ever, or drop out between two reports.
    /// </summary>
    /// <param name="secondsLater">How long after the last report.</param>
    /// <param name="expected">Whether the session is active.</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    public void ActiveAt_TenMinutesFromTheLastReport(int secondsLater, bool expected)
    {
        var controller = Controller(new PluginConfiguration(), new FakeClientFactory());
        controller.Touch(Reported);

        var active = controller.ActiveAt(Reported.AddSeconds(secondsLater));

        Assert.Equal(expected, active);
    }

    /// <summary>
    /// A session may be removed twenty minutes after its last report and not before.
    /// True: the dashboard sees the session idle before it is gone.
    /// False: a session would vanish while the dashboard still lists its device, or never be removed.
    /// </summary>
    /// <param name="secondsLater">How long after the last report.</param>
    /// <param name="expected">Whether the session may be removed.</param>
    [Theory]
    [InlineData(600, false)]
    [InlineData(1199, false)]
    [InlineData(1200, true)]
    public void RemovableAt_TwentyMinutesFromTheLastReport(int secondsLater, bool expected)
    {
        var controller = Controller(new PluginConfiguration(), new FakeClientFactory());
        controller.Touch(Reported);

        var removable = controller.RemovableAt(Reported.AddSeconds(secondsLater));

        Assert.Equal(expected, removable);
    }

    /// <summary>
    /// A new report moves the window.
    /// True: a session that keeps reporting never goes stale.
    /// False: a long film would be stopped ten minutes in.
    /// </summary>
    [Fact]
    public void Touch_LaterReport_RestartsTheWindow()
    {
        var controller = Controller(new PluginConfiguration(), new FakeClientFactory());
        controller.Touch(Reported);

        controller.Touch(Reported.AddMinutes(30));

        Assert.True(controller.ActiveAt(Reported.AddMinutes(39)));
        Assert.False(controller.RemovableAt(Reported.AddMinutes(49)));
    }

    /// <summary>
    /// Media control is offered only while the linking server's entry is here, enabled, and set up.
    /// True: the dashboard offers remote control only where a command can be sent.
    /// False: the dashboard would show controls that do nothing.
    /// </summary>
    /// <param name="present">Whether the entry exists.</param>
    /// <param name="enabled">Whether the entry is enabled.</param>
    /// <param name="apiKey">The entry's API key.</param>
    /// <param name="expected">Whether control is offered.</param>
    [Theory]
    [InlineData(true, true, "k", true)]
    [InlineData(true, false, "k", false)]
    [InlineData(true, true, "", false)]
    [InlineData(false, true, "k", false)]
    public void SupportsMediaControl_FollowsTheServerEntry(bool present, bool enabled, string apiKey, bool expected)
    {
        var config = new PluginConfiguration();
        if (present)
        {
            config.Servers.Add(Peer(enabled, apiKey));
        }

        var supported = Controller(config, new FakeClientFactory()).SupportsMediaControl;

        Assert.Equal(expected, supported);
    }

    /// <summary>
    /// A message's timeout is passed on when it is a whole number and dropped when it is not.
    /// True: a malformed timeout still shows the message.
    /// False: a bad timeout from the dashboard would throw and lose the message.
    /// </summary>
    /// <param name="timeout">The timeout argument, or null for none.</param>
    /// <param name="expected">The timeout passed on.</param>
    [Theory]
    [InlineData("5000", 5000L)]
    [InlineData("abc", null)]
    [InlineData("1.5", null)]
    [InlineData(null, null)]
    public void RequestFor_DisplayMessage_ParsesTheTimeout(string? timeout, long? expected)
    {
        var command = new GeneralCommand { Name = GeneralCommandType.DisplayMessage };
        command.Arguments["Header"] = "Hello";
        if (timeout is not null)
        {
            command.Arguments["TimeoutMs"] = timeout;
        }

        var request = LinkSessionController.RequestFor(SessionMessageType.GeneralCommand, command);

        Assert.NotNull(request);
        Assert.Equal("Hello", request.Header);
        Assert.Null(request.Text);
        Assert.Equal(expected, request.TimeoutMs);
    }

    /// <summary>
    /// A stop with no controlling user, any other general command, and any other message are not passed on.
    /// True: Jellyfin's own housekeeping stop never stops a viewer on another server.
    /// False: a session cleanup here would end playback there.
    /// </summary>
    [Fact]
    public void RequestFor_AnythingElse_IsNull()
    {
        var ownStop = LinkSessionController.RequestFor(SessionMessageType.Playstate, new PlaystateRequest { Command = PlaystateCommand.Stop });
        var volume = LinkSessionController.RequestFor(SessionMessageType.GeneralCommand, new GeneralCommand { Name = GeneralCommandType.VolumeUp });
        var other = LinkSessionController.RequestFor(SessionMessageType.LibraryChanged, "data");
        var nothing = LinkSessionController.RequestFor<object?>(SessionMessageType.Playstate, null);

        Assert.Null(ownStop);
        Assert.Null(volume);
        Assert.Null(other);
        Assert.Null(nothing);
    }

    /// <summary>
    /// A command from a user goes to the linking server's control route, naming this server and the viewer's session.
    /// True: pressing pause on the source's dashboard pauses the viewer's client.
    /// False: the command would reach the wrong session, or the peer could not tell who sent it.
    /// </summary>
    [Fact]
    public async Task SendMessage_UserCommand_ReachesThePeer()
    {
        HttpRequestMessage? sent = null;
        using var handler = new StubHandler(request =>
        {
            sent = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var factory = new LinkStubClientFactory(handler);
        var controller = Controller(Config(Peer()), factory);
        var playstate = new PlaystateRequest { Command = PlaystateCommand.Seek, SeekPositionTicks = 42, ControllingUserId = Guid.NewGuid().ToString("N") };

        await controller.SendMessage(SessionMessageType.Playstate, Guid.NewGuid(), playstate, CancellationToken.None);

        Assert.Equal(new[] { "peer" }, factory.Created);
        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("/Sync/Peer/Control", sent.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("this-server", body.RootElement.GetProperty("ServerId").GetString());
        Assert.Equal("viewer-session", body.RootElement.GetProperty("SessionKey").GetString());
        Assert.Equal("Seek", body.RootElement.GetProperty("Command").GetString());
        Assert.Equal(42, body.RootElement.GetProperty("SeekPositionTicks").GetInt64());
    }

    /// <summary>
    /// A message that is not passed on, or one for a server that is disabled or gone, builds no client.
    /// True: nothing is sent where nothing should be.
    /// False: a disabled server would still be called for every dashboard event.
    /// </summary>
    /// <param name="passable">Whether the message is one that is passed on.</param>
    /// <param name="enabled">Whether the server entry is enabled.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SendMessage_NothingToSendOrNoServer_BuildsNoClient(bool passable, bool enabled)
    {
        var factory = new FakeClientFactory();
        var controller = Controller(Config(Peer(enabled)), factory);
        var playstate = new PlaystateRequest { Command = PlaystateCommand.Pause, ControllingUserId = passable ? "user" : null };

        await controller.SendMessage(SessionMessageType.Playstate, Guid.NewGuid(), playstate, CancellationToken.None);

        Assert.Equal(0, factory.Requests);
    }

    /// <summary>
    /// A peer that answers with an error, or a client that cannot be built, does not throw out of the send.
    /// True: a failed remote command leaves the dashboard working.
    /// False: one unreachable server would break session messaging on the source.
    /// </summary>
    /// <param name="peerAnswers">Whether a client can be built and the peer answers with an error.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendMessage_PeerFails_DoesNotThrow(bool peerAnswers)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var controller = Controller(Config(Peer()), peerAnswers ? new LinkStubClientFactory(handler) : new FakeClientFactory());
        var playstate = new PlaystateRequest { Command = PlaystateCommand.Pause, ControllingUserId = "user" };

        var failure = await Record.ExceptionAsync(() => controller.SendMessage(SessionMessageType.Playstate, Guid.NewGuid(), playstate, CancellationToken.None));

        Assert.Null(failure);
        Assert.Equal(peerAnswers ? 1 : 0, handler.Requests);
    }

    /// <summary>
    /// A send whose caller already cancelled ends as cancelled.
    /// True: a shutdown is not logged as a peer failure.
    /// False: cancellation would be swallowed and reported as a server that could not be reached.
    /// </summary>
    [Fact]
    public async Task SendMessage_CallerCancelled_Throws()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var controller = Controller(Config(Peer()), new LinkStubClientFactory(handler));
        var playstate = new PlaystateRequest { Command = PlaystateCommand.Pause, ControllingUserId = "user" };
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => controller.SendMessage(SessionMessageType.Playstate, Guid.NewGuid(), playstate, cancelled.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }

    private static SourceServer Peer(bool enabled = true, string apiKey = "k")
        => new() { Key = "peer", Name = "Peer", Url = "http://peer:8096", ApiKey = apiKey, IsEnabled = enabled, Mode = ServerMode.Push };

    private static PluginConfiguration Config(SourceServer server)
    {
        var config = new PluginConfiguration();
        config.Servers.Add(server);
        return config;
    }

    private static LinkSessionController Controller(PluginConfiguration config, Jellyfin.Plugin.ServerSync.Services.Clients.ISourceServerClientFactory factory)
    {
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_SystemId", _ => "this-server");
        return new LinkSessionController("peer", "viewer-session", new FakeConfigManager(config), factory, host, NullLogger.Instance);
    }
}

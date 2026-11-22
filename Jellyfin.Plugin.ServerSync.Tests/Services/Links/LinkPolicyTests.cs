using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Links;

/// <summary>
/// The link policy must allow a stream only for a file this server holds, as a user it maps the caller to, within the limits set for that server.
/// </summary>
public sealed class LinkPolicyTests : IDisposable
{
    private static readonly Guid RemoteUser = Guid.Parse("99999999999999999999999999999999");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serversync-linkpolicy-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly User _user = new("viewer", "Default", "Default");
    private readonly LinkSessions _sessions = new();
    private readonly List<SessionInfo> _ownSessions = new();
    private readonly Movie _movie;
    private BaseItem? _item;
    private User? _found;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkPolicyTests"/> class.
    /// </summary>
    public LinkPolicyTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Film.mkv");
        File.WriteAllText(_file, "video");
        _movie = new Movie { Id = Guid.NewGuid(), Path = _file };
        _item = _movie;
        _found = _user;
        _user.SetPermission(PermissionKind.EnableMediaPlayback, true);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The policy and its checks refuse missing services and a missing server entry.
    /// True: the mistake is named where it was made.
    /// False: a stream check with no caller would fail with no hint of the cause.
    /// </summary>
    [Fact]
    public void Members_NullArguments_Throw()
    {
        var (library, _) = InterfaceStub.Create<ILibraryManager>();
        var (users, _) = InterfaceStub.Create<IUserManager>();
        var (sessions, _) = InterfaceStub.Create<ISessionManager>();

        Assert.Throws<ArgumentNullException>(() => new LinkPolicy(null!, users, sessions, _sessions));
        Assert.Throws<ArgumentNullException>(() => new LinkPolicy(library, null!, sessions, _sessions));
        Assert.Throws<ArgumentNullException>(() => new LinkPolicy(library, users, null!, _sessions));
        Assert.Throws<ArgumentNullException>(() => new LinkPolicy(library, users, sessions, null!));
        Assert.Throws<ArgumentNullException>(() => Policy().Check(null!, Guid.NewGuid(), null, null, probe: true));
        Assert.Throws<ArgumentNullException>(() => LinkPolicy.UserFor(null!, null, null));
    }

    /// <summary>
    /// An id that names nothing, a folder, an item with no path, or a file gone from disk is refused as no longer on the source.
    /// True: only a file this server can read is ever streamed.
    /// False: a stale id would start a stream that fails halfway, or a folder would be opened as a file.
    /// </summary>
    /// <param name="kind">What the id names.</param>
    [Theory]
    [InlineData("nothing")]
    [InlineData("folder")]
    [InlineData("nopath")]
    [InlineData("gone")]
    public void Check_NoFileToStream_IsRefused(string kind)
    {
        _item = kind switch
        {
            "nothing" => null,
            "folder" => new Folder { Path = _dir },
            "nopath" => new Movie(),
            _ => new Movie { Path = Path.Combine(_dir, "Gone.mkv") }
        };

        var verdict = Policy().Check(Peer(), Guid.NewGuid(), null, null, probe: true);

        Assert.False(verdict.Allowed);
        Assert.Equal("the file is no longer on the source", verdict.Reason);
        Assert.Null(verdict.Item);
    }

    /// <summary>
    /// A probe that names no user is allowed for the item alone, with no user to stream as.
    /// True: the calling server can read a new link's media info before anyone plays it.
    /// False: no link could be probed until a mapped user pressed play.
    /// </summary>
    [Fact]
    public void Check_ProbeWithoutUser_AllowsTheItemOnly()
    {
        var verdict = Policy().Check(Peer(), _movie.Id, null, null, probe: true);

        Assert.True(verdict.Allowed);
        Assert.Same(_movie, verdict.Item);
        Assert.Null(verdict.UserId);
        Assert.Null(verdict.Reason);
    }

    /// <summary>
    /// A caller's user with no mapping here and no user for unmapped users is refused, whatever user the request claims.
    /// True: the user a stream runs as is chosen by this server alone.
    /// False: a peer could name an administrator here and stream as them.
    /// </summary>
    /// <param name="probe">Whether the request is flagged as a probe.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Check_UnmappedUser_IsRefused(bool probe)
    {
        var peer = Peer(mapped: false);

        var verdict = Policy().Check(peer, _movie.Id, RemoteUser.ToString("N"), _user.Id.ToString("N"), probe);

        Assert.False(verdict.Allowed);
        Assert.Equal("your account is not mapped to a user on the source", verdict.Reason);
    }

    /// <summary>
    /// A mapping to a user who no longer exists, or who is disabled, is refused.
    /// True: disabling an account here stops its link streams.
    /// False: a deleted or disabled account could still stream through another server.
    /// </summary>
    /// <param name="exists">Whether the mapped user still exists.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Check_UserMissingOrDisabled_IsRefused(bool exists)
    {
        _user.SetPermission(PermissionKind.IsDisabled, true);
        _found = exists ? _user : null;

        var verdict = Check();

        Assert.False(verdict.Allowed);
        Assert.Equal("the account you stream as on the source is missing or disabled", verdict.Reason);
    }

    /// <summary>
    /// A user who may not play media here is refused.
    /// True: the source's own playback permission holds for links.
    /// False: a user barred from playback could watch everything through another server.
    /// </summary>
    [Fact]
    public void Check_UserMayNotPlay_IsRefused()
    {
        _user.SetPermission(PermissionKind.EnableMediaPlayback, false);

        var verdict = Check();

        Assert.False(verdict.Allowed);
        Assert.Equal("the account you stream as on the source may not play media", verdict.Reason);
    }

    /// <summary>
    /// A download needs the user's download permission here as well as playback, and a play does not.
    /// True: the source's own download permission holds for downloads through links.
    /// False: a user barred from downloading could save files through another server, or be refused playback.
    /// </summary>
    /// <param name="mayDownload">Whether the user may download here.</param>
    /// <param name="download">Whether the request is a download.</param>
    /// <param name="allowed">Whether it is allowed.</param>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Check_Download_NeedsDownloadPermission(bool mayDownload, bool download, bool allowed)
    {
        _user.SetPermission(PermissionKind.EnableContentDownloading, mayDownload);

        var verdict = Policy().Check(Peer(), _movie.Id, RemoteUser.ToString("N"), null, probe: false, download: download);

        Assert.Equal(allowed, verdict.Allowed);
        Assert.Equal(allowed ? null : "the account you stream as on the source may not download media", verdict.Reason);
    }

    /// <summary>
    /// The media probe cannot download a file.
    /// True: a download always runs as a user the source checks.
    /// False: a download asked for as a probe would skip every user check.
    /// </summary>
    [Fact]
    public void Check_ProbeDownload_IsRefused()
    {
        var verdict = Policy().Check(Peer(), _movie.Id, null, null, probe: true, download: true);

        Assert.False(verdict.Allowed);
        Assert.Equal("a media probe cannot download a file", verdict.Reason);
    }

    /// <summary>
    /// A mapped user who may play is allowed, and the verdict names the item and the user here.
    /// True: the stream runs as this server's own user for the caller.
    /// False: an allowed stream would run with no user, uncounted and unlimited.
    /// </summary>
    [Fact]
    public void Check_MappedUser_IsAllowedAsTheLocalUser()
    {
        var verdict = Check();

        Assert.True(verdict.Allowed);
        Assert.Same(_movie, verdict.Item);
        Assert.Equal(_user.Id, verdict.UserId);
    }

    /// <summary>
    /// A file above the user's remote bitrate limit is refused only when the entry honors that limit.
    /// True: the setting decides whether Jellyfin's own bitrate limit applies to links.
    /// False: the limit would be ignored when on, or enforced when off.
    /// </summary>
    /// <param name="honor">Whether the entry honors the bitrate limit.</param>
    /// <param name="bitrate">The file's bitrate.</param>
    /// <param name="allowed">Whether the stream is allowed.</param>
    [Theory]
    [InlineData(true, 8_000_001, false)]
    [InlineData(true, 8_000_000, true)]
    [InlineData(false, 80_000_000, true)]
    public void Check_BitrateLimit_FollowsTheSetting(bool honor, int bitrate, bool allowed)
    {
        _user.RemoteClientBitrateLimit = 8_000_000;
        _movie.TotalBitrate = bitrate;
        var peer = Peer();
        peer.LinkHonorBitrateLimit = honor;

        var verdict = Policy().Check(peer, _movie.Id, RemoteUser.ToString("N"), null, probe: false);

        Assert.Equal(allowed, verdict.Allowed);
        Assert.Equal(allowed ? null : "the file's bitrate is above the 8 Mbps the source allows your account", verdict.Reason);
    }

    /// <summary>
    /// A server at its stream limit is refused a new stream, but not a request of one already running, nor a cache download.
    /// True: the limit holds for new viewers while seeking and caching carry on.
    /// False: a viewer would be cut off by their own stream, or the limit would not hold at all.
    /// </summary>
    /// <param name="sameStream">Whether the running stream is this user's stream of this item.</param>
    /// <param name="watching">Whether someone is watching.</param>
    /// <param name="allowed">Whether the stream is allowed.</param>
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void Check_ServerStreamLimit_CountsOnlyNewStreams(bool sameStream, bool watching, bool allowed)
    {
        var peer = Peer();
        peer.LinkMaxStreams = 1;
        using var running = _sessions.Open(peer.Key, sameStream ? _user.Id : Guid.NewGuid(), _movie.Id);

        var verdict = Policy().Check(peer, _movie.Id, RemoteUser.ToString("N"), null, probe: false, watching);

        Assert.Equal(allowed, verdict.Allowed);
        if (!allowed)
        {
            Assert.Contains("at once and all are in use", verdict.Reason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A user's limit counts their own playback here when the setting says so, and never a Server Sync session.
    /// True: a user watching at home and through a link shares one allowance, and a link stream is not counted twice.
    /// False: the user's limit would be doubled, or their link session would lock them out of their own stream.
    /// </summary>
    /// <param name="countOwn">Whether the entry counts the user's own sessions.</param>
    /// <param name="client">The client of the user's session here.</param>
    /// <param name="allowed">Whether the stream is allowed.</param>
    [Theory]
    [InlineData(true, "Jellyfin Web", false)]
    [InlineData(false, "Jellyfin Web", true)]
    [InlineData(true, LinkPolicy.ClientName, true)]
    public void Check_UserStreamLimit_CountsOwnPlaybackWhenSet(bool countOwn, string client, bool allowed)
    {
        var peer = Peer();
        peer.UserMappings[0].LinkMaxStreams = 1;
        peer.LinkCountOwnSessions = countOwn;
        _ownSessions.Add(Session(_user.Id, client, playing: true));

        var verdict = Policy().Check(peer, _movie.Id, RemoteUser.ToString("N"), null, probe: false);

        Assert.Equal(allowed, verdict.Allowed);
    }

    /// <summary>
    /// The local user is the enabled mapping's, else the user set for unmapped users, and never the one the request claims.
    /// True: a mapping whose local user is unreadable gives no user, not the fallback.
    /// False: a broken mapping would quietly stream as the fallback account.
    /// </summary>
    /// <param name="remote">The user's id on the calling server.</param>
    /// <param name="mappingEnabled">Whether the mapping is enabled.</param>
    /// <param name="localId">The mapping's local user id.</param>
    /// <param name="fallback">The user for unmapped users.</param>
    /// <param name="expected">The local user's id, or null.</param>
    [Theory]
    [InlineData("99999999999999999999999999999999", true, "11111111111111111111111111111111", "", "11111111111111111111111111111111")]
    [InlineData("99999999-9999-9999-9999-999999999999", true, "11111111111111111111111111111111", "", "11111111111111111111111111111111")]
    [InlineData("99999999999999999999999999999999", true, "not-a-guid", "22222222222222222222222222222222", null)]
    [InlineData("99999999999999999999999999999999", false, "11111111111111111111111111111111", "22222222222222222222222222222222", "22222222222222222222222222222222")]
    [InlineData("88888888888888888888888888888888", true, "11111111111111111111111111111111", "", null)]
    [InlineData(null, true, "11111111111111111111111111111111", "22222222222222222222222222222222", "22222222222222222222222222222222")]
    [InlineData("garbage", true, "11111111111111111111111111111111", "also-garbage", null)]
    public void UserFor_MappingThenFallback(string? remote, bool mappingEnabled, string localId, string fallback, string? expected)
    {
        var peer = new SourceServer
        {
            LinkFallbackUserId = fallback,
            UserMappings = { new UserMapping { SourceUserId = RemoteUser.ToString("N"), LocalUserId = localId, IsEnabled = mappingEnabled } }
        };

        var user = LinkPolicy.UserFor(peer, remote, "33333333333333333333333333333333");

        Assert.Equal(expected is null ? null : Guid.Parse(expected), user);
    }

    /// <summary>
    /// Own playback counts the user's sessions that are playing under another client, and is 0 with the setting off.
    /// True: only real playback by that user here uses up their allowance.
    /// False: idle sessions, other users, or link sessions would count against them.
    /// </summary>
    /// <param name="countOwn">Whether own sessions are counted.</param>
    /// <param name="expected">The sessions counted.</param>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public void OwnPlayback_CountsThisUsersPlayingSessions(bool countOwn, int expected)
    {
        _ownSessions.Add(Session(_user.Id, "Jellyfin Web", playing: true));
        _ownSessions.Add(Session(_user.Id, "Jellyfin Android", playing: true));
        _ownSessions.Add(Session(_user.Id, "Jellyfin Web", playing: false));
        _ownSessions.Add(Session(_user.Id, LinkPolicy.ClientName, playing: true));
        _ownSessions.Add(Session(Guid.NewGuid(), "Jellyfin Web", playing: true));

        var count = Policy().OwnPlayback(_user.Id, countOwn);

        Assert.Equal(expected, count);
    }

    private static SessionInfo Session(Guid userId, string client, bool playing)
    {
        var (manager, _) = InterfaceStub.Create<ISessionManager>();
        return new SessionInfo(manager, NullLogger.Instance) { Id = Guid.NewGuid().ToString("N"), UserId = userId, Client = client, NowPlayingItem = playing ? new BaseItemDto() : null };
    }

    private LinkVerdict Check() => Policy().Check(Peer(), _movie.Id, RemoteUser.ToString("N"), null, probe: false);

    private SourceServer Peer(bool mapped = true)
    {
        var peer = new SourceServer { Key = "peer", Name = "Peer", IsEnabled = true, Mode = ServerMode.Push, AllowLinking = true };
        if (mapped)
        {
            peer.UserMappings.Add(new UserMapping { SourceUserId = RemoteUser.ToString("N"), LocalUserId = _user.Id.ToString("N") });
        }

        return peer;
    }

    private LinkPolicy Policy()
    {
        var (library, libraryStub) = InterfaceStub.Create<ILibraryManager>();
        libraryStub.Answer(nameof(ILibraryManager.GetItemById), _ => _item);
        var (users, usersStub) = InterfaceStub.Create<IUserManager>();
        usersStub.Answer(nameof(IUserManager.GetUserById), args => _found is not null && (Guid)args[0]! == _found.Id ? _found : null);
        var (sessions, sessionsStub) = InterfaceStub.Create<ISessionManager>();
        sessionsStub.Answer("get_Sessions", _ => _ownSessions);
        return new LinkPolicy(library, users, sessions, _sessions);
    }
}

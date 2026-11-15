using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Models.Queue;

/// <summary>
/// An outbound row goes on the wire as a hint that names this server as its origin and carries the edit it stands for.
/// </summary>
public sealed class OutboundHintTests
{
    private static readonly DateTime Edited = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private static OutboundHint Row() => new()
    {
        Id = 5,
        HintId = "me:7",
        PeerKey = "peer-key",
        Kind = HintKind.Content,
        Key = "file-key",
        ItemPath = "/local/movies/film.mkv",
        ItemId = "item",
        ItemType = "Movie",
        UserId = "user",
        UserName = "alice",
        VersionServerId = "editor",
        VersionTimestamp = Edited,
        Recorded = true,
        State = OutboundState.Sent,
        Attempts = 4,
        LastError = "timeout"
    };

    /// <summary>
    /// The hint carries the row's id, kind, key, item, user, and version, with the given server as origin.
    /// True: the peer receives the edit as this server recorded it and knows who sent it.
    /// False: the peer would apply the wrong version, or credit the hint to the wrong server.
    /// </summary>
    [Fact]
    public void ToHint_FullRow_CarriesTheEditAndNamesTheOrigin()
    {
        var hint = Row().ToHint("this-server");

        Assert.Equal(("me:7", "this-server", HintKind.Content, "file-key"), (hint.HintId, hint.OriginServerId, hint.Kind, hint.Key));
        Assert.Equal(("/local/movies/film.mkv", "item", "Movie"), (hint.ItemPath, hint.ItemId, hint.ItemType));
        Assert.Equal(("user", "alice"), (hint.UserId, hint.UserName));
        Assert.Equal(("editor", Edited), (hint.VersionServerId, hint.VersionTimestamp));
        Assert.True(hint.Recorded);
    }

    /// <summary>
    /// The origin is the server sending, not the server whose edit the row carries.
    /// True: a relayed edit keeps its editor in the version and its sender in the origin.
    /// False: the peer would answer the editor, which never sent it the hint.
    /// </summary>
    [Fact]
    public void ToHint_RelayedEdit_KeepsOriginAndVersionServerApart()
    {
        var hint = Row().ToHint("this-server");

        Assert.Equal("this-server", hint.OriginServerId);
        Assert.Equal("editor", hint.VersionServerId);
    }

    /// <summary>
    /// Each call builds a new hint, so changing one does not change the row or the next hint.
    /// True: a retry sends the same hint the first attempt did.
    /// False: a change made while sending would alter what the row sends later.
    /// </summary>
    [Fact]
    public void ToHint_CalledTwice_BuildsSeparateHints()
    {
        var row = Row();
        var first = row.ToHint("this-server");
        first.Key = "changed";

        var second = row.ToHint("this-server");

        Assert.NotSame(first, second);
        Assert.Equal("file-key", second.Key);
        Assert.Equal("file-key", row.Key);
    }

    /// <summary>
    /// A row with no item or user sends a hint with those fields empty.
    /// True: a people hint goes out without made up ids.
    /// False: the peer would try to match an item or user that does not exist.
    /// </summary>
    [Fact]
    public void ToHint_RowWithNoItemOrUser_LeavesThemNull()
    {
        var row = new OutboundHint { HintId = "me:1", Kind = HintKind.People, Key = "Jane Doe" };

        var hint = row.ToHint("this-server");

        Assert.Null(hint.ItemPath);
        Assert.Null(hint.ItemId);
        Assert.Null(hint.ItemType);
        Assert.Null(hint.UserId);
        Assert.Null(hint.UserName);
    }
}

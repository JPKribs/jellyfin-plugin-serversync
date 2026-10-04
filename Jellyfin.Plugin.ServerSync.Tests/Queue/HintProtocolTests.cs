using System;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public class HintProtocolTests
{
    /// <summary>
    /// A history key round trips through its text form.
    /// True: the receiver can fetch the origin's object from the key alone.
    /// False: every history hint would be dropped as malformed.
    /// </summary>
    [Fact]
    public void HistoryKey_RoundTrips()
    {
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        Assert.True(HintProtocol.TryParseHistoryKey(HintProtocol.HistoryKey(user, item), out var u, out var i));
        Assert.Equal(user, u);
        Assert.Equal(item, i);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("|")]
    [InlineData("notaguid|alsonot")]
    public void TryParseHistoryKey_RejectsBadInput(string? key)
    {
        Assert.False(HintProtocol.TryParseHistoryKey(key, out _, out _));
    }

    /// <summary>
    /// Backoff climbs one, five, fifteen minutes, then holds at an hour forever.
    /// True: a peer that is down is retried gently and never given up on.
    /// False: a down peer is hammered or abandoned.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 60)]
    [InlineData(40, 60)]
    public void NextDelay_FollowsSchedule(int attempts, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), HintProtocol.NextDelay(attempts));
    }

    /// <summary>
    /// People keys fold case and padding so the same person on two servers shares one key.
    /// True: a hint for "alice actor" finds the row for "Alice Actor".
    /// False: a casing difference would make every person look like two.
    /// </summary>
    [Fact]
    public void PeopleKey_FoldsCaseAndPadding()
    {
        Assert.Equal(HintProtocol.PeopleKey("Alice Actor"), HintProtocol.PeopleKey("  alice actor "));
        Assert.NotEqual(HintProtocol.PeopleKey("Alice Actor"), HintProtocol.PeopleKey("Bob Director"));
    }

    [Fact]
    public void MetadataKey_IsTheItemIdWithoutDashes()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString("N"), HintProtocol.MetadataKey(id));
    }

    /// <summary>
    /// Only a metadata edit raises an item hint. Provider downloads and image refreshes are left to the scan.
    /// True: a server that fetches its own metadata after receiving a file does not push it over the peer's.
    /// False: every library scan would flood the pool with hints.
    /// </summary>
    [Theory]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit, true)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit | MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate, true)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.MetadataDownload, false)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate, false)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.None, false)]
    public void IsHintedUpdate_OnlyForEdits(MediaBrowser.Controller.Library.ItemUpdateType reason, bool expected)
    {
        Assert.Equal(expected, LocalChangeObserver.IsHintedUpdate(reason));
    }

    /// <summary>
    /// A server name with emoji or quotes cannot travel in an HTTP header.
    /// True: a server named with emoji can still negotiate, queue, and complete with its peers.
    /// False: every hand built peer request from such a server fails before it is sent.
    /// </summary>
    [Theory]
    [InlineData("Living Room", "Living Room")]
    [InlineData("\U0001F920\U0001F920 Cowboy", "Cowboy")]
    [InlineData("\U0001F920\U0001F920", "Server Sync")]
    [InlineData("say \"hi\" \\ there", "say hi  there")]
    [InlineData("", "Server Sync")]
    public void HeaderSafe_KeepsPrintableAscii(string name, string expected)
    {
        Assert.Equal(expected, Jellyfin.Plugin.ServerSync.Services.SourceServerClient.HeaderSafe(name));
    }
}

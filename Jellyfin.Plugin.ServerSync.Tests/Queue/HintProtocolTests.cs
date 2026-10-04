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
    /// Every item update that changes something travels. A metadata edit, or an image change outside a
    /// scan and outside a refresh of the item, is a hand made edit. An image change during either, or a
    /// metadata download, is a provider's work and travels marked, so it fills in on the other side
    /// without replacing a hand made edit.
    /// True: a poster someone picks and a poster a scan fetched both reach the peers, and the peers can tell them apart.
    /// False: either posters never travel, or a scan's posters silently replace posters chosen by hand.
    /// </summary>
    [Theory]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate, false, false, ChangeOrigin.Edit)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate, true, false, ChangeOrigin.Provider)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate, false, true, ChangeOrigin.Provider)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.MetadataEdit, true, true, ChangeOrigin.Edit)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.MetadataDownload, false, false, ChangeOrigin.Provider)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.MetadataImport, false, false, ChangeOrigin.None)]
    [InlineData(MediaBrowser.Controller.Library.ItemUpdateType.None, false, false, ChangeOrigin.None)]
    public void Classify_SortsEditsFromProviderWork(MediaBrowser.Controller.Library.ItemUpdateType reason, bool refreshing, bool scanning, ChangeOrigin expected)
    {
        Assert.Equal(expected, LocalChangeObserver.Classify(reason, refreshing, scanning));
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

    /// <summary>
    /// A version ahead of this server's clock reads as now; one behind it is kept as is.
    /// True: a peer's far future stamp can never outrank every later real edit here.
    /// False: one hint dated 2999 locks an object out of ever being edited again.
    /// </summary>
    [Fact]
    public void BoundVersion_ClampsTheFutureToNow()
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now, HintProtocol.BoundVersion(new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc), now));
        Assert.Equal(now, HintProtocol.BoundVersion(now.AddSeconds(1), now));
        Assert.Equal(now.AddMinutes(-1), HintProtocol.BoundVersion(now.AddMinutes(-1), now));
        Assert.Equal(now.AddMinutes(-1), HintProtocol.BoundVersion(DateTime.SpecifyKind(now.AddMinutes(-1), DateTimeKind.Unspecified), now));
    }

    /// <summary>
    /// Honest skew is within the lead; anything past it is a bad clock or a lie and is declined outright.
    /// True: a peer a minute ahead still works, a peer years ahead is told to fix its clock.
    /// False: either every skewed peer is refused, or a forged stamp is accepted and merely clamped.
    /// </summary>
    [Fact]
    public void IsFutureVersion_AllowsSkewAndRefusesTheFarFuture()
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(HintProtocol.IsFutureVersion(now.AddMinutes(1), now));
        Assert.False(HintProtocol.IsFutureVersion(now.AddMinutes(-10), now));
        Assert.True(HintProtocol.IsFutureVersion(now.AddMinutes(6), now));
        Assert.True(HintProtocol.IsFutureVersion(new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc), now));
    }
}

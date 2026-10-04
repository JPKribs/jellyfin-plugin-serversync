using System;
using Jellyfin.Plugin.ServerSync.Models.Queue;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Queue;

public class VersionDeciderTests
{
    private static ObjectVersion V(string server, int minute) => new()
    {
        Kind = HintKind.History,
        Key = "k",
        ServerId = server,
        Timestamp = new DateTime(2026, 10, 3, 12, minute, 0, DateTimeKind.Utc)
    };

    /// <summary>
    /// Equal values adopt the incoming version without a write, whatever the versions say.
    /// True: a copy that already matches raises nothing and ends the loop.
    /// False: every hint would be written back and re-hinted forever.
    /// </summary>
    [Fact]
    public void Decide_EqualValues_Adopts()
    {
        Assert.Equal(VersionDecision.Adopt, VersionDecider.Decide(V("a", 10), V("b", 5), valuesEqual: true));
    }

    /// <summary>
    /// An object with no version here takes the incoming edit.
    /// True: the first hint for an object lands.
    /// False: nothing would ever sync until a full scan.
    /// </summary>
    [Fact]
    public void Decide_NoLocalVersion_Applies()
    {
        Assert.Equal(VersionDecision.Apply, VersionDecider.Decide(null, V("b", 5), valuesEqual: false));
    }

    /// <summary>
    /// The newer origin timestamp wins, and an older one is kept out.
    /// True: the newest human edit always wins across the pool.
    /// False: a stale copy could overwrite a fresh edit.
    /// </summary>
    [Theory]
    [InlineData(5, 10, VersionDecision.Apply)]
    [InlineData(10, 5, VersionDecision.Keep)]
    public void Decide_DifferentTimes_NewerWins(int localMinute, int incomingMinute, VersionDecision expected)
    {
        Assert.Equal(expected, VersionDecider.Decide(V("a", localMinute), V("b", incomingMinute), valuesEqual: false));
    }

    /// <summary>
    /// A tie breaks on the server id, the same way on both servers.
    /// True: two servers that edit in the same second converge on one value.
    /// False: each would keep its own and the pool never settles.
    /// </summary>
    [Fact]
    public void Decide_SameTime_BreaksTiesOnServerId()
    {
        Assert.Equal(VersionDecision.Apply, VersionDecider.Decide(V("a", 5), V("b", 5), valuesEqual: false));
        Assert.Equal(VersionDecision.Keep, VersionDecider.Decide(V("b", 5), V("a", 5), valuesEqual: false));
    }

    /// <summary>
    /// The second edit on one server outranks a copy of its first edit that landed elsewhere later.
    /// True: the specific failure the design guards against cannot happen.
    /// False: B's copy of A's first edit would fan a stale value across the pool.
    /// </summary>
    [Fact]
    public void Decide_CopyOfOlderEdit_LosesToNewerEdit()
    {
        var secondEditOnA = V("a", 2);
        var copyOfFirstEditHeldByB = V("a", 1);
        Assert.Equal(VersionDecision.Keep, VersionDecider.Decide(secondEditOnA, copyOfFirstEditHeldByB, valuesEqual: false));
    }

}

using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.UserSync;

/// <summary>
/// How a user's settings are settled between this server and a server it syncs with: which of the two
/// changed them since they last agreed, and what a permission becomes when both did.
/// </summary>
public class UserConflictTests
{
    private static string Policy(object value) => JsonSerializer.Serialize(value);

    private static Dictionary<string, JsonElement> Read(string? json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json!)!;

    private static UserSyncItem Agreed(string agreedValue, string localSeen, SyncStatus status = SyncStatus.Synced, string? mergedSeen = null)
    {
        var row = new UserSyncItem { PropertyCategory = UserPropertyCategory.Policy, Status = status };
        row.UpdateMergedValue(agreedValue);
        row.MarkSynced();
        row.UpdateMergedValue(mergedSeen ?? agreedValue);
        row.LocalValue = localSeen;
        return row;
    }

    /// <summary>
    /// Permissive takes the more generous of each permission and Restrictive the stricter, whichever
    /// way a permission is written: a switch that grants, a switch that takes away, a limit where zero
    /// means none, a list of what is blocked, and a list of the only things allowed.
    /// True: the user ends with everything either server allowed, or only what both did.
    /// False: a setting written the other way round, such as a block list, would be combined backwards.
    /// </summary>
    [Fact]
    public void Combine_TakesTheMostOrTheLeastOfEachPermission()
    {
        var theirs = Policy(new { EnableContentDownloading = true, EnableLiveTvAccess = false, IsHidden = true, MaxActiveSessions = 2, RemoteClientBitrateLimit = 0, BlockedTags = new[] { "a", "b" }, AllowedTags = new[] { "x" }, SyncPlayAccess = 1 });
        var mine = Policy(new { EnableContentDownloading = false, EnableLiveTvAccess = false, IsHidden = false, MaxActiveSessions = 5, RemoteClientBitrateLimit = 8000, BlockedTags = new[] { "b", "c" }, AllowedTags = new[] { "y" }, SyncPlayAccess = 0 });

        var most = Read(UserPolicyCombiner.Combine(theirs, mine, UserConflictRule.Permissive));
        Assert.True(most["EnableContentDownloading"].GetBoolean());
        Assert.False(most["EnableLiveTvAccess"].GetBoolean());
        Assert.False(most["IsHidden"].GetBoolean());
        Assert.Equal(5, most["MaxActiveSessions"].GetInt32());
        Assert.Equal(0, most["RemoteClientBitrateLimit"].GetInt32());
        Assert.Equal("[\"b\"]", most["BlockedTags"].GetRawText());
        Assert.Equal("[\"x\",\"y\"]", most["AllowedTags"].GetRawText());
        Assert.Equal(0, most["SyncPlayAccess"].GetInt32());

        var least = Read(UserPolicyCombiner.Combine(theirs, mine, UserConflictRule.Restrictive));
        Assert.False(least["EnableContentDownloading"].GetBoolean());
        Assert.True(least["IsHidden"].GetBoolean());
        Assert.Equal(2, least["MaxActiveSessions"].GetInt32());
        Assert.Equal(8000, least["RemoteClientBitrateLimit"].GetInt32());
        Assert.Equal("[\"a\",\"b\",\"c\"]", least["BlockedTags"].GetRawText());
        Assert.Equal("[]", least["AllowedTags"].GetRawText());
        Assert.Equal(1, least["SyncPlayAccess"].GetInt32());
    }

    /// <summary>
    /// Both servers reach the same permissions from the same two values, whichever does the combining.
    /// True: two servers that each settle the conflict on their own scan end up agreeing.
    /// False: each would write a different result and the two would trade values on every scan.
    /// </summary>
    [Theory]
    [InlineData(UserConflictRule.Permissive)]
    [InlineData(UserConflictRule.Restrictive)]
    public void Combine_GivesBothServersTheSameAnswer(UserConflictRule rule)
    {
        var a = Policy(new { EnableContentDownloading = true, IsHidden = true, MaxActiveSessions = 0, MaxParentalRating = (int?)13, BlockedTags = new[] { "a" }, AllowedTags = new string[0], IsDisabled = false });
        var b = Policy(new { EnableContentDownloading = false, IsHidden = false, MaxActiveSessions = 3, MaxParentalRating = (int?)null, BlockedTags = new[] { "b", "a" }, AllowedTags = new[] { "z" }, IsDisabled = true });

        var onA = UserPolicyCombiner.Combine(b, a, rule);
        var onB = UserPolicyCombiner.Combine(a, b, rule);

        Assert.True(JsonComparisonUtility.JsonEquals(onA, onB), $"{onA} / {onB}");
    }

    /// <summary>
    /// A disabled user stays disabled under either rule, a permission this code does not know takes
    /// the other server's value, and Newest takes the other server's value for everything.
    /// True: a lockout made on one server is never undone by combining, and nothing is guessed at.
    /// False: Permissive would re-enable a user someone just locked out.
    /// </summary>
    [Fact]
    public void Combine_NeverEnablesADisabledUser_AndLeavesTheUnknownAlone()
    {
        var theirs = Policy(new { IsDisabled = false, SomeNewSetting = true, EnableRemoteAccess = false });
        var mine = Policy(new { IsDisabled = true, SomeNewSetting = false, EnableRemoteAccess = true });

        var most = Read(UserPolicyCombiner.Combine(theirs, mine, UserConflictRule.Permissive));
        Assert.True(most["IsDisabled"].GetBoolean());
        Assert.True(most["SomeNewSetting"].GetBoolean());
        Assert.True(most["EnableRemoteAccess"].GetBoolean());

        Assert.Equal(theirs, UserPolicyCombiner.Combine(theirs, mine, UserConflictRule.Newest));
    }

    /// <summary>
    /// Only the other server changed the settings since the two agreed: its value applies. Only this
    /// server did: this server's value is kept and the agreed value stays as it was. Both did: the
    /// rule decides. Before the two ever agreed, the other server's value applies.
    /// True: a change made on either server reaches the other, and neither undoes the other's.
    /// False: a scan would overwrite a change made here with the other server's older value.
    /// </summary>
    [Fact]
    public void SettleBothWays_FollowsWhoChangedSinceTheTwoAgreed()
    {
        var agreed = Policy(new { EnableContentDownloading = false });
        var changed = Policy(new { EnableContentDownloading = true });
        string? Combined(string? other) => "combined";

        // Never agreed.
        Assert.Equal((changed, false), UserSyncTableService.SettleBothWays(new UserSyncItem(), changed, agreed, Combined));

        // Only the other server changed it.
        Assert.Equal((changed, false), UserSyncTableService.SettleBothWays(Agreed(agreed, agreed), changed, agreed, Combined));

        // Only this server changed it: kept, on this scan and on the next, until the other server takes it.
        Assert.Equal((changed, true), UserSyncTableService.SettleBothWays(Agreed(agreed, agreed), agreed, changed, Combined));
        Assert.Equal((changed, true), UserSyncTableService.SettleBothWays(Agreed(agreed, changed, mergedSeen: changed), agreed, changed, Combined));

        // Both changed it since the last scan, or this server's change was already being kept.
        var other = Policy(new { EnableContentDownloading = true, IsHidden = true });
        Assert.Equal(("combined", false), UserSyncTableService.SettleBothWays(Agreed(agreed, agreed), other, changed, Combined));
        Assert.Equal(("combined", false), UserSyncTableService.SettleBothWays(Agreed(agreed, changed, mergedSeen: changed), other, changed, Combined));

        // The other server's change is still waiting to be applied here: that is not a change of this server's.
        Assert.Equal((other, false), UserSyncTableService.SettleBothWays(Agreed(agreed, agreed, SyncStatus.Queued, mergedSeen: changed), other, agreed, Combined));
    }

    /// <summary>
    /// A row whose own change is kept does not move the value the two servers agreed on, and one
    /// that only recorded another server's settings agrees on nothing.
    /// True: the next scan still sees that this server's value differs from what was agreed, and keeps it again.
    /// False: the kept value would become the agreed one, and the other server's unchanged value would then overwrite it.
    /// </summary>
    [Fact]
    public void KeptAndObservedRows_DoNotMoveTheAgreedValue()
    {
        var row = Agreed(Policy(new { A = 1 }), Policy(new { A = 1 }));
        var before = row.SyncedValueHash;

        row.UpdateMergedValue(Policy(new { A = 2 }));
        row.KeepAgreedValue = true;
        row.MarkSynced();
        Assert.Equal(before, row.SyncedValueHash);

        row.KeepAgreedValue = false;
        row.Observed = true;
        row.MarkSynced();
        Assert.Equal(before, row.SyncedValueHash);

        row.Observed = false;
        row.MarkSynced();
        Assert.NotEqual(before, row.SyncedValueHash);
    }
}

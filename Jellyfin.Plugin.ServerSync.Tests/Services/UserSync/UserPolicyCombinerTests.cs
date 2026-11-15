using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.UserSync;

/// <summary>
/// What a user's permissions become when both this server and a server it syncs with changed them.
/// </summary>
public class UserPolicyCombinerTests
{
    private static string Policy(object value) => JsonSerializer.Serialize(value);

    private static Dictionary<string, JsonElement> Read(string? json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json!)!;

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
}

using System.Text.Json;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.UserSync;

/// <summary>
/// Which of two servers changed a user's settings since they last agreed, and what the scan keeps.
/// </summary>
public class UserSyncTableServiceTests
{
    private static string Policy(object value) => JsonSerializer.Serialize(value);

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
}

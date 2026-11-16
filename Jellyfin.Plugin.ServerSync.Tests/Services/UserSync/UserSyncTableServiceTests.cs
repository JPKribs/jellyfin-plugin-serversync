using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// A source picture is downloaded once and its hash is kept while the source's tag for it is unchanged. A new tag, or the deep check, reads it again.
    /// True: a regular scan downloads no picture that has not changed.
    /// False: every scan would download every user's picture, or a new picture would never be seen.
    /// </summary>
    [Fact]
    public async Task BuildRecordAsync_ProfileImage_DownloadsOnlyWhenTheTagChangesOrTheCheckIsDeep()
    {
        var sourceId = Guid.NewGuid();
        var route = "/Users/" + sourceId + "/Images/Primary";
        var server = new ClientStubServer().On(route, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        using var client = server.Client();
        var service = new UserSyncTableService(NullLogger<UserSyncTableService>.Instance);
        var local = new Jellyfin.Database.Implementations.Entities.User("alice", "provider", "reset");
        var mapping = new UserMapping { SourceUserId = sourceId.ToString("N"), LocalUserId = local.Id.ToString("N") };
        var config = new PluginConfiguration();
        Task<UserSyncItem?> Scan(string tag, UserSyncItem? existing)
            => service.BuildRecordAsync(mapping, UserPropertyCategory.ProfileImage, new Jellyfin.Sdk.Generated.Models.UserDto { Id = sourceId, Name = "alice", PrimaryImageTag = tag }, new MediaBrowser.Model.Dto.UserDto(), local, client, config, existing, CancellationToken.None);

        // The scan builds on the row it is given, so each value is read before the next scan changes it.
        var first = await Scan("tag-1", null);
        var (afterFirst, firstHash) = (server.To(route).Count, first!.SourceImageHash);
        var second = await Scan("tag-1", first);
        var (afterSecond, secondTag, secondHash, secondSize) = (server.To(route).Count, second!.SourceValueHash, second.SourceImageHash, second.SourceImageSize);
        var changed = await Scan("tag-2", second);
        var (afterChanged, changedTag) = (server.To(route).Count, changed!.SourceValueHash);
        config.DeepImageVerification = true;
        var deep = await Scan("tag-2", changed);

        Assert.Equal((1, 1, 2, 3), (afterFirst, afterSecond, afterChanged, server.To(route).Count));
        Assert.Equal(("tag-1", "tag-2"), (secondTag, changedTag));
        Assert.Equal(firstHash, secondHash);
        Assert.Equal(3, secondSize);
        Assert.True(deep!.HasChanges);
    }
}

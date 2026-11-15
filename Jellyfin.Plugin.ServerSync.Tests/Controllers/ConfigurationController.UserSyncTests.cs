using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The Users routes show one row per user mapping built from that mapping's category rows, and change exactly the rows or mappings named.
/// </summary>
public sealed class ConfigurationControllerUserSyncTests : IDisposable
{
    private const string Policy = UserPropertyCategory.Policy;
    private const string Settings = UserPropertyCategory.Configuration;
    private const string Image = UserPropertyCategory.ProfileImage;

    private readonly ControllerTestHarness _harness = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationControllerUserSyncTests"/> class, with one peer.
    /// </summary>
    public ConfigurationControllerUserSyncTests() => _harness.AddPeer().ExternalUrl = "https://peer.example";

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// The counts are per user mapping, so a mapping with rows in two statuses counts under both.
    /// True: the counter matches the list below it, one line per mapping.
    /// False: the counter would count category rows, three times the mappings shown.
    /// </summary>
    [Fact]
    public void GetUserSyncStatus_Mappings_AreCountedOncePerStatusTheyHold()
    {
        Add("alice", Policy, SyncStatus.Synced);
        Add("alice", Settings, SyncStatus.Synced);
        Add("alice", Image, SyncStatus.Errored);
        Add("bob", Policy, SyncStatus.Synced);
        _harness.Config.Configuration.LastRunFailures = new List<SyncRunFailure>
        {
            new() { ModuleKey = "Users", Phase = "Wrong", Reason = "another key", Timestamp = DateTime.UtcNow },
            new() { ModuleKey = "User", Phase = "Apply", Reason = "refused", Timestamp = DateTime.UtcNow.AddDays(-1) }
        };

        var result = _harness.Configuration().GetUserSyncStatus(_harness.Users);

        var body = Assert.IsType<BaseSyncStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((0, 0, 2, 1, 0), (body.Pending, body.Queued, body.Synced, body.Errored, body.Ignored));
        Assert.Equal(("Apply", "refused"), (body.LastFailurePhase, body.LastFailureReason));
    }

    /// <summary>
    /// A mapping's overall status is the most urgent of its rows, where Errored outranks Queued, then Pending, and Ignored needs every row.
    /// True: a mapping with one failing category shows as Errored, and one partly ignored still shows as Synced.
    /// False: a failure in one category would hide behind the others.
    /// </summary>
    [Theory]
    [InlineData(SyncStatus.Synced, SyncStatus.Queued, SyncStatus.Errored, "Errored")]
    [InlineData(SyncStatus.Pending, SyncStatus.Queued, SyncStatus.Synced, "Queued")]
    [InlineData(SyncStatus.Pending, SyncStatus.Ignored, SyncStatus.Synced, "Pending")]
    [InlineData(SyncStatus.Ignored, SyncStatus.Ignored, SyncStatus.Ignored, "Ignored")]
    [InlineData(SyncStatus.Ignored, SyncStatus.Ignored, SyncStatus.Synced, "Synced")]
    [InlineData(SyncStatus.Synced, SyncStatus.Synced, SyncStatus.Synced, "Synced")]
    public void GetUserSyncUsers_OverallStatus_IsTheMostUrgentOfItsRows(SyncStatus policy, SyncStatus settings, SyncStatus image, string expected)
    {
        Add("alice", Policy, policy);
        Add("alice", Settings, settings);
        Add("alice", Image, image);

        var user = Assert.Single(Users().Items);
        var detail = Detail("alice");

        Assert.Equal(expected, user.OverallStatus);
        Assert.Equal(expected, detail.OverallStatus);
        Assert.Equal((policy.ToString(), settings.ToString(), image.ToString()), (user.PolicyStatus, user.ConfigurationStatus, user.ProfileImageStatus));
    }

    /// <summary>
    /// A mapping's row carries each category's row ID, the names, the server, the newest sync time, and the errors joined into one message.
    /// True: the Users tab can act on each category and shows every failure once.
    /// False: a category could not be queued from the list, or its error would be lost.
    /// </summary>
    [Fact]
    public void GetUserSyncUsers_Mapping_CarriesItsCategoriesNamesAndErrors()
    {
        var newest = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc);
        var policy = Add("alice", Policy, SyncStatus.Errored, r => (r.Reason, r.LastSyncTime) = ("policy refused. ", newest.AddDays(-2)));
        var settings = Add("alice", Settings, SyncStatus.Errored, r => (r.Reason, r.LastSyncTime) = ("settings refused", newest));
        var image = Add("alice", Image, SyncStatus.Synced);

        var user = Assert.Single(Users().Items);

        Assert.Equal((policy.Id, settings.Id, image.Id), (user.PolicyId, user.ConfigurationId, user.ProfileImageId));
        Assert.Equal(("alice", "local-alice", "Alice", "Local Alice"), (user.SourceUserId, user.LocalUserId, user.SourceUserName, user.LocalUserName));
        Assert.Equal(("https://peer.example", "peer"), (user.SourceServerUrl, user.ServerKey));
        Assert.Equal("policy refused. settings refused", user.ErrorMessage);
        Assert.Equal(newest, user.LastSyncTime);
        Assert.Equal((false, "No Changes"), (user.HasChanges, user.TotalChanges));
    }

    /// <summary>
    /// A mapping with one category only has no IDs or statuses for the others, and no error message.
    /// True: a mapping that syncs only its policy shows the other categories as absent.
    /// False: the list would fail on, or invent rows for, categories that are switched off.
    /// </summary>
    [Fact]
    public void GetUserSyncUsers_MappingWithOneCategory_LeavesTheOthersEmpty()
    {
        var policy = Add("alice", Policy, SyncStatus.Synced);

        var user = Assert.Single(Users().Items);

        Assert.Equal((policy.Id, (long?)null, (long?)null), (user.PolicyId, user.ConfigurationId, user.ProfileImageId));
        Assert.Equal(("Synced", null, null), (user.PolicyStatus, user.ConfigurationStatus, user.ProfileImageStatus));
        Assert.Null(user.ErrorMessage);
    }

    /// <summary>
    /// A changed profile image is reported as one image change.
    /// True: the list says a mapping has an image to sync.
    /// False: an image waiting to sync would read as No Changes.
    /// </summary>
    [Fact]
    public void GetUserSyncUsers_ChangedProfileImage_IsReportedAsOneImage()
    {
        Add("alice", Image, SyncStatus.Queued, r => (r.SourceImageHash, r.SourceImageSize, r.LocalImageHash) = ("new", 2048, "old"));

        var user = Assert.Single(Users().Items);

        Assert.Equal((true, true, "1 image"), (user.HasChanges, user.ProfileImageHasChanges, user.TotalChanges));
    }

    /// <summary>
    /// The list narrows by status, is one line per mapping, and pages within 1 to 200 mappings.
    /// True: three category rows of one user are one line, and a bad page size still lists.
    /// False: each category would be its own line, or one request could load every mapping.
    /// </summary>
    [Theory]
    [InlineData(null, 0, 50, 2, 2)]
    [InlineData("Errored", 0, 50, 1, 1)]
    [InlineData("Bogus", 0, 50, 2, 2)]
    [InlineData(null, -1, 0, 2, 1)]
    [InlineData(null, 1, int.MaxValue, 2, 1)]
    public void GetUserSyncUsers_StatusAndPaging_ShapeThePage(string? status, int skip, int take, int total, int rows)
    {
        Add("alice", Policy, SyncStatus.Synced);
        Add("alice", Settings, SyncStatus.Synced);
        Add("bob", Policy, SyncStatus.Errored);

        var page = Users(status, skip, take);

        Assert.Equal((total, rows), (page.TotalCount, page.Items.Count));
    }

    /// <summary>
    /// The detail of a mapping carries each category's row and whether that category is switched on.
    /// True: the dialog shows each category with the switch that governs it.
    /// False: the dialog could not tell a category that is off from one with nothing to sync.
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void GetUserSyncUserDetail_Mapping_CarriesItsRowsAndTheSwitches(bool policyOn, bool settingsOn, bool imageOn)
    {
        var config = _harness.Config.Configuration;
        (config.UserSyncPolicy, config.UserSyncConfiguration, config.UserSyncProfileImage) = (policyOn, settingsOn, imageOn);
        var policy = Add("alice", Policy, SyncStatus.Errored, r => r.Reason = "refused.");
        Add("alice", Image, SyncStatus.Synced);

        var detail = Detail("alice");

        Assert.Equal((policyOn, settingsOn, imageOn), (detail.PolicyEnabled, detail.ConfigurationEnabled, detail.ProfileImageEnabled));
        Assert.Equal(policy.Id, detail.PolicyItem!.Id);
        Assert.Null(detail.ConfigurationItem);
        Assert.NotNull(detail.ProfileImageItem);
        Assert.Equal(("Alice", "Local Alice", "refused", "Errored"), (detail.SourceUserName, detail.LocalUserName, detail.ErrorMessage, detail.OverallStatus));
    }

    /// <summary>
    /// A mapping with no rows answers 404.
    /// True: the dialog for a removed mapping closes with a clear miss.
    /// False: the route would answer an empty mapping as if it existed.
    /// </summary>
    [Theory]
    [InlineData("alice", "someone-else")]
    [InlineData("nobody", "local-alice")]
    [InlineData("", "")]
    public void GetUserSyncUserDetail_NoRowsForTheMapping_Answers404(string sourceUserId, string localUserId)
    {
        Add("alice", Policy, SyncStatus.Synced);

        var result = _harness.Configuration().GetUserSyncUserDetail(sourceUserId, localUserId, _harness.Users);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// Queue by row ID changes the rows that exist and reports the rest, and a status with no rows queues that whole status.
    /// True: one category can be queued alone, and Retry All reaches every errored row.
    /// False: queueing one category would queue the whole mapping, or miss rows beyond the page.
    /// </summary>
    [Fact]
    public void QueueUserSyncItems_ByIdsOrStatus_QueuesExactlyThose()
    {
        var policy = Add("alice", Policy, SyncStatus.Errored);
        var settings = Add("alice", Settings, SyncStatus.Errored);
        var image = Add("alice", Image, SyncStatus.Synced);
        var controller = _harness.Configuration();

        var byIds = controller.QueueUserSyncItems(new BulkUserSyncItemsRequest { Ids = { policy.Id, 987654 } }, _harness.Users);
        var afterIds = _harness.Users.GetById(settings.Id)!.Status;
        var byStatus = controller.QueueUserSyncItems(new BulkUserSyncItemsRequest { Status = "errored" }, _harness.Users);

        var first = Assert.IsType<BulkOperationResult>(Assert.IsType<OkObjectResult>(byIds).Value);
        Assert.Equal((1, 2, 987654L), (first.Updated, first.Requested, Assert.Single(first.Failed).Id));
        Assert.Equal(SyncStatus.Errored, afterIds);
        Assert.Equal(1, Assert.IsType<BulkOperationResult>(Assert.IsType<OkObjectResult>(byStatus).Value).Updated);
        Assert.Equal((SyncStatus.Queued, SyncStatus.Queued, SyncStatus.Synced), (_harness.Users.GetById(policy.Id)!.Status, _harness.Users.GetById(settings.Id)!.Status, _harness.Users.GetById(image.Id)!.Status));
    }

    /// <summary>
    /// Queue and Ignore by mapping change every row of the named mappings and no other mapping's.
    /// True: a user's whole mapping is queued or ignored in one action, and the count is its rows.
    /// False: another user's rows would change, or only one category would.
    /// </summary>
    [Theory]
    [InlineData(true, SyncStatus.Queued)]
    [InlineData(false, SyncStatus.Ignored)]
    public void QueueAndIgnoreUserSyncUsers_ByMapping_ChangeEveryRowOfThatMapping(bool queue, SyncStatus expected)
    {
        var policy = Add("alice", Policy, SyncStatus.Synced);
        var settings = Add("alice", Settings, SyncStatus.Errored);
        var other = Add("bob", Policy, SyncStatus.Synced);
        var request = new BulkUserMappingsRequest
        {
            UserMappings =
            {
                new UserMappingRequest { SourceUserId = "alice", LocalUserId = "local-alice" },
                new UserMappingRequest { SourceUserId = "nobody", LocalUserId = "nowhere" }
            }
        };

        var result = queue ? _harness.Configuration().QueueUserSyncUsers(request, _harness.Users) : _harness.Configuration().IgnoreUserSyncUsers(request, _harness.Users);

        var body = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.Equal(2, body.GetType().GetProperty("Updated")!.GetValue(body));
        Assert.Equal((expected, expected, SyncStatus.Synced), (_harness.Users.GetById(policy.Id)!.Status, _harness.Users.GetById(settings.Id)!.Status, _harness.Users.GetById(other.Id)!.Status));
    }

    /// <summary>
    /// A request that names no rows or mappings is refused with 400, as is a status that is not one.
    /// True: an empty selection changes nothing.
    /// False: an empty request would act on every mapping.
    /// </summary>
    [Fact]
    public void BulkRoutes_NothingSelectedOrBadStatus_Answer400()
    {
        var row = Add("alice", Policy, SyncStatus.Errored);
        var controller = _harness.Configuration();

        Assert.IsType<BadRequestObjectResult>(controller.QueueUserSyncItems(null!, _harness.Users));
        Assert.IsType<BadRequestObjectResult>(controller.QueueUserSyncItems(new BulkUserSyncItemsRequest(), _harness.Users));
        Assert.IsType<BadRequestObjectResult>(controller.QueueUserSyncItems(new BulkUserSyncItemsRequest { Status = "Bogus" }, _harness.Users));
        Assert.IsType<BadRequestObjectResult>(controller.QueueUserSyncUsers(null!, _harness.Users));
        Assert.IsType<BadRequestObjectResult>(controller.QueueUserSyncUsers(new BulkUserMappingsRequest(), _harness.Users));
        Assert.IsType<BadRequestObjectResult>(controller.IgnoreUserSyncUsers(null!, _harness.Users));
        Assert.IsType<BadRequestObjectResult>(controller.IgnoreUserSyncUsers(new BulkUserMappingsRequest { UserMappings = null! }, _harness.Users));
        Assert.Equal(SyncStatus.Errored, _harness.Users.GetById(row.Id)!.Status);
    }

    /// <summary>
    /// Each Users route requires elevation and answers one verb at its own path.
    /// True: only an administrator can read or change user sync rows.
    /// False: any signed in user could requeue another user's policy sync.
    /// </summary>
    [Theory]
    [InlineData(nameof(ConfigurationController.GetUserSyncStatus), "GET", "UserStatus")]
    [InlineData(nameof(ConfigurationController.QueueUserSyncItems), "POST", "UserItems/Queue")]
    [InlineData(nameof(ConfigurationController.GetUserSyncUsers), "GET", "UserSyncUsers")]
    [InlineData(nameof(ConfigurationController.GetUserSyncUserDetail), "GET", "UserSyncUsers/{sourceUserId}/{localUserId}")]
    [InlineData(nameof(ConfigurationController.IgnoreUserSyncUsers), "POST", "UserSyncUsers/Ignore")]
    [InlineData(nameof(ConfigurationController.QueueUserSyncUsers), "POST", "UserSyncUsers/Queue")]
    public void Route_RequiresElevation(string action, string verb, string template)
    {
        ControllerRouteAssert.Elevated(typeof(ConfigurationController), action, verb, template);
    }

    private PagedResult<UserSyncUserDto> Users(string? status = null, int skip = 0, int take = 50)
    {
        var result = _harness.Configuration().GetUserSyncUsers(_harness.Users, status: status, skip: skip, take: take);
        return Assert.IsType<PagedResult<UserSyncUserDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private UserSyncUserDetailDto Detail(string user)
    {
        var result = _harness.Configuration().GetUserSyncUserDetail(user, "local-" + user, _harness.Users);
        return Assert.IsType<UserSyncUserDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private UserSyncItem Add(string user, string category, SyncStatus status, Action<UserSyncItem>? change = null)
    {
        var row = new UserSyncItem
        {
            SourceUserId = user,
            LocalUserId = "local-" + user,
            SourceUserName = char.ToUpperInvariant(user[0]) + user[1..],
            LocalUserName = "Local " + char.ToUpperInvariant(user[0]) + user[1..],
            PropertyCategory = category,
            Status = status,
            StatusDate = DateTime.UtcNow,
            ServerKey = "peer"
        };
        change?.Invoke(row);
        _harness.Users.Upsert(row);
        return _harness.Users.GetByKey((user, "local-" + user, category))!;
    }
}

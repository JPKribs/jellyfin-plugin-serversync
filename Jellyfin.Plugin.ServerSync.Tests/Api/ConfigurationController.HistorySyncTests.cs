using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Api;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Api;

/// <summary>
/// The History routes list and count rows as stored, and change the status of exactly the rows a request names.
/// </summary>
public sealed class ConfigurationControllerHistorySyncTests : IDisposable
{
    private readonly ControllerTestHarness _harness = new();

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// The list narrows by status and by source user, and each row carries its server's browser URL.
    /// True: the History tab shows one user's rows in one status, linked to the right server.
    /// False: the filters would be ignored, or rows would link to nothing.
    /// </summary>
    [Theory]
    [InlineData(null, null, 3)]
    [InlineData("Errored", null, 1)]
    [InlineData(null, "alice", 2)]
    [InlineData("Synced", "alice", 1)]
    [InlineData("Synced", "nobody", 0)]
    [InlineData("errored", null, 3)]
    [InlineData("42", null, 3)]
    public void GetHistoryItems_StatusAndUser_NarrowTheList(string? status, string? sourceUserId, int expected)
    {
        _harness.AddPeer().ExternalUrl = "https://peer.example";
        Add("alice", "one", SyncStatus.Synced);
        Add("alice", "two", SyncStatus.Errored);
        Add("bob", "one", SyncStatus.Synced);

        var result = _harness.Configuration().GetHistoryItems(_harness.History, status: status, sourceUserId: sourceUserId);

        var page = Assert.IsType<PagedResult<HistorySyncItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(expected, page.TotalCount);
        Assert.Equal(expected, page.Items.Count);
        Assert.All(page.Items, i => Assert.Equal("https://peer.example", i.SourceServerUrl));
    }

    /// <summary>
    /// The page size is held between 1 and 200 and a negative skip starts at the first row.
    /// True: a caller cannot ask for an unbounded page.
    /// False: one request could load the whole table.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(-1, 1000, 3)]
    [InlineData(2, 50, 1)]
    [InlineData(int.MaxValue, int.MinValue, 0)]
    public void GetHistoryItems_Paging_IsClamped(int skip, int take, int expectedRows)
    {
        Add("alice", "one", SyncStatus.Synced);
        Add("alice", "two", SyncStatus.Synced);
        Add("alice", "three", SyncStatus.Synced);

        var result = _harness.Configuration().GetHistoryItems(_harness.History, skip: skip, take: take);

        var page = Assert.IsType<PagedResult<HistorySyncItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(expectedRows, page.Items.Count);
        Assert.Equal(3, page.TotalCount);
    }

    /// <summary>
    /// The status counts cover each status and carry the History failure, not another sync type's.
    /// True: the History card shows its own counts and its own last failure.
    /// False: the card would show a Content failure as its own.
    /// </summary>
    [Fact]
    public void GetHistoryStatus_Rows_AreCountedWithTheHistoryFailure()
    {
        Add("alice", "one", SyncStatus.Pending);
        Add("alice", "two", SyncStatus.Queued);
        Add("alice", "three", SyncStatus.Synced);
        Add("alice", "four", SyncStatus.Errored);
        Add("alice", "five", SyncStatus.Ignored);
        Add("bob", "one", SyncStatus.Ignored);
        var at = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        _harness.Config.Configuration.LastRunFailures = new List<SyncRunFailure>
        {
            new() { ModuleKey = "Content", Phase = "Download", Reason = "not this one", Timestamp = at.AddDays(1) },
            new() { ModuleKey = "HISTORY", Phase = "Apply", Reason = "the source was unreachable", Timestamp = at }
        };

        var result = _harness.Configuration().GetHistoryStatus(_harness.History);

        var body = Assert.IsType<BaseSyncStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((1, 1, 1, 1, 2), (body.Pending, body.Queued, body.Synced, body.Errored, body.Ignored));
        Assert.Equal(("Apply", "the source was unreachable", at), (body.LastFailurePhase, body.LastFailureReason, body.LastFailureTime));
    }

    /// <summary>
    /// Queue and Ignore by row ID change the rows that exist and report each ID that does not.
    /// True: the page shows how many changed and which rows were already gone.
    /// False: a removed row would be counted as changed, or would fail the whole batch.
    /// </summary>
    [Theory]
    [InlineData(true, SyncStatus.Queued)]
    [InlineData(false, SyncStatus.Ignored)]
    public void QueueAndIgnoreHistoryItems_ByIds_ChangeFoundRowsAndReportTheRest(bool queue, SyncStatus expected)
    {
        var first = Add("alice", "one", SyncStatus.Errored);
        var second = Add("alice", "two", SyncStatus.Synced);
        var untouched = Add("bob", "one", SyncStatus.Synced);
        var request = new BulkHistoryItemsRequest { Ids = { first.Id, second.Id, 987654 } };

        var result = queue ? _harness.Configuration().QueueHistoryItems(_harness.History, request) : _harness.Configuration().IgnoreHistoryItems(_harness.History, request);

        var body = Assert.IsType<BulkOperationResult>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal((2, 3), (body.Updated, body.Requested));
        Assert.Equal(987654, Assert.Single(body.Failed).Id);
        Assert.Equal(expected, _harness.History.GetById(first.Id)!.Status);
        Assert.Equal(expected, _harness.History.GetById(second.Id)!.Status);
        Assert.Equal(SyncStatus.Synced, _harness.History.GetById(untouched.Id)!.Status);
    }

    /// <summary>
    /// Rows named by user and item change when no row IDs are sent.
    /// True: the page can act on a row it knows only by its user and item.
    /// False: a selection without row IDs would be refused.
    /// </summary>
    [Fact]
    public void IgnoreHistoryItems_ByUserAndItem_ChangesThoseRows()
    {
        var named = Add("alice", "one", SyncStatus.Synced);
        var other = Add("alice", "two", SyncStatus.Synced);
        var request = new BulkHistoryItemsRequest { Items = { new HistoryItemReference { SourceUserId = "alice", SourceItemId = "one" } } };

        var result = _harness.Configuration().IgnoreHistoryItems(_harness.History, request);

        var body = Assert.IsType<BulkOperationResult>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal((1, 1), (body.Updated, body.Requested));
        Assert.Equal(SyncStatus.Ignored, _harness.History.GetById(named.Id)!.Status);
        Assert.Equal(SyncStatus.Synced, _harness.History.GetById(other.Id)!.Status);
    }

    /// <summary>
    /// Queue with no rows named and a status queues every row in that status, whatever its case, and resets their retries.
    /// True: Retry All reaches every errored row, not only the loaded page.
    /// False: rows beyond the page would stay errored, or rows in other statuses would be queued.
    /// </summary>
    [Theory]
    [InlineData("Errored")]
    [InlineData("errored")]
    public void QueueHistoryItems_StatusWithNoIds_QueuesEveryRowInThatStatus(string status)
    {
        var first = Add("alice", "one", SyncStatus.Errored, retries: 4);
        var second = Add("bob", "one", SyncStatus.Errored);
        var synced = Add("alice", "two", SyncStatus.Synced);

        var result = _harness.Configuration().QueueHistoryItems(_harness.History, new BulkHistoryItemsRequest { Status = status });

        var body = Assert.IsType<BulkOperationResult>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal((2, 2), (body.Updated, body.Requested));
        Assert.Equal((SyncStatus.Queued, 0), (_harness.History.GetById(first.Id)!.Status, _harness.History.GetById(first.Id)!.RetryCount));
        Assert.Equal(SyncStatus.Queued, _harness.History.GetById(second.Id)!.Status);
        Assert.Equal(SyncStatus.Synced, _harness.History.GetById(synced.Id)!.Status);
    }

    /// <summary>
    /// A status that names no real status is refused with 400.
    /// True: a bad value queues nothing.
    /// False: an undefined number would be read as a status and run against the table.
    /// </summary>
    [Theory]
    [InlineData("Bogus")]
    [InlineData("99")]
    [InlineData("-1")]
    public void QueueHistoryItems_StatusThatIsNotOne_Answers400(string status)
    {
        var row = Add("alice", "one", SyncStatus.Errored);

        var result = _harness.Configuration().QueueHistoryItems(_harness.History, new BulkHistoryItemsRequest { Status = status });

        Assert.Equal("The status is not valid.", Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal(SyncStatus.Errored, _harness.History.GetById(row.Id)!.Status);
    }

    /// <summary>
    /// A request that names no rows is refused with 400, and Ignore does not take a status in their place.
    /// True: an empty selection changes nothing, and Ignore can never sweep a whole status.
    /// False: an empty Ignore would mark every row of a status as Ignored.
    /// </summary>
    [Fact]
    public void QueueAndIgnoreHistoryItems_NothingSelected_Answer400()
    {
        var row = Add("alice", "one", SyncStatus.Errored);
        var controller = _harness.Configuration();

        Assert.IsType<BadRequestObjectResult>(controller.QueueHistoryItems(_harness.History, null!));
        Assert.IsType<BadRequestObjectResult>(controller.QueueHistoryItems(_harness.History, new BulkHistoryItemsRequest()));
        Assert.IsType<BadRequestObjectResult>(controller.QueueHistoryItems(_harness.History, new BulkHistoryItemsRequest { Ids = null!, Items = null!, Status = string.Empty }));
        Assert.IsType<BadRequestObjectResult>(controller.IgnoreHistoryItems(_harness.History, null!));
        Assert.IsType<BadRequestObjectResult>(controller.IgnoreHistoryItems(_harness.History, new BulkHistoryItemsRequest { Status = "Errored" }));
        Assert.Equal(SyncStatus.Errored, _harness.History.GetById(row.Id)!.Status);
    }

    /// <summary>
    /// Each History route requires elevation and answers one verb at its own path.
    /// True: only an administrator can read or change History rows.
    /// False: any signed in user could read every user's watch history rows.
    /// </summary>
    [Theory]
    [InlineData(nameof(ConfigurationController.GetHistoryItems), "GET", "HistoryItems")]
    [InlineData(nameof(ConfigurationController.GetHistoryStatus), "GET", "HistoryStatus")]
    [InlineData(nameof(ConfigurationController.QueueHistoryItems), "POST", "HistoryItems/Queue")]
    [InlineData(nameof(ConfigurationController.IgnoreHistoryItems), "POST", "HistoryItems/Ignore")]
    public void Route_RequiresElevation(string action, string verb, string template)
    {
        ControllerRouteAssert.Elevated(typeof(ConfigurationController), action, verb, template);
    }

    private HistorySyncItem Add(string user, string item, SyncStatus status, int retries = 0)
    {
        _harness.History.Upsert(new HistorySyncItem
        {
            SourceUserId = user,
            LocalUserId = "local-" + user,
            SourceItemId = item,
            SourceLibraryId = "lib",
            LocalLibraryId = "local",
            ItemName = item,
            Status = status,
            StatusDate = DateTime.UtcNow,
            RetryCount = retries,
            ServerKey = "peer"
        });
        return _harness.History.GetByKey((user, item))!;
    }
}

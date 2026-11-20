using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Api;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Api;

/// <summary>
/// The People routes list and count rows as stored, read one row even when its source cannot be reached, and change exactly the rows named.
/// </summary>
public sealed class ConfigurationControllerPeopleSyncTests : IDisposable
{
    private readonly ControllerTestHarness _harness = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationControllerPeopleSyncTests"/> class, with one peer.
    /// </summary>
    public ConfigurationControllerPeopleSyncTests() => _harness.AddPeer().ExternalUrl = "https://peer.example";

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// A row that does not exist answers 404.
    /// True: the dialog for a removed person closes with a clear miss.
    /// False: the route would throw on a missing row.
    /// </summary>
    [Theory]
    [InlineData(987654L)]
    [InlineData(0L)]
    [InlineData(long.MinValue)]
    public async Task GetPeopleSyncItem_NoSuchRow_Answers404(long id)
    {
        Add("Someone", SyncStatus.Synced);

        var result = await _harness.Configuration().GetPeopleSyncItem(id, _harness.People, Service(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// One row is read with its values and its server's browser URL.
    /// True: the dialog shows the person and links to the right server.
    /// False: the dialog would open empty.
    /// </summary>
    [Fact]
    public async Task GetPeopleSyncItem_Row_AnswersWithItsValues()
    {
        var row = Add("Someone", SyncStatus.Errored, sourceMetadata: "{\"Overview\":\"Bio\"}");

        var result = await _harness.Configuration().GetPeopleSyncItem(row.Id, _harness.People, Service(), CancellationToken.None);

        var dto = Assert.IsType<PeopleSyncItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((row.Id, "Someone", "Errored"), (dto.Id, dto.PersonName, dto.Status));
        Assert.Equal(("{\"Overview\":\"Bio\"}", "https://peer.example", "peer"), (dto.SourceMetadataValue, dto.SourceServerUrl, dto.ServerKey));
    }

    /// <summary>
    /// A row with source images is still answered when its source refuses, errors, or cannot be reached.
    /// True: the dialog opens with the sizes the scan stored.
    /// False: an unreachable source would make every People row unreadable.
    /// </summary>
    [Theory]
    [InlineData("factory-refuses")]
    [InlineData("source-errors")]
    [InlineData("source-unreachable")]
    public async Task GetPeopleSyncItem_SourceImagesCannotBeMeasured_StillAnswersTheRow(string scenario)
    {
        _harness.Config.Configuration.PeopleSyncImages = true;
        var row = Add("Someone", SyncStatus.Synced, sourceImages: "{\"Primary\":[{\"Tag\":\"abc\"}]}");
        _harness.Clients.Refusal = scenario == "factory-refuses" ? "that address is not allowed" : null;
        _harness.Peer = _ => scenario == "source-unreachable" ? throw new HttpRequestException("connection refused") : new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await _harness.Configuration().GetPeopleSyncItem(row.Id, _harness.People, Service(), CancellationToken.None);

        Assert.Equal(row.Id, Assert.IsType<PeopleSyncItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Id);
        Assert.Single(_harness.Clients.Created);
    }

    /// <summary>
    /// The source is not asked for image sizes when image sync is off or the row has no source images.
    /// True: opening a row makes no call it does not need.
    /// False: every dialog would call the source, even with images switched off.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GetPeopleSyncItem_ImagesOffOrNone_BuildsNoClient(bool syncImages, bool hasImages)
    {
        _harness.Config.Configuration.PeopleSyncImages = syncImages;
        var row = Add("Someone", SyncStatus.Synced, sourceImages: hasImages ? "{\"Primary\":[{\"Tag\":\"abc\"}]}" : null);

        var result = await _harness.Configuration().GetPeopleSyncItem(row.Id, _harness.People, Service(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(_harness.Clients.Created);
    }

    /// <summary>
    /// The list narrows by name and by status, and is paged within 1 to 200 rows.
    /// True: the People tab shows the rows asked for, a bounded page at a time.
    /// False: the filters would be ignored, or one request could load every person.
    /// </summary>
    [Theory]
    [InlineData(null, null, 0, 50, 3, 3)]
    [InlineData("Ann", null, 0, 50, 2, 2)]
    [InlineData(null, "Errored", 0, 50, 1, 1)]
    [InlineData("Ann", "Errored", 0, 50, 0, 0)]
    [InlineData(null, "Bogus", 0, 50, 3, 3)]
    [InlineData(null, null, -5, 0, 3, 1)]
    [InlineData(null, null, 2, int.MaxValue, 3, 1)]
    public void GetPeopleSyncItems_SearchStatusAndPaging_ShapeThePage(string? search, string? status, int skip, int take, int total, int rows)
    {
        Add("Ann Actor", SyncStatus.Synced);
        Add("Anna Other", SyncStatus.Synced);
        Add("Bob Third", SyncStatus.Errored);

        var result = _harness.Configuration().GetPeopleSyncItems(_harness.People, search, status, skip, take);

        var page = Assert.IsType<PagedResult<PeopleSyncItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((total, rows), (page.TotalCount, page.Items.Count));
        Assert.All(page.Items, i => Assert.Equal("https://peer.example", i.SourceServerUrl));
    }

    /// <summary>
    /// The status counts carry the number of people, the last sync time, and the People failure.
    /// True: the People card shows its counts, when it last ran, and why it last failed.
    /// False: the card would be missing its header facts.
    /// </summary>
    [Fact]
    public void GetPeopleSyncStatus_Rows_AreCountedWithTheLastRunAndTotal()
    {
        Add("One", SyncStatus.Pending);
        Add("Two", SyncStatus.Synced);
        Add("Three", SyncStatus.Synced);
        Add("Four", SyncStatus.Ignored);
        var at = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        _harness.Config.Configuration.LastPeopleSyncTime = at;
        _harness.Config.Configuration.LastRunFailures = new List<SyncRunFailure> { new() { ModuleKey = "People", Phase = "Refresh", Reason = "timed out", Timestamp = at } };

        var result = _harness.Configuration().GetPeopleSyncStatus(_harness.People);

        var body = Assert.IsType<PeopleSyncStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((1, 0, 2, 0, 1), (body.Pending, body.Queued, body.Synced, body.Errored, body.Ignored));
        Assert.Equal((at, 4, "timed out"), (body.LastSyncTime, body.PersonCount, body.LastFailureReason));
    }

    /// <summary>
    /// Queue and Ignore change the rows that exist and report each ID that does not.
    /// True: the page shows how many changed and which rows were already gone.
    /// False: a removed row would be counted as changed.
    /// </summary>
    [Theory]
    [InlineData(true, SyncStatus.Queued)]
    [InlineData(false, SyncStatus.Ignored)]
    public void QueueAndIgnorePeopleSyncItems_ByIds_ChangeFoundRowsAndReportTheRest(bool queue, SyncStatus expected)
    {
        var named = Add("One", SyncStatus.Errored);
        var untouched = Add("Two", SyncStatus.Synced);
        var request = new BulkPeopleSyncItemsRequest { Ids = { named.Id, 987654 } };

        var result = queue ? _harness.Configuration().QueuePeopleSyncItems(request, _harness.People) : _harness.Configuration().IgnorePeopleSyncItems(request, _harness.People);

        var body = Assert.IsType<BulkOperationResult>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal((1, 2), (body.Updated, body.Requested));
        Assert.Equal(987654, Assert.Single(body.Failed).Id);
        Assert.Equal(expected, _harness.People.GetById(named.Id)!.Status);
        Assert.Equal(SyncStatus.Synced, _harness.People.GetById(untouched.Id)!.Status);
    }

    /// <summary>
    /// Queue with a status and no rows queues every row in that status, and a status that is not one is refused.
    /// True: Retry All reaches every errored person, and a bad value queues nothing.
    /// False: people beyond the loaded page would stay errored.
    /// </summary>
    [Theory]
    [InlineData("Errored", 2)]
    [InlineData("Bogus", -1)]
    public void QueuePeopleSyncItems_StatusWithNoIds_QueuesThatStatusOrRefuses(string status, int updated)
    {
        var first = Add("One", SyncStatus.Errored);
        Add("Two", SyncStatus.Errored);
        var synced = Add("Three", SyncStatus.Synced);

        var result = _harness.Configuration().QueuePeopleSyncItems(new BulkPeopleSyncItemsRequest { Status = status }, _harness.People);

        if (updated < 0)
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(SyncStatus.Errored, _harness.People.GetById(first.Id)!.Status);
            return;
        }

        Assert.Equal(updated, Assert.IsType<BulkOperationResult>(Assert.IsType<OkObjectResult>(result).Value).Updated);
        Assert.Equal(SyncStatus.Queued, _harness.People.GetById(first.Id)!.Status);
        Assert.Equal(SyncStatus.Synced, _harness.People.GetById(synced.Id)!.Status);
    }

    /// <summary>
    /// A request that names no rows is refused with 400, and Ignore does not take a status in their place.
    /// True: an empty selection changes nothing.
    /// False: an empty Ignore would mark a whole status as Ignored.
    /// </summary>
    [Fact]
    public void QueueAndIgnorePeopleSyncItems_NothingSelected_Answer400()
    {
        var row = Add("One", SyncStatus.Errored);
        var controller = _harness.Configuration();

        Assert.IsType<BadRequestObjectResult>(controller.QueuePeopleSyncItems(null!, _harness.People));
        Assert.IsType<BadRequestObjectResult>(controller.QueuePeopleSyncItems(new BulkPeopleSyncItemsRequest { Ids = null! }, _harness.People));
        Assert.IsType<BadRequestObjectResult>(controller.IgnorePeopleSyncItems(null!, _harness.People));
        Assert.IsType<BadRequestObjectResult>(controller.IgnorePeopleSyncItems(new BulkPeopleSyncItemsRequest { Status = "Errored" }, _harness.People));
        Assert.Equal(SyncStatus.Errored, _harness.People.GetById(row.Id)!.Status);
    }

    /// <summary>
    /// Each People route requires elevation and answers one verb at its own path.
    /// True: only an administrator can read or change People rows.
    /// False: any signed in user could requeue or ignore people sync.
    /// </summary>
    [Theory]
    [InlineData(nameof(ConfigurationController.GetPeopleSyncItem), "GET", "PeopleItems/{id}")]
    [InlineData(nameof(ConfigurationController.GetPeopleSyncItems), "GET", "PeopleItems")]
    [InlineData(nameof(ConfigurationController.GetPeopleSyncStatus), "GET", "PeopleStatus")]
    [InlineData(nameof(ConfigurationController.QueuePeopleSyncItems), "POST", "PeopleItems/Queue")]
    [InlineData(nameof(ConfigurationController.IgnorePeopleSyncItems), "POST", "PeopleItems/Ignore")]
    public void Route_RequiresElevation(string action, string verb, string template)
    {
        ControllerRouteAssert.Elevated(typeof(ConfigurationController), action, verb, template);
    }

    private PeopleSyncTableService Service() => new(NullLogger<PeopleSyncTableService>.Instance, _harness.Library);

    private PeopleSyncItem Add(string name, SyncStatus status, string? sourceMetadata = null, string? sourceImages = null)
    {
        var row = new PeopleSyncItem { PersonName = name, Status = status, StatusDate = DateTime.UtcNow, ServerKey = "peer" };
        row.Metadata.Source = sourceMetadata;
        row.Images.Source = sourceImages;
        _harness.People.Upsert(row);
        return _harness.People.GetByKey(name)!;
    }
}

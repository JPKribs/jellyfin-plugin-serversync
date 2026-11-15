using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Controllers;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Links;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.Queue;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using JPKribs.Jellyfin.Base;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Controllers;

/// <summary>
/// The Metadata routes list and count rows as stored, read one row even when its source cannot be reached, and change exactly the rows named.
/// </summary>
public sealed class ConfigurationControllerMetadataSyncTests : IDisposable
{
    private readonly ControllerTestHarness _harness = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationControllerMetadataSyncTests"/> class, with one peer and one mapped library.
    /// </summary>
    public ConfigurationControllerMetadataSyncTests()
    {
        var peer = _harness.AddPeer();
        peer.ExternalUrl = "https://peer.example";
        peer.LibraryMappings.Add(new LibraryMapping { SourceLibraryId = "lib", SourceLibraryName = "Source Films", LocalLibraryName = "Films" });
    }

    /// <inheritdoc/>
    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// A row that does not exist answers 404.
    /// True: the dialog for a removed row closes with a clear miss.
    /// False: the route would throw on a missing row.
    /// </summary>
    [Theory]
    [InlineData(987654L)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task GetMetadataSyncItem_NoSuchRow_Answers404(long id)
    {
        Add("one", SyncStatus.Synced);

        var result = await _harness.Configuration().GetMetadataSyncItem(id, _harness.Metadata, Service(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// One row is read with its values, its library names, and its server's browser URL.
    /// True: the dialog shows both sides of the row and links to the right server.
    /// False: the dialog would open empty.
    /// </summary>
    [Fact]
    public async Task GetMetadataSyncItem_Row_AnswersWithItsValuesAndNames()
    {
        var row = Add("one", SyncStatus.Errored, sourceMetadata: "{\"Name\":\"Film\"}");

        var result = await _harness.Configuration().GetMetadataSyncItem(row.Id, _harness.Metadata, Service(), CancellationToken.None);

        var dto = Assert.IsType<MetadataSyncItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((row.Id, "one", "Errored"), (dto.Id, dto.SourceItemId, dto.Status));
        Assert.Equal(("Source Films", "Films", "https://peer.example"), (dto.SourceLibraryName, dto.LocalLibraryName, dto.SourceServerUrl));
        Assert.Equal("{\"Name\":\"Film\"}", dto.SourceMetadataValue);
    }

    /// <summary>
    /// A row with source images is still answered when its source refuses, errors, or is no longer listed.
    /// True: the dialog opens with the sizes the scan stored.
    /// False: an unreachable source would make every Metadata row unreadable.
    /// </summary>
    [Theory]
    [InlineData("factory-refuses")]
    [InlineData("source-errors")]
    [InlineData("source-unreachable")]
    [InlineData("entry-gone")]
    public async Task GetMetadataSyncItem_SourceImagesCannotBeMeasured_StillAnswersTheRow(string scenario)
    {
        _harness.Config.Configuration.MetadataSyncImages = true;
        var row = Add("one", SyncStatus.Synced, sourceImages: "{\"Primary\":[{\"Tag\":\"abc\"}]}");
        _harness.Clients.Refusal = scenario == "factory-refuses" ? "that address is not allowed" : null;
        _harness.Peer = _ => scenario == "source-unreachable" ? throw new HttpRequestException("connection refused") : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        if (scenario == "entry-gone")
        {
            _harness.Config.Configuration.Servers.Clear();
        }

        var result = await _harness.Configuration().GetMetadataSyncItem(row.Id, _harness.Metadata, Service(), CancellationToken.None);

        var dto = Assert.IsType<MetadataSyncItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(row.Id, dto.Id);
        Assert.Equal(scenario == "entry-gone" ? 0 : 1, _harness.Clients.Created.Count);
    }

    /// <summary>
    /// The source is not asked for image sizes when image sync is off or the row has no source images.
    /// True: opening a row makes no call it does not need.
    /// False: every dialog would call the source, even with images switched off.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GetMetadataSyncItem_ImagesOffOrNone_BuildsNoClient(bool syncImages, bool hasImages)
    {
        _harness.Config.Configuration.MetadataSyncImages = syncImages;
        var row = Add("one", SyncStatus.Synced, sourceImages: hasImages ? "{\"Primary\":[{\"Tag\":\"abc\"}]}" : null);

        var result = await _harness.Configuration().GetMetadataSyncItem(row.Id, _harness.Metadata, Service(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(_harness.Clients.Created);
    }

    /// <summary>
    /// The list narrows by status and source library, and leaves the large values out of each row.
    /// True: the Metadata tab loads a light page of the rows asked for.
    /// False: every page would carry every row's full metadata, or the filters would be ignored.
    /// </summary>
    [Theory]
    [InlineData(null, null, 3)]
    [InlineData("Errored", null, 1)]
    [InlineData(null, "lib", 2)]
    [InlineData("Synced", "other", 1)]
    [InlineData("Synced", "none", 0)]
    [InlineData("Nope", null, 3)]
    public void GetMetadataSyncItems_StatusAndLibrary_NarrowTheListWithoutBlobs(string? status, string? library, int expected)
    {
        Add("one", SyncStatus.Synced, sourceMetadata: "{\"Name\":\"Film\"}");
        Add("two", SyncStatus.Errored, sourceMetadata: "{\"Name\":\"Other\"}");
        Add("three", SyncStatus.Synced, library: "other");

        var result = _harness.Configuration().GetMetadataSyncItems(_harness.Metadata, status: status, sourceLibraryId: library);

        var page = Assert.IsType<PagedResult<MetadataSyncItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(expected, page.TotalCount);
        Assert.Equal(expected, page.Items.Count);
        Assert.All(page.Items, i => Assert.Null(i.SourceMetadataValue));
        Assert.All(page.Items, i => Assert.Equal("https://peer.example", i.SourceServerUrl));
    }

    /// <summary>
    /// The page size is held between 1 and 200 and a negative skip starts at the first row.
    /// True: a caller cannot ask for an unbounded page.
    /// False: one request could load the whole table.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(-3, 100000, 2)]
    [InlineData(1, 50, 1)]
    public void GetMetadataSyncItems_Paging_IsClamped(int skip, int take, int expectedRows)
    {
        Add("one", SyncStatus.Synced);
        Add("two", SyncStatus.Synced);

        var result = _harness.Configuration().GetMetadataSyncItems(_harness.Metadata, skip: skip, take: take);

        var page = Assert.IsType<PagedResult<MetadataSyncItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(expectedRows, page.Items.Count);
        Assert.Equal(2, page.TotalCount);
    }

    /// <summary>
    /// The status counts carry the last sync time, the number of mapped libraries, and the Metadata failure.
    /// True: the Metadata card shows its counts, when it last ran, and why it last failed.
    /// False: the card would be missing its header facts.
    /// </summary>
    [Fact]
    public void GetMetadataSyncStatus_Rows_AreCountedWithTheLastRunAndLibraries()
    {
        Add("one", SyncStatus.Pending);
        Add("two", SyncStatus.Queued);
        Add("three", SyncStatus.Errored);
        var at = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        _harness.Config.Configuration.LastMetadataSyncTime = at;
        _harness.Config.Configuration.LastRunFailures = new List<SyncRunFailure> { new() { ModuleKey = "Metadata", Phase = "Refresh", Reason = "timed out", Timestamp = at } };

        var result = _harness.Configuration().GetMetadataSyncStatus(_harness.Metadata);

        var body = Assert.IsType<MetadataSyncStatusResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((1, 1, 0, 1, 0), (body.Pending, body.Queued, body.Synced, body.Errored, body.Ignored));
        Assert.Equal((at, 1, "timed out"), (body.LastSyncTime, body.LibraryCount, body.LastFailureReason));
    }

    /// <summary>
    /// Queue and Ignore change the rows that exist and report each ID that does not.
    /// True: the page shows how many changed and which rows were already gone.
    /// False: a removed row would be counted as changed.
    /// </summary>
    [Theory]
    [InlineData(true, SyncStatus.Queued)]
    [InlineData(false, SyncStatus.Ignored)]
    public void QueueAndIgnoreMetadataSyncItems_ByIds_ChangeFoundRowsAndReportTheRest(bool queue, SyncStatus expected)
    {
        var named = Add("one", SyncStatus.Errored);
        var untouched = Add("two", SyncStatus.Synced);
        var request = new BulkMetadataSyncItemsRequest { Ids = { named.Id, 987654 } };

        var result = queue ? _harness.Configuration().QueueMetadataSyncItems(_harness.Metadata, request) : _harness.Configuration().IgnoreMetadataSyncItems(_harness.Metadata, request);

        var body = Assert.IsType<BulkOperationResult>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal((1, 2), (body.Updated, body.Requested));
        Assert.Equal(987654, Assert.Single(body.Failed).Id);
        Assert.Equal(expected, _harness.Metadata.GetById(named.Id)!.Status);
        Assert.Equal(SyncStatus.Synced, _harness.Metadata.GetById(untouched.Id)!.Status);
    }

    /// <summary>
    /// Queue with a status and no rows queues every row in that status, and a status that is not one is refused.
    /// True: Retry All reaches every errored row, and a bad value queues nothing.
    /// False: rows beyond the loaded page would stay errored.
    /// </summary>
    [Theory]
    [InlineData("Errored", 2)]
    [InlineData("ignored", 0)]
    [InlineData("Bogus", -1)]
    [InlineData("7", -1)]
    public void QueueMetadataSyncItems_StatusWithNoIds_QueuesThatStatusOrRefuses(string status, int updated)
    {
        var first = Add("one", SyncStatus.Errored);
        Add("two", SyncStatus.Errored);
        var synced = Add("three", SyncStatus.Synced);

        var result = _harness.Configuration().QueueMetadataSyncItems(_harness.Metadata, new BulkMetadataSyncItemsRequest { Status = status });

        if (updated < 0)
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(SyncStatus.Errored, _harness.Metadata.GetById(first.Id)!.Status);
            return;
        }

        Assert.Equal(updated, Assert.IsType<BulkOperationResult>(Assert.IsType<OkObjectResult>(result).Value).Updated);
        Assert.Equal(updated == 2 ? SyncStatus.Queued : SyncStatus.Errored, _harness.Metadata.GetById(first.Id)!.Status);
        Assert.Equal(SyncStatus.Synced, _harness.Metadata.GetById(synced.Id)!.Status);
    }

    /// <summary>
    /// A request that names no rows is refused with 400, and Ignore does not take a status in their place.
    /// True: an empty selection changes nothing.
    /// False: an empty Ignore would mark a whole status as Ignored.
    /// </summary>
    [Fact]
    public void QueueAndIgnoreMetadataSyncItems_NothingSelected_Answer400()
    {
        var row = Add("one", SyncStatus.Errored);
        var controller = _harness.Configuration();

        Assert.IsType<BadRequestObjectResult>(controller.QueueMetadataSyncItems(_harness.Metadata, null!));
        Assert.IsType<BadRequestObjectResult>(controller.QueueMetadataSyncItems(_harness.Metadata, new BulkMetadataSyncItemsRequest { Ids = null! }));
        Assert.IsType<BadRequestObjectResult>(controller.IgnoreMetadataSyncItems(_harness.Metadata, null!));
        Assert.IsType<BadRequestObjectResult>(controller.IgnoreMetadataSyncItems(_harness.Metadata, new BulkMetadataSyncItemsRequest { Status = "Errored" }));
        Assert.Equal(SyncStatus.Errored, _harness.Metadata.GetById(row.Id)!.Status);
    }

    /// <summary>
    /// Each Metadata route requires elevation and answers one verb at its own path.
    /// True: only an administrator can read or change Metadata rows.
    /// False: any signed in user could requeue or ignore metadata sync.
    /// </summary>
    [Theory]
    [InlineData(nameof(ConfigurationController.GetMetadataSyncItem), "GET", "MetadataItems/{id}")]
    [InlineData(nameof(ConfigurationController.GetMetadataSyncItems), "GET", "MetadataItems")]
    [InlineData(nameof(ConfigurationController.GetMetadataSyncStatus), "GET", "MetadataStatus")]
    [InlineData(nameof(ConfigurationController.QueueMetadataSyncItems), "POST", "MetadataItems/Queue")]
    [InlineData(nameof(ConfigurationController.IgnoreMetadataSyncItems), "POST", "MetadataItems/Ignore")]
    public void Route_RequiresElevation(string action, string verb, string template)
    {
        ControllerRouteAssert.Elevated(typeof(ConfigurationController), action, verb, template);
    }

    private MetadataSyncTableService Service()
    {
        var (local, _) = InterfaceStub.Create<ILocalItems>();
        var (links, _) = InterfaceStub.Create<ILinkRegistry>();
        return new MetadataSyncTableService(NullLogger<MetadataSyncTableService>.Instance, _harness.Library, local, links);
    }

    private MetadataSyncItem Add(string item, SyncStatus status, string library = "lib", string? sourceMetadata = null, string? sourceImages = null)
    {
        var row = new MetadataSyncItem
        {
            SourceLibraryId = library,
            LocalLibraryId = "local",
            SourceItemId = item,
            ItemName = item,
            Status = status,
            StatusDate = DateTime.UtcNow,
            ServerKey = "peer"
        };
        row.Metadata.Source = sourceMetadata;
        row.Images.Source = sourceImages;
        _harness.Metadata.Upsert(row);
        return _harness.Metadata.GetByKey((library, item))!;
    }
}

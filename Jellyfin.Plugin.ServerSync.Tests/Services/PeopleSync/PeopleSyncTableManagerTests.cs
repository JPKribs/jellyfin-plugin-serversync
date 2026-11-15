using System;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.PeopleSync;

/// <summary>
/// The People sync table keeps one row for each person name and pages them from any offset.
/// </summary>
public sealed class PeopleSyncTableManagerTests : IDisposable
{
    private static readonly DateTime At = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly SyncTestDatabase _db = new("peopletable");
    private readonly PeopleSyncTableManager _table;

    public PeopleSyncTableManagerTests()
    {
        _table = new PeopleSyncTableManager(_db.Provider, NullLogger<PeopleSyncTableManager>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private void Add(string name, SyncStatus status = SyncStatus.Synced)
        => _table.Upsert(new PeopleSyncItem { PersonName = name, Status = status, StatusDate = At });

    private void AddFive()
    {
        Add("Alice", SyncStatus.Synced);
        Add("Bob", SyncStatus.Ignored);
        Add("Carol", SyncStatus.Pending);
        Add("Dave", SyncStatus.Queued);
        Add("Zed", SyncStatus.Errored);
    }

    /// <summary>
    /// A person's ids, both values, both source hashes, and the status fields survive a write and a read.
    /// True: what a scan stored for a person is what the apply step reads.
    /// False: a person's images would read back under metadata, or its ids would be lost.
    /// </summary>
    [Fact]
    public void Upsert_FullRow_RoundTrips()
    {
        var row = new PeopleSyncItem
        {
            PersonName = "Jane Doe",
            SourcePersonId = "src-person",
            LocalPersonId = "loc-person",
            Status = SyncStatus.Errored,
            StatusDate = At,
            LastSyncTime = At.AddHours(1),
            Reason = "failed",
            RetryCount = 2,
            ServerKey = "server"
        };
        row.Metadata.UpdateSource("{\"Overview\":\"Bio\"}");
        row.Metadata.Local = "{\"Overview\":\"Old\"}";
        row.Images.UpdateSource("{\"Primary\":[{\"ImageType\":\"Primary\",\"Size\":10}]}");
        row.Images.Local = "{}";

        _table.Upsert(row);
        var read = _table.GetByKey("Jane Doe")!;

        Assert.Equal(("src-person", "loc-person", "server"), (read.SourcePersonId, read.LocalPersonId, read.ServerKey));
        Assert.Equal((row.Metadata.Source, row.Metadata.Local, row.Metadata.SourceHash), (read.Metadata.Source, read.Metadata.Local, read.Metadata.SourceHash));
        Assert.Equal((row.Images.Source, row.Images.Local, row.Images.SourceHash), (read.Images.Source, read.Images.Local, read.Images.SourceHash));
        Assert.Equal((SyncStatus.Errored, At, At.AddHours(1), "failed", 2), (read.Status, read.StatusDate, read.LastSyncTime, read.Reason, read.RetryCount));
    }

    /// <summary>
    /// A second write for the same name replaces the first and keeps the row's id.
    /// True: a person is one row however many scans see them.
    /// False: every scan would add a duplicate person, or bulk actions by id would miss.
    /// </summary>
    [Fact]
    public void Upsert_SameName_UpdatesTheOneRow()
    {
        _table.Upsert(new PeopleSyncItem { PersonName = "Jane Doe", SourcePersonId = "old", StatusDate = At });
        var firstId = _table.GetByKey("Jane Doe")!.Id;

        _table.Upsert(new PeopleSyncItem { PersonName = "Jane Doe", SourcePersonId = "new", StatusDate = At });

        var read = _table.GetByKey("Jane Doe")!;
        Assert.Equal(1, _table.Count());
        Assert.Equal(("new", firstId), (read.SourcePersonId, read.Id));
    }

    /// <summary>
    /// A read and a delete by name act on the exact name only.
    /// True: removing "Jane Doe" keeps "Jane Doe Jr".
    /// False: a person whose name starts the same would be removed too.
    /// </summary>
    [Fact]
    public void KeyRoutes_ExactName_TouchOnlyThatPerson()
    {
        Add("Jane Doe");
        Add("Jane Doe Jr");

        _table.DeleteByKey("Jane Doe");

        Assert.Null(_table.GetByKey("Jane Doe"));
        Assert.NotNull(_table.GetByKey("Jane Doe Jr"));
        Assert.Null(_table.GetByKey("Jane"));
        Assert.Null(_table.GetByKey(string.Empty));
    }

    /// <summary>
    /// Rows come back errored first, then queued, pending, ignored, and synced.
    /// True: people that failed are at the top of the People tab.
    /// False: errors would be buried under the people already in sync.
    /// </summary>
    [Fact]
    public void SearchPaginated_MixedStatuses_OrdersByStatusPriority()
    {
        AddFive();

        var page = _table.SearchPaginated(null, null, 0, 50);

        Assert.Equal(new[] { "Zed", "Dave", "Carol", "Bob", "Alice" }, page.Items.Select(p => p.PersonName).ToArray());
        Assert.Equal(5, page.TotalCount);
    }

    /// <summary>
    /// A skip that is not a multiple of the page size starts at exactly that row.
    /// True: a scrolling list can ask for the rows after any offset.
    /// False: the list would repeat or skip people when it loads more.
    /// </summary>
    [Fact]
    public void SearchPaginated_SkipNotAMultipleOfTake_StartsAtThatRow()
    {
        AddFive();

        var page = _table.SearchPaginated(null, null, 1, 3);

        Assert.Equal(new[] { "Dave", "Carol", "Bob" }, page.Items.Select(p => p.PersonName).ToArray());
        Assert.Equal(5, page.TotalCount);
    }

    /// <summary>
    /// A negative skip reads as 0, and a take outside 1 to 200 is pulled into that range.
    /// True: a bad request still gets a sane page.
    /// False: a negative offset would fail the query, and a huge take would load every person.
    /// </summary>
    [Theory]
    [InlineData(-10, 2, 2)]
    [InlineData(0, 0, 1)]
    [InlineData(0, -3, 1)]
    [InlineData(0, 5000, 5)]
    [InlineData(5, 10, 0)]
    public void SearchPaginated_OutOfRangeSkipOrTake_IsPulledIntoRange(int skip, int take, int expectedRows)
    {
        AddFive();

        var page = _table.SearchPaginated(null, null, skip, take);

        Assert.Equal(expectedRows, page.Items.Count);
        Assert.Equal(5, page.TotalCount);
    }

    /// <summary>
    /// A search matches part of the name, and a status narrows it.
    /// True: the People tab finds a person by any part of their name, in one status.
    /// False: a filter would be ignored and the tab would list every person.
    /// </summary>
    [Fact]
    public void SearchPaginated_TextAndStatus_NarrowThePeople()
    {
        Add("Jane Doe", SyncStatus.Errored);
        Add("John Doe", SyncStatus.Synced);
        Add("Alice", SyncStatus.Errored);

        var byName = _table.SearchPaginated("Doe", null, 0, 50);
        var byBoth = _table.SearchPaginated("Doe", SyncStatus.Errored, 0, 50);
        var none = _table.SearchPaginated("Nobody", null, 0, 50);

        Assert.Equal(new[] { "Jane Doe", "John Doe" }, byName.Items.Select(p => p.PersonName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "Jane Doe" }, byBoth.Items.Select(p => p.PersonName).ToArray());
        Assert.Equal(1, byBoth.TotalCount);
        Assert.Empty(none.Items);
        Assert.Equal(0, none.TotalCount);
    }

    /// <summary>
    /// A page number turns into the matching offset, and a page below 1 reads as the first.
    /// True: page 2 of 2 shows the third and fourth people.
    /// False: page numbers would land on the wrong rows.
    /// </summary>
    [Theory]
    [InlineData(1, new[] { "Zed", "Dave" })]
    [InlineData(0, new[] { "Zed", "Dave" })]
    [InlineData(2, new[] { "Carol", "Bob" })]
    [InlineData(3, new[] { "Alice" })]
    public void Paginate_PageNumber_ReturnsThatPage(int page, string[] expected)
    {
        AddFive();

        var result = _table.Paginate(new PaginationRequest { Page = page, PageSize = 2 });

        Assert.Equal(expected, result.Items.Select(p => p.PersonName).ToArray());
        Assert.Equal(5, result.TotalCount);
    }

    /// <summary>
    /// A missing row or request is refused.
    /// True: the caller fails at once with the argument named.
    /// False: a null would surface as an error from inside the SQL builder.
    /// </summary>
    [Fact]
    public void UpsertAndPaginate_NullArgument_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => _table.Upsert(null!));
        Assert.Throws<ArgumentNullException>(() => _table.Paginate(null!));
    }
}

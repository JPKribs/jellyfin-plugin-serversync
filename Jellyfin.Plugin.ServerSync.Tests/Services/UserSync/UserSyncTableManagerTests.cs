using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.UserSync;

/// <summary>
/// The Users sync table keeps one row for each user mapping and category, and groups, counts, and changes them by mapping.
/// </summary>
public sealed class UserSyncTableManagerTests : IDisposable
{
    private static readonly DateTime At = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly SyncTestDatabase _db = new("usertable");
    private readonly UserSyncTableManager _table;

    public UserSyncTableManagerTests()
    {
        _table = new UserSyncTableManager(_db.Provider, NullLogger<UserSyncTableManager>.Instance);
    }

    /// <inheritdoc/>
    public void Dispose() => _db.Dispose();

    private void Add(string source, string local, string category, SyncStatus status = SyncStatus.Synced, string? sourceName = null, string? localName = null)
        => _table.Upsert(new UserSyncItem
        {
            SourceUserId = source,
            LocalUserId = local,
            PropertyCategory = category,
            SourceUserName = sourceName ?? source,
            LocalUserName = localName ?? local,
            Status = status,
            StatusDate = At
        });

    private void AddMapping(string source, string local, SyncStatus status = SyncStatus.Synced, string? sourceName = null, string? localName = null)
    {
        Add(source, local, UserPropertyCategory.Policy, status, sourceName, localName);
        Add(source, local, UserPropertyCategory.Configuration, status, sourceName, localName);
        Add(source, local, UserPropertyCategory.ProfileImage, status, sourceName, localName);
    }

    private static string[] Categories(IEnumerable<UserSyncItem> rows) => rows.Select(r => r.PropertyCategory).ToArray();

    /// <summary>
    /// The names, the three values, the hashes, and the image hashes and sizes survive a write and a read.
    /// True: what a scan stored for a user is what the apply step reads.
    /// False: a policy would read back as the local value, or an image size would be lost.
    /// </summary>
    [Fact]
    public void Upsert_FullRow_RoundTrips()
    {
        var row = new UserSyncItem
        {
            SourceUserId = "src",
            LocalUserId = "loc",
            PropertyCategory = UserPropertyCategory.Policy,
            SourceUserName = "alice",
            LocalUserName = "alice-here",
            SourceValue = "{\"a\":1}",
            LocalValue = "{\"a\":2}",
            MergedValue = "{\"a\":3}",
            SourceValueHash = "source-hash",
            SyncedValueHash = "synced-hash",
            SourceImageHash = "img-src",
            LocalImageHash = "img-loc",
            SyncedImageHash = "img-synced",
            SourceImageSize = long.MaxValue,
            LocalImageSize = 0,
            SyncedImageSize = 5,
            Status = SyncStatus.Queued,
            StatusDate = At,
            ServerKey = "server"
        };

        _table.Upsert(row);
        var read = _table.GetByKey(("src", "loc", UserPropertyCategory.Policy))!;

        Assert.Equal(("alice", "alice-here", "server"), (read.SourceUserName, read.LocalUserName, read.ServerKey));
        Assert.Equal(("{\"a\":1}", "{\"a\":2}", "{\"a\":3}"), (read.SourceValue, read.LocalValue, read.MergedValue));
        Assert.Equal(("source-hash", "synced-hash"), (read.SourceValueHash, read.SyncedValueHash));
        Assert.Equal(("img-src", "img-loc", "img-synced"), (read.SourceImageHash, read.LocalImageHash, read.SyncedImageHash));
        Assert.Equal((long.MaxValue, 0L, 5L), (read.SourceImageSize, read.LocalImageSize, read.SyncedImageSize));
    }

    /// <summary>
    /// A write that carries no synced value keeps each stored one.
    /// True: a scan does not erase the record of what the two servers last agreed on.
    /// False: every scan would forget the agreed value and a local change would be overwritten.
    /// </summary>
    [Fact]
    public void Upsert_SyncedValuesNotGiven_AreKept()
    {
        _table.Upsert(new UserSyncItem { SourceUserId = "src", LocalUserId = "loc", PropertyCategory = "Policy", SyncedValueHash = "v", SyncedImageHash = "h", SyncedImageSize = 9, StatusDate = At });

        _table.Upsert(new UserSyncItem { SourceUserId = "src", LocalUserId = "loc", PropertyCategory = "Policy", SourceValueHash = "new", StatusDate = At });

        var read = _table.GetByKey(("src", "loc", "Policy"))!;
        Assert.Equal(("v", "h", 9L, "new"), (read.SyncedValueHash, read.SyncedImageHash, read.SyncedImageSize, read.SourceValueHash));
        Assert.Equal(1, _table.Count());
    }

    /// <summary>
    /// A read, a delete, and a status change by key touch only the row with the source user, the local user, and the category.
    /// True: a failed profile image does not mark the same user's policy as failed.
    /// False: one category's status or removal would spread to the whole mapping.
    /// </summary>
    [Fact]
    public void KeyRoutes_MappingAndCategory_TouchOnlyThatRow()
    {
        AddMapping("src", "loc");
        AddMapping("src", "other-loc");

        _table.UpdateStatusByKey(("src", "loc", UserPropertyCategory.ProfileImage), SyncStatus.Errored, "no image");
        _table.DeleteByKey(("src", "loc", UserPropertyCategory.Configuration));

        Assert.Equal((SyncStatus.Errored, "no image"), (_table.GetByKey(("src", "loc", UserPropertyCategory.ProfileImage))!.Status, _table.GetByKey(("src", "loc", UserPropertyCategory.ProfileImage))!.Reason));
        Assert.Equal(SyncStatus.Synced, _table.GetByKey(("src", "loc", UserPropertyCategory.Policy))!.Status);
        Assert.Equal(SyncStatus.Synced, _table.GetByKey(("src", "other-loc", UserPropertyCategory.ProfileImage))!.Status);
        Assert.Null(_table.GetByKey(("src", "loc", UserPropertyCategory.Configuration)));
        Assert.Null(_table.GetByKey(("loc", "src", UserPropertyCategory.Policy)));
        Assert.Equal(5, _table.Count());
    }

    /// <summary>
    /// A mapping is counted once under each status any of its rows is in.
    /// True: the Users card counts people, not categories.
    /// False: one user with three rows would count as three users.
    /// </summary>
    [Fact]
    public void CountUserMappingsByStatus_MappingInTwoStatuses_CountsOnceUnderEach()
    {
        AddMapping("alice", "a");
        _table.UpdateStatusByKey(("alice", "a", UserPropertyCategory.Policy), SyncStatus.Errored);
        AddMapping("bob", "b");

        Assert.Equal(2, _table.CountUserMappingsByStatus(SyncStatus.Synced));
        Assert.Equal(1, _table.CountUserMappingsByStatus(SyncStatus.Errored));
        Assert.Equal(0, _table.CountUserMappingsByStatus(SyncStatus.Queued));
    }

    /// <summary>
    /// A mapping's rows come back ordered by category, and another mapping's rows are left out.
    /// True: the detail view always lists Configuration, Policy, then ProfileImage for one user.
    /// False: the categories would shuffle between loads, or another user's rows would mix in.
    /// </summary>
    [Fact]
    public void GetByUserMapping_OneMapping_ReturnsItsRowsByCategory()
    {
        AddMapping("alice", "a");
        AddMapping("alice", "b");

        var rows = _table.GetByUserMapping("alice", "a");
        var none = _table.GetByUserMapping("a", "alice");

        Assert.Equal(new[] { "Configuration", "Policy", "ProfileImage" }, Categories(rows));
        Assert.All(rows, r => Assert.Equal("a", r.LocalUserId));
        Assert.Empty(none);
    }

    /// <summary>
    /// Mappings come back by source user name, each with all of its rows, and the total counts mappings.
    /// True: the Users tab shows one entry for each user with its three categories.
    /// False: the pager would count rows, and a page would cut a user's categories in half.
    /// </summary>
    [Fact]
    public void SearchUserMappingsPaginated_ThreeMappings_GroupsRowsAndCountsMappings()
    {
        AddMapping("id-3", "l3", sourceName: "carol");
        AddMapping("id-1", "l1", sourceName: "alice");
        AddMapping("id-2", "l2", sourceName: "bob");

        var page = _table.SearchUserMappingsPaginated(null, null, 0, 2);
        var rest = _table.SearchUserMappingsPaginated(null, null, 2, 2);

        Assert.Equal(new[] { "alice", "bob" }, page.Items.Select(g => g.SourceUserName).ToArray());
        Assert.Equal(new[] { "l1", "l2" }, page.Items.Select(g => g.LocalUserId).ToArray());
        Assert.All(page.Items, g => Assert.Equal(new[] { "Configuration", "Policy", "ProfileImage" }, Categories(g.Items)));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { "carol" }, rest.Items.Select(g => g.SourceUserName).ToArray());
    }

    /// <summary>
    /// A status filter picks the mappings with a row in that status, and each still carries all of its rows.
    /// True: filtering to Errored shows the whole user whose image failed.
    /// False: the user would show with only the failed category and look half configured.
    /// </summary>
    [Fact]
    public void SearchUserMappingsPaginated_StatusFilter_KeepsEveryRowOfAMatchingMapping()
    {
        AddMapping("alice", "a");
        _table.UpdateStatusByKey(("alice", "a", UserPropertyCategory.ProfileImage), SyncStatus.Errored);
        AddMapping("bob", "b");

        var page = _table.SearchUserMappingsPaginated(null, SyncStatus.Errored, 0, 50);

        var group = Assert.Single(page.Items);
        Assert.Equal("alice", group.SourceUserId);
        Assert.Equal(3, group.Items.Count);
        Assert.Equal(1, page.TotalCount);
    }

    /// <summary>
    /// A search matches either user name, and an empty table or no match gives no mappings.
    /// True: a user is found by their name on either server.
    /// False: a user known here by another name could not be found.
    /// </summary>
    [Fact]
    public void SearchUserMappingsPaginated_Search_MatchesEitherName()
    {
        var empty = _table.SearchUserMappingsPaginated(null, null, 0, 50);
        AddMapping("id-1", "l1", sourceName: "alice", localName: "wonder");
        AddMapping("id-2", "l2", sourceName: "bob", localName: "builder");

        var bySource = _table.SearchUserMappingsPaginated("ali", null, 0, 50);
        var byLocal = _table.SearchUserMappingsPaginated("build", null, 0, 50);
        var none = _table.SearchUserMappingsPaginated("nobody", null, 0, 50);

        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.TotalCount);
        Assert.Equal("id-1", Assert.Single(bySource.Items).SourceUserId);
        Assert.Equal("id-2", Assert.Single(byLocal.Items).SourceUserId);
        Assert.Empty(none.Items);
        Assert.Equal(0, none.TotalCount);
    }

    /// <summary>
    /// A page below 1 reads as the first page, and a page size outside 1 to 200 is pulled into that range.
    /// True: a bad request still gets a sane page of mappings.
    /// False: a page of 0 would give a negative offset, and a huge page size would load every user.
    /// </summary>
    [Theory]
    [InlineData(0, 2, 2)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 5000, 3)]
    [InlineData(2, 2, 1)]
    public void PaginateUserMappings_OutOfRangeRequest_IsPulledIntoRange(int page, int pageSize, int expectedMappings)
    {
        AddMapping("alice", "a");
        AddMapping("bob", "b");
        AddMapping("carol", "c");

        var result = _table.PaginateUserMappings(new PaginationRequest { Page = page, PageSize = pageSize });

        Assert.Equal(expectedMappings, result.Items.Count);
        Assert.Equal(3, result.TotalCount);
        Assert.Throws<ArgumentNullException>(() => _table.PaginateUserMappings(null!));
    }

    /// <summary>
    /// A bulk status change moves every category of the listed mappings and reports the rows changed.
    /// True: ignoring a user ignores their policy, configuration, and image together.
    /// False: a user would be left half ignored, or an unlisted user would be changed.
    /// </summary>
    [Fact]
    public void BulkUpdateStatusByMappings_ListedMappings_MoveEveryCategory()
    {
        AddMapping("alice", "a");
        AddMapping("bob", "b");
        AddMapping("carol", "c");

        var updated = _table.BulkUpdateStatusByMappings(new[] { ("alice", "a"), ("carol", "c"), ("nobody", "x") }, SyncStatus.Ignored, "by hand");

        Assert.Equal(6, updated);
        Assert.All(_table.GetByUserMapping("alice", "a"), r => Assert.Equal((SyncStatus.Ignored, "by hand"), (r.Status, r.Reason)));
        Assert.All(_table.GetByUserMapping("bob", "b"), r => Assert.Equal(SyncStatus.Synced, r.Status));
    }

    /// <summary>
    /// No mappings changes nothing, and a missing list is refused.
    /// True: an empty selection is a harmless no-op.
    /// False: an empty selection would touch every row, or a null would fail inside the transaction.
    /// </summary>
    [Fact]
    public void BulkUpdateStatusByMappings_NoMappings_ChangesNothing()
    {
        AddMapping("alice", "a");

        var updated = _table.BulkUpdateStatusByMappings(Array.Empty<(string, string)>(), SyncStatus.Ignored);

        Assert.Equal(0, updated);
        Assert.Equal(3, _table.CountByStatus(SyncStatus.Synced));
        Assert.Throws<ArgumentNullException>(() => _table.BulkUpdateStatusByMappings(null!, SyncStatus.Ignored));
    }

    /// <summary>
    /// Deleting a mapping removes its rows, reports how many, and leaves a mapping that shares one of its users.
    /// True: unmapping a user clears that pair only.
    /// False: a source user mapped to two local users would lose both.
    /// </summary>
    [Fact]
    public void DeleteByUserMapping_OneMapping_RemovesOnlyItsRows()
    {
        AddMapping("alice", "a");
        AddMapping("alice", "b");

        var deleted = _table.DeleteByUserMapping("alice", "a");
        var deletedAgain = _table.DeleteByUserMapping("alice", "a");

        Assert.Equal(3, deleted);
        Assert.Equal(0, deletedAgain);
        Assert.Equal(3, _table.GetByUserMapping("alice", "b").Count);
    }

    /// <summary>
    /// The source user and category filters ignore case, and each narrows the rows.
    /// True: an id or category typed in another case still finds its rows.
    /// False: the Users tab would come back empty for an id copied in upper case.
    /// </summary>
    [Fact]
    public void SearchPaginated_UserAndCategoryFilters_IgnoreCase()
    {
        AddMapping("abc", "a", sourceName: "alice");
        AddMapping("def", "b", sourceName: "bob");

        var byUser = _table.SearchPaginated(null, null, "ABC", null, 0, 50);
        var byCategory = _table.SearchPaginated(null, null, null, "profileimage", 0, 50);
        var byBoth = _table.SearchPaginated(null, null, "DEF", "POLICY", 0, 50);

        Assert.Equal(3, byUser.TotalCount);
        Assert.All(byUser.Items, r => Assert.Equal("abc", r.SourceUserId));
        Assert.Equal(new[] { "abc", "def" }, byCategory.Items.Select(r => r.SourceUserId).ToArray());
        Assert.Equal(("def", "Policy"), (Assert.Single(byBoth.Items).SourceUserId, byBoth.Items[0].PropertyCategory));
    }

    /// <summary>
    /// Rows come back errored first, then by source user name and category, with skip and take pulled into range.
    /// True: the flat list is stable and failed rows lead it.
    /// False: rows would shuffle between loads, or a bad offset would fail the query.
    /// </summary>
    [Fact]
    public void SearchPaginated_MixedRows_OrdersAndPages()
    {
        AddMapping("id-2", "b", sourceName: "bob");
        AddMapping("id-1", "a", sourceName: "alice");
        _table.UpdateStatusByKey(("id-2", "b", UserPropertyCategory.Policy), SyncStatus.Errored);

        var page = _table.SearchPaginated(null, null, null, null, -5, 3);
        var clamped = _table.SearchPaginated(null, null, null, null, 0, 0);

        Assert.Equal(new[] { ("bob", "Policy"), ("alice", "Configuration"), ("alice", "Policy") }, page.Items.Select(r => (r.SourceUserName!, r.PropertyCategory)).ToArray());
        Assert.Equal(6, page.TotalCount);
        Assert.Single(clamped.Items);
    }

    /// <summary>
    /// The request's page, status, and search reach the query, and a missing row or request is refused.
    /// True: the generic list path filters like the Users tab does.
    /// False: a filtered request would return every row.
    /// </summary>
    [Fact]
    public void Paginate_StatusAndSearch_FilterTheRows()
    {
        AddMapping("id-1", "a", sourceName: "alice");
        AddMapping("id-2", "b", sourceName: "bob");
        _table.UpdateStatusByKey(("id-1", "a", UserPropertyCategory.Policy), SyncStatus.Errored);
        _table.UpdateStatusByKey(("id-2", "b", UserPropertyCategory.Policy), SyncStatus.Errored);

        var result = _table.Paginate(new PaginationRequest { StatusFilter = SyncStatus.Errored, SearchTerm = "ali", Page = 0, PageSize = 5000 });

        Assert.Equal("id-1", Assert.Single(result.Items).SourceUserId);
        Assert.Equal(1, result.TotalCount);
        Assert.Throws<ArgumentNullException>(() => _table.Paginate(null!));
        Assert.Throws<ArgumentNullException>(() => _table.Upsert(null!));
    }
}

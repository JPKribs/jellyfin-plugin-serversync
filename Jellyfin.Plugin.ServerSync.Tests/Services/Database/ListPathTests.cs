using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.Extensions;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services.ContentSync.Tables;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Jellyfin.Plugin.ServerSync.Services.HistorySync;
using Jellyfin.Plugin.ServerSync.Services.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services.PeopleSync;
using Jellyfin.Plugin.ServerSync.Services.UserSync;
using Jellyfin.Plugin.ServerSync.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Database;

/// <summary>
/// Every module's list path, the exact queries the Sync tab runs, against a real database that was
/// closed and opened again. A query whose column list drifts from its mapper throws on every row, which
/// Jellyfin answers as a 400 and the tab shows as empty, so the real SQL runs here against real SQLite.
/// </summary>
public sealed class ListPathTests : IDisposable
{
    private readonly string _tempDir;

    public ListPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-lists-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Every module's list queries return rows from a database that was closed and opened again.
    /// True: the Sync tab lists every module's rows.
    /// False: a column drifting from its mapper would make a tab answer 400 and show empty.
    /// </summary>
    [Fact]
    public void EveryModuleListPath_AfterReopening_ReturnsRows()
    {
        using (var db = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir))
        {
            var provider = new FixedProvider(db);

            var metadata = new MetadataSyncTableManager(provider, NullLogger<MetadataSyncTableManager>.Instance);
            for (var i = 0; i < 60; i++)
            {
                var item = new MetadataSyncItem
                {
                    SourceLibraryId = "lib-1",
                    LocalLibraryId = "local-1",
                    SourceItemId = "item-" + i,
                    LocalItemId = Guid.NewGuid().ToString("N"),
                    ItemName = "Item " + i,
                    SourcePath = "/src/item" + i + ".mkv",
                    LocalPath = "/dst/item" + i + ".mkv",
                    ItemType = "Movie",
                    Status = (SyncStatus)(i % 5),
                    StatusDate = DateTime.UtcNow
                };
                item.Metadata.UpdateSource("{\"Name\":\"Item " + i + "\"}");
                item.Metadata.Local = "{\"Name\":\"Item " + i + " local\"}";
                metadata.Upsert(item);
            }

            var content = new ContentSyncTableManager(provider, NullLogger<ContentSyncTableManager>.Instance);
            content.Upsert(new SyncItem
            {
                SourceLibraryId = "lib-1",
                LocalLibraryId = "local-1",
                SourceItemId = "content-1",
                SourcePath = "/src/a.mkv",
                LocalPath = "/dst/a.mkv",
                SourceSize = 100,
                SourceCreateDate = DateTime.UtcNow,
                Status = SyncStatus.Queued,
                StatusDate = DateTime.UtcNow
            });

            var history = new HistorySyncTableManager(provider, NullLogger<HistorySyncTableManager>.Instance);
            history.Upsert(new HistorySyncItem
            {
                SourceUserId = "su-1",
                LocalUserId = "lu-1",
                SourceLibraryId = "lib-1",
                LocalLibraryId = "local-1",
                SourceItemId = "hist-1",
                ItemName = "Watched thing",
                Status = SyncStatus.Queued,
                StatusDate = DateTime.UtcNow
            });

            var people = new PeopleSyncTableManager(provider, NullLogger<PeopleSyncTableManager>.Instance);
            people.Upsert(new PeopleSyncItem
            {
                PersonName = "Some Actor",
                Status = SyncStatus.Queued,
                StatusDate = DateTime.UtcNow
            });

            var users = new UserSyncTableManager(provider, NullLogger<UserSyncTableManager>.Instance);
            users.Upsert(new UserSyncItem
            {
                SourceUserId = "su-1",
                LocalUserId = "lu-1",
                PropertyCategory = UserPropertyCategory.Policy,
                Status = SyncStatus.Queued,
                StatusDate = DateTime.UtcNow
            });
        }

        using (var db = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir))
        {
            var provider = new FixedProvider(db);

            var metadata = new MetadataSyncTableManager(provider, NullLogger<MetadataSyncTableManager>.Instance);
            var (metaPage2, metaTotal) = metadata.SearchMetadataSyncItemsPaginated(null, null, null, skip: 50, take: 50);
            Assert.Equal(60, metaTotal);
            Assert.Equal(10, metaPage2.Count);
            foreach (var dto in metaPage2.Select(item => item.ToDto(null, "http://src", includeBlobs: false)))
            {
                _ = dto.HasChanges;
                _ = dto.ChangesSummary;
            }

            var content = new ContentSyncTableManager(provider, NullLogger<ContentSyncTableManager>.Instance);
            var (contentItems, contentTotal) = content.SearchPaginated(null, null, null, 0, 50);
            Assert.Equal(1, contentTotal);
            Assert.Single(contentItems);

            var history = new HistorySyncTableManager(provider, NullLogger<HistorySyncTableManager>.Instance);
            var (histItems, histTotal) = history.SearchHistoryItemsPaginated(null, null, null, 0, 50);
            Assert.Equal(1, histTotal);
            Assert.Single(histItems);

            var people = new PeopleSyncTableManager(provider, NullLogger<PeopleSyncTableManager>.Instance);
            var peoplePage = people.Paginate(new PaginationRequest { Page = 1, PageSize = 50 });
            Assert.Equal(1, peoplePage.TotalCount);

            var users = new UserSyncTableManager(provider, NullLogger<UserSyncTableManager>.Instance);
            Assert.Single(users.GetAllStrict());
        }
    }
}

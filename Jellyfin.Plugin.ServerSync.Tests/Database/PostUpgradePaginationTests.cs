using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.PeopleSync;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Jellyfin.Plugin.ServerSync.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Database;

/// <summary>
/// End-to-end guard for the 66.0 regression: a populated v21 database is
/// upgraded in place (v22 adds RetryCount), then every module's list path —
/// the exact queries the UI tabs hit — must return rows.
/// <para>
/// The original failure was NOT the migration: Metadata's paginated query
/// carried a hand-written column list that predated RetryCount, so
/// MapFromReader's GetOrdinal threw ArgumentOutOfRangeException on every row.
/// Jellyfin's exception middleware maps ArgumentException to 400, and the
/// Metadata tab rendered empty. These tests run the real SQL against real
/// SQLite so any projection that drifts from its mapper fails here first.
/// </para>
/// </summary>
public sealed class PostUpgradePaginationTests : IDisposable
{
    private readonly string _tempDir;

    public PostUpgradePaginationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serversync-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedProvider : ISyncDatabaseProvider
    {
        public FixedProvider(SyncDatabase db) => Database = db;

        public SyncDatabase Database { get; }
    }

    [Fact]
    public void EveryModuleListPath_AfterV21Upgrade_ReturnsRows()
    {
        // Phase 1: build a current-schema DB and populate every table.
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
            var person = new PeopleSyncItem
            {
                PersonName = "Some Actor",
                Status = SyncStatus.Queued,
                StatusDate = DateTime.UtcNow
            };
            people.Upsert(person);

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

        // Phase 2: shape the file back to a v21 install — no RetryCount
        // columns, user_version 21 — exactly what a 65.x database looks like.
        var dbPath = Path.Combine(_tempDir, "serversync", "sync.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            foreach (var table in new[] { "HistorySyncItems", "UserSyncItems", "PeopleSyncItems", "MetadataSyncItems" })
            {
                using var drop = conn.CreateCommand();
                drop.CommandText = $"ALTER TABLE {table} DROP COLUMN RetryCount";
                drop.ExecuteNonQuery();
            }

            using var ver = conn.CreateCommand();
            ver.CommandText = "PRAGMA user_version = 21";
            ver.ExecuteNonQuery();
        }

        // Phase 3: reopen the way the plugin does at startup (migration runs),
        // then hit every list path the UI uses.
        using (var db = new SyncDatabase(NullLogger<SyncDatabase>.Instance, _tempDir))
        {
            var provider = new FixedProvider(db);

            var metadata = new MetadataSyncTableManager(provider, NullLogger<MetadataSyncTableManager>.Instance);
            var (metaPage2, metaTotal) = metadata.SearchMetadataSyncItemsPaginated(null, null, null, skip: 50, take: 50);
            Assert.Equal(60, metaTotal);
            Assert.Equal(10, metaPage2.Count);
            foreach (var item in metaPage2)
            {
                var dto = item.ToDto(null, "http://src", includeBlobs: false);
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
            var userRows = users.GetAllStrict();
            Assert.Single(userRows);
        }
    }
}

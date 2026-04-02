# People Syncing

## Summary

People Syncing copies person metadata from a Source Jellyfin Server and applies it to matching person entities on your Local Server. The plugin derives the list of people to sync from your already-synced metadata items (actors, directors, writers associated with your content), then fetches each person's full record from the Source Server. Person metadata includes biographical information (overview, birth/death dates), external IDs (IMDB, TMDB), and profile images. People are matched across servers by name rather than by ID, since person GUIDs differ between Jellyfin installations. This is useful when you curate person metadata on a primary server and want secondary servers to reflect those changes.

---

## Statuses

| Status | Description |
|--------|-------------|
| **Pending** | Item is awaiting initial processing (rarely seen in normal operation). |
| **Queued** | Person has metadata or image differences and is waiting for the next sync task to apply changes. |
| **Synced** | Person metadata matches between servers with no pending changes. |
| **Errored** | Person failed to sync. Check the error message for details. |
| **Ignored** | Person has been explicitly skipped and will not be processed in future syncs. |

---

## How It Works

### Refresh Sync Table

The Refresh task extracts unique person names from all synced metadata items in the metadata sync table, rather than querying the global `/Persons` endpoint. This ensures only people associated with your mapped content are tracked. For each unique person name, the plugin fetches the full person record from the Source Server and compares it against the Local Server's person entity.

The plugin tracks metadata and images in a single sync record per person, with separate flags indicating which categories have changes. Stale records for people no longer associated with any synced content are automatically removed.

**Source Server APIs Used:**

| API | Purpose |
|-----|---------|
| `GET /Persons/{name}` | Fetches the full person record as a BaseItemDto (overview, dates, provider IDs, tags, etc.) |
| `GET /Items/{id}/Images` | Retrieves image info including size, dimensions, and type for each person image |

### Metadata Categories

Person metadata is stored as a single JSON blob and compared as a whole:

| Field | Description |
|-------|-------------|
| **Name** | Person display name |
| **OriginalTitle** | Alternative name |
| **SortName** | Name used for sorting |
| **ForcedSortName** | Manually overridden sort name |
| **Overview** | Biography text |
| **PremiereDate** | Birth date |
| **EndDate** | Death date |
| **ProductionYear** | Birth year |
| **Tags** | Associated tags |
| **ProviderIds** | External IDs (IMDB, TMDB, etc.) |
| **LockedFields** | Fields locked from automatic updates |
| **LockData** | Whether the person record is locked |

### Image Comparison

Images are compared by type count and file size rather than raw content. The plugin fetches image metadata (size, dimensions) from the Source Server and compares against local file sizes. This detects changes without downloading image data during refresh. Only during sync are changed images downloaded and applied. People typically only have Primary (portrait) images.

### Sync People

The Sync task processes all Queued items by applying Source metadata to Local person entities. For each person with metadata changes, it deserializes the Source metadata JSON blob and applies each field individually. For image changes, it downloads images from the Source Server and saves them locally through Jellyfin's provider manager.

**Local Server Internal APIs Used:**

| Service | Purpose |
|---------|---------|
| `ILibraryManager.GetPerson()` | Finds the local person entity by name |
| `ILibraryManager.GetItemById()` | Loads the full person entity for metadata comparison and updates |
| `BaseItem.UpdateToRepositoryAsync()` | Saves updated metadata fields to the person |
| `IProviderManager.SaveImage()` | Downloads and saves images from Source Server |

### Comparison Logic

People are matched between servers by **name** (case-insensitive). Unlike content or metadata sync which uses file path translation, people sync relies on person names being consistent across servers, which they are when both servers use the same metadata providers.

For metadata fields, the plugin serializes both Source and Local values to JSON and compares them semantically using the same JSON comparison utility as metadata sync. For images, the plugin compares image count per type and file sizes to detect changes without downloading the actual image data.

After sync completes, the plugin updates the person's Local values in the tracking database. On subsequent refreshes, only people with new differences are re-queued. People can also be manually Ignored if you want to preserve Local metadata for specific individuals.

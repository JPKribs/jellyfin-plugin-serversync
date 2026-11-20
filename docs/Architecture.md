# Architecture

How the parts of Server Sync connect, and the rules that hold across all of them. Paths are under `Jellyfin.Plugin.ServerSync/`.

## Overview

```
Scan          Other servers ──▶ Refresh task ──▶ Sync table ──▶ Apply task ──▶ Jellyfin library

Live, out     Jellyfin library ──▶ LocalChangeObserver ──▶ LocalHintPublisher ──▶ OutboundHintWorker ──▶ Other servers

Live, in      Other servers ──▶ PeerController.Queue ──▶ InboundHintWorker ──▶ Hint handler ──▶ Jellyfin library

Link          Player ──▶ LinkController ──▶ LinkStreamer ──▶ Source server

Provider      Jellyfin refresh ──▶ PeerMetadataProvider ──▶ PeerMetadataSource ──▶ Source server
```

* **Scans** read every source on a schedule and settle everything. They are the safety net.
* **Live changes** carry a hint the moment something changes, between servers that both run Server Sync.
* **Links** stream a source's file through Server Sync on both servers instead of copying it.
* **The metadata provider** answers Jellyfin's own refresh, identify, and image lookups from a source.
* **sync.db** holds every table, queue, version, and pairing.

## Startup

`Plugin.cs` builds the plugin on `PluginBase` from Base.

1. It reads the stored database version. When the database is older than this release can open, the plugin is **held**. It copies the settings aside, refuses every save, starts no background work, and every page and `GET /Sync/Health` say why.
2. It builds a `SecretProtector` with Base's `StableSecretProtection`.
3. It encrypts any API key still stored in plain text.

`PluginServiceRegistrator.cs` registers services.

* Every class with `[PluginService]` is registered by reflection.
* Each hosted service except `HiddenTaskScheduleCleaner` is wrapped in `Gate<T>`, which starts nothing while the plugin is held.
* `Tasks/HiddenTaskScheduleCleaner` removes any schedule left on a hidden task, so only the two visible tasks run on a timer.
* `LinkPlaybackFilter` is added as an MVC filter on Jellyfin's own routes.
* Two named `HttpClient`s carry the SSRF guard, Base's `BaseRetryHandler`, and `RedirectRefusingHandler`. `ServerSyncSource` may reach private addresses and `ServerSyncSourcePublic` may not.

`Services/Clients/SourceServerClientFactory` builds every `SourceServerClient`. It decrypts the key, checks the URL, picks the client by **Allow private network address**, and attaches pairing when the entry is saved.

## Configuration

`Configuration/PluginConfiguration.cs` holds the settings.

* `Servers` is the list of server entries, in rank order. An entry's place in the list is its rank.
* `Enable*Sync` turns each sync type on.
* `MetadataSyncPrecedence`, `PeopleSyncPrecedence`, `UserSyncPrecedence`, and `PlaylistSyncPrecedence` are each `Newest` or `Rank`. Content is decided by rank and origin, and history by merging.
* `UserSyncConflict` is **User conflicts**, as `Newest`, `Permissive`, or `Restrictive`.
* `PlaylistRemovedAction` is **Removed playlists**, as `Delete` or `Keep`.
* `HintDebounceSeconds` is **Send changes after**.

`Models/Configuration/SourceServer.cs` is one server entry.

| Member | Holds |
| :--- | :--- |
| `Key` | A stable id carried on every row as `ServerKey` |
| `Url`, `ExternalUrl`, `AllowPrivateNetwork` | How to reach the server |
| `ApiKey` | The key, encrypted at rest |
| `Mode` | `Pull`, `Push`, or `Sync`. `Pulls` and `Pushes` read it |
| `LibraryMappings` | Roots, the library filter, `StorageMode`, `ExcludeFromLinkCache`, history negotiation, and **Always keep** picks |
| `UserMappings` | Users, with each user's link limits |
| `AllowLinking` and the `Link*` members | What a linking server may stream |

A save from the page goes through Jellyfin's plugin configuration route to `Plugin.UpdateConfiguration`, which takes `ConfigurationSaveLock` and calls `OnConfigurationSaving`. That hook sanitizes values, resolves kept API keys with `SecretProtector.ResolveIncoming`, and clears the link cache folder when the cache is turned off or moved. A save from the plugin goes through `Services/Configuration/PluginConfigurationManager`.

## Scheduled Tasks

Two visible tasks run every six hours. Each runs hidden tasks in order. Each step catches its own failure, and the failures are raised together at the end.

| Visible task | Steps |
| :--- | :--- |
| **Sync Content** (`SyncContentTask`) | `UpdateSyncTablesTask`, then `SyncMissingContentTask` when the refresh succeeded, then `SyncCollectionsTask` |
| **Sync Information** (`SyncInformationTask`) | Metadata, People, Users, and History, each a refresh then an apply, then `SyncPlaylistsTask` |

**Empty Recycling bin** and **Clean Temporary Directory** run daily. `POST /Sync/Tasks/{taskKey}/Run` starts a single refresh or apply step from the **Sync** tab.

The task bases live in `Tasks/Common/`.

* `RefreshSyncTaskBase` scans the sources, builds rows, settles which server speaks for each item, prunes, and records watermarks.
* `SyncQueueTaskBase` applies every `Queued` row.
* `SyncModuleMutex` lets one task per sync type run at a time.

## Sync Types

Every sync type follows the same path. A refresh task reads the sources and fills a sync table. An apply task writes each queued row to Jellyfin.

| Type | Refresh | Table | Apply |
| :--- | :--- | :--- | :--- |
| Content | `UpdateSyncTablesTask` | `SyncItems` | `SyncMissingContentTask` |
| History | `RefreshHistorySyncTableTask` | `HistorySyncItems` | `SyncMissingHistoryTask` |
| Metadata | `RefreshMetadataSyncTableTask` | `MetadataSyncItems` | `SyncMissingMetadataTask` |
| People | `RefreshPeopleSyncTableTask` | `PeopleSyncItems` | `SyncMissingPeopleTask` |
| Users | `RefreshUserSyncTableTask` | `UserSyncItems` | `SyncMissingUserTask` |
| Playlists | `SyncPlaylistsTask` | `PlaylistCopies` | `SyncPlaylistsTask` |

* Each of the first five tables has a manager on `SyncTableManagerBase` in `Services/Database/`. `PlaylistCopies` is held by `PlaylistCopyStore`.
* When several sources offer the same item, `RefreshSyncTaskBase.CollectAsync` lets one speak for it. Rank decides unless the task's `ContestAsync` names another.
* Content writes copies with `DownloadService`, links with `LinkWriter`, and removals with `FileDeletionService` and `RecyclingBinService`.
* Content takes a file from the highest ranked source that holds it. On an incremental scan, `UpdateSyncTablesTask.ApplyReportedRemovalsAsync` asks each source for `GET /Sync/Peer/Changes`, the files that left its library, which the source keeps in `RemovedItems`.
* History merges three way in `HistorySyncMergeService`. The source whose history of a file moved since the last scan speaks for it, so each source is merged in turn. For a Sync server with **Negotiate history** on, the merge is offered to the other server through `POST /Sync/Peer/History`, which `PeerHistoryService` answers there.
* Metadata and people let the source with the newest stamp speak, through `ScanConflictSettler.NewestOffersAsync`, unless precedence is `Rank`. `ScanConflictSettler.SettleAsync` then keeps a value this server edited more recently.
* Users have no stamps and no live changes. The source that changed a user since the last scan speaks for that user. With a Sync server, `UserSyncTableService.SettleBothWays` keeps a change made on this server until that server takes it, and `UserPolicyCombiner` combines the two policies when both changed, by **User conflicts**.
* Playlists mirror through `PlaylistMirror` and decide with `VersionConflictResolver`. A deletion is stamped like an edit. `SyncPlaylistsTask.SettleDeletedAsync` settles a deletion that was not announced, by **Removed playlists**, and a playlist this server knew and no longer holds is not made again from an older copy.
* Content and metadata scan incrementally between full scans, using `ScanWatermarkStore`.

## Peers

### Pairing

Servers that list each other share a secret, so one server cannot pretend to be another.

1. A server asks the other for `GET /Sync/Peer/Link`.
2. `PeerController` calls back to the entry's own saved URL with `POST /Sync/Peer/Pair`, carrying a new secret.
3. `PeerPairController` accepts the secret only while its side expects a pairing.
4. Both sides store the secret in `PeerPairings`, encrypted with `SecretProtector`.

Every later request carries the secret in `X-ServerSync-Pairing`. `PeerController.Sender` answers `409` for a server it does not list and `428` for a wrong secret, and the client pairs again on `428`.

### Protocol

`Services/Queue/HintProtocol` sets `ProtocolVersion` and `MinProtocolVersion`. Requests carry them in headers, and `RequireCompatibleProtocolAttribute` answers `426` when two servers do not overlap. `GET /Sync/Peer/Capabilities` reports the protocol, the sync types this server accepts, and `Link` when it offers links. `OutboundHintWorker` reads it every minute and pauses a server whose protocol does not overlap.

### Who May Call

`PeerCallerGuardAttribute` checks every peer route against the **Whitelist** with `PeerCallerPolicy`. Administrators and API keys from an enabled server entry always pass. A standard user is held to their own data.

### Api

| Controller | Route | Serves |
| :--- | :--- | :--- |
| `PeerController` | `/Sync/Peer` | Hints, completion, status, interest, versions, history, playlists, lookups, media segments |
| `PeerController.Links` | `/Sync/Peer` | Streams, stream checks, playback reports, remote control, link details, images, removals, copies |
| `PeerPairController` | `/Sync/Peer/Pair` | Pairing |
| `LinkController` | `/Sync/Link` | The address a `.strm` holds, for this server only |
| `HintsController` | `/Sync/Hints` | The **Change queue**, **Check link**, and stamp lookups |
| `ServersController` | `/Sync/Servers` | Removing a server's rows |
| `ConfigurationController` | `/Sync` | Everything else the pages call |

Peer routes need a signed in caller and the whitelist. Operator routes need an administrator.

## Live Changes

A change travels as a hint that says what changed, never the value. The receiver pulls the value itself.

1. **Observe.** `Services/Queue/LocalChangeObserver` listens to Jellyfin's user data and library events. It stays quiet for a write Server Sync made itself, a file it downloaded, a metadata fill from another server, and anything no peer wants. A file rewritten in place is told by a file time newer than its Content stamp. A file that leaves the library is held for `HintProtocol.RemovalHold` before it is published, so the new file of a rename goes first.
2. **Stamp.** A hand made edit is stamped at once in `ObjectVersions` with this server and the time. History is also stamped by part, played or favorite, as `HistoryParts` names them.
3. **Gather.** The change waits until the item has gone untouched for **Send changes after**.
4. **Publish.** `LocalHintPublisher` writes one `OutboundHints` row per server that this one pushes to and that wants it. `LoopBreaker` holds a change that keeps coming back.
5. **Deliver.** `OutboundHintWorker` sends batches to `POST /Sync/Peer/Queue`. `HintDelivery` decides what each answer means. A `200` waits for completion, a `400` fails, a refusal pauses the server, and anything else retries with growing delays.
6. **Receive.** `PeerController.Queue` checks pairing and direction, sorts each hint with `HintTriage`, and stores it in `InboundHints`. A hint for an item that has not arrived yet is parked until it does.
7. **Apply.** `InboundHintWorker` runs one lane for content and one for everything else.

    | Kind | Handler |
    | :--- | :--- |
    | History | `HistoryHintHandler` merges three way and writes. When the agreed base cannot be used, it takes only the parts the hint names, each by its own stamp |
    | Metadata, People | `ItemHintHandler` decides with `VersionDecider` and applies through the apply task |
    | Content | `ContentHintHandler` downloads a new file. A new id in the folder of a gone file is paired with its row by `RenameMatcher` and re-keyed, so the file moves or is replaced. Every change to a linked file writes the link again. For a file that left the source, it moves the link to another holder with `LinkRehomer`, or removes it, and applies Removed files to a copy at once |
    | Playlists | `PlaylistHintHandler` mirrors through `PlaylistMirror`, and deletes or keeps a copy by **Removed playlists** |

    A history hint names its parts in its item type, such as `Movie+Played`, written by `HintProtocol.HistoryItemType`.

8. **Complete.** The receiver reports `POST /Sync/Peer/Complete`, and the sender deletes the row. `OutboundHintWorker` asks for `GET /Sync/Peer/Status` after ten minutes and resends any hint the receiver lost.

`QueueMaintenanceService` clears settled hints, retries errored rows, and raises items that have failed for a day on the **Activity** page. `HintActivityLog` writes one activity entry per outcome with Base's `ActivitySummary`.

## Links

A link is a `.strm` holding an address on the linking server itself, `/Sync/Link/{id}?k={secret}`. The secret is an HMAC keyed by `serversync-link.key`, so no credential for the source is ever written to a file.

| Step | Where |
| :--- | :--- |
| Write the `.strm`, `.nfo`, and subtitles | `LinkWriter`, `LinkFiles`, `LinkNfo` |
| Give the link the source's media info | `LinkMediaInfo`, `LinkProbe` |
| Ask the source whether a user may play | `LinkPlaybackFilter`, `LinkAccess`, `POST /Sync/Peer/Stream/Check` |
| Allow the stream for a short time | `LinkGrants` |
| Serve the address in the `.strm` | `LinkController`, which accepts only this server's own requests |
| Stream the bytes | `LinkStreamer`, from the link cache or `GET /Sync/Peer/Stream/{itemId}` |
| Map the user and enforce limits on the source | `LinkPolicy`, `LinkSessions`, `ThrottledReadStream` |
| Show playback on the source | `LinkPlaybackReporter`, `LinkPlaybackSessions` |
| Keep played links locally | `LinkCache`, `LinkCacheFiller` |
| Check each link still streams | `LinkVerifier`, during **Sync Content** |
| Move a link to another holder | `LinkRehomer` |
| Hold a title as a copy | `AlwaysKeep`, `KeptCopies` routes |

`SyncItems.StorageMode` is `Copy`, `Link`, `Promoting` for a link becoming a copy, or `Unkeeping` for a copy going back to a link.

## Metadata Provider

Server Sync is also a metadata and image provider that Jellyfin calls on a refresh, an identify, and an image search. Jellyfin builds the providers itself, so they are not registered in `PluginServiceRegistrator.cs`.

| Step | Where |
| :--- | :--- |
| Answer Jellyfin for each media type | `PeerMetadataProvider<TItem, TInfo>` and its subclasses, and `PeerImageProvider`, each in a file of its own in `Services/Providers/` |
| List the sources that can answer a lookup | `ParentDirectory` |
| Find the item on a source and read its metadata and images | `PeerMetadataSource`, through `POST /Sync/Peer/Lookup` |
| Keep the fill from being announced as an edit made here | `ProviderFills` |
| Read the sources' media segments into each Metadata row on refresh, and write them to the local segment table on sync | `RefreshMetadataSyncTableTask` through `POST /Sync/Peer/Segments` and `MediaSegmentsValue.Choose`, then `SyncMissingMetadataTask` into `MediaSegmentStore` |
| Answer the source's media segments, with the server they were first made on | `PeerSegmentReader` |
| Answer Jellyfin's **Media Segment Scan** from the kept segments | `PeerSegmentProvider`, registered with `[PluginService]` as an `IMediaSegmentProvider` |

While the plugin is held, a lookup finds nothing. So does a lookup for an item while `EnableMetadataSync` or `MetadataSyncProvider` is off, and for a person while `EnablePeopleSync` or `PeopleSyncProvider` is off, and no server is asked.

The media segment provider answers only while `EnableMetadataSync` and `MetadataSyncSegmentProvider` are on, and never asks a source itself. It uses kept segments only when the source's file has the same size and runtime as the local one, and `PeerSegmentProvider.Plan` decides the rest with `MetadataSyncReplaceSegments` and `MetadataSyncRemoveSegments`. It runs last, as `IHasOrder` with `int.MaxValue`. When nothing is kept for an item, it gives back the segments Jellyfin holds, so nothing is removed. `PeerSegmentReader` serves every segment a file shows, with `OriginServerId` set to the server they were first made on when Server Sync copied them, and null when they were made there. `MediaSegmentsValue.Choose` takes the first server in rank order with segments, keeps the origin, or the server's id when it is null, and passes over segments whose origin is this server, so a server never takes back its own.

## Database

`sync.db` lives in `<Jellyfin data>/serversync/`. `Services/Database/SyncDatabase` opens it in WAL mode with one write connection and up to four readers. A corrupt file, or one from a newer release this one cannot read, is set aside with Base's `DatabaseSchema.SetAside`.

`Services/Database/SyncSchema` creates every table and sets the version.

* `CurrentSchemaVersion` is the schema this release writes.
* `MinReaderVersion` is the oldest schema version that can read it. It rises to `LinkReaderVersion` once the database holds a link, and to `UnkeepReaderVersion` once a copy is going back to a link.
* `OldestUpgradableRelease` is `12.4.13.0`, the oldest release this one upgrades from, and `OldestUpgradableSchema` is the schema it writes. `SyncSchema.Upgrade` brings a database from there forward by creating the tables added since, and changes no stored row.

| Table | Holds | Store |
| :--- | :--- | :--- |
| `SyncItems` | Content rows, with status, storage, and origin | `ContentSyncTableManager` |
| `HistorySyncItems` | Watch history per user and item | `HistorySyncTableManager` |
| `MetadataSyncItems` | Item details, images, and media segments | `MetadataSyncTableManager` |
| `PeopleSyncItems` | People's details and images | `PeopleSyncTableManager` |
| `UserSyncItems` | User settings per mapping and category | `UserSyncTableManager` |
| `PlaylistCopies` | Which local playlist copies which source playlist | `PlaylistCopyStore` |
| `OutboundHints` | Hints owed to other servers | `OutboundHintStore` |
| `InboundHints` | Hints received and not yet applied | `InboundHintStore` |
| `ObjectVersions` | The current stamp of each item | `VersionStore` |
| `PendingVersions` | Stamps read but not yet applied | `VersionStore` |
| `PeerPairings` | Pairing secrets | `PeerPairingStore` |
| `ScanWatermarks` | Where each incremental scan left off | `ScanWatermarkStore` |
| `SyncAttention` | When each failing row first failed | `MaintenanceStore` |
| `RemovedItems`, `RemovedItemsLog` | Media removed from the library in the last 30 days | `RemovedItemsStore` |
| `ScanRuns` | What each of the last 10 scans of a sync type found | `SyncStatisticsStore` |
| `LinkStreamDays` | Link streams started for each server on each of the last 30 days | `SyncStatisticsStore` |
| `SourceMediaSegments` | The media segments a source holds for each local file, with the size and runtime of the source's file | `MediaSegmentStore` |

The five sync table managers extend `SyncTableManagerBase`, and every other store extends `QueueStoreBase`. Both write under the database's one write lock, and neither opens the database until it is first used.

## Config Pages

Each tab is a page in `Web/` with a script of the same name. Each page calls `createServerSyncPage` in `serversync_shared.js`, which runs the page through Base's `createConfigPage`, sets the tabs, and builds the helper bag with the `Sync` route prefix. Settings load and save through Jellyfin's plugin configuration route. Every page calls `Health` as it loads and shows the reason at its top while the plugin is held. Thumbnails of a source's items load through `ImageProxy`, so the page never holds a source's API key.

| Tab | Page | Calls |
| :--- | :--- | :--- |
| **Sync** | `serversync_sync.html` and the `serversync_sync_*.js` modules | Each sync table's routes, `Tasks/{taskKey}/Run`, and the `Hints` routes |
| **Servers** | `serversync_servers.html` | `TestConnection`, `Authenticate`, `GetSourceLibraries`, `GetSourceUsers`, `Hints/CheckPeer`, `Servers/{key}/Rows` |
| **Storage** | `serversync_storage.html` | `SourceLibraryItems`, `KeptCopies`, `KeptCopies/Unkeep` |
| **Statistics** | `serversync_statistics.html` | `Statistics` |
| **Settings** | `serversync_settings.html` | `GetSourceUsers`, `LinkCache`, `LinkCache/Clear`, `Reset/{table}` |

## Rules That Hold Everywhere

| Rule | What it means | Where it holds |
| :--- | :--- | :--- |
| No echoes | A write made because of another server is never announced back | `ApplyGuard`, `WrittenFiles`, `ProviderFills`, and `VersionDecision.Adopt`, which writes nothing when values match |
| One hop | A server announces only changes made on it. A link or `.strm` is never passed on | Writes under `ApplyGuard`, `LocalHintPublisher` with `excludePeerKey`, `UpdateSyncTablesTask.IsStrm` |
| No loops | A change cannot circle a ring of servers | `LoopBreaker`, and `LocalChangeObserver` dropping a change a newer one replaced |
| Newest edit wins | With **Newest** precedence, the latest stamp wins and the server id breaks ties. Users have no stamps, so the source that changed since the last scan wins | `VersionDecider`, `VersionConflictResolver`, `ScanConflictSettler`, `RefreshUserSyncTableTask.ContestAsync` |
| Rank breaks the rest | With **Ranked** precedence, or with no stamps, the higher server wins | `RefreshSyncTaskBase` pull order |
| Scans correct everything | A full scan lists every source and settles each item with the source that speaks for it, so a lost hint is settled later. An incremental scan lists only what changed | `RefreshSyncTaskBase.DecideRunKind`, watermarks recorded only after a clean run |
| Never remove in bulk by accident | A refresh that would prune half the rows stops | `RefreshSyncTaskBase.MaxPruneFraction`, `PruneIfSafeAsync` |
| Stop calling a server that is down | Repeated failures open a breaker for that source | `CircuitBreaker` from Base in `SyncMissingContentTask` |
| Only reach allowed addresses | Every outbound call passes the SSRF guard and refuses redirects | `ConfigurationUtilities`, `RedirectRefusingHandler` |
| Secrets stay secret | API keys and pairing secrets are encrypted at rest. The browser never receives a key in plain text, and sends back a placeholder for a key it did not change | `SecretProtector` from Base |

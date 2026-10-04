# Multi Server Sync

Plan written 2026-10-03. This describes how Server Sync grows from a single source pull into a pool of servers that keep each other current, without giving up the pull based scan that already works.

## Goals

* Keep every write on the pull path. A server only ever changes its own data after reading the peer. Nothing is ever pushed into a server's library from outside.
* The destination always runs the plugin. The source only needs the plugin to send hints.
* Changes travel as hints, not payloads. A hint says what changed and where. The receiver pulls the live state.
* Nothing is lost. Hints are stored on both ends until the work is done, and the scheduled scan remains the safety net for anything the hints missed.
* No loops in a pool of three or more servers, and the newest human edit always wins.

## Server modes

Each configured server has one mode.

| Mode | Meaning |
| --- | --- |
| Scan | This server pulls from it on a schedule. Today's behavior. The remote needs no plugin. Only one server can be in Pull mode. |
| Send | This server sends hints to it when local data changes. The remote runs the plugin and pulls the change. |
| Both | Pull and Push together. Used for a pool of equals. |

Hints are accepted from any configured server that holds a key for this one. Hints are sent to every server in Push or Sync mode.

## Data model

All tables live in the existing sync database.

**Versions.** One row per object this plugin tracks: kind, key, origin server id, origin timestamp. A local edit sets the version to this server and the time of the edit. An apply that came from a hint sets the version to the one the hint carried. Saved dates from Jellyfin are never used for conflict decisions. See Conflict resolution.

**Outbound.** One row per peer per hint: hint id, peer, kind, key, context, version, state, attempts, next attempt, last error. The hint id is this server's id plus a sequence number. Rows coalesce on peer, kind, and key, so repeated edits before delivery are one row. States move from pending to sent to done, and a done row is deleted. Persisted so a restart loses nothing.

**Inbound.** One row per hint received: hint id, source server, kind, context, version, date received, attempts, last error. The row stays until the work is complete, then it is removed and the source is told.

**Context by kind.** Content, metadata, and images carry the item path. History carries the item path and the source username. People carry the person name. Users carry the username. The receiver maps these through its library and user mappings. The source's own ids are included as a convenience but never trusted as keys.

## Endpoints

All routes sit behind Jellyfin's RequiresElevation policy. The caller presents an API key the receiving server issued. The plugin adds no authentication of its own. The routes live under `/ServerSync/Peer`, beside the history negotiation endpoint, because `/ServerSync/Status` was already taken by the dashboard's content status.

**POST /ServerSync/Peer/Queue.** Receive a hint. The receiver stores it durably and answers 200, which means queued, not applied. A hint for something the receiver does not map, such as a path in an unmapped library, is still answered 200 and dropped, because the sender has no knowledge of the receiver's mappings.

**POST /ServerSync/Peer/Complete.** The receiver reports a hint id as done. The source removes the matching outbound row.

**GET /ServerSync/Peer/Status.** Lists this server's inbound queue. A source consults it only for outbound rows that were sent and not completed after a grace period of one hour. If the hint id is still queued the peer is working and the source waits. If it is absent and was never completed, the work was lost and the source resends.

**POST /ServerSync/Peer/Versions.** Returns effective versions for a batch of keys. Used by the full scan against a Both peer so that conflicts resolve on origin versions rather than saved dates.

**Response codes seen by the sender.**

| Code | Meaning on the sender |
| --- | --- |
| 200 | Queued. Mark the row sent, stop resending. |
| 400 | Malformed. Permanent for that row, surfaced on the dashboard. |
| 401 or 403 | Bad key. Pause the peer, surface it. |
| 404 | No plugin on that server. Pause the peer with a warning. |
| 409 | The receiver does not list the sender as a Pull or Sync server, so it has nothing to pull from. Pause the peer with the reason. |
| 5xx or no answer | Retry with backoff of one minute, five, fifteen, then one hour, capped, forever. |

## Lifecycle of one change

1. A local event fires: library item added, updated, or removed, user data saved, user created, updated, or deleted. The subscriber filters by the library and user mappings and debounces five seconds per key.
2. If the key is in the inbound queue, the event is the result of an apply and no hint is raised. Otherwise the version row is set to this server and now, and one outbound row per Push or Sync peer is written.
3. The outbound worker delivers each row immediately, in order per peer, and records the state.
4. The receiver stores the inbound row, answers 200, and its inbound worker fetches that one object from the source, builds the record, decides, and applies through the same code the full scan uses.
5. After the apply the receiver compares its final value to the source's value. Equal means stay quiet. Different, which happens when this server also had a change or history merged to something new, means raise outbound hints to every peer including the source, carrying this server's version.
6. The receiver removes the inbound row and posts Complete to the source. The source removes the outbound row. When the last peer completes, the hint is gone everywhere.

## Conflict resolution

**Content equality first.** If the pulled value equals the local value nothing is written, nothing is raised, and the version is adopted from the hint. This alone ends most loops.

**Versions decide the rest.** When values differ, the newer origin timestamp wins. Ties break by origin server id. Because an applied copy carries the origin's version rather than the time the copy landed, a copy can never look newer than the edit it came from. Only recorded edits count. Jellyfin's saved dates are never used, because a library scan moves them too, and a freshly scanned copy would otherwise outrank the edit it came from. A recorded edit on either side beats an unrecorded value on the other, and when neither side has recorded an edit the source wins, as it does for a scan only source. This is the specific failure the design guards against: server A edits twice in quick succession, server B applies the first edit after the second was made, and without origin versions B's copy would outrank A's newer edit and fan the stale value across the pool.

**History** keeps its negotiated merge: three way against the last agreed base, most recent play wins when both sides moved, favorites are kept when both sides moved. The base must be recorded on both servers, not only on the one that proposed the state, or the next change coming the other way is merged against a stale base and an older play outranks a newer unmark. The negotiation endpoint therefore updates the receiving server's own row for the sender whenever a proposal is applied or already held, creating the row from the sender's ids on first contact. When no base exists at all and this server knows when its own value was edited, the first contact is decided on origin versions like every other kind. With neither a base nor a local version the two way merge stands, so a play that predates versioning is never lost.

**Scan only sources** keep today's rule. The source wins, since the plugin cannot read versions from a server without the plugin.

## Loop prevention

Two guards, used together.

* Inbound membership. Writes made while an inbound row for that key exists raise no hints. The check happens when the event fires.
* Final value comparison. After the apply, a value that still differs from the source's is a genuine local change and is hinted outward. A value that matches is not.

Every origin sends to every peer directly. No server forwards a hint it received. The pool is a full mesh and each server is configured with every other.

## Tasks and workers

Two scheduled tasks replace the ten that exist today.

* **Sync Content** scans and downloads files.
* **Sync Information** scans and applies metadata, people, users, and history, in that order, under one progress bar.

Both tasks run the full comparison on a schedule and catch anything the hints missed, including events raised while the plugin was down. Per module run buttons on the dashboard call plugin endpoints rather than scheduled tasks.

Two background workers run continuously and are not scheduled tasks. The outbound worker delivers hints and retries. The inbound worker applies hints and retries. A slow download never delays a hint, and a hint never waits for the next scan.

## Files

Distribute cannot push media into a server, there is no endpoint and the design does not want one. A content hint tells the receiver a path appeared or changed, and the receiver's existing download logic fetches it. This is the one kind where the inbound apply can take a long time, which is why 200 means queued rather than applied.

## Initial alignment for a pool

Before switching hints on between two servers in Sync mode, each runs one full scan of the other. History uses the negotiated merge. Everything else resolves on recorded versions, and an object no one has edited since the plugin started recording follows the scan rule, so the source wins the first pass. After that pass, hints carry the pool forward. The earlier idea of treating Jellyfin's saved date as a local edit was tried and dropped: a library scan moves that date, so a copy that had just been downloaded outranked the source's real edit.

## Configuration changes

* The single source becomes a list of servers, each with a name, URL, key, mode, library mappings, and user mappings. The existing source migrates into the first entry in Pull mode on upgrade.
* The settings page gains a server list with a mode selector per server and one shared sync section instead of a section per module.
* The History tab and the other tables show the version an object carries and the queue state when one exists.
* The dashboard gains a queue view: outbound rows per peer with state and last error, inbound rows with age and last error, and a paused peer warning.

## Phases

**Phase one. Queues and history.** Done 2026-10-03.
Versions, outbound, and inbound tables with the schema migration (v25). The endpoints above. Both workers as hosted services, plus the change observer with its five second debounce. The history handler builds the same row the scan builds, merges, settles with the origin through the negotiation endpoint, and writes under the apply guard. The two guards. Sync Information as the first consolidated task carrying history only, so the full scan safety net exists from day one. `GET /ServerSync/Hints` shows both queues and every peer's delivery state to the operator, `POST /ServerSync/Hints/Run` forces a pass, and the two `DELETE` routes under it discard a row. Tests: version decisions, queue state transitions, lost work recovery, mapping, the migration, and the Docker scenario "history hints travel across three servers without loops", which configures a full mesh of three servers in Sync mode and proves delivery without a scan, drained queues, a quiet pool, and a newer unmark reversing an older play everywhere.

**Phase two. Metadata, images, and people.** Done 2026-10-03.
Item events raise metadata and people hints. Only a metadata edit raises one: provider downloads and image refreshes are left to the scan, because a server that fetches its own metadata after receiving a file would otherwise push it over the other server's curated values the moment it arrived. Images still travel, as part of the item's metadata row. The refresh and apply task bases gained `RefreshOneAsync` and `ApplyRowAsync`, so a hint builds and writes exactly the row the full scan would, under the module mutex, and the apply tasks register their writes with the guard and record the version read from the peer. Conflicts resolve on origin versions in the hint handler and, through the Versions endpoint, in the full scan against any peer that carries versions: a newer local edit is kept with the reason on the row and the peer is told to pull it. Sync Information covers metadata, people, and history. The Docker scenario "metadata and people hints, newest edit wins, scan keeps newer local" proves each of these across the three servers.

**Phase three. Users and content.** Done 2026-10-03.
User hints come from Jellyfin's user updated event plus a thirty second check of mapped users against a snapshot, because policy and configuration saves raise no event. Content hints come from the library's item added event, and the receiver downloads through the content task's single row entry, which keeps the disk check and the approval mode, then asks Jellyfin for its queued library scan rather than a blocking full validation. Content rows run in their own inbound lane so a download never delays an information hint. Sync Content and Sync Information are the two scheduled tasks. The per module tasks are hidden from the task list, their persisted schedules are cleared on startup, and they stay registered for the dashboard's buttons and as the steps the two tasks run. The module switches gate receiving, not sending: a server raises a hint for anything a peer it sends to has mapped, and the peer decides. The Docker scenario "content and user hints across the pool" proves a file added on a source downloaded by local on a hint alone, picked up by the queued scan, and a policy change travelling both ways with the later edit winning.

**Phase four. Many servers.** Mostly done ahead of order: the server list, the migration from the single source, and the settings page shipped with the multi source scan. What remains is the queue view on the dashboard and showing versions on the tables.

**Phase four, remaining.** Done 2026-10-03: the Queue view on the Sync page, reading `/ServerSync/Hints` and refreshing itself every eight seconds while it shows, with Run Now and per row discard. Each concluded hint also writes to Jellyfin's activity log through `IActivityManager`. Each module's detail dialog shows the version the object carries, which server last edited it and when, through `GET /ServerSync/Hints/Version`.

**Phase four, original note.**
The server list in configuration with the migration from the single source. The settings page rework. The queue view on the dashboard.

## Risks and open items

* Clock skew between servers decides conflicts when two humans edit the same object within the skew window. Servers on NTP keep this to under a second. Done: the capabilities answer carries the peer's clock and Check Link warns when the two differ by more than thirty seconds.
* The inbound membership guard assumes the event fires on the same server and thread that is applying. It does for user data and library items today. Verified for user policy and configuration updates in phase three: the Docker scenario proves the pool is quiet after a user change travels.
* A paused peer accumulates outbound rows without bound. Done: pending rows are capped at ten thousand per peer, the oldest beyond the cap are dropped with a log line, and the Queue view shows the count. The full scan recovers anything dropped.
* Rows that fail forever on the receiver, such as a path that never maps, need an operator action. The DELETE route and a clear button on the queue view cover it.
* Removing a server entry leaves rows carrying its key in every table with nothing to pull from. Done: the Servers tab asks `DELETE /ServerSync/Servers/{key}/Rows` to remove them with the entry. Files already downloaded stay.
* Writing an item's cast updates its Person items as a side effect. While a metadata apply runs, every people hint is held back, so a human edit to a person in that short window is caught by the scan rather than a hint.
* A people hint is accepted only for a person who already exists on the receiver. Looking a person up must never use Jellyfin's `GetPerson`, which creates one when missing.

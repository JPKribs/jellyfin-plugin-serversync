# Linked Content (.strm)

Plan written 2026-11-08. This describes an optional storage mode for content sync where a mapped library gets a small `.strm` link for each source file instead of a full copy. Playback streams from the source through Server Sync on both servers, the source decides per user whether the stream is allowed, and playback is reported back to the source as a real session.

Jellyfin behavior referenced here was checked against the v12.2 tag, the version this plugin targets.

## Goals

* A linked library appears at once and uses almost no disk.
* Server Sync runs on both servers. A source without the plugin, or with linking switched off, never serves a link.
* No source credential is ever written to disk in a `.strm` file or handed to a client.
* The source decides per user whether a stream is allowed and enforces its own limits.
* Playback on a link shows on the source as a live session and updates played state there.
* History, metadata, collections, playlists, and hints keep working when one server holds `Movie.mkv` and the other holds `Movie.strm`.
* With linking switched off, every existing behavior is unchanged. See Non-regression.

## Terms

| Term | Meaning |
| --- | --- |
| Source | The server that holds the real file. Called A below. |
| Receiver | The server that holds the `.strm` link. Called B below. |
| Link | A `.strm` file Server Sync wrote, tracked in the content sync table with `StorageMode = Link`. A `.strm` the user made is never a link. |
| Grant | A short lived record on B that ties a link to the local user about to play it. |

## Configuration

**Library mapping, on B.** A new **Storage** setting with **Copy** (default, today's behavior) and **Link**. Link is only offered when the source advertises `Link` in its capabilities. Settings that mean nothing for links are greyed out on a Link mapping: Temporary Folder, Detect Updated Files, Minimum Free Space, Download Speed, Scheduled Speed, Downloads At Once, Download Retries.

**Server entry, on A.** A new **Allow linking** switch, off by default, on the entry for B. Linking also needs B to be listed in a mode that already allows it to call A, and A's user mappings for B decide which source users B may stream as. Optional limits on the same entry:

* Maximum concurrent link streams for this server.
* Maximum concurrent link streams for each user, optionally counting that user's own sessions on A.
* Honor each user's `RemoteClientBitrateLimit` by refusing files above it.
* Fallback source user for local users with no mapping, empty by default, which refuses them.

**Capabilities.** `Link` is added to a new `PeerCapabilities.Features` list only when at least one server entry has Allow linking on. `ProtocolVersion` and `MinProtocolVersion` do not change.

## File layout

A link is written exactly where the copied file would have gone, under the same name, with only the extension changed: `Movie (2020) - 4K.mkv` on A becomes `Movie (2020) - 4K.strm` on B. Jellyfin pairs sidecars by name without extension, so NFOs, external subtitles, local artwork, and version suffixes (`- 4K`, `- 1080p`) keep working next to the link. External subtitles are still downloaded beside the link when Include Companion Files is on.

The file holds one line, a loopback URL to B's own proxy:

```
http://127.0.0.1:{port}/Sync/Link/{linkId}
```

`linkId` is the content sync row's stable key, so a rename on the source only updates the row and the `.strm` never changes. Jellyfin accepts http, https, rtsp, and rtp in a `.strm` (`ProbeProvider.FetchShortcutInfo`), so a loopback http URL is valid.

**Name collisions.** When two source files differ only by extension (`Movie.mkv` and `Movie.mp4` side by side) they would both become `Movie.strm`. The second row is marked as a conflict on the Sync tab and not written.

## Data model

Schema 31 to 32 adds one column to the content sync table:

* `StorageMode INTEGER NOT NULL DEFAULT 0`, where 0 is Copy and 1 is Link.

For a link row, `LocalPath` holds the `.strm` path and `SourcePath` keeps the source file's real path and extension. The migration only adds a defaulted column, so `MinReaderVersion` stays at 28.

New table on B, in memory only:

* **Grants.** Link id, local user id, play session id, created at. Expire after two minutes or on first use by the proxy, whichever comes first.

New table on A, in memory only:

* **Link sessions.** Peer, source user, source item, Jellyfin session id, last check in. Used for concurrency limits and to tie playback reports to a session.

## Matching across servers

Every cross server match today translates a source path to a local path and then calls `FindByPath`. That misses a link because the extension differs.

**One local lookup.** `LocalItems.FindByPath` becomes the single way to find a local item by path:

1. Exact path. If it matches, return it. Every match that works today still returns the same item.
2. Otherwise, if the path is a file and a link row exists whose `LocalPath` is the same path with `.strm`, return that item.

The roughly twelve direct `_libraryManager.FindByPath` calls move to it: `HistorySyncTableService`, `MetadataSyncTableService`, `SyncCollectionsTask`, `PlaylistMirror`, `HintTriage`, `ContentHintHandler`, `LocalServerClient`, `PeerItemLookup`, and the content sync callers.

**Path indexes.** `MetadataSyncTableService.GetLocalItemsByPath` and the `localPaths` map in `RefreshHistorySyncTableTask` get the same two step rule, for files only. Folders always match as they do today.

**Local to source.** `HistoryAgreementRecorder` turns a local path back into a source path. For a link it reads `SourcePath` from the link row instead of translating, so the real extension is kept.

**Links are not content.** A link is never announced as new content to peers (`LocalHintPublisher`), and content discovery skips a source item that the source reports as one of its own links. History and metadata for that item still sync. A `.strm` the user made on the source is not a link and syncs exactly as it does today.

`PeerItemLookup` already falls back to provider ids, so peer lookups mostly worked before this change. The new lookup makes the path match succeed first.

**A peer's links.** Everything above covers this server's own links. The other direction needs more: when a peer holds the link and this server reads that peer's items, the scheduled refreshes and every hint handler fetch them through Jellyfin's own API (`GetItemsByIdsAsync`, library paging). The path then arrives ending in `.strm` and nothing marks it as a link. This server cannot tell a peer's link from a `.strm` the user made there, so it must not guess from the name. Instead, a peer that advertises `Link` answers `GET /Sync/Peer/Links` with its link paths and the copied path for each. This server translates the copied path instead of the `.strm`. A peer without `Link` is never asked, and its paths are used as they arrive, exactly as today.

## Playback flow

1. A client on B asks for `PlaybackInfo` on a linked item.
2. B's global action filter sees the request before the action runs, finds the item is a link, and records a grant for the calling user. It asks A whether that user may stream the item, and refuses with a clear error if not.
3. Jellyfin probes the `.strm` during that same request (`MediaSourceManager.GetPlaybackMediaSources` refreshes a `.strm` with `EnableRemoteContentProbe` on every play). The probe hits B's proxy, which uses the grant.
4. B's result filter rewrites the linked media source in the response so the client never sees the loopback URL. See Direct play.
5. The client plays through B's own `/Videos/{id}/stream?static=true`. Jellyfin proxies an http source there with Range forwarding (`FileStreamResponseHelpers.GetStaticRemoteStreamResult`). That route allows anonymous callers, so it carries a user only when the client sends its token. The filter refreshes the grant when it does, but `PlaybackInfo` is the only point that always has the user.
6. B's proxy at `/Sync/Link/{linkId}` uses the grant, resolves the current source item and server from the link row, and calls A's stream route with B's peer credentials and the mapped source user.
7. A checks the request and streams the file bytes with Range support.

**Direct play.** For a `.strm`, Jellyfin sets the media source to `IsRemote = true`, `Protocol = Http`, and `Path` to the raw `.strm` URL (`BaseItem.GetVersionInfo`), and returns it to clients. A client that direct plays would open the loopback URL on its own device and fail. Universal audio goes further and redirects the client to that URL (`UniversalAudioController`, `enableRedirection`). For linked items only:

* The `PlaybackInfo` result filter clears `IsRemote` and the raw path so clients build B's static stream URL.
* The universal audio filter forces redirection off.

**Media info before first play.** Jellyfin never probes a `.strm` during a library scan (`FFProbeVideoInfo` only probes a shortcut with `EnableRemoteContentProbe`), so a link has no runtime or codecs until it is played. After writing a link, B runs its own refresh with `EnableRemoteContentProbe` under a grant the plugin owns, so the library shows media info right away.

## Endpoints

On A, beside the existing peer routes, behind `[PeerCallerGuard]` and `[RequireCompatibleProtocol]`:

**GET /Sync/Peer/Stream/{sourceItemId}.** Streams the file with Range support. The request names the source user. A refuses unless all of these hold:

* The caller's server entry has Allow linking on.
* The named user is one A maps for that server, or is the fallback user.
* The item is visible to that user (library access and parental rating).
* The user has `EnableMediaPlayback`.
* The server and user concurrency limits are not reached.
* The file's bitrate is under the user's `RemoteClientBitrateLimit`, when that option is on.

A plugin grant for a probe is allowed without a user, capped to the first few megabytes of the file.

**POST /Sync/Peer/Stream/Check.** Same checks without streaming. Used by B's filter at step 2 so a refusal shows at `PlaybackInfo` instead of failing partway through ffmpeg.

**POST /Sync/Peer/Playback.** Receives start, progress, and stop for a link session. See Playback reporting.

On B:

**GET /Sync/Link/{linkId}.** Loopback only. Refuses any caller that is not this server. Needs a live grant. Jellyfin's ffmpeg and its static stream proxy call it without a Jellyfin token, so it cannot carry `[Authorize]`. `AuthorizationTests` fails any controller without it, which the spike confirmed. The real route takes a per-link secret in the `.strm` URL, checked along with the loopback address, and gets a single, documented exemption in those tests.

## Playback reporting

B already sees `PlaybackStart`, `PlaybackProgress`, and `PlaybackStopped` from `ISessionManager`. For linked items it forwards them to A's playback route with the mapped source user, the source item, position, pause state, and play method. Progress is forwarded at most every 30 seconds.

On A, the first report creates a session with `ISessionManager.LogSessionActivity`, app name `Server Sync`, device name `{B's name} (Server Sync)`, and the source user. Later reports call `OnPlaybackStart`, `OnPlaybackProgress`, and `OnPlaybackStopped` on it. That gives A:

* A live entry on the dashboard.
* Activity log and Playback Reporting plugin entries through Jellyfin's own events.
* Played state, resume position, and play count updated by Jellyfin's normal stop handling.

Jellyfin stops any playing session that has not checked in for more than five minutes (`SessionManager.CheckForIdlePlayback`), so a 30 second cadence keeps it alive and a crashed B cleans up on its own. `LogSessionActivity` does not check `MaxActiveSessions`, which is only enforced at sign in, so A's own link limits are the ones that apply.

**History dedupe.** A linked item whose playback was reported to A has already updated A. History sync records that in `HistoryAgreementRecorder` and skips the duplicate, so play counts are not doubled. Items that are not links are unaffected.

**Later.** An `ISessionController` on A's link session so pressing Stop on A's dashboard reaches B. Until then A can cut the proxy stream.

## Promotion

A link can be turned into a full copy from the Sync tab, or by approval when the mapping's New files approval is set to approve. The real file is downloaded through the normal pipeline, then the `.strm` is removed. Jellyfin sees one item removed and one added, so the local item id changes. Watched state usually survives because Jellyfin keys it on provider ids. Rows in the plugin's tables that hold the old `LocalItemId` are re-resolved, and history sync fixes anything left.

## Non-regression

With linking switched off, every existing behavior must be unchanged.

1. **Off by default at three levels.** Storage defaults to Copy, Allow linking defaults to off, and `Link` only appears in capabilities once enabled. Existing configurations load unchanged.
2. **Only plugin links are special.** Every new rule applies only to rows with `StorageMode = Link`. A user's own `.strm` is never matched by the lookup fallback, never skipped as content, and never touched by the filters.
3. **Exact path first.** The lookup fallback only runs after an exact miss.
4. **Filters do nothing for other items.** Each filter returns at once when the item is not a link. A failure in the link lookup is logged and the request goes through unchanged, so a link bug cannot break normal playback.
5. **Additive migration.** One defaulted column, `MinReaderVersion` unchanged, so a downgrade still opens the database.
6. **Older peers.** No protocol bump. A peer without `Link` in its capabilities never receives a link call, and hints, history, and metadata with it are unchanged.
7. **History dedupe only for reported links.**
8. **Copy path untouched.** Download, validation, disk space, recycling bin, and Ignore are not edited. Link mode branches off early into its own services instead of adding conditions throughout.

## Testing

**Baseline.** Before any change: 922 passed, 0 failed, 16 skipped. The suite stays green throughout, and no existing test is edited to make it pass. Any test that has to change is called out in review.

**Unit tests.**

* An exact path wins over the `.strm` fallback.
* A user's own `.strm` is never returned by the fallback.
* Folders never use the fallback.
* `HistoryAgreementRecorder` keeps the real source extension for a link.
* Each filter leaves a non-link request and response untouched, and passes the request through when the link lookup throws.
* Migration 31 to 32 keeps existing rows and defaults them to Copy.
* A peer without `Link` gets no link or playback calls.
* History dedupe does not apply to items that are not links.
* A's stream checks: unmapped user, hidden item, playback disabled, limits reached, bitrate over cap.
* Name collisions are flagged and not written.

**Integration (`integration/run.py`, Jellyfin 12.2 pool).**

* All current scenarios run unchanged with linking off. This is the main regression check.
* A Link mapping: links appear with media info, play through B, show on A's dashboard, and update played state on A.
* History and metadata sync across a link in both directions.
* A refused user gets an error at `PlaybackInfo`.
* A mixed version run with one server on the current release.

## Phases

1. **Filter spike.** A global `MvcOptions` filter registered from `PluginServiceRegistrator`, run against the 12.2 image. Prove it runs on Jellyfin's own controllers, that the `PlaybackInfo` rewrite makes clients play through B, and that it has no effect on normal items. Plugin registrators add to the same service collection `AddJellyfinApi` later uses and nothing clears `MvcOptions.Filters`, so this is expected to work, but nothing below starts until it is shown.
2. **Matching.** The single lookup, the path indexes, and the local to source change, with their tests. Useful on its own and safe with no links present. This server's links come from `ILinkRegistry`, which answers "no link" for every path until phase 3 replaces it.
3. **Links without users.** Schema 32, the Storage setting, the `.strm` writer, a table backed `ILinkRegistry`, B's proxy, A's stream route limited to server level checks, capabilities, the first play probe, and `GET /Sync/Peer/Links` so a peer's links match here.
4. **Per user.** Grants, the check route, A's per user checks and limits, refusal at `PlaybackInfo`.
5. **Playback reporting.** Forwarding, link sessions on A, history dedupe.
6. **Promotion.** Link to copy from the Sync tab and by approval.
7. **Later.** Stop from A's dashboard through `ISessionController`. Transcoding on A for users over their bitrate cap.

## Spike results

Run 2026-11-08 on the `spike/strm-filter` branch, against the integration pool on `jellyfin/jellyfin:12.2.20261005-225228`. The spike registered one global filter and a stand-in `/Sync/Link/{linkId}` route that served a local file, both inert without a marker file in the data folder.

* **The global filter runs on Jellyfin's own controllers.** `Configure<MvcOptions>` from `PluginServiceRegistrator` reached `MediaInfo.GetPostedPlaybackInfo` and `Videos.GetVideoStreamByContainer`.
* **The grant is in place before the probe.** The filter recorded the user at `PlaybackInfo`, and Jellyfin's probe (`Lavf`) reached the link route about 25 ms later, inside the same request, and found that grant.
* **The direct play problem is real.** Before the rewrite, the link's media source was `IsRemote: true`, `Protocol: Http`, `SupportsDirectPlay: true`, with the loopback URL as its path.
* **The rewrite works with the web client.** After the filter set `IsRemote: false`, `Protocol: File`, and cleared the path, Jellyfin Web 12.2.0 played the link from B's `/Videos/{id}/stream.mp4?Static=true`. Jellyfin proxied that to the link route with Range support (206, correct `Content-Range`). `PlaybackStart` fired on B as for any item.
* **No media info until first play, as expected.** The link had no runtime after the scan. It had runtime and streams after the first `PlaybackInfo`.
* **The static stream route does not always carry the user.** It allows anonymous callers. The web client sends its token so the user was known. A request without the header had none. The grant has to come from `PlaybackInfo`.
* **Other items are untouched.** A normal `.mp4` and a user's own `.strm` pointing elsewhere returned exactly what Jellyfin returns without the plugin, and the filter logged nothing for either.
* **The authorization tests catch an anonymous route.** `AuthorizationTests` failed two tests for the stand-in route. All 920 other tests passed.

Not covered: Swiftfin and other clients, universal audio, and timings on a real network.

## Phase 3 results

Built 2026-11-09 on `feature/strm-links-matching`. Unit tests: 961 passed, 0 failed. The new `links` integration scenario passes 20 of 20 checks on the 12.2 pool: a Link mapping writes a `.strm` with a loopback URL and nothing is downloaded, the link gets media info without being played, `PlaybackInfo` hides the loopback URL, the static stream through B is the source file byte for byte, the link URL refuses a caller that is not B, history matches the link in both directions, and turning linking off on A stops the stream.

Where it differs from the plan above:

* **`Features`, not `Accepts`.** `Link` is announced in a new `Features` list on the capabilities. Older peers read `Accepts` as hint kinds, so a new name there was a risk, and they ignore an unknown property.
* **The capability is server wide.** A advertises `Link` when any entry allows linking. A source that advertises it but does not allow this server refuses at the stream route.
* **A file skipped because the source offers no links is listed again on the next scan.** Its server's incremental watermark is not moved forward, so the file is linked as soon as the source allows it rather than at the next full scan.
* **Only new files follow the mapping.** A row keeps the storage it was made with, so switching a mapping to Link never removes a copy, and switching back never replaces a link. Turning links into copies is promotion, phase 6.
* **A real copy wins.** A file new to a Link mapping that already has a full copy at its name is tracked as a copy.
* **A peer's links.** The peer answers `GET /Sync/Peer/Links` whatever it advertises, and an older peer answers 404. This server only asks once an item read from that peer has a `.strm` path, so peers without links cost nothing. Items it lists take the copy's path as they are read, and content discovery skips them. Hints for a link carry the copy's path from the sender, so receivers of any version match them.
* **The link secret.** The URL carries an HMAC of the link id under a key kept in `serversync-link.key` in Jellyfin's data folder, checked together with the loopback address.

Left for later phases or not yet done:

* **Companion files.** Subtitles and other companions are not downloaded beside a link yet.
* **Run preflight.** The Sync run's disk space check and a server whose key may not download still stop or skip a run, even when every queued row is a link.
* **Clients.** Only the web client's path (PlaybackInfo, then the static stream) is covered by the scenario. Swiftfin and universal audio are not.

## Phase 4 results

Built 2026-11-09 on the same branch. Unit tests: 968 passed, 0 failed. The `links` scenario grew to 26 checks and passes all of them.

* **Grants on B.** The filter records the user at `PlaybackInfo` and universal audio, after asking A, and again on Jellyfin's stream and HLS routes when the request carries a user. The link route refuses a read with no live grant. A grant nobody uses expires after two minutes. Once used it lasts an hour after its last use, so a long pause does not close it. When two users play one link at once, the latest is named to A.
* **The probe.** The media probe for a new link reads under its own grant with no user, which lasts ten minutes from when it was made and is never extended. A serves it with only the server level checks and does not count it.
* **A decides.** `POST /Sync/Peer/Stream/Check` and the stream route run the same checks: linking allowed, the user A streams as exists, is enabled, may play media, and can see the item, the bitrate limit when that option is on, and the limits. A refusal at `PlaybackInfo` is a 403 whose text carries A's reason.
* **Who A streams as.** A's own user mapping in its entry for B decides first. When that entry maps no users at all, A takes the user B maps, since B's key already holds access to A. Otherwise the fallback user streams, or no one does. A standard user's key on A is held to that user.
* **Limits.** A counts a stream while a request for it is open and for 90 seconds after the last one ends, once per user and item, so seeking never counts twice. A probe at `PlaybackInfo` counts as the user's stream for that window.
* **Settings on A's entry for B.** Link Streams At Once, Link Streams Per User, Count Their Own Playback, Honor Remote Bitrate Limits, and Stream Unmapped Users As, shown under Allow Linking.
* **The URL never leaves B.** The result filter clears a link's URL from every media source in `PlaybackInfo`, item, and item list responses, matched by the URL itself, so no route shows a user the secret.

Still open after phase 4:

* **Two users on one link at once** are both allowed when both pass the checks, but A names only the latest for the stream that follows.
* **Bitrate** is checked against the item's total bitrate, which Jellyfin only knows once the link has been probed.

## Phase 5 results

Built 2026-11-09 on the same branch. Unit tests: 970 passed, 0 failed. The `links` scenario grew to 32 checks and passes all of them.

* **B forwards.** A background service on B listens to Jellyfin's playback start, progress, and stop for its links and sends them to A's `POST /Sync/Peer/Playback` in order, off Jellyfin's event thread. Progress goes at most every 30 seconds, and at once on a pause or resume. A lost report is made good by the next one.
* **A shows a session.** Each viewer on B is a device on A under the client name Server Sync, named after B and the viewer's client. A runs Jellyfin's own start, progress, and stop on it for the user it streams as, so the dashboard, the activity log, and playback plugins see it, and stopping updates played state, play count, and resume position. The session ends when playback stops, and Jellyfin's idle check ends it if B goes away. A progress report whose start was lost begins the playback.
* **No dedupe needed.** History sync takes the larger play count when both sides moved, so a play counted on A by the report and on B by Jellyfin stays one play after history sync. The scenario checks this, so the dedupe step planned for `HistoryAgreementRecorder` was not built.
* **Limits ignore these sessions.** Count Their Own Playback skips sessions under the client name Server Sync, so a link stream is never counted twice.

## Phase 6 results

Built 2026-11-09 on the same branch. Unit tests: 975 passed, 0 failed. The `links` scenario grew to 37 checks and passes all of them.

* **A third storage state.** `Promoting` (2) marks a link on its way to a copy. It is still a link to the registry, the proxy, and matching, so it keeps playing until the copy lands. Approving or queueing a row clears its pending type, so the pending type could not carry this. No schema change: the column already holds an integer.
* **By hand.** The Sync tab's new Turn links into full copies action (`POST /Sync/PromoteItems`) marks the chosen links. Anything that is not a link is skipped.
* **By the mapping.** A link under a mapping now set to Copy is marked by the next refresh, queued when New files is on, waiting for approval as a Pending Replacement when New files needs approval, and left alone when New files is off. Switching back to Link before approval keeps it a link. This replaces the phase 3 rule that switching a mapping never changes a file already there, for links.
* **The swap.** The Sync run downloads the copy through the normal copy path, with its disk space, validation, and temporary folder steps, to the path the copy belongs at. Only once the copy is there does it remove the `.strm`, and only a `.strm` this plugin wrote. A failed download leaves the link in place and the row errored for a retry.
* **Item ids.** Jellyfin sees the link removed and the copy added, so the local item id changes. The content row's id is cleared and resolved again, and the history and metadata refreshes match by path, so they find the copy on their next run.

## Phase 7 results and remaining gaps

Built 2026-11-09 on the same branch. Unit tests: 976 passed, 0 failed. The `links` scenario grew to 40 checks and passes all of them.

* **Stop from A's dashboard.** A attaches an `ISessionController` to each link session and reports it as controllable. Stop, pause, resume, seek, and a dashboard message go to B's new `POST /Sync/Peer/Control`, which passes them to the viewer's session there. B only accepts a command for a session that is playing one of its links to that server. A needs a configured entry for B, with an address and key, to send them.
* **Only video.** See Edge case testing: audio and other non-video files are copied even in a Link mapping.
* **Companions beside links.** With Include Companion Files on, subtitles and other companions are downloaded beside a new link, by the source file's name, the same way a copy gets them. A link is still written when they cannot be fetched.
* **Link only runs.** A Sync run whose queue holds only links is no longer stopped by the disk space check, by no server connecting, or by every server being left out. Link rows are written even when their server did not connect, since writing one reads nothing from it. Any copy in the queue keeps every check in force.

Not built, on purpose:

* **Transcoding on A for users over their bitrate cap.** B's Jellyfin probes and seeks a link as a static file over HTTP ranges. A transcode on A is a live stream with no ranges, so B could neither probe nor seek it, and Jellyfin would have to transcode a second time on B for most clients. The bitrate option refuses such files instead. Doing this properly means B playing A's HLS directly, which is a different design from links.
* **Two users on one link at once.** Jellyfin's ffmpeg and stream proxy call a link without saying who for, and the `.strm` holds one URL, so B cannot tell the two streams apart. Both users are checked by A at `PlaybackInfo`, and A names the latest for the stream.

Not covered by the integration scenario: Swiftfin and other clients, universal audio, and a link only run with a full disk.

## Audit, 2026-11-09

Three independent reviews of the whole change, for security, correctness against existing behaviour, and concurrency with Jellyfin integration, checked against Jellyfin 12.2. Fixed:

* **The secret through Jellyfin's file routes.** `GET /Items/{id}/File` and `/Download` served a link's `.strm`, which is its URL and secret, to any user who could see the item. The filter now refuses both for links.
* **A reverse proxy on the same host.** Jellyfin turns forwarded headers off when no proxies are known, so a proxy on the same machine made every outside request look local. The link route now also needs a loopback local address and refuses any request with a forwarding header (`X-Forwarded-*`, `X-Real-IP`, `Forwarded`, `Via`). IPv4 mapped addresses are normalised.
* **Grants could be borrowed.** A grant was one user per link, and stream requests re-granted without asking A. Grants are now kept per user and link. Only A's approval at `PlaybackInfo` or universal audio makes one. A stream request (Videos, Audio, DynamicHls) on a link must carry a user who already holds one, or it is refused before Jellyfin opens the link. A client that streams a link without first calling `PlaybackInfo`, or without a token, is refused.
* **Universal audio redirection.** An argument the client leaves at its default is not in the action's arguments, so redirection stayed on for most clients and sent them the link's URL. It is now set off every time.
* **Who A streams as.** A's fallback user now wins over the user B names, so a fallback set as a restriction cannot be bypassed.
* **Playback reports** now need the user to be able to see the item.
* **Sessions that never ended.** A link session now stops counting as active ten minutes after its last report, so Jellyfin closes it when B goes away.
* **A's own idle stop.** Jellyfin stops paused sessions on its own, naming no user. That is no longer passed to B, so A's housekeeping cannot stop someone watching there. A person's Stop still is.
* **The key file** is written whole through a temporary file, readable by the server's account only.
* **Links are held to the library boundary** a copy is held to.
* **Logged and shown text.** Reasons from a peer are sanitised, a refusal body is read to 2 KB at most, and a message from A always names A.
* **Endless full scans.** The phase 3 rule that held a server's watermark when it offered no links could keep a server on full scans for good. Instead, a server with a Link mapping carries whether it offered links in its scan scope, so the run after it starts offering them is a full scan.
* **An upgrade made every scan full once.** `StorageMode` is left out of a mapping's JSON while it is Copy, so the scope hash of every existing mapping is unchanged.
* **The dashboard showed the last server's fallback user** on a server with none, and saving stored it. Fixed.
* **A watched by all link** kept its `.strm` path instead of being pointed at a copy that is not there.
* **Link rows of a server no longer set up** are no longer written. Only an enabled server's links apply without a connection.
* **Marking a waiting promotion synced** keeps it a link, and a Copy mapping asks for approval again rather than copying it unasked.
* **Promotion** skips rows on their way to deletion.
* **A failed read of a peer's links** is no longer cached as "no links". A server error is asked again a minute later, and until then that peer's `.strm` files are neither matched nor copied. A peer's link that arrives by hint is never copied.
* **One slow source** held up playback reports for every other. Each report now waits ten seconds at most.
* **Probe grants** last an hour, so a probe queued behind a large sync still finds one.
* **Stream limits** are checked and the stream opened under one lock, so requests at the same moment cannot pass a limit together.
* **The registry** loads outside its lock and swaps the snapshot in, so lookups never wait on the database.
* **A source that drops a stream partway** ends the response quietly instead of logging an unhandled error.

Reviewed and left as they are:

* **A probe without a user** gets only the server level checks. B's key on A already lets it download any item that key can see, and a standard user's key is held to its user, so this grants nothing new.
* **`GET /Sync/Peer/Links` lists every link**, including links to other servers. A peer must know them all to match a link to a third server, and its key already reads every item path here.
* **Paths match without regard to case**, as everywhere else in this plugin.
* **Activity entries.** Ending A's session on every stop adds a session ended and started entry between episodes.
* **A source that stops sending mid-stream** without closing the connection holds the read until the player gives up, since Jellyfin's proxy passes no cancellation.
* **Clients that read `MediaSource.Path` directly**, such as some Kodi setups, cannot play a link, since the path is cleared.

## Edge case testing, 2026-11-09

Twelve new scenarios on the Docker pool, beyond the main `links` scenario: byte ranges and seeking, HLS transcoding, hostile requests, a source that is down, restarts of both servers, a rename and a removal on the source, two files differing only by extension, a user's own `.strm`, a full disk, two users starting at once against a limit of one, and universal audio.

* **Found: audio links leaked their secret.** Jellyfin 12.2 only follows a `.strm` for video (`BaseItem.GetVersionInfo` checks `Video.IsShortcut`). An audio `.strm` is served as a plain file, so universal audio and the audio stream route handed the player the link's URL and secret. Only video (movies, episodes, videos, music videos, trailers) is now linked. Audio and anything else in a Link mapping is copied, and the filter refuses the audio routes for any audio link that exists anyway.
* **Found: the full disk check could not be tested on `/media`.** .NET in the container reads the Docker Desktop volume as about 19.7 TB free, so the free space floor never tripped and the first version of the scenario passed without testing anything. Local now has a 64 MB `tmpfs` at `/media-tiny`, and the scenario maps a library there.
* **A rename on the source is a removal and an addition.** Jellyfin derives item ids from paths, so a renamed file gets a new id. The new name is linked as a new file, and the old link follows the missing files setting, as a copy would.

## Upgrade, downgrade, and a real client, 2026-11-09

* **Upgrade from `master`.** A pool built from `master` (schema 31) ran its own content, history, and metadata scenarios, then this branch's plugin replaced it. All three servers migrated from 31 to 32 with no errors. Every table kept its row count, local's content rows were unchanged, and all read as Copy. `master`'s whole scenario suite then ran against the upgraded servers with this branch's plugin: 206 of 209. Two were the stale version test that `master` also fails. The third, a playlist deletion that must arrive within 60 seconds, passed three reruns in a row and logged no plugin error, so it is timing under load.
* **Found: a downgrade could overwrite links.** `master` does not know links. It would take a `.strm` for a copy whose size is wrong, queue it again, and download the media over it. A database that holds a link now names v32 as its oldest reader (`LinkReaderVersion`), raised when the first link is written and kept by later migrations. Tested for real: with a link in place, `master` set the database aside, started fresh, and downloaded a copy beside the link, leaving the `.strm` byte for byte as it was. Plain downgrades without links still open the database as before: `master` used a v32 database with no links as is and passed its scenarios.
* **Jellyfin Web.** A 20 second linked film showed its media info before anyone played it, played in the web client, and seeked to 0:14. Source-a showed it as a Server Sync session named after local and Jellyfin Web, saw the pause, and pressing Stop on source-a closed the player in the browser. Source-a recorded the play once and ended its session.

## Chaos testing and the .nfo, 2026-11-09

* **Randomized and race tests.** About 30,000 random operations on grants, stream counting, and link paths, each checked against a plain model, and 64 threads at once on the limits, the grants, and the registry. Ten bugs were planted in the code one at a time to see whether the tests notice: nine were caught, and the tenth changed nothing. The first race test caught a planted race in only one run of three, so it was strengthened until it caught it in five of five.
* **Chaos on the pool.** The source killed mid-stream, `.strm` files deleted, made stale, filled with garbage, pointed elsewhere, or emptied, the link key deleted, linking turned off mid-stream, 50 PlaybackInfo calls and 200 random ranges at once, 420 malformed requests at every route, and 300 links at once. Deleted and stale links are repaired. A `.strm` that no longer points at this plugin is left alone with a reason. A lost key breaks every link until the next sync writes them again with a new secret.
* **Found: probes lost during a full scan.** With 300 links, about one in ten never got media info. The probe was queued the moment Jellyfin added the item, and Jellyfin's own refresh of the new item during a full scan sometimes saved over it. The probe now waits 20 seconds for the item to settle, checks two minutes later, and tries again up to three times. All 300 now get media info.
* **Found: subtitle and font extraction was not checked.** Jellyfin extracts subtitles and fonts held inside a file by reading the file, through the link for a link, and its subtitle and attachment routes name the item `routeItemId` and `videoId`, so the filter never looked at them. They are now held to the stream's rule: a user the source allowed. Tested with an MKV holding an SRT and an ASS track: both are listed, both extract through the link, a user who never pressed play is refused, and a subtitle burns into an HLS transcode.
* **The .nfo.** Each link gets an `.nfo` beside it, under the same name, from the source's details: title, original title, year, dates, plot, tagline, rating, runtime, genres, studios, tags, and ids, and for an episode its show, season, and number. It carries a marker and is only ever replaced while the marker is there, so deleting the marker line keeps the user's own edits, and an `.nfo` the plugin did not write is never touched. It moves and is removed with its link, as other companions do, and stays beside the copy when a link is promoted. A settled link without one is queued, so links written before this get one on the next sync. A link is still written when the source's details cannot be read.

## Risks and open items

* **The filter approach.** Proven by the spike for the web client. If a later Jellyfin release changes how plugin filters load, user attribution falls back to matching `ISessionManager` sessions, which is ambiguous when two users play the same link at once.
* **Probe on every play.** Each `PlaybackInfo` on a link costs a probe round trip to A before playback starts. Measure the delay and check whether the refresh also reaches remote metadata providers.
* **Client direct play.** The rewrite is checked on the web client and Swiftfin during the spike. Other clients are checked during integration.
* **Bandwidth and availability.** Every play streams from A, and a link cannot play while A is down. Both need saying plainly in the docs for the Storage setting.
* **Trickplay and chapter images.** These read the whole file over the network. The docs recommend turning them off on linked libraries.
* **The .nfo and metadata sync.** Jellyfin reads a local `.nfo` again on a full refresh, so an edit made on this server or by metadata sync can be put back to the source's values by a full refresh while the plugin's `.nfo` is in place. Deleting the marker line, or the `.nfo`, hands the metadata back to Jellyfin.

## Play-time fallback

A link streams from the server that won its file in sync. When that server is down, at its limits, or refuses, the linking server tries the other servers it pulls from, in priority order. A server qualifies when one of its enabled mappings covers the link's folder. It is asked by the file's path there through `Stream/Check`, which now accepts `Path` and `Probe`, and is used only when it allows the user and the file is the same size. This happens at PlaybackInfo, and again in the link route before any byte is sent. The grant keeps the chosen server, so later reads, playback reports, and remote control follow it.

Results:

* Unit tests: 13 new tests in `LinkFallbackTests`. Both planted bugs, a dropped size check and retrying the failed server, were caught.
* Docker: the `links: a second source stands in when the first cannot` scenario covers a stream begun on source-a finishing from source-b byte for byte, PlaybackInfo with source-a down, source-a refusing, the report and control on source-b, the refusal naming the servers tried, and a different-size file. It passed three times in a row, with the "source killed mid-stream" chaos scenario.
* The scenario found a cut-off upstream response escaping the link route as an unhandled error. Every upstream read in the route is now guarded.
* Harness: rows are read a page at a time, since `/Sync/Items` answers at most 200 per request. The parallel limit test waits 120 seconds, past the 90-second linger.
* Found: on a brand-new server, Jellyfin dropped the first folder refresh the plugin asked for, so a new file waited for the next scan. Jellyfin refreshes a reported folder through the nearest folder it has recorded, and it records no item for a library's folder while that folder is empty. This affected copies and links alike. A run now checks that Jellyfin knows a folder at or above each changed folder, and scans the libraries when it does not. A file from a hint queues a scan in that case.

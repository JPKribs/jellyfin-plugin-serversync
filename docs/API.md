# Server Sync API

Server Sync adds a small HTTP API to Jellyfin. Two servers that both run the plugin talk to each other through the **Peer** routes, and the dashboard's Servers tab and Queue view use the **Hints** and **Servers** routes. The dashboard routes sit behind Jellyfin's own `RequiresElevation` policy. The `Peer` routes accept any signed in caller that the receiver's **Whitelist** allows: an administrator from a server on its Servers tab, or a user listed there calling from one of their locations. The plugin adds no login of its own. A standard user's token is refused with `403` on the dashboard routes, and no token gets `401` everywhere. See **[Permissions](Permissions.md)**.

Requests and responses are JSON. Property names are Pascal case, enums travel as their names, and dates are UTC in ISO 8601. The server reads property names without regard to case.

```bash
curl -H 'Authorization: MediaBrowser Token="YOUR_API_KEY"' http://server:8096/Sync/Peer/Capabilities
```

## Peer routes

These are what one Server Sync installation calls on another. You will not normally call them yourself, but they are plain HTTP and can be exercised from the command line.

Servers in a pool must speak overlapping wire protocols, not the same plugin version. Every peer route except `Capabilities` needs the headers `X-ServerSync-Version` (the caller's plugin version), `X-ServerSync-Protocol` (the protocol it speaks) and `X-ServerSync-Min-Protocol` (the oldest protocol it still speaks). A route answers `426` with the reason when neither side's protocol reaches the other's minimum. A caller that sends no protocol header predates protocol versions and is only accepted on exactly the same plugin version. `Capabilities` reports `ProtocolVersion` and `MinProtocolVersion` beside `PluginVersion`. Check Link reports a mismatch as an error.

### GET /Sync/Peer/Capabilities

Which server this is and what it applies. A `404` means the plugin is not installed there, and a `403` with `{ "Code", "Message" }` means the whitelist refuses the caller. It answers whatever version the caller runs, so Check Link can name both versions when they differ.

```json
{
  "ServerId": "2f40d5d86aba4689819184b48738d2ad",
  "PluginVersion": "12.2.1.1",
  "Accepts": ["History", "Metadata", "People", "Content"],
  "Features": ["Link"],
  "ServerTime": "2026-10-03T19:30:47.9590000Z"
}
```

`Accepts` lists the kinds this server applies, which are the modules that are on. A sender announces only those kinds. `Features` lists optional features this server offers: `Link` when it allows linking for at least one server, see [Links](Links.md). Older releases send no `Features`. `ServerTime` is the peer's clock, which Check Link compares against the caller's.

### GET /Sync/Peer/Link?serverId={id}

Whether, and how, this server lists the asking server, and the pairing. When the asking server is listed, enabled, and configured, this server issues it a fresh secret by calling that server's `Pair` route over its own connection to it, and says whether that worked. A server about to Push asks this first, since hints are only accepted from a server listed as Pull or Sync, and every server asks it again on hearing `428`.

```json
{
  "ServerId": "…",
  "ServerName": "local",
  "Listed": true,
  "Mode": "Sync",
  "Enabled": true,
  "PullsFromYou": true,
  "SendsToYou": true,
  "Paired": true,
  "PairingError": null,
  "AccessLevel": "User",
  "AccessUser": "viewer",
  "CanDownload": true
}
```

`AccessLevel`, `AccessUser`, and `CanDownload` describe the key this server holds for the asking server, so the asker knows what it may read here. `PairingError` says why no secret could be issued: the listed server refused this server's key, which happens when the key was revoked or its Server Sync is older than this one, or could not be reached at its URL.

### POST /Sync/Peer/Pair

A peer hands this server the secret it must present on requests that name it as the sender to that peer. Any caller the whitelist allows may call it, so a peer holding a whitelisted standard user's key for this server can still pair. Only a server this one lists can call it usefully. A secret stored by anyone else only makes this server present the wrong one, and the next `428` pairs it again.

```json
{ "ServerId": "…", "Secret": "…" }
```

Answers `{ "Paired": true }`. A secret is only taken while this server is asking the issuing server's `Link` route, so a call at any other time answers `409`, as does an issuing server that is not listed here. `Secret` is 32 to 256 letters, digits, `-` or `_`, and anything else answers `400`.

#### The pairing header

Every request below that carries a `SenderServerId` must also carry the header `X-ServerSync-Pairing` with the secret that the receiver issued to that sender. A request without it, or with another server's secret, answers `428` with the reason, and the sender pairs through `Link` and tries once more.

### POST /Sync/Peer/Queue

Offer a batch of change hints. A hint says what changed and where, never the value. The receiver pulls the live state from the sender afterwards. The sender must be configured on the receiver as a Pull or Sync server and paired, and every hint's `OriginServerId` must equal `SenderServerId`, since hints are never forwarded. At most 500 hints per request. A hint whose `VersionTimestamp` lies more than five minutes ahead of the receiver's clock is declined with the reason, and one less far ahead is read as now. `ItemType` is the item's Jellyfin type, for the queue view's poster shape.

```json
{
  "SenderServerId": "2f40d5…",
  "Items": [
    {
      "HintId": "2f40d5…:42",
      "OriginServerId": "2f40d5…",
      "Kind": "Metadata",
      "Key": "9c1e0f…",
      "ItemPath": "/media/Movies/Only A (2021)/Only A (2021).mp4",
      "ItemId": "9c1e0f…",
      "UserId": null,
      "UserName": null,
      "VersionServerId": "2f40d5…",
      "VersionTimestamp": "2026-10-03T19:30:47Z",
      "Recorded": true
    }
  ]
}
```

`Recorded` says whether the change was made by hand (`true`) or is a provider's work such as a poster a scan fetched (`false`). Provider work is applied only where the receiver has recorded no edit of its own and leaves no version behind. `Kind` is one of `History`, `Metadata`, `People`, `Content`, or `Playlists`. `Users` is declined, since user settings travel on the scheduled task only. A `Playlists` hint is keyed by the id of the playlist it was first made as, carries the owner in `UserId`, and an `ItemType` of `Playlist` or `PlaylistCopy`, or `PlaylistDeleted` or `PlaylistCopyDeleted` when it was deleted. A copy is a playlist Server Sync made from another server's, which matters because Jellyfin gives two playlists with the same name the same id on different servers. `Key` is the sender's own key for the object: the item id for metadata and content, the user id for users, the person's name for people, and `userId|itemId` for history. `ItemPath` and `UserName` are what the receiver maps through its own library and user mappings for the sender.

The answer is one result per hint, in request order, each with a `Code` and the `Reason` when it is not `Queued`. `Queued` is stored and applied in turn. `NoLocalItem` is stored but parked until the local item arrives, and the receiver now holds it, so the sender treats it as done the same as every other code.

```json
{
  "Items": [
    { "HintId": "2f40d5…:42", "Code": "Queued", "Reason": null },
    { "HintId": "2f40d5…:43", "Code": "NotMapped", "Reason": "the path is not in a library mapped on this server" }
  ]
}
```

| Code | Meaning |
| --- | --- |
| `Queued` | Stored, and applied in turn. |
| `NoLocalItem` | Stored but parked: the item is not on the receiver yet. It applies when the item arrives, for up to six hours, then is dropped. A new item brings its metadata with it through the metadata provider. |
| `AlreadyCurrent` | The receiver already holds that version. |
| `Superseded` | The receiver holds a newer edit. |
| `ModuleOff` | That module is off on the receiver. |
| `NotMapped` | The path or user is not mapped on the receiver. |
| `Filtered` | The library mapping's whitelist or blacklist leaves the path out. |
| `Invalid` | The key or origin is malformed. |
| `FutureVersion` | The version is dated more than five minutes ahead of the receiver's clock. |
| `Unsupported` | The receiver does not apply that kind live. |

Completions add the codes an apply can end with: `Applied`, `Unchanged`, `Locked`, `AwaitingApproval`, `Ignored`, `SourceGone`, `Unreadable` when the sender did not say where a file is, `Priority` when another server provides the file, and `GaveUp` after a day of failed attempts. `NotMapped`, `Filtered`, `Ignored`, `ModuleOff`, `Unsupported`, and `Priority` hold that object back from the receiver for a day. `NoLocalItem` holds nothing back.

| Status | Meaning on the sender |
| --- | --- |
| `200` | Queued. The rows are marked sent and wait for completion. |
| `400` | The request itself is malformed, such as a hint with no `HintId`. Permanent for those rows, and written once to the activity log. A problem with one hint is declined for that hint alone in a `200` answer. |
| `401` or `403` | The key is refused, or the sender is not on the receiver's whitelist. The peer is paused for fifteen minutes, set by **First wait** in the **Refused** column of the Retries table under Settings > Processing. |
| `404` | No Server Sync behind that URL. The peer is paused. |
| `409` | The receiver does not list the sender as Pull or Sync, or its entry for the sender is disabled. The peer is paused with the reason. |
| `426` | The two servers' protocols do not overlap. The peer is paused with the reason. |
| `428` | The sender is not paired. The sender pairs through `Link` and sends once more. If that still answers `428`, the peer is paused with the reason. |
| `503` | The receiver already holds ten thousand hints from this sender. Retried with backoff, nothing is lost. |
| anything else | Retried after thirty seconds, doubling up to one hour, forever. The Retries table under Settings > Processing changes these, and a hint out of **Max tries** is marked failed. |

### POST /Sync/Peer/Complete

A receiver reports hints as done. The origin removes the matching outbound rows queued for that receiver, and only when the row still carries the version that was applied, so an edit made after delivery is not lost to a late completion. The receiver must be paired, and a reported version ahead of the origin's clock is read as now. At most 500 completions per request.

```json
{
  "SenderServerId": "…",
  "Items": [ { "HintId": "2f40d5…:42", "VersionTimestamp": "2026-10-03T19:30:47Z", "Code": "Applied", "Reason": null } ]
}
```

Answers `{ "Removed": 1 }`.

### GET /Sync/Peer/Status?serverId={id}

The hints the server named by `serverId` sent this one that are still queued, and those it finished in the last day. The caller must be that server, paired with the `X-ServerSync-Pairing` header, so `serverId` is required. An origin reads it every few minutes for rows that were sent and not completed after ten minutes. A hint still held means the peer is working, a hint in `CompletedHints` with a version at least the row's is done, and a hint that is neither was lost and is sent again.

```json
{
  "ServerId": "…",
  "Inbound": [
    { "Id": 7, "HintId": "…:42", "OriginServerId": "…", "Kind": "Content", "Key": "…", "ReceivedAt": "…", "Attempts": 0, "LastError": null }
  ],
  "CompletedHints": [ { "HintId": "…:41", "VersionTimestamp": "…", "Code": "Applied", "Reason": null } ]
}
```

### GET /Sync/Peer/Interest?serverId={id}

What this server wants from the server named by `serverId`: the kinds whose module is on, the source roots it maps with their whitelists and blacklists, and the source users it maps. The caller must be that server, paired. A sender reads it every minute and queues nothing outside it. `Revision` changes whenever any of it does, and a new revision lets every held back hint for this server go again. A server this one does not pull from is answered with an empty interest.

```json
{
  "Kinds": ["History", "Metadata", "People", "Content"],
  "Libraries": [ { "Root": "/media/Movies", "FilterMode": "Blacklist", "Filtered": ["/media/Movies/Home Videos"] } ],
  "Users": ["5c0a71…"],
  "Revision": "9F2C41B07A1D3E55"
}
```

### POST /Sync/Peer/Lookup

Finds one of this server's items, so the metadata provider on a server that pulls from this one can fill its copy from here. Any whitelisted caller may call it, and it records nothing. A standard user only finds items they can see. Path is at most 4096 characters and Name at most 512. It tries `Path` on this server first, then `ProviderIds`, then for a `Season` or `Episode` the show's `SeriesProviderIds` with `IndexNumber` and, for an episode, `ParentIndexNumber`, and `Name` for a `Person`, `MusicArtist`, or `BoxSet`. `ItemType` is the Jellyfin type and must match when given. At most 16 ids in each set.

```json
{ "Path": "/media/Shows/Test Show (2023)/Season 01/Test Show (2023) - S01E01 - Pilot.mp4", "ItemType": "Episode", "ProviderIds": {}, "SeriesProviderIds": { "Tvdb": "446831" }, "IndexNumber": 1, "ParentIndexNumber": 1 }
```

Answers `{ "Found": true, "ItemId": "9c1e0f…", "ItemType": "Movie" }`, or `{ "Found": false }`. The caller then reads the item through Jellyfin's own API.

### GET /Sync/Peer/Playlists?serverId={id}&id={id}

Every playlist on this server with its owner, shares, open access, and items in order, so a server that pulls from this one can copy them to the right users. Any whitelisted caller may call it, and it records nothing. `serverId` names the caller: when this server lists it, only playlists owned by users it maps are returned. `id` limits the answer to one playlist. A standard user only gets playlists they can see, and only the items they can see. Each item carries its id and path, and the caller finds its own copy by path. `RootId` is the id of the playlist it was first made as, and `IsCopy` says whether Server Sync made it from another server's playlist.

```json
{ "Playlists": [ { "Id": "5d2c…", "Name": "Road Trip", "MediaType": "Audio", "OwnerUserId": "0f8f…", "OpenAccess": false, "Shares": [ { "UserId": "7c9e…", "CanEdit": false } ], "Entries": [ { "ItemId": "9c1e…", "Path": "/media/Music/Artist/Album/01 Song.flac" } ] } ] }
```

### POST /Sync/Peer/Versions

The versions this server holds for a batch of its own keys. Used by the full scan against a peer that carries versions, so conflicts resolve on where and when a value was last edited rather than on saved dates. Keys with no recorded version are left out. At most 500 keys per request.

```json
{ "Kind": "Metadata", "Keys": ["9c1e0f…", "77ab12…"] }
```

```json
{ "Items": [ { "Kind": "Metadata", "Key": "9c1e0f…", "ServerId": "…", "Timestamp": "2026-10-03T19:30:47Z" } ] }
```

### POST /Sync/Peer/History

Negotiate watch history. Each entry proposes a merged state for one of the receiver's user and item pairs and says what the sender believes the receiver currently holds. The receiver writes only when its live state still matches `Expected`, answers `Stale` with its live state otherwise, and records the agreed state in its own row for the sender so both sides merge three way from the same base next time. A sender the receiver lists must be paired, since the agreement is recorded against that sender's row. A sender it does not list negotiates as any whitelisted caller may and nothing is recorded. A standard user only negotiates entries for their own user, and every other entry answers `Failed`. At most 500 entries per request.

```json
{
  "SenderServerId": "…",
  "Items": [
    {
      "UserId": "receiver user id",
      "ItemId": "receiver item id",
      "Expected": { "Played": true, "PlayCount": 1, "PlaybackPositionTicks": 0, "LastPlayedDate": "…", "IsFavorite": false },
      "Proposed": { "Played": true, "PlayCount": 2, "PlaybackPositionTicks": 0, "LastPlayedDate": "…", "IsFavorite": true },
      "SenderUserId": "sender user id",
      "SenderItemId": "sender item id"
    }
  ]
}
```

```json
{
  "Items": [
    { "UserId": "…", "ItemId": "…", "Outcome": "Applied", "Reason": null, "Current": { "Played": true, "PlayCount": 2, "PlaybackPositionTicks": 0, "LastPlayedDate": "…", "IsFavorite": true } }
  ]
}
```

`Outcome` is `Applied`, `Unchanged`, `Stale`, `NotFound`, or `Failed`. A null field in a state means no opinion and matches anything.

### Link routes

These serve [links](Links.md). The source answers `Stream`, `Stream/Check`, `Playback`, `Links/Describe`, and `Links/{itemId}/Images`. The linking server answers `Control` and `Links`. All need the protocol headers. `Stream`, `Stream/Check`, `Playback`, `Control`, `Links/Describe`, and `Links/{itemId}/Images` also need the caller to be paired, as for `Queue`, and answer `400` when the server id or body is missing, `409` when the caller is not on the Servers tab, and `428` when it is not paired.

#### GET /Sync/Peer/Stream/{itemId}?serverId={id}&userId={id}&sourceUserId={id}&probe={bool}&fill={bool}

Streams one of this server's files to a server that links to it, with range support. `HEAD` answers the headers alone. `itemId` is this server's id. `userId` is the user's id on the calling server, and `sourceUserId` is the user here the calling server maps them to, if it does. See [who the source streams as](Links.md#who-the-source-streams-as). `probe=true` with no user is the calling server's media probe, which is checked for the server and the item only and is not counted. `fill=true` is a download into the calling server's [link cache](Links.md#the-link-cache): it passes the same checks for the user apart from the stream limits, is held to the speed limits for that server and user, and is not counted as a stream. A caller holds at most two at once, and a third answers `429`. A stream for a user is held to **Link Stream Speed**, or the user's own, after the first three seconds' worth.

Answers `200` or `206` with the file, or `403` with the reason as text: linking is off for the caller, the user cannot be resolved or may not play or see the item, the bitrate is over the user's limit, or a limit is reached. A file that is itself a `.strm` here is refused. A standard user's key only streams as that user.

#### POST /Sync/Peer/Stream/Check

The same checks without streaming, made when a user presses play so a refusal shows before playback starts.

```json
{ "ServerId": "…", "ItemId": "9c1e0f…", "UserId": "caller user id", "SourceUserId": "user here, or null" }
```

A server [falling back](Links.md#when-the-source-cannot-stream) from another source leaves `ItemId` empty and names the file by its `Path` here instead, at most 4096 characters. An item at that path that is a folder, or no item, is refused with `the file is not on this server`. `Probe: true` with no `UserId` is the calling server's media probe, checked as for `Stream`.

```json
{ "ServerId": "…", "ItemId": "", "Path": "/media/Movies/Film (2024)/Film (2024).mkv", "UserId": "caller user id", "SourceUserId": null, "Probe": false }
```

An allowed answer carries this server's id for the item and the file's size in bytes, so the caller streams by that id and can tell the file is the same. A refusal carries the reason.

```json
{ "Allowed": true, "Reason": null, "ItemId": "4b07c2…", "Size": 734003200 }
```

```json
{ "Allowed": false, "Reason": "the source allows 2 link stream(s) at once and all are in use" }
```

A source older than fallback answers `400` to a request by path, and is listed as tried.

#### POST /Sync/Peer/Playback

Playback of a link, reported by the linking server. The first report opens a session here under the client name **Server Sync**, for the user the stream runs as, and later reports run Jellyfin's own progress and stop handling on it. `SessionKey` is the linking server's session id, so each viewer there is a session of its own here. `Event` is `Start`, `Progress`, or `Stop`. `SessionKey`, `ClientName`, and `UserName` are at most 256 characters. The session is named after `ClientName` and `UserName`, the viewer's user on the linking server, as in `Jellyfin Web (Joe)`.

```json
{
  "ServerId": "…",
  "Event": "Progress",
  "ItemId": "9c1e0f…",
  "UserId": "caller user id",
  "SourceUserId": null,
  "SessionKey": "…",
  "PlaySessionId": "…",
  "PositionTicks": 145000000,
  "IsPaused": true,
  "PlayMethod": "DirectPlay",
  "ClientName": "Jellyfin Web",
  "UserName": "Joe"
}
```

Answers `204`, or `403` with the reason: linking is off, the item or user is not here, the user cannot see the item, the report names no session, or a standard user's key reports for another user.

#### POST /Sync/Peer/Control

A remote control command from the source's dashboard for a viewer of a link here. Only a session here that is playing one of this server's links to the caller can be controlled. `Command` is a Jellyfin playstate command such as `Stop`, `Pause`, `Unpause`, or `Seek`, with `SeekPositionTicks` for a seek. A request with `Text` and no `Command` shows a message, headed with the caller's name. `Text` is at most 1024 characters and `Header` at most 256.

```json
{ "ServerId": "…", "SessionKey": "…", "Command": "Stop" }
```

Answers `204`, `400` for an unknown command, or `404` when no session here is playing a link to the caller.

#### POST /Sync/Peer/Links/Describe

Answers for files the caller holds links to: whether each is still here to stream and its size, and with `Details` its media info. The linking server asks on every content sync to [check its links](Links.md#the-link-check), and asks with `Details` to give a link its media info without probing the file. At most 200 items a request. More, or no `ItemIds`, answers `400`. A folder or an unknown id answers `Exists: false`.

```json
{ "ServerId": "…", "ItemIds": [ "…" ], "Details": true }
```

```json
{
  "Items": [
    {
      "ItemId": "…", "Exists": true, "Size": 734003200, "HasDetails": true,
      "RunTimeTicks": 72000000000, "Container": "mkv", "TotalBitrate": 8000000,
      "Streams": [ … ], "Attachments": [ … ],
      "Chapters": [ { "StartPositionTicks": 0, "Name": "Chapter 1", "HasImage": true } ],
      "Trickplay": [ { "Width": 320, "Height": 180, "TileWidth": 10, "TileHeight": 10, "ThumbnailCount": 720, "Interval": 10000, "Bandwidth": 22000 } ]
    },
    { "ItemId": "…", "Exists": false, "Reason": "the file is no longer on the source" }
  ]
}
```

`Streams` and `Attachments` are Jellyfin's own media stream and attachment objects, for the streams inside the file only. Answers `403` with the reason when linking is off for the caller. A standard user's key only learns about items that user can see. Releases before this route answer `404`, and the linking server then probes its links instead and does not check them.

#### GET /Sync/Peer/Links/{itemId}/Images/{kind}/{index}?serverId={id}&width={width}

One image this server made for a file the caller links to. `kind` is `Chapter`, with the chapter's index, or `Trickplay`, with the tile's index and the `width` of its set. Answers the image as JPEG, `403` when linking is off for the caller, or `404` when there is no such image, kind, or item, or the item is one a standard user's key cannot see. `width` is ignored for `Chapter`.

#### GET /Sync/Peer/Links

This server's links, each with the path its full copy would have, in this server's paths. A server that reads this server's items asks once it meets a `.strm`, matches each link by the file it stands in for, and never copies one as content. Releases before links answer `404`, which means no links.

```json
{ "Links": [ { "LinkPath": "/media/Movies/Film (2020)/Film (2020).strm", "CopiedPath": "/media/Movies/Film (2020)/Film (2020).mkv" } ] }
```

#### GET /Sync/Link/{linkId}?k={secret}

The address written inside each link. Jellyfin's own ffmpeg and stream proxy call it while playing a link, with no token, so it is the one plugin route that needs no Jellyfin sign in. It only answers a request from and to this server's own loopback address that carries no proxy header, with the link's secret, while a user who was allowed at play, or this server's media probe, may read it. It sends the file from the link cache when it is there, and otherwise streams it from the source, with range support, and `HEAD` answers the headers alone. The probe's read ends after 256 MB. Direct play does not come through here unless **Stream Direct Play of Links Per User** is off. Anything else is `403`, an unknown link is `404`, and a source that cannot be reached or is no longer set up is `502` or `503`.

## Operator routes

These back the dashboard, and are handy for scripting or for watching a pool from the outside.

### GET /Sync/Hints

Both queues on this server and the delivery state of every peer it sends to.

```json
{
  "Pending": 0,
  "Gathering": [ { "Kind": "Metadata", "Change": "Metadata for Only A (2021)", "Title": "Only A", "Subtitle": "2021", "ItemId": "…", "UserId": null, "UserName": null, "EditedAt": "…", "DueAt": "…", "Recorded": true } ],
  "Outbound": [
    { "Id": 42, "HintId": "…:42", "PeerKey": "…", "PeerName": "source-a", "Kind": "Metadata", "Key": "…", "Title": "Only A", "Subtitle": "2021", "ItemId": "…", "ItemPath": "…", "UserId": null, "UserName": null,
      "State": "Sent", "Attempts": 1, "NextAttempt": "…", "SentAt": "…", "LastError": null, "CreatedAt": "…" }
  ],
  "Inbound": [ { "Id": 7, "HintId": "…", "OriginServerId": "…", "Kind": "History", "Key": "…", "ReceivedAt": "…", "Attempts": 0, "LastError": null } ],
  "Peers": [ { "Key": "…", "Name": "source-a", "LastAttempt": "…", "PausedUntil": null, "Reason": null, "Sends": ["History", "Metadata", "People"] } ]
}
```

`Pending` counts local edits still gathering before they become hints, and `Gathering` names them with when each goes out. `Unmatched` counts local changes since start that no Push or Sync server mapped, with the last one described in `LastUnmatched`, which is the first thing to check when nothing is being sent. `State` is `Pending`, `Sent`, or `Failed`. A paused peer carries `PausedUntil` and the `Reason`. `Sends` lists the kinds this server sends to that peer, which are the modules selected there, or is null until the peer has said. The lists hold at most five hundred rows each. `OutboundCounts` by state name and `InboundCount` cover the whole table, and `InboundFailingCount` counts the inbound rows whose apply has failed at least once.

### POST /Sync/Hints/Run

Raises every gathering edit, delivers, and applies now instead of waiting for the workers. Answers `204`.

### POST /Sync/Hints/CheckPeer

What the Servers tab's Check Link button calls. Judges a server entry against the direction it is about to be saved with.

```json
{ "ServerUrl": "http://source-a:8096", "ApiKey": "…", "ServerKey": "entry key", "AllowPrivateNetwork": true, "Mode": "Sync" }
```

`ApiKey` may be the stored key's sentinel `__JPK_SECRET_KEPT__` with `ServerKey` naming the entry, so a key never has to be re-entered. `Mode` is `Pull`, `Push`, or `Sync`.

```json
{
  "Reachable": true, "ServerName": "source-a", "ServerId": "…",
  "HasPlugin": true, "PluginVersion": "12.2.1.0",
  "ListsThisServer": true, "PeerMode": "Sync", "SendsToThisServer": true,
  "Accepts": ["History", "Metadata", "People"],
  "ClockSkewSeconds": 0,
  "Paired": true, "PairingError": null,
  "PeerAccessLevel": "Administrator", "PeerAccessUser": null, "PeerCanDownload": true,
  "Severity": "ok",
  "Message": "Ready. Server Sync 12.2.1.0 there lists this server as Sync, so changes travel both ways as they happen."
}
```

`Severity` is `ok`, `warn`, or `error`, which the page shows as green, yellow, or red.

### GET /Sync/Hints/Version?kind={kind}&localItemId={id}&localUserId={id}&name={name}

The version an object carries on this server: which server last edited it and when. Pass `localItemId` for `Metadata` and `Content`, `localUserId` for `Users`, both for `History`, and `name` for `People`. Answers `204` when no edit has been recorded since versions began.

```json
{ "ServerId": "…", "ServerName": "source-a", "Timestamp": "2026-10-03T19:30:47Z", "IsThisServer": false }
```

### POST /Sync/Hints/Failed/Retry and DELETE /Sync/Hints/Failed

Every failed row at once, the **Retry Failed** and **Dismiss Failed** buttons on the Queue tab. A failed row is an outbound row in the `Failed` state, or an inbound row whose apply has failed at least once and waits to be tried again. Retry clears each row's attempts, then delivers the outbound rows and applies the inbound rows now, and answers `{ "Retried": 1200, "Outbound": 1150, "Inbound": 50 }`. Delete discards them, and answers `{ "Removed": 1200, "Outbound": 1150, "Inbound": 50 }`. A discarded inbound row is reported to its sender as finished, so it is not sent again. The scheduled tasks still carry the changes either way.

### DELETE /Sync/Hints/Outbound/{id} and DELETE /Sync/Hints/Inbound/{id}

Discard one row from either queue by its `Id`. A discarded inbound row is reported to its sender as finished, so it is not sent again. The scheduled tasks still carry the change. Answers `204`, or `404`.

### POST /Sync/PromoteItems

Turns links into full copies, the **Keep a full local copy** action on the Sync tab. A link whose file is in the link cache has the cached file moved into place, and a Sync Content run starts now when any is. Any other copy downloads on the next Sync Content run. The `.strm` is removed once the copy is in place. Rows that are not links, are ignored, or are on their way to deletion are skipped. `FromCache` counts the promoted links that come from the cache.

```json
{ "SourceItemIds": ["9c1e0f…", "77ab12…"] }
```

```json
{ "Promoted": 2, "FromCache": 1, "Skipped": 0 }
```

Content rows from `GET /Sync/Items` carry `StorageMode`: `Copy`, `Link`, or `Promoting` for a link on its way to a copy, and `Cached`, true for a link whose file is in the link cache. `cached=true` on `GET /Sync/Items` lists only those.

### GET /Sync/LinkCache

What the [link cache](Links.md#the-link-cache) holds now. The Settings tab shows it under the cache's settings.

```json
{ "Enabled": true, "Location": "/cache/serversync-links", "Files": 12, "UsedBytes": 48318382080, "UsedFormatted": "45 GB", "LimitBytes": 107374182400, "LimitFormatted": "100 GB", "Waiting": 2 }
```

`Waiting` counts files queued to be cached. With the cache off, `Enabled` is false and the counts are 0.

### POST /Sync/LinkCache/Clear

Deletes every cached file, as **Empty the Cache** does. Links keep playing from their sources. Answers `204`.

### DELETE /Sync/Servers/{key}/Rows

Removes every sync row and queued hint that came from one server entry, by the entry's `Key`. The Servers tab calls this after a server is removed. Files already downloaded are not touched.

```json
{ "Content": 11, "History": 4, "Metadata": 11, "People": 1, "Users": 2, "Hints": 0 }
```

# Server Sync API

Server Sync adds a small HTTP API to Jellyfin. Two servers that both run the plugin talk to each other through the **Peer** routes, and the dashboard's Servers tab and Queue view use the **Hints** and **Servers** routes. The dashboard routes sit behind Jellyfin's own `RequiresElevation` policy. The `Peer` routes accept any signed in caller that the receiver's **Whitelist** allows: an administrator from a server on its Servers tab, or a user listed there calling from one of their locations. The plugin adds no login of its own. A standard user's token is refused with `403` on the dashboard routes, and no token gets `401` everywhere. See **[Permissions](Permissions.md)**.

Requests and responses are JSON. Property names are Pascal case, enums travel as their names, and dates are UTC in ISO 8601. The server reads property names without regard to case.

```bash
curl -H 'Authorization: MediaBrowser Token="YOUR_API_KEY"' http://server:8096/Sync/Peer/Capabilities
```

## Peer routes

These are what one Server Sync installation calls on another. You will not normally call them yourself, but they are plain HTTP and can be exercised from the command line.

Every server in a pool runs the same Server Sync version. Every peer route except `Capabilities` needs the header `X-ServerSync-Version` with the caller's version, and answers `426` with both versions when it differs. Check Link reports a mismatch as an error.

### GET /Sync/Peer/Capabilities

Which server this is and what it applies. A `404` means the plugin is not installed there, and a `403` with `{ "Code", "Message" }` means the whitelist refuses the caller. It answers whatever version the caller runs, so Check Link can name both versions when they differ.

```json
{
  "ServerId": "2f40d5d86aba4689819184b48738d2ad",
  "PluginVersion": "12.2.1.1",
  "Accepts": ["History", "Metadata", "People", "Content"],
  "ServerTime": "2026-10-03T19:30:47.9590000Z"
}
```

`Accepts` lists the kinds this server applies, which are the modules that are on. A sender announces only those kinds. `ServerTime` is the peer's clock, which Check Link compares against the caller's.

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

Answers `{ "Paired": true }`, or `409` when the issuing server is not listed here.

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

Completions add the codes an apply can end with: `Applied`, `Unchanged`, `Locked`, `AwaitingApproval`, `Ignored`, `SourceGone`, `Unreadable` when the sender did not say where a file is, `Priority` when another server provides the file, and `GaveUp` after a day of failed attempts. `NotMapped`, `Filtered`, `Ignored`, `ModuleOff`, `Unsupported`, `Priority`, and a final `NoLocalItem` hold that object back from the receiver for a day.

| Status | Meaning on the sender |
| --- | --- |
| `200` | Queued. The rows are marked sent and wait for completion. |
| `400` | The request itself is malformed, such as a hint with no `HintId`. Permanent for those rows, and written once to the activity log. A problem with one hint is declined for that hint alone in a `200` answer. |
| `401` or `403` | The key is refused, or the sender is not on the receiver's whitelist. The peer is paused for fifteen minutes, set by **First wait** in the **Refused** column of the Retries table under Settings > Processing. |
| `404` | No Server Sync behind that URL. The peer is paused. |
| `409` | The receiver does not list the sender as Pull or Sync. The peer is paused with the reason. |
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

`Pending` counts local edits still gathering before they become hints, and `Gathering` names them with when each goes out. `Unmatched` counts local changes since start that no Push or Sync server mapped, with the last one described in `LastUnmatched`, which is the first thing to check when nothing is being sent. `State` is `Pending`, `Sent`, or `Failed`. A paused peer carries `PausedUntil` and the `Reason`. `Sends` lists the kinds this server sends to that peer, which are the modules selected there, or is null until the peer has said. The lists hold at most five hundred rows each. `OutboundCounts` by state name and `InboundCount` cover the whole table.

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

### DELETE /Sync/Hints/Outbound/{id} and DELETE /Sync/Hints/Inbound/{id}

Discard one row from either queue by its `Id`. The scheduled tasks still carry the change. Answers `204`, or `404`.

### DELETE /Sync/Servers/{key}/Rows

Removes every sync row and queued hint that came from one server entry, by the entry's `Key`. The Servers tab calls this after a server is removed. Files already downloaded are not touched.

```json
{ "Content": 11, "History": 4, "Metadata": 11, "People": 1, "Users": 2, "Hints": 0 }
```

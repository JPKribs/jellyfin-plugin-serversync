# Glossary

The agreed name for every concept in this plugin. The UI, the docs, messages, and code use these words and no others.

* Use the **Term** in the UI, the docs, and messages.
* The **In Code** column names the type or member that holds it, when the code name differs.
* Never use a word from the **Not** column for the concept.
* Add a term here before it appears anywhere else. Rename it here first when it changes, then everywhere in the same commit.

## Servers

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Server | A Jellyfin server that runs, or is set up with, Server Sync | `SourceServer` for an entry | Node, instance, host |
| This server | The server whose dashboard is open | Local | Destination, target |
| Source | A server this one reads files or data from | `SourceServer` | Parent, origin, upstream |
| Peer | A server that also runs Server Sync and talks to this one. Used in the API docs and code, not the UI | `Peer*` | Partner, remote |
| Pool | Every server that syncs with each other | | Cluster, mesh, network |
| Server entry | One server's settings on the **Servers** tab | `SourceServer` | Connection, profile |
| Server URL | The base address this server uses to reach another | `Url` | Address, host, endpoint |
| External URL | The address a browser uses for that server, when it differs | `ExternalUrl` | Public URL |
| Allow private network address | Lets a server entry use a loopback or LAN address | `AllowPrivateNetwork` | Allow LAN, local address |
| API Key | A Jellyfin key for another server | `ApiKey` | Token, Api Key, API key |
| Rank | A server's place in the **Servers** list. Higher wins ties | Order in `SourceServers` | Priority, weight |

## Directions

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Direction | Which way changes travel between this server and another | `ServerMode` | Mode, role |
| Pull | This server reads from that one on a schedule | `ServerMode.Pull` | Download, mirror |
| Push | This server sends its changes to that one as they happen | `ServerMode.Push` | Upload |
| Sync | Both ways, for a pool of equals | `ServerMode.Sync` | Two way, bidirectional |
| Paired | Two servers that list each other and share a secret | | Linked, trusted |
| Protocol | The numbered wire format two servers must share to exchange changes | Protocol version | API version |

## Mappings and Libraries

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Library mapping | Which library on another server fills which library here | `LibraryMapping` | Library link, library pair |
| User mapping | Which user on another server is which user here | `UserMapping` | User link |
| Unmapped users | Users here with no mapping, and who they stream as on the source | | Guests |
| Root folder | The folder a mapped library starts at. Files match by path under it | | Base path, library path |
| Storage | How a library mapping keeps its files, as **Copy** or **Link** | | Mode, sync type |
| Copy | A full file downloaded to this server | | Download, local file |
| Library filter | **Allow all**, a **Whitelist**, or a **Blacklist** of files, collections, and playlists | `LibraryFilterMode` | Rule, include list |
| Whitelist | The users and locations that may call this server's sync routes. Also a Library filter mode | `PeerCallerGrant` | Allow list |

## Syncing

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Sync type | Content, History, Metadata, People, Users, or Playlists | Module | Feature, module |
| Content | Media files | `ContentSync` | Media, files |
| History | Watch history, such as played state, position, and favorites | `HistorySync` | Watch state, user data |
| Metadata | Item details and images | `MetadataSync` | Info |
| People | People's details and images | `PeopleSync` | Persons, cast |
| Users | User settings | `UserSync` | Accounts, profiles |
| Playlists | Playlists and their items | `PlaylistSync` | Lists |
| Sync Content | The scheduled task that refreshes and downloads files | | Content task |
| Sync Information | The scheduled task for metadata, people, users, history, and playlists | | Info task |
| Scan | A scheduled pass that compares everything with every source | Refresh | Poll, crawl |
| Full scan | A scan that also checks each synced file on disk | | Deep scan |
| Sync table | The table of tracked items for one sync type on the **Sync** tab | `ISyncTableManager` | Grid, list |
| Precedence | Who speaks for an item held on several servers, **Newest** or **Ranked** | `SyncPrecedence` | Priority, winner |
| Newest | The server that changed an item last speaks for it | `SyncPrecedence.Newest` | Latest |
| Ranked | The highest ranked server that holds an item speaks for it | `SyncPrecedence.Rank` | Priority |
| Origin | The server a copy of a file first came from | | Owner, home |
| Stamp | The server and time of the edit a value came from | `ObjectVersion` | Version tag, timestamp |
| Approvals | Whether new and changed files download on their own, wait, or are left alone | | Confirmations |
| Removed files | What happens to a local file once its item is gone from the source | | Deletions, orphans |
| Metadata provider | Server Sync as a Jellyfin metadata and image fetcher that reads from the servers this one pulls from | `PeerMetadataSource` | Fetcher, scraper |
| Media segment provider | Server Sync as a Jellyfin media segment provider that copies segments from the same file on the servers this one pulls from | `PeerSegmentProvider` | Segment plugin, intro provider |
| Media segment | A marked part of a file, such as an intro or credits, as Jellyfin names it | `MediaSegmentDto` | Chapter, marker, skip |
| Recycling bin | The folder removed and replaced files move to when it is on | | Trash, recycle bin |

### Item Statuses

| Term | Meaning | In Code |
| :--- | :--- | :--- |
| Pending | Waiting for approval | `SyncStatus.Pending` |
| Queued | Will be synced on the next run | `SyncStatus.Queued` |
| Synced | Matches the source | `SyncStatus.Synced` |
| Errored | Failed and will be retried | `SyncStatus.Errored` |
| Ignored | Will not be synced until queued again | `SyncStatus.Ignored` |
| Deleting | Will be removed on the next run | `SyncStatus.Deleting` |
| Pending deletion | Waiting for approval to be removed | |

## Live Changes

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Live changes | Changes servers announce to each other as they happen | Hints | Real time sync, push sync |
| Hint | A message that says what changed, never the value | `OutboundHint`, `InboundHint` | Event, notification, update |
| Change queue | The **Queue** view of hints waiting, sent, and failed | | Hint queue, outbox |
| Send changes after | How long an item must go untouched before its hint goes out | | Debounce, delay |
| Gathering | A change waiting out **Send changes after** | | Collecting, buffering |
| Sent | A hint the peer accepted, waiting for it to finish | `OutboundState.Sent` | Delivered |
| Failed | A hint the peer refused as malformed. It is not retried | `OutboundState.Failed` | Rejected, dead |
| Held | A change kept back for the scheduled scan because it keeps coming back | Loop breaker | Throttled, blocked |
| Paused | A server left alone for a while because it cannot take changes | | Suspended, backed off |
| Echo | A change sent back to the server it came from | | Loop, bounce |
| One hop | A server announces only changes made on it, never ones it received | | Relay, forward |

## Links

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Link | A `.strm` Server Sync wrote for a source file. A `.strm` a user made is never a link | | Shortcut, stub, pointer |
| Linking server | The server that holds the link | | Client, consumer |
| Allow linking | The source's switch that lets a server link its files | | Sharing |
| Link cache | Local copies of links that were played | `LinkCache` | Cache, buffer |
| Cached link | A link whose file is in the link cache | `Cached` | Downloaded link, local link |
| Link check | The check during **Sync Content** that each link still streams | | Validation, health check |
| Always keep | Shows and films a Link library holds as copies | | Pin, keep local |
| Permanent copy | A full copy held in a Link library, with the reason | `KeptCopies` | Kept copy, local override |
| Turn back into links | Makes permanent copies links again | Unkeep | Revert, demote |

## Housekeeping

| Term | Meaning | Not |
| :--- | :--- | :--- |
| Activity page | Jellyfin's dashboard activity log | Activity feed, log page |
| Server log | Jellyfin's log files | Debug log |
| Reset | Forgets a sync table's tracking, never media | Wipe, purge |
| Files | The **Storage** tab's chart of one server's synced files as copies, links, and cached links | File stats, breakdown |
| Usage | The **Storage** tab's bar of how much of the link cache is used | Disk usage, capacity |
| Prune safety limit | The stop that refuses to remove a large share of links or rows in one run | Breaker, guard |

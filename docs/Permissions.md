# Permissions

Server Sync talks to every other server with the credential on that server's entry, which is an **API Key** or a token from **Sign in**. That credential decides what this server can read there. Writes always happen on this server through Jellyfin itself, so this server needs no extra rights to apply what it reads.

Give each entry the least access that covers what you want it to do. A standard user can pull, push, and sync. It is limited to what that user can see and to that user's own history, settings, and playlists.

## Kinds of Credential

| Credential | What Jellyfin treats it as |
| :--- | :--- |
| An **API Key** from **Dashboard > API Keys** | Full access to everything. An API Key belongs to no user. |
| A **Sign in** as an administrator | Full access to everything, plus the media downloading setting of that account. |
| A **Sign in** as a standard user | What that user may see and do, nothing more. |

**Test connection** reads which of these the entry holds, who it belongs to, and whether that account may download media. The **Authentication** step then lists what works with it, and the parts that need more are turned off.

## What Each Function Needs on the Other Server

| Function | Standard user | Administrator or API Key |
| :--- | :--- | :--- |
| Test connection | Works | Works |
| Content listing | Only the libraries that user can see. Folder paths are only shown to administrators, so type each source root path by hand | Every library, with its folder paths |
| File downloads | Needs **Allow media downloading** on that user | Works. A signed in administrator also needs **Allow media downloading** |
| Subtitle and companion files | Works for the items that user can see | Works |
| Metadata, images, people | Only items in the libraries that user can see | Everything |
| Collections | Only collections in the libraries that user can see | Everything |
| Playlist whitelists | Only playlists that user owns or that are shared with them | Playlists the account can open |
| Skip items watched by | Only that user's own plays | Every selected user |
| Watch history | Only that user's own history | Every mapped user |
| User settings | Only that user's own settings and profile image | Every mapped user |
| Playlists | Only the playlists that user can see, all owned by that user here | Every playlist owned by a mapped user. Exact owners and shares need Server Sync there |
| Playlist changes and deletions as they happen | Once that server whitelists the user | Works with Push and Sync |
| Two way history | Only that user's own history, once that server whitelists the user | Works when Server Sync runs there |
| Push and Sync | Works once that server whitelists the user | Works when Server Sync runs there |
| Metadata provider lookups and edit versions | Only items that user can see and that user's own history and settings, once that server whitelists the user | Works when Server Sync runs there |

A standard user's mappings to other users stay saved but are skipped, and the **Users** step marks them. History and settings for those users need an administrator.

These user settings on the other server affect a standard user's entry:

* **Disable this user** blocks every call.
* **Allow remote connections to this server** is needed when this server reaches that one from outside its local network.
* **Enable access to all libraries** and the library list decide what the user can see.
* **Allow media downloading** is needed to copy files.
* Access schedules apply. Calls outside the allowed hours are refused.

## Push and Sync

With Push and Sync, a change is announced as a hint and the receiving server pulls the change itself. So two credentials are involved:

* **The sender's key for the receiver** must be allowed by the receiver's whitelist. An administrator's key is allowed from any server on the receiver's **Servers** tab. A standard user's key needs that user on the receiver's whitelist.
* **The receiver's key for the sender** decides what the receiver can read when it pulls. If it is a standard user's key, the receiver only gets what that user can see, and only that user's history and settings. The sender is never announced a change the receiver could not read.

**Check link** reports both. It says whether the other server lists this one, and the access the other server holds for this one.

## Whitelist

The routes under `/Sync/Peer` are what other servers call to send changes, pair, look up items, and negotiate history. **Settings > Whitelist** decides who may call them. Your own dashboard is never limited.

| Caller | Allowed when |
| :--- | :--- |
| Administrator or API Key | It calls from the address of an enabled server on the **Servers** tab, by its Server URL or External URL, or it has a whitelist row that covers its location |
| Standard user | It has a whitelist row that covers its location |

Each whitelist row is one user and the locations they may call from, separated by commas, such as `myserver.example.com, 192.168.1.0/24`. A location is a host name, a URL, an IP address, or a range. Leave the locations blank to allow every location. Select **API keys** as the user to cover API Keys.

A standard user who is let in reaches only their own data. History and version reads work only for their own user, lookups and playlists cover only what they can see, and playlist items they cannot see are left out.

**Test connection** on the other server's entry reports whether this server's key may use its sync endpoints, and why not:

| Message | Meaning |
| :--- | :--- |
| This user is not whitelisted | The user has no whitelist row there |
| This IP is not whitelisted | An administrator calling from an address that is not on that server's **Servers** tab or whitelist |
| This user is not whitelisted for this IP | The user has a row, but none of its locations match |
| Server Sync is not installed there | The other server has no Server Sync |
| A version message | Server Sync there is a different version |

Every refused call is answered with `403` and a body such as `{ "Code": "UserNotWhitelisted", "Message": "This user is not whitelisted" }`, where `Code` is `UserNotWhitelisted`, `IpNotWhitelisted`, or `UserNotWhitelistedForIp`. It is also logged on the server that refused it, with the user, the address, and the reason.

Host names are looked up once and remembered for an hour. When a caller does not match a remembered address, the name is looked up again before the call is refused, at most every thirty seconds. A dynamic DNS name that moved therefore keeps working. A name that cannot be looked up is logged as an error and its last known addresses are kept. Behind a reverse proxy, add the proxy under **Dashboard > Networking > Known Proxies** so Jellyfin sees the real address.

## Links

Streaming a [link](Links.md) uses the linking server's key for the source, like every other read, and the source's whitelist must allow it. On top of that the source decides, on every play that streams from it, which of its users the stream runs as and whether that user may play the item, by its own user mappings, the **Unmapped users** choice, and its limits. A source entry that holds a standard user's key streams as that user only. A link whose file is in the link cache plays under the linking server's rules alone.

| Function | Standard user | Administrator or API Key |
| :--- | :--- | :--- |
| Stream a link | Only as that user, only what that user can see and play | As the user the source maps, within the source's limits |
| Report playback of a link | Only for that user | For the mapped user |
| Check links and take their media info and images | Only items that user can see | Every linked item |
| Remote control of a viewer from the source's dashboard | Works, once the source's entry for the linking server holds a key that server's whitelist allows | Works, once the source's entry for the linking server holds a key |

The address inside a link, `/Sync/Link`, is the one route that needs no Jellyfin sign in, since Jellyfin's own ffmpeg calls it without a token. It answers only this server itself, from and to its loopback address with no proxy header, with the link's secret, while a user who was allowed at play is playing. See [Links security](Links.md#security).

## On This Server

The dashboard routes on this server need an administrator. The peer routes need a signed in caller that the whitelist allows. The link address needs no sign in and answers only this server, as above.

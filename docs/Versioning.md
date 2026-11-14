# Versioning

Releases use a four part version, `JJ.JJ.F.B`, that matches the supported Jellyfin version with the plugin's own feature and bug count:

```
12.0.1.2
└──┘ └┬┘
 │    └── 1 = Plugin feature release
 │        2 = Plugin bug/patch release within that feature
 │
 └─── 12.0 = Jellyfin version this build was tested/released for
```

## Mixing versions in a pool

Servers in a pool do not need the same plugin version. Each build speaks a numbered wire protocol, and two servers exchange live changes, history negotiation, and conflict checks as long as their protocols overlap. A patch or feature release that does not change the protocol works alongside the release before it, so servers can be updated one at a time.

The protocol number changes only when a hint, a peer route, or a conflict rule changes in a way an older server would misread. The changelog says so when it does. Until both servers are compatible, changes for the other server wait in the queue and Check Link reports the reason.

Releases before protocol versions, up to 12.2.8.1, only talk to exactly the same version, so the first update to a protocol aware release still needs every server in the pool updated.

## Updating from an older release

This release carries no upgrade steps for anything older than **12.4.13.0**. It opens the database and settings 12.4.13.0 leaves behind, and nothing before them.

To update a server that runs a release before 12.4.13.0:

1. Install **12.4.13.0** and restart Jellyfin.
2. Leave it running for a day. It converts the database and the settings when it starts, and marks the copies it already tracks during its upkeep, which waits for a moment when no content task is running.
3. Update to the current release and restart Jellyfin.

A server already on 12.4.13.0 or later updates directly.

When a server skips 12.4.13.0, Server Sync holds itself and Jellyfin runs as usual:

* **The database is left exactly as it was.** Nothing is read from it, written to it, or moved aside.
* **The settings are left exactly as they were.** They are copied to `Jellyfin.Plugin.ServerSync.xml.before-12.4.13.0` beside the settings file before anything reads them, and every save is refused, so the settings an older release wrote are still there for 12.4.13.0 to convert.
* **Nothing of Server Sync runs.** No background work starts, playback is not touched, and its metadata lookups answer nothing. Its tasks fail with the reason.
* **Every Server Sync page says why** in a line at its top. The Jellyfin log and the **Activity** page say the same, as "Server Sync Needs an Earlier Update First".

Installing 12.4.13.0, starting Jellyfin, leaving it a day, and then updating again picks up where the server left off.

## Downgrading

Server Sync's database notes the oldest release that can read it. A release that only added to the database leaves that alone, so an older release keeps using the file. Once a server holds a link, the database names the first release with links as its oldest reader, and once a kept copy is on its way back to a link, the first release that knows that. An older release sets such a database aside as a `.newer-*` backup and starts fresh, rather than take a `.strm` for a broken copy and download over it.

## Links and other releases

[Links](Links.md) need Server Sync on both servers. The source advertises `Link` among its features, and a server with a Link mapping waits for a source that does.

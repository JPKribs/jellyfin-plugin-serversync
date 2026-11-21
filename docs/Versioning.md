# Versioning

Releases use a four part version, `JJ.JJ.F.B`. It matches the supported Jellyfin version, followed by the plugin's own feature and bug count:

```
12.4.18.1
└┬─┘ └┬┘└
 │    │ └ 1 = Plugin bug release within that feature
 │    └─── 18 = Plugin feature release
 └──────── 12.4 = Jellyfin version this build is released for
```

## Mixing Versions in a Pool

Servers in a pool do not need the same plugin version. Each build speaks a numbered protocol. Two servers exchange live changes, history negotiation, and conflict checks as long as their protocols overlap. A release that does not change the protocol works alongside the release before it, so servers can be updated one at a time.

The protocol number changes only when a hint, a peer route, or a conflict rule changes in a way an older server would misread. The changelog says so when it does. Until both servers are compatible, changes for the other server wait in the queue and **Check link** reports the reason.

A server that reports no protocol is compatible only when it runs exactly the same plugin version.

## Updating From an Older Release

Version 12.4.18.1 carries no upgrade steps for anything older than 12.4.13.0. It opens the database and settings that 12.4.13.0 leaves behind, and nothing before them. On its first start it adds the two tables the **Statistics** tab reads, the table of media segments, and four media segment columns to the Metadata table, and changes nothing else in it.

To update a server that runs a release before 12.4.13.0:

1. Install 12.4.13.0 and restart Jellyfin.
2. Leave it running for a day. It converts the database and the settings when it starts, and marks the copies it already tracks during its upkeep, which waits for a moment when no content task is running.
3. Update to 12.4.18.1 and restart Jellyfin.

A server already on 12.4.13.0 or later updates directly.

When a server skips 12.4.13.0, Server Sync holds itself and Jellyfin runs as usual. These things are true until 12.4.13.0 has run:

* **The database is left exactly as it was.** Nothing is read from it, written to it, or moved aside.
* **The settings are left exactly as they were.** They are copied to `Jellyfin.Plugin.ServerSync.xml.before-12.4.13.0` beside the settings file before anything reads them. Every save is refused, so the settings an older release wrote are still there for 12.4.13.0 to convert.
* **Nothing of Server Sync runs.** No background work starts, playback is not touched, and its metadata lookups answer nothing. Its tasks fail with the reason.
* **Every Server Sync page says why** in a line at its top. The Jellyfin log says the same, and the **Activity** page shows it as "Earlier update required".

Installing 12.4.13.0, starting Jellyfin, leaving it a day, and then updating again picks up where the server left off.

## Downgrading

Server Sync's database notes the oldest schema that can read it. A release that only added to the database leaves that alone, so an older release keeps using the file. Once a server holds a link, the database names the oldest schema that knows links as its oldest reader. Once a kept copy is on its way back to a link, it names the oldest schema that knows that. An older release sets such a database aside as a `.newer-*` backup and starts fresh, rather than take a `.strm` for a broken copy and download over it.

## Links and Other Releases

[Links](Links.md) need Server Sync on both servers. The source advertises `Link` among its features, and a server with a **Link** mapping waits for a source that does.

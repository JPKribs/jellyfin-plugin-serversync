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

## Links and older releases

[Links](Links.md) need both servers on 12.2.10.0 or later. The source advertises `Link` among its features, and a server with a Link mapping waits for a source that does. Releases without links ignore the feature, and matching against them works as before.

12.2.11.1 to 12.2.11.5 change nothing in the protocol or the database, and mix freely with 12.2.11.0. A link session on the source is named after the viewer's client and user from 12.2.11.4 on, and needs both servers on it; with an older linking server it keeps the linking server's name. Ending stale link sessions, showing a link session as a direct stream, and refusing proxied requests to a link's address only need the server they run on. Keeping playback from the link cache off the source needs the linking server on 12.2.11.3 or later.

12.2.11.0 adds the link cache, media info and images from the source, the link check, and speed and per user limits, with no change to the protocol or the database. A linking server on 12.2.11.0 works with a source on 12.2.10.x: its links are probed through the link, are not checked, and a cache download for a user counts as a stream on that source. The source's speed and per user limits need 12.2.11.0 on the source.

Server Sync keeps its own database, which notes the oldest release that can read it. Updating to 12.2.10.0 or later only adds a column, so a release from 12.2.2.0 on still reads it, until the server holds a link. From the first link on, written or waiting to be, the database names the first release with links as its oldest reader. An older release sets such a database aside as a `.newer-*` backup and starts fresh, rather than take a `.strm` for a broken copy and download over it.

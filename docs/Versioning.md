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

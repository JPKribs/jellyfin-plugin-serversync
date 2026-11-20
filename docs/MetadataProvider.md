# Metadata Provider

Server Sync is also a metadata and image provider for every media type. Turn on **Server Sync** in a library's metadata and image fetchers, above the others.

It answers for items only while **Enable metadata sync** and its **Enable metadata provider** are both on, and for people only while **Enable people sync** and its **Enable metadata provider** are both on. Otherwise it asks no server and returns nothing, so Jellyfin's other fetchers fill the item.

* **Refresh** reads from the highest ranked server that has the item, found by path, then provider ids, then show ids with season and episode numbers, and by name for people, artists, and collections.
* **Identify** lists every server that has the item, named after its server, in the order of the **Servers** tab. Picking one pins the item to that server, shown as **Server Sync Item** among its external ids.
* **Images** lists the images of every server that has the item, the pinned one first, each named after its server.

## Media Segments

Server Sync is also a media segment provider. It copies intros, credits, recaps, previews, and commercials from the same file on the servers this one pulls from.

1. Turn on **Enable metadata sync** and **Enable media segment provider** under **Settings > Metadata**.
2. Keep **Server Sync** on in the library's media segment providers. It is on unless it was turned off there.
3. Run **Sync Information**, then **Media Segment Scan** under **Dashboard > Scheduled Tasks**, or wait for both.

Media segments are a category of each Metadata row, like images and people. A row's dialog lists them under **Media segments**.

![Metadata Item](screenshots/Metadata%20Item.png)

* **Refresh** reads the segments of every video and audio file it lists, 200 files to a request. For each type of segment, a file takes the segments of the highest ranked server that holds it and has segments of that type, so an intro from one server and credits from another both arrive. When no server has any, the row holds none.
* **Sync** writes a row's segments into the sync database, where they survive a restart. **Queue** and **Ignore** work on them as on any category, and the row shows **Segments: Changes** until they are written.
* Jellyfin's **Media Segment Scan** then takes them from the sync database and asks no server.
* Segments are used only when the source's file has the same size and runtime as the one here. A link is matched by runtime alone.
* A segment that runs past the end of the file here is cut at the end, and one that starts there or later is left out. A segment two providers marked is kept once.
* **Replace existing segments** replaces the segments another provider gave a file with the copied ones of the same type. While it is off, those segments stay and no copied segment of their type is added beside them. Copied segments of other types are added either way. When they already match, nothing changes.
* **Remove segments no server has** removes the segments Server Sync copied to a file when no server has any for it. While it is off, the file keeps them. Segments another provider made here are never removed, so a server's new segments are never wiped by a pool that has not copied them yet.
* **Consolidate duplicate intros** shrinks intros that overlap to the part they share, so 0:00 to 0:30 and 0:05 to 0:45 become 0:05 to 0:30. **Consolidate duplicate outros** does the same for outros.
* **Consolidate other segments** joins overlapping segments of the same type, other than intros and outros, into one that covers both, so 1:00 to 1:30 and 1:05 to 1:45 become 1:00 to 1:45. Segments of different types are never joined.
* Only segments that overlap are consolidated. Each setting is off by default, works on the segments copied from a server, and is applied at the next **Refresh**, which lists every item again after one changes.
* Server Sync runs after the other providers, so its version stands. A library that sets its own provider order decides instead.
* A server that cannot be reached, or runs a release without media segments, leaves each row's segments as they were.
* A server shares every segment it holds, including ones Server Sync copied there, and names the server each one was first made on. A server never takes back a segment it made itself. It keeps its own segments of that type instead, so a change made on one server flows out through a ring or mesh of any size, and an older copy coming back around never undoes it. Segments a server copied never stop its own segments of other types from being shared.
* Live changes never carry media segments. A scan that lists only what changed reads the segments of those items only.
* Turning **Enable media segment provider** off leaves the segments here as they are. Turning **Server Sync** off in the library's media segment providers hides them.

# Metadata Provider

Server Sync is also a metadata and image provider for every media type. Turn on **Server Sync** in a library's metadata and image fetchers, above the others.

It answers for items only while **Enable Metadata Sync** and its **Enable Metadata Provider** are both on, and for people only while **Enable People Sync** and its **Enable Metadata Provider** are both on. Otherwise it asks no server and returns nothing, so Jellyfin's other fetchers fill the item.

* **Refresh** reads from the highest ranked server that has the item, found by path, then provider ids, then show ids with season and episode numbers, and by name for people, artists, and collections.
* **Identify** lists every server that has the item, named after its server, in the order of the **Servers** tab. Picking one pins the item to that server, shown as **Server Sync Item** among its external ids.
* **Images** lists the images of every server that has the item, the pinned one first, each named after its server.

## Media Segments

Server Sync is also a media segment provider. It copies intros, credits, recaps, previews, and commercials from the same file on the servers this one pulls from.

1. Turn on **Enable Metadata Sync** and **Enable Media Segment Provider** under **Settings > Metadata**.
2. Keep **Server Sync** on in the library's media segment providers. It is on unless it was turned off there.
3. Run **Sync Information**, then **Media Segment Scan** under **Dashboard > Scheduled Tasks**, or wait for both.

Media segments are a category of each Metadata row, like images and people. A row's dialog lists them under **Media Segments**.

![Metadata Item](screenshots/Metadata%20Item.png)

* **Refresh** reads the segments of every video and audio file it lists, 200 files to a request. A file takes the segments of the highest ranked server that holds it and has segments for it. When no server has any, the row holds none.
* **Sync** writes a row's segments into the sync database, where they survive a restart. **Queue** and **Ignore** work on them as on any category, and the row shows **Segments: Changes** until they are written.
* Jellyfin's **Media Segment Scan** then takes them from the sync database and asks no server.
* Segments are used only when the source's file has the same size and runtime as the one here. A link is matched by runtime alone.
* A segment that runs past the end of the file here is cut at the end, and one that starts there or later is left out. A segment two providers marked is kept once.
* **Replace Existing Segments** replaces the segments another provider gave a file. While it is off, those segments stay and nothing is added beside them. When they already match, nothing changes either way.
* **Remove Segments No Server Has** removes a file's segments when no server has any for it. While it is off, the file keeps what it has.
* Server Sync runs after the other providers, so its version stands. A library that sets its own provider order decides instead.
* A server that cannot be reached, or runs a release without media segments, leaves each row's segments as they were.
* A server shares every segment it holds, including ones Server Sync copied there, and names the server they were first made on. A server never takes back segments it made itself, so one it deleted cannot return through another server.
* Live changes never carry media segments. A scan that lists only what changed reads the segments of those items only.
* Turning **Enable Media Segment Provider** off leaves the segments here as they are. Turning **Server Sync** off in the library's media segment providers hides them.

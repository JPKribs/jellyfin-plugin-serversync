# Metadata Provider

Server Sync is also a metadata and image provider for every media type. Turn on **Server Sync** in a library's metadata and image fetchers, above the others.

It answers for items only while **Enable Metadata Sync** and its **Enable Metadata Provider** are both on, and for people only while **Enable People Sync** and its **Enable Metadata Provider** are both on. Otherwise it asks no server and returns nothing, so Jellyfin's other fetchers fill the item.

* **Refresh** reads from the highest ranked server that has the item, found by path, then provider ids, then show ids with season and episode numbers, and by name for people, artists, and collections.
* **Identify** lists every server that has the item, named after its server, in the order of the **Servers** tab. Picking one pins the item to that server, shown as **Server Sync Item** among its external ids.
* **Images** lists the images of every server that has the item, the pinned one first, each named after its server.

## Media Segments

Server Sync is also a media segment provider. It copies intros, credits, recaps, previews, and commercials from the same file on a source.

1. Turn on **Enable Metadata Sync** and **Enable Media Segment Provider** under **Settings > Metadata**.
2. Keep **Server Sync** on in the library's media segment providers. It is on unless it was turned off there.
3. Run **Sync Information**, then **Media Segment Scan** under **Dashboard > Scheduled Tasks**, or wait for both.

* Each metadata scan reads the segments of every video and audio file the **Metadata** table matched, from the highest ranked source that holds it, 200 files to a request. It keeps them in the sync database, so they survive a restart.
* Jellyfin's **Media Segment Scan** then takes them from the sync database and asks no server.
* Segments are used only when the source's file has the same size and runtime as the one here. A link is matched by runtime alone.
* A segment that runs past the end of the file here is cut at the end, and one that starts there or later is left out. A segment two providers on the source both marked is kept once.
* For a file a source speaks for, the source's segments are the only ones kept. Server Sync runs after the other providers and removes the segments they gave the file, even when the source has none. When their segments already match the source's, nothing is replaced. A library that sets its own provider order decides instead.
* Files no source speaks for keep the segments other providers gave them.
* When a source cannot be reached, or runs a release without media segments, the segments kept for its files stay as they were.
* A source shares every segment it holds, including ones Server Sync copied there, and names the server they were first made on. A server never takes back segments it made itself, so one it deleted cannot return through another server.
* Live changes never carry media segments. They come over only with the metadata scan.
* Turning **Enable Media Segment Provider** off leaves the segments here as they are. Turning **Server Sync** off in the library's media segment providers hides them.

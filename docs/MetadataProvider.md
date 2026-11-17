# Metadata Provider

Server Sync is also a metadata and image provider for every media type. Turn on **Server Sync** in a library's metadata and image fetchers, above the others.

It answers for items only while **Enable Metadata Sync** and its **Enable Metadata Provider** are both on, and for people only while **Enable People Sync** and its **Enable Metadata Provider** are both on. Otherwise it asks no server and returns nothing, so Jellyfin's other fetchers fill the item.

* **Refresh** reads from the highest ranked server that has the item, found by path, then provider ids, then show ids with season and episode numbers, and by name for people, artists, and collections.
* **Identify** lists every server that has the item, named after its server, in the order of the **Servers** tab. Picking one pins the item to that server, shown as **Server Sync Item** among its external ids.
* **Images** lists the images of every server that has the item, the pinned one first, each named after its server.

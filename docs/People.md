# People Syncing

People syncing copies person entities from a source Jellyfin server onto this server.

## Settings

* **Enable People Sync** turns people syncing on.
* **Images** syncs profile images for people, along with any backdrops a person carries.
* **Precedence** chooses which server is heard for a person that several servers know, either the one whose edit is newest or always the highest ranked. See [Several Servers](SyncTypes.md#several-servers).

## Images

When a person's images differ from the source, this server downloads the full source set for that image type, then replaces its own set for that type with it. If a download fails, the existing images are kept and the row is marked Errored. That matters most for backdrops, where a person can hold several. Replacing the whole set stops the next library scan from picking up a leftover backdrop and bringing the difference back.

A scan tells whether an image changed from the tag the source gives it, which changes whenever the source writes the image again. Once an image here has been matched to the source's, an unchanged tag means no change, and a new tag means the image is fetched. An image that has never been matched is compared by file size, so images already on both servers are matched without being downloaded.

**Refresh Parallelism** and **Deep Image Verification** apply to Metadata and People and live under **Settings > Processing**. Deep Image Verification measures every source image on every scan and compares by size, which also catches an image file swapped on the source's disk before the source has noticed.

## Live Changes Between Servers

When a server entry is set to **Push** or **Sync**, an edit to a person made here is announced to that server as it happens, and that server pulls the person's metadata and images through the same code the scan uses. Only an edit raises a hint, and only for a person who already exists on the other server. As with metadata, the newest edit wins when the two servers disagree, unless **Precedence** is set to **Ranked**. This holds in hints and in the full scan alike.

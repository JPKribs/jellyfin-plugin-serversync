# Metadata Syncing

Metadata syncing copies item details from a source Jellyfin server onto matching local items.

## Settings

* **Enable Metadata Sync** turns metadata syncing on.
* **Metadata** syncs core fields such as titles, overviews, and dates.
* **Genres** syncs item genres.
* **Tags** syncs item tags.
* **Studios** syncs item studios.
* **People** syncs the cast and crew attached to each item.
* **Images** syncs item images such as posters and backdrops.
* **Folder Items** also syncs metadata for container items such as series and seasons.

## Images

When an item's images differ from the source, this server deletes its whole set for that image type and then downloads the full source set. Backdrops need this because an item can hold several of them. A source with two backdrops over a local five has to remove the extra three files, otherwise the next library scan picks them up again and the difference comes straight back.

Refresh parallelism and Deep Image Verification are shared across sync modules and live under **Configuration > Processing**.

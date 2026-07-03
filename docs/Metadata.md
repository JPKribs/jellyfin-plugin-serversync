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
* **Deep Image Verification** re-verifies every source image size over HTTP on each refresh, even when the image is unchanged. This catches images replaced on the source's disk without a rescan, but makes refreshes much slower on large libraries. When off (the default), previously measured sizes are reused for images whose tag has not changed.
* **Folder Items** also syncs metadata for container items such as series and seasons.

# People Syncing

People syncing copies person entities from a source Jellyfin server onto this server.

## Settings

* **Enable People Sync** turns people syncing on.
* **Images** syncs profile images for people.
* **Deep Image Verification** re-verifies every source person image size over HTTP on each refresh, even when the image is unchanged. Slower on libraries with many people. When off (the default), previously measured sizes are reused for images whose tag has not changed.

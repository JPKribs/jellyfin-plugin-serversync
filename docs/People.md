# People Syncing

People syncing copies person entities from a source Jellyfin server onto this server.

## Settings

* **Enable People Sync** turns people syncing on.
* **Images** syncs profile images for people, along with any backdrops a person carries.

## Images

When a person's images differ from the source, this server deletes its whole set for that image type and then downloads the full source set. That matters most for backdrops, where a person can hold several. Removing the old files first stops a leftover backdrop being picked up again by the next library scan and reinstating the difference.

Refresh parallelism and Deep Image Verification are shared across sync modules and live under **Configuration > Processing**.

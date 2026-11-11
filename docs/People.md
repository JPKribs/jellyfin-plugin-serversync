# People Syncing

People syncing copies person entities from a source Jellyfin server onto this server.

## Settings

* **Enable People Sync** turns people syncing on.
* **Images** syncs profile images for people, along with any backdrops a person carries.

## Images

When a person's images differ from the source, this server downloads the full source set for that image type, then replaces its own set for that type with it. If a download fails, the existing images are kept and the row is marked Errored. That matters most for backdrops, where a person can hold several. Replacing the whole set stops a leftover backdrop being picked up again by the next library scan and reinstating the difference.

**Refresh Parallelism** and **Deep Image Verification** apply to Metadata and People and live under **Settings > Processing**.

## Live changes between servers

When a server entry is in Push or Sync mode, an edit to a person made here is announced to that server as it happens, and that server pulls the person's metadata and images through the same code the scan uses. Only an edit raises a hint, and only for a person who already exists on the other server. As with metadata, the newest edit wins when the two servers disagree, in hints and in the full scan alike.

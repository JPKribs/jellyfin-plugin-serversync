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

## Live changes between servers

When a server entry is in Push or Sync mode, a metadata edit made here is announced to that server as it happens, and that server pulls the item through the same code the scan uses, images included. Only an edit raises a hint. Metadata a provider downloads and images a provider refreshes are left to the scheduled scan, so a server that fetches its own metadata after receiving a file does not push it over the other server's curated values the moment it arrives.

When the two servers disagree, the newest edit wins. Each server remembers where and when every tracked value was last edited, and a copy carries the version of the edit it came from rather than the time it landed. This applies to hints and to the full scan alike: against a peer that runs Server Sync, a scan keeps a local value that was edited more recently than the peer's, marks the row with that reason, and tells the peer to pull it. Against a server without the plugin the source still wins, as before.

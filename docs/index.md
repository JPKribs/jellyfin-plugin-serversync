# Server Sync

![Server Sync](assets/logo.png){ width="160" }

Server Sync is a Jellyfin plugin that keeps several Jellyfin servers in step. It syncs **Content**, **Watch History**, **Metadata**, **People**, **User Settings**, and **Playlists** across servers, on a schedule and, between servers that both run the plugin, as changes happen.

!!! warning "Use at your own risk"
    This plugin modifies live data on your Jellyfin server. While extensively tested, it is maintained for one person's use case and does not account for every server configuration. **Always keep backups of your Jellyfin data and configuration.** By using this plugin, you accept full responsibility for any data loss or issues that may occur.

## How It Works

Server Sync runs on your Local (destination) server and pulls data from one or more Source servers using standard Jellyfin APIs. You configure each server with its own library and user mappings, then two scheduled tasks, **Sync Content** and **Sync Information**, handle the synchronization. Content is matched by file path, so the plugin can track what needs to be downloaded, updated, or removed. No changes are required on a Source server that is only scanned.

When a Source server also runs Server Sync, the two can keep each other current between scheduled runs. A server entry in **Push** or **Sync** mode is told about changes as they happen, from a play or a favorite to a metadata edit, a person edit, or a new file, and pulls each change within seconds. User settings travel on the scheduled task only, since Jellyfin raises no event for them. The scheduled tasks remain the safety net. See [Live Changes](LiveChanges.md).

When several servers are configured, their order is their priority. If two servers offer the same item, the server higher in the list supplies it and the lower one only contributes what the higher one lacks. Reordering the list moves an item to the new winner on the next refresh without deleting anything already downloaded.

## Before You Start

**Matching libraries is the core rule.** Server Sync matches files by their path relative to each mapped library's root folder. Both servers must keep the same folder and file structure under that root.

| Source | Local | Matches |
| :--- | :--- | :--- |
| `/media/Movies/Film (2020)/Film (2020).mkv` | `/data/Movies/Film (2020)/Film (2020).mkv` | Yes, with roots `/media/Movies` and `/data/Movies` |
| `/media/Movies/Film (2020)/Film (2020).mkv` | `/data/Movies/Film/Film.mkv` | No, the names differ |

* A library must be a single folder.
* A renamed file on the source is usually downloaded again under its new name, and the old one is handled by **Removed files**.
* Interrupted downloads start over, and a changed file is found by its size.
* Every server in a pool runs the same Server Sync version. A server on another version is left alone until both match.

Next: [Installation](Installation.md).

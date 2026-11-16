# Server Sync

![Server Sync](assets/logo.png){ width="160" }

Server Sync is a Jellyfin plugin that keeps several Jellyfin servers in step. It syncs **Content**, **History**, **Metadata**, **People**, **Users**, and **Playlists** across servers, on a schedule and, between servers that both run the plugin, as changes happen.

!!! warning "Use at your own risk"
    This plugin modifies live data on your Jellyfin server. While extensively tested, I maintain this plugin for my personal use case and I do not account for every server configuration. **Always maintain backups of your Jellyfin data and configuration.** By using this plugin, you accept full responsibility for any data loss or issues that may occur.

## How It Works

Server Sync runs on this server and pulls data from one or more source servers using standard Jellyfin APIs. You set up each server with its own library and user mappings, and two scheduled tasks, **Sync Content** and **Sync Information**, do the syncing. Content is matched by file path, so the plugin can track what to download, update, or remove. A source that this server only pulls from needs no changes.

When a source also runs Server Sync, the two can keep each other current between scheduled runs. A server entry set to **Push** or **Sync** is told about changes as they happen, such as a play, a favorite, a metadata edit, a person edit, or a new file. It pulls each change once it settles, after 60 seconds by default, as set by **Send Changes After (seconds)**. User settings travel on the scheduled task only, since Jellyfin raises no event for them. The scheduled tasks remain the safety net. See [Live Changes](LiveChanges.md).

A library can also hold the source's files as **links** instead of copies. A link is a small `.strm` file that appears at once, uses almost no disk, and streams from the source when played. The source decides who may stream and shows each play on its dashboard. See [Links](Links.md).

When several servers are set up, their order on the **Servers** tab is their rank. For metadata, people, users, and playlists, **Precedence** decides which server speaks for an item that several hold. Content has no such setting. The order of the servers and the server a file first came from decide where a file comes from. Reordering the list moves an item to the new server on the next scan without deleting anything already downloaded. See [Sync Types](SyncTypes.md#several-servers).

## Before You Start

**Matching libraries is the core rule.** Server Sync matches files by their path under each mapped library's root folder. Both servers must keep the same folder and file structure under that root.

| Source | Local | Matches |
| :--- | :--- | :--- |
| `/media/Movies/Film (2020)/Film (2020).mkv` | `/data/Movies/Film (2020)/Film (2020).mkv` | Yes, with roots `/media/Movies` and `/data/Movies` |
| `/media/Movies/Film (2020)/Film (2020).mkv` | `/data/Movies/Film/Film.mkv` | No, the names differ |

* A library must be a single folder.
* A file renamed or given a new container on the source follows to its new name here when it stays in the same folder. A file moved to another folder is downloaded again, and the old one follows **Removed files**.
* A download that drops partway resumes on retry when the source supports it. One cut off by a cancelled run or a restart starts over.
* A changed file is found by its size.
* Servers in a pool need compatible Server Sync protocols, not the same version. A server whose protocol does not overlap is left alone until it does.

Next: [Installation](Installation.md).

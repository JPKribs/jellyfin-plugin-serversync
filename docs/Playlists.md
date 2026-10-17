# Playlist Syncing

Playlist syncing copies the playlists of mapped users between servers. Users without a user mapping keep their playlists to themselves. It needs a user mapping on the Servers tab before it can be turned on. It runs as the last step of the scheduled **Sync Information** task, and between servers that both run Server Sync it also travels as hints the moment a playlist changes.

## Settings

* **Enable Playlist Sync** turns playlist syncing on.
* **Sharing** shares each playlist here with the same mapped users it is shared with on the source, and copies whether it is open to everyone. This needs the exact owner, so it only applies when Server Sync on the source reports it.

## How Items Are Matched

A playlist holds items by their id, and ids differ between servers. Each item is found here by its file path instead, translated through the library mappings of that server the same way Content Sync places files. An item outside every mapped library, or one that has not been downloaded yet, is left out of the local playlist, and it is added on a later run once it is here. The order of the items is kept, and an item that appears twice stays twice.

Items the local owner cannot see here are left out too. When none of a playlist's items are here yet, the local playlist is not created until some arrive. When a playlist already copied here suddenly resolves no items at all, for example during a library rescan, it is left unchanged for that run rather than emptied.

## Owners

Every playlist belongs to one user. A copied playlist belongs to the local user mapped from the source owner, so playlists only travel for users that have a user mapping on that server.

* **With an administrator's key and Server Sync on the source**, the source reports each playlist's owner, shares, and open access exactly. Every playlist owned by a mapped user is copied.
* **With an administrator's key and no Server Sync there, or a whitelist that refuses it**, Jellyfin does not report owners over its API, so no playlists are copied and the reason is logged.
* **With a standard user's key**, only the playlists that user can see are copied, whether they own them or they are shared with them, and every copy belongs to that user's local account here. Shares and open access are not copied, and a local playlist that belongs to another user is never changed.

## Directions

| Direction | What happens |
| :--- | :--- |
| Pull | The other server's copy always wins. Local edits to a copied playlist are replaced on the next run, and a playlist deleted there is deleted here, unless that server lists no playlists at all for the mapped users, which is taken as a failed read. Changes arrive as they happen when that server pushes to this one, otherwise on the schedule |
| Push | This server's copy always wins on the other server. Every edit and deletion is sent as it happens |
| Sync | The newest edit wins on either side. Edits and deletions travel both ways as they happen. The scheduled run creates copies that are missing and replaces a copy only when the other server holds a newer recorded edit. It never deletes, since deletions travel as hints |

Making a playlist, adding, removing, or moving items, renaming it, changing who it is shared with, and deleting it each send a hint. The other server then reads the whole playlist from this one and replaces its copy, or deletes its copy for a deletion. Changes made by Server Sync itself are never announced back, so a pair does not echo.

Server Sync records which local playlists are copies in its database, and tags them as well, so a database reset rebuilds the record from the tags. A copied playlist remembers the playlist it was first made as, so both copies stay one playlist across any number of servers. A local playlist that was never copied is only touched when the other server holds a copy made from it, and a hint or a scheduled run only changes or deletes a local playlist that belongs to the mapped owner. Every change holds the playlist lock, so a hint and the scheduled run never create the same copy twice.

See [Permissions](Permissions.md) for what each kind of key can read.

Playlists only travel between servers running the same Server Sync version.

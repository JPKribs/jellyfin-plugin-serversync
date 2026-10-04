# History Syncing

History syncing copies watch state from a source Jellyfin server onto this server using your user mappings.

## Settings

* **Enable History Sync** turns watch history syncing on.
* **Played / Unplayed Status** syncs whether each item is marked played.
* **Playback Position** syncs the resume position for partly watched items.
* **Play Count** syncs how many times each item was played.
* **Last Played Date** syncs the date each item was last played.
* **Favorites** syncs which items are marked as favorites.
* **Negotiate With Source Server** writes the merged history back to the Source Server as well. See below.

## Two Way History

By default history flows one way. The Source Server's watch state is merged with this server's and the result is written here only. With **Negotiate With Source Server** turned on, the merged result is also written to the Source Server, so a play, a resume position, or a favorite made on either server reaches the other.

This needs Server Sync installed on the Source Server too. This server calls the Source Server's plugin at `/ServerSync/Peer/History` using the same API key the other modules use, and the Source Server accepts the call only because Jellyfin itself validates that key as an administrator credential. The plugin adds no login of its own.

Each write is a proposal. This server tells the Source Server what state it last read there and what state it wants both servers to hold. The Source Server writes only if its live state still matches the reading. If someone played the item there in the meantime, the Source Server reports the change back instead of accepting the write, this server merges again with the fresh state, and tries once more. A row that cannot settle after that is marked Errored with the reason.

Once both servers agree on a state, that state is remembered as the base for the next merge. From then on a change made on one server alone always wins over a server that has not changed, which is what lets a favorite be removed or an item be marked unplayed from either side. When both servers changed since the last agreement, the usual rules break the tie: the most recent play wins, the higher play count wins, and a favorite wins over an unfavorite.

Turning the setting off leaves the remembered base in place but stops writing to the Source Server. The merge keeps using the base, so history read from the Source Server still cannot overwrite a change made here.

## Live changes between servers

When a server entry is in Push or Sync mode, a change made here is announced to that server as it happens instead of waiting for the next scan. This server watches its own user data for plays, resume positions, and favorites, gathers a few seconds of edits to one item into one notice, and queues a hint for every Push or Sync server whose mappings cover that user and that library. A hint says what changed and where, never the value. The other server runs Server Sync too, lists this server as a Pull or Sync server, and pulls the change from here through the same code the scan uses, including the negotiation above, so hints and scans can never disagree.

Hints are stored on both ends until the work is done. A server that is down, refuses the key, or lacks the plugin is retried with backoff and shown on the dashboard with the reason, and a hint the other server accepted but never finished is checked and sent again. Nothing is ever given up on, and the scheduled Sync Information task still runs the full comparison as the safety net for anything the hints missed.

A write made because of a hint raises no hint of its own, and a value that already matches is never written, so a pool of three or more servers settles without echoing. When two servers have never exchanged a particular item, the newer edit wins on origin versions. After that the agreed base recorded on both sides makes every merge three way.

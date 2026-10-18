# Live Changes

Servers that both run Server Sync tell each other about changes as they happen. A change is sent as a hint that says what changed, never the value, and the other server pulls the value itself. Hints wait until an item has gone untouched for **Send Changes After**, are retried until delivered, and show on the **Queue** view.

| Change Queue | On a phone |
| :--- | :--- |
| ![Change Queue](screenshots/Queue.png) | ![Change Queue on a phone](screenshots/Queue%20Phone.png) |

* **No echoes.** A write made because of a hint is never announced back, and a value that already matches is never written. An item a peer changed that is announced more than ten times in ten minutes is held back for the scheduled task. Deletions always go out.
* **Conflicts.** History merges three way against what the two servers last agreed on. Everything else goes to the newest edit.
* **Pairing.** Servers that list each other share a secret, so one server cannot pretend to be another.
* **Only what is wanted.** A server only announces what the other side maps and has turned on.
* **Same version only.** Changes for a server on another Server Sync version wait in the queue until both match.

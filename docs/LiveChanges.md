# Live Changes

Servers that both run Server Sync tell each other about changes as they happen. A change is sent as a hint that says what changed, never the value, and the other server pulls the value itself. Hints wait until an item has gone untouched for **Send changes after (seconds)**, are retried until delivered, and show on the **Queue** view. A file that leaves the library waits ten minutes instead, so the new file of a rename or a new container goes first, and shows in the queue as Removed meanwhile.

| Change queue | Change queue on a phone |
| :---: | :---: |
| ![Change queue](screenshots/Queue.png) | ![Change queue on a phone](screenshots/Queue%20Phone.png) |

* **No echoes.** A write made because of a hint is never announced back, and a value that already matches is never written. A change announced more than ten times in ten minutes, each time right after it was applied here, is held for the scheduled scan. Deletions always go out.
* **Conflicts.** History merges three way against what the two servers last agreed on. Everything else goes to the newest edit, and a scan follows **Precedence**.
* **Pairing.** Servers that list each other share a secret, so one server cannot pretend to be another.
* **Only what is wanted.** A server announces only what the other side maps and has turned on.
* **Compatible versions only.** Servers on different Server Sync versions exchange changes while their protocols overlap. Changes for a server whose protocol does not overlap wait in the queue until it does. See [Versioning](Versioning.md#mixing-versions-in-a-pool).

## Activity Log

Changes from other servers are summed on the dashboard's **Activity** page rather than listed one by one. Once no change has arrived for 30 seconds, or every 5 minutes during a long sync, one entry is written per outcome:

| Name | Overview |
| :--- | :--- |
| Items synced | Synced 12 items from source-a (9) and source-b (3) |
| Items not synced | Did not sync 3 items from source-a (2) and source-b: filtered out (2) and ignored here |
| Items not applied yet | 4 items from source-a could not be applied yet and will be retried |

A server's own events are written as they happen, with the server in the overview. "Changes paused" names a server that cannot take changes, "Changes rejected" one that refused a request as malformed, and "Changes resent" one that lost changes it had accepted. Each change is still in the server log, and in the Change queue on the **Sync** tab.

A paused server is left alone until the pause runs out. Changing its address, key, or direction on the **Servers** tab ends the pause, so the waiting changes go out on the next pass.

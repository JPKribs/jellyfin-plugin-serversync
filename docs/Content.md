# Content Syncing

Content syncing mirrors missing or updated media files from a source Jellyfin server onto this server. A library mapping can instead hold them as **links** that stream from the source, set by its **Storage**. See [Links](Links.md). Files are matched by their path under each mapped library's root folder, so both servers must keep the same folder and file structure under that root. A file renamed or moved on the source usually gets a new item id there, so it is downloaded again at its new path, and the old copy is handled by **Removed files**. A file replaced by one of the same name in another container, as `Film.mkv` by `Film.mp4`, is handled the same way but sooner: as soon as the new file is down and the source answers that the old one is gone, the old copy follows **Removed files** on that run instead of waiting for the next full scan, and the images and trickplay the two share stay with the new file.

## Settings

Under **Settings > Content**:

* **Enable Content Sync** turns content syncing on.
* **Detect Updated Files** checks each synced file on disk against the source's size on a full scan, and downloads it again when it differs or is missing. A size change on the source is always picked up and follows **Changed files**.
* **Mirror Synced Collections** recreates collections whitelisted in a library filter, with their synced items. See [Collection Syncing](#collection-syncing).
* **Include Companion Files** also downloads external subtitle files with each media file, and beside each link. Other companions (NFO files, local artwork, external audio tracks) have no download route in the Jellyfin API and are skipped. A link gets an `.nfo` of its own, written from the source's details.
* **Skip Items Watched By** skips items every user picked there has watched. Picking no one syncs everything.
* **Approvals** sets an action for each kind of change. **New files** download automatically (Enabled), wait for approval (Require Approval), or are left alone (Disabled), and **Changed files** replace the same three ways. A file already in the library at the path a new item would take is kept as the synced copy when it is the source's size. Any other file there is not this plugin's, so replacing it follows **Changed files**.
* **Removed files** decides what happens to a local file once its item is gone from the source. **Keep** leaves it. **Delete** deletes it on the next Sync Content run that does a full scan (**Full Scan Every (hours)** under Processing, 24 by default), but still asks first whenever the plugin cannot be sure the file should go. **Delete After Approval** holds every deletion as Pending Deletion until you approve it. **Ignore** keeps the files and, once everything in a movie or show folder is gone from the source, writes an empty `.ignore` file in that folder so Jellyfin hides it on its next scan. A season is never hidden on its own, so a show that still has episodes on the source stays as it is. A folder that already has a `.ignore` is left alone. A hidden title shows again when it comes back on the source, or when Removed files is set to anything else. Switching away from Delete cancels deletions that were still waiting. A link is not a local file and does not follow this setting: it is always removed once no server offers its file. See [Links](Links.md#when-the-file-is-gone).
* **Enable Recycling Bin** moves deleted or replaced files to a recycling bin instead of removing them for good. **Recycling Bin Path** is its folder.
* **Keep For (days)** sets how many days files stay in the recycling bin before permanent removal. Only files and trickplay folders the plugin put there expire. They carry the time they were recycled in their name, and anything else in the folder is left alone. A file from deep inside a library gets a shortened name, ending in its own file name and that time, so the move never fails on the filesystem's name length limit.
* **Remove Empty Folders** deletes parent folders that become empty after a file is removed.
* **Downloads At Once** limits how many files download at once.
* **Temporary Folder** is where files download before they move into place. Empty uses a `serversync` folder in Jellyfin's cache folder.
* **Minimum Free Space (GB)** blocks downloads when free space falls below this many gigabytes. Downloads running at the same time hold space for each other, so several large files cannot together fill a disk that each would fit on alone. A file that only fits once another download finishes waits for it.
* **Take Link Images From the Source** and **Stream Direct Play of Links Per User**, both on by default, and the link cache settings are covered in [Links](Links.md#the-link-cache).

Elsewhere:

* **Storage** and **Library Filter**, on each library mapping in the Storage tab. Storage holds the source's files as full **Copy** files, the default, or as **Link** files that stream from the source. See [Links](Links.md). The filter is covered below.
* **Downloads** under **Settings > Bandwidth** caps the total rate of every download to this server, in Mbps, where 0 means no limit. With **Scheduled Speed** on, the **Scheduled** rate applies during the set hours, in the server's time. A download already running follows the hours as they begin and end. Downloads into the link cache are held to it too.
* **Failed Item Retries** under **Settings > Processing** sets how many times a failed download or item is tried within one run. One that still fails is tried again on the next run, and is raised on the Activity page once it has failed for a day. See [When something goes wrong](#when-something-goes-wrong). A retry picks up where the failed attempt stopped when the source supports it, and starts over if the file changed on the source in between.

A `.strm` on the source is never copied or linked, whether it is one of the source's links or one a user made.

!!! note "Sizes must match exactly"
    A local file counts as the source's file only when its size is exactly the source's. Servers on different operating systems or file systems can report a size slightly differently, for example after a file's tags are rewritten, which makes the file download again. If a file keeps downloading between two such servers, compare the sizes each server reports for it.

## Collection Syncing

Each library mapping's **Library Filter**, on the Storage tab, is **Allow All**, or a **Whitelist** or **Blacklist** of files, collections, and playlists. It applies to content, history, and metadata alike.

A source **collection** can be used as a sync list: on the Storage tab, switch a
library mapping's **Library Filter** to **Whitelist** mode, open the item picker, flip the browse toggle to
**Collections**, and select the collection. From then
on:

* Everything in the collection syncs through the normal content pipeline.
  A Series in the collection keeps picking up **new episodes** automatically,
  because membership is resolved on every refresh.
* Anyone with collection-management permission on the source can add or remove
  items from the collection in the normal Jellyfin UI, no plugin access
  needed. Removing an item removes it from the whitelist, and its local
  copy waits for deletion approval when **Removed files** is Delete or
  Delete After Approval, since a membership change is not a removal from the source.
* A collection can span libraries (movies + shows). Each mapping syncs only
  the members that live under its own source root. Whitelist the same
  collection under a mapping for each library it draws from.
* With **Mirror Synced Collections** enabled (default), the last step of
  **Sync Content** recreates the collection on this server containing the local copies of
  its synced members, so it appears in your local UI just like on the source.
  Mirrored collections are tagged and their membership tracks the source. A
  collection that disappears from the source stops being updated but is never
  emptied automatically.

### Playlists

Playlists work the same way as collections in both directions, whitelist a
playlist to sync its members, blacklist one to exclude them. Membership is
re-resolved every refresh. Two differences from collections:

* Playlists are user-scoped on the source. Member resolution uses the
  account from the server entry's **Sign In**, so that user must be
  able to see the playlist (own it or have it shared). An entry set up with a
  pasted API key has no user. Switch it to **Sign In** to give it one.
* Playlists are not mirrored locally (they have owners, which a mirror can't
  faithfully reproduce). Use a collection when you want the container to
  appear on this server too.

### Blacklisting a collection

In **Blacklist** mode a selected collection excludes its members: the library
syncs everything *except* what's in the collection (membership is re-resolved
every refresh, so adding an item to the collection excludes it on the next
run). Items dropped by a blacklist edit go through **deletion approval** even
when **Removed files** is Delete. Narrowing scope is a config
change, not a source-side removal. If the collection can't be read during a
refresh, that mapping's discovery is skipped for the run rather than syncing
the excluded items.

Filters are **per mapping**: blacklisting a collection in one library's
mapping has no effect on any other mapping, an item reachable through
another enabled mapping still syncs there.

### Interaction with Skip Items Watched By

**Skip Items Watched By** composes with every filter mode,
including collections. Whitelisted collection members (episodes included) that
every selected user has played are marked Ignored instead of downloading.
Blacklisted-collection exclusions take precedence (excluded items are never
tracked at all). The setting only prevents future downloads, already-synced
items are never removed because someone watched them, and Ignored is sticky
until you un-ignore the row in the sync table.

Safety notes: a collection (or any whitelist entry) that no longer exists on
the source fails the refresh loudly instead of being treated as empty, remove
the dead entry from the whitelist to resume pruning. An intentionally emptied
whitelist still reconciles normally.

## Live changes between servers

When a server entry is in Push or Sync mode, a file that appears here is announced to that server as soon as this server's library has picked it up, and that server downloads it through the same code Sync Content uses, respecting its own approval mode and library mappings. When several servers offer the same file, the earliest entry in that server's list provides it. Downloads run in their own lane so a large file never delays a history or metadata change arriving at the same time, and the receiving library is asked to refresh that file's folder once the file is in place. A file that leaves this server's library is announced the same way. A server that holds a link to it moves the link to another server that holds the file, or removes the link when none does. A server that holds a copy of it keeps the copy until its next scan, which settles it by **Removed files**. A `.strm` is never announced, so a link is never passed on to a third server.

A server also keeps a record of the files that left its library, for 30 days. A server reading it asks for that record on every scan, so a removal is settled on the next scan rather than the next full scan, whether or not the announcement arrived. The full scan remains as the check on both.

## When something goes wrong

Server Sync is meant to be set up and left alone. What fails is tried again without anyone asking, and only what cannot fix itself is brought to an administrator.

* **Every failed item is retried on the next run**, for every sync type, once the tries set by **Failed Item Retries** are used up. A full disk, a source that was down, or a network that dropped clears on its own.
* **An item that has failed on every run for a day is raised once** on Jellyfin's **Activity** page, as "Server Sync Needs Attention", with how many items of each kind are failing and one reason as an example. It goes on being retried. Filter the Sync tab by Errored to see each one.
* **A link that is missing or out of date is written again** at the start of every Sync Content run. A link holds nothing, so it is never repaired, only replaced.
* **Copies tracked before origins were recorded are marked once.** After the update, each is looked at: one that never went through a download was on this server already and is marked as its own, and one that did is marked with the server it came from.
* **A file that was already here is this server's own.** A file on this server before it was matched to another server's is tracked, and stays when that server removes its file, whatever **Removed files** is set to. Only files this server downloaded follow **Removed files**.
* **When both servers already hold a title as different files, the direction decides.** A **Pull** server is a source to mirror: its file replaces this server's by **Changed files**, and is from then on a downloaded file like any other. A **Sync** server is an equal: each keeps its own file, the row is settled and says the two differ, and only what each lacks is filled in. Queue the row on the Sync tab to take the other server's file by hand. A [Link library](Links.md#versions) keeps this server's own file the same way, whatever the direction.
* **A copy is kept above all else.** A download interrupted while replacing a file puts the old file, its subtitles, artwork, and `.nfo` back on the next run. A download that does not match the size the source reported never replaces a file that is already here.
* **A server that does not answer holds only its own files.** Removed files are still cleaned up for the servers that answered. The same goes for a server whose library filter names a collection it no longer has.
* **A full disk does not stop removals.** Files waiting to be removed are removed before the run gives up for lack of space.
* **Leftovers are cleared.** A half-copied download left in a library folder by a crash is removed after a day, and removing a server removes the links that pointed at it. Its copies are real files and stay.

## Servers that sync with each other

When servers pass files between each other, each copy remembers the server it first came from, its **origin**. A server that downloads a file records the origin its source names for it, or the source itself when the file is the source's own. The rules below apply to files a library copies. A **Link** library works the other way round: a film is there for as long as any server it reads from holds a real copy, so every holder's offer counts and a link is written to it. See [Links](Links.md).

* A file that first came from this server is this server's to decide about. Another server's copy of it is never downloaded back, so a film removed here stays removed, and the other servers remove their copies under their own **Removed files** setting.
* A file that first came from another server this one syncs the same folder from is that server's to decide about. A copy of it on a third server is not taken as a file of its own, so once the origin removes the film, its copies on the other servers stop keeping each other in place.
* Any other offer counts. A file still reaches a server through one in between when that server does not sync from the origin, or when the origin does not answer.
* Change or replace a file on the server it first came from. A copy changed on another server is that server's local edit, and its next sync puts the origin's file back when **Detect Updated Files** is on.

A server goes on naming the origin of a copy for a day after removing it, since Jellyfin lists a removed file until it next scans the folder.

An origin is recorded when a file is downloaded. A file that was already on a server when it was matched to another server's has none recorded, and counts as that server's own. Every server in the pool must run a release that records origins for a file's origin to be passed along. A server that does not is read as if every file on it were its own.

## Deleting From the Sync Tab

Deleting a file from the Sync tab removes it from disk, or moves it to the recycling bin, and keeps its row as **Ignored**, so the next run does not download it again. Queue the row to download it again.

## Library refresh

After a Sync Content run that downloaded or removed files, Jellyfin is asked to refresh just the folders that changed, the same way its library monitor reports a change it saw. A run that touched many folders, such as a first run that copies a whole library, runs one full library scan instead. So does a run into a library Jellyfin has not scanned since it was empty, since Jellyfin does not record an empty library's folder and would drop the request. A file that arrives through a hint queues a library scan in that case.

# Content Syncing

Content syncing mirrors missing or updated media files from a source Jellyfin server onto this server.

## Settings

* **Enable Content Sync** turns content syncing on.
* **Temp Download Path** sets the staging folder where files download before they move to the final location.
* **Include Companion Files** also downloads external subtitle files with each media file. Other companions (NFO files, local artwork, external audio tracks) have no download route in the Jellyfin API and are skipped.
* **Max Concurrent Downloads** limits how many files download at once.
* **Max Download Speed** caps the download rate, where 0 means unlimited.
* **Download New Content** chooses whether brand new items download automatically or wait for approval.
* **Replace Existing Content** chooses whether changed items replace automatically or wait for approval.
* **Delete Missing Content** chooses whether items removed from the source are deleted locally automatically or wait for approval.
* **Detect Updated Files** requeues files whose size or date no longer matches the source.
* **Enable Bandwidth Scheduling** uses an alternate download speed during a set window of hours.
* **Scheduled Download Speed** sets the rate used during the scheduled hours.
* **Minimum Free Disk Space** blocks downloads when free space falls below this many gigabytes.
* **Enable Recycling Bin** moves deleted or replaced files to a recycling bin instead of removing them for good.
* **Recycling Bin Path** sets the folder where soft deleted files are kept.
* **Recycling Bin Retention** sets how many days files stay in the recycling bin before permanent removal.
* **Remove Empty Folders** deletes parent folders that become empty after a file is removed.
* **Max Retry Count** sets how many times a failed download retries before it errors.
* **Skip Items Watched by All Selected Users** skips items already watched by every selected user.

## Collection Syncing

A source **collection** can be used as a sync list: switch a library mapping to
**Whitelist** mode, open the item picker, flip the browse toggle to
**Collections**, and select the collection. From then
on:

* Everything in the collection syncs through the normal content pipeline —
  a Series in the collection keeps picking up **new episodes** automatically,
  because membership is resolved on every refresh.
* Anyone with collection-management permission on the source can add or remove
  items from the collection in the normal Jellyfin UI — no plugin access
  needed. Removing an item removes it from the whitelist, and (per your
  **Delete Missing Content** mode) frees the space locally.
* A collection can span libraries (movies + shows). Each mapping syncs only
  the members that live under its own source root — whitelist the same
  collection under a mapping for each library it draws from.
* With **Mirror Synced Collections** enabled (default), the *Sync Collections*
  task recreates the collection on this server containing the local copies of
  its synced members, so it appears in your local UI just like on the source.
  Mirrored collections are tagged and their membership tracks the source; a
  collection that disappears from the source stops being updated but is never
  emptied automatically.

### Playlists

Playlists work the same way as collections in both directions — whitelist a
playlist to sync its members, blacklist one to exclude them; membership is
re-resolved every refresh. Two differences from collections:

* Playlists are user-scoped on the source. Member resolution uses the
  account from the plugin's **Generate Token** flow, so that user must be
  able to see the playlist (own it or have it shared). If you configured the
  plugin with a pasted API key instead, sharing the playlist with the
  authenticating user or re-running token generation gives it a user context.
* Playlists are not mirrored locally (they have owners, which a mirror can't
  faithfully reproduce). Use a collection when you want the container to
  appear on this server too.

### Blacklisting a collection

In **Blacklist** mode a selected collection excludes its members: the library
syncs everything *except* what's in the collection (membership is re-resolved
every refresh, so adding an item to the collection excludes it on the next
run). Items dropped by a blacklist edit go through **deletion approval** even
when Delete Missing Content is set to automatic — narrowing scope is a config
change, not a source-side removal. If the collection can't be read during a
refresh, that mapping's discovery is skipped for the run rather than syncing
the excluded items.

Filters are **per mapping**: blacklisting a collection in one library's
mapping has no effect on any other mapping — an item reachable through
another enabled mapping still syncs there.

### Interaction with the watched-by-all filter

**Skip Items Watched by All Selected Users** composes with every filter mode,
including collections. Whitelisted collection members (episodes included) that
every selected user has played are marked Ignored instead of downloading;
blacklisted-collection exclusions take precedence (excluded items are never
tracked at all). The filter only prevents future downloads — already-synced
items are never removed because someone watched them — and Ignored is sticky
until you un-ignore the row in the sync table.

Safety notes: a collection (or any whitelist entry) that no longer exists on
the source fails the refresh loudly instead of being treated as empty — remove
the dead entry from the whitelist to resume pruning. An intentionally emptied
whitelist still reconciles normally.

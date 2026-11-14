# Sync Types

Two scheduled tasks do the work, and two daily housekeeping tasks, **Empty Recycling Bin** and **Clean Temporary Directory**, tidy up after them. **Sync Content** refreshes and downloads files, handles removed files, and mirrors collections. **Sync Information** compares and applies metadata, people, user settings, and history, then copies playlists.

| Type | Matched by | Live with Push or Sync | Details |
| :--- | :--- | :--- | :--- |
| Content | File path under the library root | New files | [Content](Content.md), and [Links](Links.md) for libraries that stream instead of copy |
| History | File path and user mapping | Yes, merged three way | [History](History.md) |
| Metadata | File path | Yes, newest edit wins | [Metadata](Metadata.md) |
| People | Name | Yes, newest edit wins | [People](People.md) |
| Users | User mapping | No, scheduled only | [Users](Users.md) |
| Playlists | Owner mapping, items by file path | Yes, including deletions | [Playlists](Playlists.md) |

Content, History, Metadata, People, and Users each have a table on the **Sync** tab, and each row opens to show what was found and what happens next. Playlists have no table. Their live changes show on the **Queue** view.

## Several servers

A server can pull from one parent, pull from several ranked parents, sync with one partner, sync with several, and sync with partners that have partners of their own it has never heard of. The same rules cover all of them.

**Who is heard.** When more than one server holds the same thing, one of them speaks for it on each scan. Each sync type but history has a **Precedence** setting:

| Setting | Who speaks |
| :--- | :--- |
| Newest | The server that changed it last, wherever it ranks. Rank decides only when nothing says which is newer: before any change has been recorded, when two servers hold the same change, and against a server without Server Sync |
| Ranked | The highest server on the Servers tab that holds it, whatever the others hold. A lower server only speaks for what the ones above it do not have |

| Type | How the newest change is known |
| :--- | :--- |
| Metadata, People | Each hand made edit is stamped with the server that made it and when. A copy carries the stamp of the edit it came from, so a copy never looks newer than its original |
| Playlists | The same stamps, and a deletion is stamped too |
| Users | Jellyfin raises nothing when a user's settings are saved, so a server is known to have changed them when they differ from what was last seen of it |
| History | Never one server over another. Each server's history of a file is merged in, see [History](History.md) |

**One hop.** A server announces the changes made on it, and never passes on one it received. A change reaches every server that syncs with the server it was made on as it happens, and a server further away takes it on its next scan of a server that has it, still stamped with where it was made.

**No loops.** A change cannot go round a ring of servers and come back:

* Only the server a change was made on announces it.
* Applying a change raises no announcement.
* A server offered a change it already holds, or an older one, writes nothing.
* Something announced ten times in ten minutes, each time right after it was applied here, is held, and the scheduled scan carries it.

**Self correcting.** Announcements are a shortcut. The scheduled scan compares everything with every server and settles whatever an announcement missed, including a change made on the server every other server ranks last.

| Sync Table | Item Detail |
| :--- | :--- |
| ![Sync Table](screenshots/Sync%20Table.png) | ![Item Detail](screenshots/Item%20Detail.png) |

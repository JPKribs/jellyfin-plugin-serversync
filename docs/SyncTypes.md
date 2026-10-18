# Sync Types

Two scheduled tasks do the work. **Sync Content** refreshes and downloads files, handles removed files, and mirrors collections. **Sync Information** compares and applies metadata, people, user settings, and history, then copies playlists.

| Type | Matched by | Live with Push or Sync | Details |
| :--- | :--- | :--- | :--- |
| Content | File path under the library root | New files | [Content](Content.md) |
| History | File path and user mapping | Yes, merged three way | [History](History.md) |
| Metadata | File path | Yes, newest edit wins | [Metadata](Metadata.md) |
| People | Name | Yes, newest edit wins | [People](People.md) |
| Users | User mapping | No, scheduled only | [Users](Users.md) |
| Playlists | Owner mapping, items by file path | Yes, including deletions | [Playlists](Playlists.md) |

Each type has its own table on the **Sync** tab, and each row opens to show what was found and what happens next.

| Sync Table | Item Detail |
| :--- | :--- |
| ![Sync Table](screenshots/Sync%20Table.png) | ![Item Detail](screenshots/Item%20Detail.png) |

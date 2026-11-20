# User Syncing

User syncing copies account settings from a source Jellyfin server onto mapped local users. With a Sync server they travel both ways.

An entry that holds a standard user's key syncs only that user's own settings. Mappings to other users are skipped. See [Permissions](Permissions.md).

## Settings

* **Enable user sync** turns user settings syncing on.
* **User policy** syncs each user's permissions and access policy, including whether the account is disabled. Administrator rights are never copied, since each server's administrator grants them there. Library, channel, and device access lists stay local too.
* **User configuration** syncs each user's display and playback preferences.
* **Profile image** syncs each user's profile picture. A scan downloads the source's picture only when its tag has changed, or on every scan when **Deep image verification** is on.
* **Precedence** chooses which server speaks for a user that several servers map onto the same local user. See [Several servers](SyncTypes.md#several-servers).
* **User conflicts** chooses what a user's permissions become when they were changed on this server and on a Sync server since the two last agreed.

![Several servers](screenshots/settings/Several%20Servers.png)

## Between Sync Servers

From a Pull server, settings only ever arrive. With a Sync server they travel both ways, and each scan settles a user from which of the two changed the settings since they last agreed:

| What changed | What happens |
| :--- | :--- |
| Only the other server | Its settings are applied here |
| Only this server | This server's settings stay, and the other server takes them on its own scan |
| Both | **User conflicts** decides |

| User conflicts | The user's permissions become |
| :--- | :--- |
| Other server | The other server's |
| Permissive | The more generous of the two for each permission, so the user ends with everything either server allowed |
| Restrictive | The stricter of the two for each permission, so the user ends with only what both servers allowed |

More generous means a switch that grants something is on, a switch that takes something away is off, a limit is higher or absent, a list of what is blocked is shorter, and a list of the only things allowed is longer or empty. Both servers reach the same answer from the same two values. A permission with no more and no less, and display and playback preferences, take the other server's value. A disabled user stays disabled under either rule, so a lockout on one server is never undone by the other.

With several servers, one speaks for a user on each scan. It is the highest ranked one that changed the settings since this server last looked. When none has, the server whose settings were applied last goes on speaking, so a lower ranked server's change is not undone by a higher one with nothing new.

## Live Changes Between Servers

User settings are never sent as they happen. Jellyfin raises no event for policy and configuration saves, so the scheduled **Sync Information** task carries them.

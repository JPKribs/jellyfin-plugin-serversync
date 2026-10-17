# User Syncing

User syncing copies account settings from a source Jellyfin server onto mapped local users.

An entry that holds a standard user's key only syncs that user's own settings. Mappings to other users are skipped. See [Permissions](Permissions.md).

## Settings

* **Enable User Sync** turns user settings syncing on.
* **User Policy** syncs each user's permissions and access policy.
* **User Configuration** syncs each user's display and playback preferences.
* **Profile Image** syncs each user's profile picture.

## Live changes between servers

User settings are never sent as they happen. Jellyfin raises no event for policy and configuration saves, so the scheduled Sync Information task carries them.

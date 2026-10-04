# User Syncing

User syncing copies account settings from a source Jellyfin server onto mapped local users.

## Settings

* **Enable User Sync** turns user settings syncing on.
* **User Policy** syncs each user's permissions and access policy.
* **User Configuration** syncs each user's display and playback preferences.
* **Profile Image** syncs each user's profile picture.

## Live changes between servers

When a server entry is in Push or Sync mode, a change to a mapped user's policy, configuration, or profile image here is announced to that server and applied there through the same code the scan uses. Jellyfin raises an event for some user changes and none for policy or configuration saves, so mapped users are also checked every thirty seconds against a snapshot. As with metadata, the newest edit wins when the two servers disagree.

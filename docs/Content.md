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

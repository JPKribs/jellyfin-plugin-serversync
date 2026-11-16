# Installation

Server Sync needs Jellyfin 12.4 or later. A server on an older Jellyfin does not see Server Sync 12.4.16.1 in its catalog and keeps the last release built for it.

## Add the Plugin Repository

1. Open Jellyfin and go to **Dashboard > Plugins > Repositories**.
2. Select **Add Repository**.
3. Enter this repository URL and select **Save**:

```
https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-serversync/master/manifest.json
```

## Install the Plugin

1. Open the **Catalog** tab in the Plugins section.
2. Find **Server Sync** and select **Install**.
3. Wait for the installation to complete.

## Restart Jellyfin

Restart Jellyfin completely and wait for it to start.

## Check It Works

Go to **Dashboard > Plugins > Server Sync** and confirm the configuration page loads.

## Updating

A server on 12.4.13.0 or later updates from the catalog as usual. A server on anything older must run 12.4.13.0 for a day first, since 12.4.16.1 does not convert databases or settings written before 12.4.13.0. See [Updating From an Older Release](Versioning.md#updating-from-an-older-release).

Servers in a pool can run different Server Sync versions as long as their wire protocols overlap, so they can be updated one at a time. See [Versioning](Versioning.md). A server whose protocol does not overlap holds its changes until it does.

Next: [Setup](Setup.md).

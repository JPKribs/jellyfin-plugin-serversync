# Installation

## Add the Plugin Repository

1. Open Jellyfin and go to **Dashboard → Plugins → Repositories**.
2. Click **Add Repository**.
3. Enter this repository URL and click **Save**:

```
https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-serversync/master/manifest.json
```

## Install the Plugin

1. Open the **Catalog** tab in the Plugins section.
2. Find **Server Sync** and click **Install**.
3. Wait for the installation to complete.

## Restart Jellyfin

Restart your Jellyfin server completely and wait for it to start up.

## Check It Works

Go to **Dashboard → Plugins → Server Sync** and confirm the configuration page loads.

Install the same version on every server in a pool. Servers on different versions hold their changes for each other until both match.

Next: [Setup](Setup.md).

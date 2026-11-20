# Screenshot Kit

Takes every image in `docs/screenshots/`. It serves the plugin's real pages with jellyfin-web's real styles and a stand-in server that holds a generic pool, and drives headless Chromium. No Jellyfin server runs.

The pool is this server, Living Room, which syncs with Basement, pulls from Cabin, and pushes to Office. Every name, path, and count in it is made up.

| File | Does |
| :--- | :--- |
| `server.py` | Serves the plugin pages, Base from the NuGet cache, jellyfin-web, and the plugin's strings, the way Jellyfin's `configurationpage` route and the plugin's Translations route do |
| `index.html` | Loads jellyfin-web's stylesheets in load order, draws the dashboard header, and mounts one tab |
| `upgrade.js` | Adds the classes and markup Jellyfin's `emby-*` elements add on upgrade, so the real CSS applies |
| `stub.js` | A stand-in `ApiClient`, `Dashboard`, and `LibraryMenu` that answers every route the pages call with the generic pool |
| `shoot.mjs` | Opens one page in Chromium at 2x with the dashboard header hidden |
| `shots.mjs` | Sets up each subject and writes its PNG to `docs/screenshots/` |

## Requirements

* Python 3 and Node.
* Base restored to the NuGet cache at the version `Jellyfin.Plugin.ServerSync.csproj` names. Run `dotnet restore` on the plugin once.
* jellyfin-web. The default is the macOS app's. Set `JELLYFIN_WEB` to use another folder, such as one copied out of the `jellyfin/jellyfin` image with `docker create` and `docker cp`.
* playwright-core. Set `PLAYWRIGHT_CORE` to its `index.mjs` unless Node resolves `playwright-core` on its own.
* Chromium. The default is Playwright's `chromium-1148` in `~/Library/Caches/ms-playwright`. Set `CHROMIUM` to use another binary.

## Running

Start the server from this folder.

```bash
python3 -I server.py 8874 &
```

Take every shot.

```bash
PLAYWRIGHT_CORE=/path/to/node_modules/playwright-core/index.mjs node shots.mjs
```

Take only the named shots. A name is the file's path under `docs/screenshots/` without `.png`.

```bash
PLAYWRIGHT_CORE=/path/to/node_modules/playwright-core/index.mjs node shots.mjs "Queue,settings/Storage"
```

`PORT` runs the shots against a server on another port. Each shot prints `ok`, or the page errors and any route the stand-in does not answer. Look at every PNG it writes before committing it. The queue and the dialogs show times relative to now, so their clock text changes on every run.

A new subject is one more entry in the `shots` list of `shots.mjs`. A new route or sample data goes in `stub.js`.

# Documentation

How to write the README, the docs site, screenshots, and release notes for this plugin. It applies to every page under `docs/`, to `README.md`, and to the `changelog` in `build.yaml`.

## The One Rule

Documentation describes the release it ships with. It reads as if this version were the only one that ever existed.

* Write what the plugin does. Never write what it used to do, what it no longer does, or what it now does.
* Write how a feature works. Never write that a bug was fixed, a problem is gone, or a limit was lifted.
* History belongs in the release notes. See [Release Notes](#release-notes).

| Do not write | Write |
| :--- | :--- |
| Links now survive a restart. | Links survive a restart. |
| The Stream direction was removed. | Leave it out. |
| No longer converts older databases. | Converts databases from 12.4.13.0 or later. |
| Fixed an issue where scans missed items. | A scan hears every server that holds an item. |
| New in 12.4.14.0, Precedence decides... | Precedence decides... |
| This release carries no upgrade steps. | Version 12.4.14.0 carries no upgrade steps. |

"Now" and "no longer" are fine when they describe state rather than history. "The file is no longer on the source" describes what the plugin sees, so it stays.

A version number belongs in docs only when the reader needs it to act, such as the minimum Jellyfin version or the oldest release that can upgrade directly. Keep those facts in [Installation](../docs/Installation.md) and [Versioning](../docs/Versioning.md), and name the version outright instead of "this release" or "an older release".

Remove compatibility notes for peers the plugin no longer supports. The plugin requires Jellyfin 12.4, so text about 12.2 peers describes servers that cannot be in the pool.

## Writing Style

### Sentences

* Use present tense and plain declarative sentences.
* Describe the system in the third person. "Server Sync copies the file."
* Address the reader in the second person only in steps. "Turn on **Server Sync** in the library's metadata settings."
* State one fact per sentence. Aim for 20 words or fewer and split anything over 30.
* Use no contractions. Write "cannot", not "can't".
* Use no em dashes, en dashes as dashes, or semicolons. Split the sentence, or join it with a comma and "and".
* Use colons only to introduce a list or a code block.
* Use parentheses rarely. Move the aside into its own sentence.
* End every sentence with a period.
* Write compounds as separate words the way the UI does, such as "one time setup" and "sign in".
* Write one paragraph per line. Never hard wrap prose.
* Use no marketing words such as "powerful", "seamless", "easily", or "simply".
* Write examples with generic names. Use `source-a`, `source-b`, `local`, and `viewer`, never a real server, person, or address.

### Headings

* Use Title Case for every heading. "## Live Changes Between Servers".
* Page titles are `#` and appear once per page.
* Name the thing, not the action, unless the section is a procedure. "## Settings" and "## Add the Plugin Repository" are both correct.
* Never rename a heading without fixing its anchors. `mkdocs build --strict` fails on a broken anchor.

### UI Names

* Write a label exactly as the page shows it, in bold. **Send changes after (seconds)**.
* Write a path through the UI with `>` between the steps, in bold. **Dashboard > Plugins > Server Sync > Settings**.
* Write a tab as its label, in bold. **Sync**, **Servers**, **Storage**, **Statistics**, **Settings**.
* Quote activity log names and on-screen messages in double quotes. "Server Sync needs attention".
* Write routes, files, settings keys, and values in code. `POST /Sync/Tasks/{taskKey}/Run`.

A docs page and the UI must use the same word for the same thing. When a label changes, search `docs/` for the old one and change it everywhere in the same commit.

## Site Structure

The docs site is MkDocs with the Material theme, built from `docs/` by `mkdocs.yml` and published to `https://jpkribs.github.io/jellyfin-plugin-serversync/`.

### Navigation

Keep the nav in this order.

1. **Overview** (`index.md`). What the plugin is, the risk warning, and the key ideas.
2. **Getting Started**. Installation and Setup.
3. **Sync Types**. One page per sync type.
4. **Features**. One page per feature that spans sync types.
5. **Reference**. Permissions, API, and Versioning.
6. **Development**. [Architecture](../docs/Architecture.md), then building and testing.

Name page files in PascalCase with no spaces, such as `LiveChanges.md`.

## Architecture

[docs/Architecture.md](../docs/Architecture.md) maps how the parts of the plugin connect and the rules that hold across all of them. It is the page a new contributor, person or AI, reads first.

Review it after every change, and update it in the same commit when the change affects any of these.

* A part is added, removed, renamed, or moved to another folder.
* A part calls a different part, or data takes a different path.
* A scheduled task, hosted service, controller, or database table is added or removed.
* A rule that holds everywhere changes, such as no echoes, one hop, or how versions decide.
* The protocol or schema version changes.

Name classes and files exactly as the code does, in code format, with the folder when it is not obvious. Never link to source files, since the docs build refuses links outside `docs/`. A wrong name on this page sends every reader to the wrong file, so check each one against the code.

### Page Templates

A sync type page follows this shape.

```markdown
# Content Syncing

One sentence that says what this sync type copies.

## Settings

* **Setting Name** does one thing.

## <Feature Sections>

## Live Changes Between Servers
```

A feature page opens with one sentence on what the feature is, then one section per question a reader asks, then **Security** and **Limitations** where they apply.

An API route follows this shape.

````markdown
### POST /Sync/Example?serverId={id}

One sentence on what the route does and who calls it.

```json
{ "Example": true }
```

| Status | Meaning |
| :--- | :--- |
| `200` | The change was saved |
````

### Settings References

List settings as bullets in the order the page shows them. Start each with the label in bold and follow it with a verb.

```markdown
* **Play count** syncs how many times each item was played.
```

When a page has many settings with defaults, use a table instead.

| Setting | What it does | Default |
| :--- | :--- | :--- |
| **Play count** | Syncs how many times each item was played | On |

Table cells have no closing period. Keep them to one sentence.

### Troubleshooting

Write trouble as a table of what the reader sees and what to do.

| Reason | What to do |
| :--- | :--- |
| linking is not allowed for *server* here | Turn on **Allow linking** on the source's entry for the linking server |

Put the exact on-screen text in the first column. Use italics for a placeholder inside it, such as *server*.

### Admonitions

Give every admonition a short title.

```markdown
!!! warning "Use at your own risk"
    One or two sentences.
```

* `note` for a detail the reader needs.
* `tip` for a better way to do something.
* `warning` for anything that can lose data or break a server.
* `info` for a requirement, such as both servers running the plugin.

### Links

* Link pages relatively with their `.md` name, such as `[Links](Links.md#the-link-cache)`.
* End a Getting Started page with one "Next" line, such as `Next: [Setup](Setup.md).`
* Link to Base's README for a shared component instead of describing it again.

## README

The README is a landing page. The docs site holds the detail.

1. The icon, `<img src="docs/assets/icon.png" alt="Server Sync" width="96" align="left">`, then a one paragraph pitch and `<br clear="left">`.
2. `## Documentation`. One sentence on what the docs cover, a bold link to the site, then the repository URL for Jellyfin in a code block.
3. `## Use At Your Own Risk`. The same words as the warning on `docs/index.md`.

Use `##` for README headings. Keep the README under 40 lines.

## Screenshots

### What to Capture

* Capture every page and dialog a docs page describes.
* Capture the dark theme only, which is Jellyfin's default.
* Capture from the Docker pool so every name is generic.
* Capture after a sync has run so lists, counts, and badges are filled.
* Hide nothing with editing. When a value is private, change the pool data instead.

### Sizes

| Kind | Size | How |
| :--- | :--- | :--- |
| Dialog | 800 wide | The browser at 800 px, cropped to the dialog |
| Full page | 1920 wide | The browser at 1920 px, cropped to the content area |
| Phone | 375 wide at 2x | The browser's device mode at 375 px |

Save every capture as PNG. Crop out the browser frame and the Jellyfin header unless the header is the subject.

### Names and Placement

* Save to `docs/screenshots/`, with dialogs for the Settings tab in `docs/screenshots/settings/`.
* Name the file in Title Case after what it shows, with spaces. `Library Mapping.png`, `Queue Phone.png`.
* Link with `%20` for spaces and alt text that names the subject. `![Library Mapping](screenshots/settings/Library%20Mapping.png)`.
* Place two related shots side by side in a table whose header row holds the captions.

```markdown
| Change queue | Change queue on a phone |
| :---: | :---: |
| ![Change queue](screenshots/Queue.png) | ![Change queue on a phone](screenshots/Queue%20Phone.png) |
```

### When to Retake

Retake a screenshot in the same commit as any change that alters what it shows, including a label, a tab, a layout, or the Jellyfin version in the header. A new Base version changes how every component looks, so retake every screenshot when it is raised. A screenshot that shows a tab or label the plugin no longer has is a bug.

## The Docker Pool

The pool runs three Jellyfin servers with the plugin built from this checkout. Use it for screenshots, for manual checks, and for the scenarios in [testing.md](testing.md#pool-scenarios).

Docker and the dotnet SDK must be on the path. The scripts use only the Python standard library.

```bash
python3 tools/integration/run.py up
```

| Server | Address |
| :--- | :--- |
| `source-a` | http://localhost:8111 |
| `source-b` | http://localhost:8112 |
| `local` | http://localhost:8113 |

The admin user is `admin`, and the password is in `tools/integration/.state/credentials.json`. A `viewer` user exists on every server. The servers reach each other at `http://<name>:8096`.

```bash
python3 tools/integration/run.py down
```

```bash
python3 tools/integration/run.py reset
```

`down` stops the pool and keeps its state. `reset` deletes the state, the media, and the credentials.

* Run `reset` before a full `test` run. Leftover link rows trip the prune safety limit.
* Never sign in as admin from another script while `test` or `scenarios` runs. It revokes the harness token.
* Never open `sync.db` with sqlite on the host while the pool is up. The container gets disk I/O errors.

To take screenshots, run `up`, sign in to `local` at port 8113, and trigger the syncs you need from the **Sync** tab. Take them only while no `test` or `scenarios` run is going, since a sign in revokes the harness token.

`up` on a running pool rebuilds the plugin into it. Reload the page afterward, since the dashboard caches scripts.

## Installing a Build by Hand

```bash
./build.sh
```

The zip lands in `dist/`. Unzip it into `<config>/plugins/Server Sync_<version>/` on any Jellyfin 12.4 server and restart the server.

## Building the Docs

```bash
pip install mkdocs==1.6.1 mkdocs-material==9.7.7
```

```bash
mkdocs serve -a 127.0.0.1:8765
```

```bash
mkdocs build --strict
```

Run the strict build before every docs commit. CI runs the same build and refuses broken links, anchors, and nav entries.

## Release Notes

Release notes live in the `changelog` of `build.yaml`, under four keys. The publish workflow copies them into `manifest.json`, so every line reaches users' plugin catalogs.

| Key | Holds | Voice |
| :--- | :--- | :--- |
| `added` | A feature that did not exist | The feature name, then what it does |
| `changed` | Behavior a user will notice | The new behavior, in present tense |
| `fixed` | A bug | What went wrong in past tense, then the fix in present tense |
| `developer` | Routes, schema, tests, and tooling | Plain statements for other developers |

```yaml
changelog:
  added:
    - Precedence, for metadata, people, playlists, and user settings. Newest lets a lower ranked server be heard
  fixed:
    - A scan only heard the highest ranked server. Every server that holds an item is heard
```

* This is the one place that may say "now", "no longer", and "was".
* Write full sentences with no closing period.
* Write one entry per change a user would notice. Fold internal steps into the change they serve.
* Name UI labels exactly as the page shows them, without bold or backticks.
* Name the Base version in `developer`, with what it replaced locally.
* Give a new or changed route or query parameter a `developer` line, and document it in [API](../docs/API.md) in the same commit.

## Commit Messages

* Write an imperative sentence in sentence case with no closing period. "Take provider ids that differ only by case as one id".
* Describe the behavior, not the files. Use no prefixes such as `fix:` or `feat:`.
* Add a body of plain paragraphs, wrapped at 72 characters, when the reason is not obvious.
* Commit docs and screenshots with the change they describe.

## Checklist

* [ ] Nothing describes an earlier version, a removed feature, or a fixed bug.
* [ ] Every UI label matches the page exactly and is bold.
* [ ] Every heading is Title Case.
* [ ] No em dashes, semicolons, or contractions.
* [ ] Every screenshot matches the current UI and uses pool data.
* [ ] [Architecture](../docs/Architecture.md) was reviewed and matches the code.
* [ ] Every term matches [glossary.md](glossary.md).
* [ ] `mkdocs build --strict` passes.
* [ ] `build.yaml` has a changelog entry for any change a user would notice.

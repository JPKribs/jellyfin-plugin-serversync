# Style

How every page, dialog, label, and message in this plugin looks and reads. It is drawn from Base and from all nine JPKribs plugins, so the same rules carry to each of them.

Base is [JPKribs.Jellyfin.Base](https://github.com/JPKribs/jellyfin-plugin-base). Its README documents each component's options. This guide says which component to use, when, and what to write in it.

## Principles

1. **One component per concept.** When Base has it, use it. When Base lacks it and a second plugin needs it, add it to Base. Never build a second version.
2. **Short beats complete.** A label is a few words. A description is one sentence. A message is one sentence.
3. **The page explains itself.** A reader understands a setting from its label and its one line description, without the docs.
4. **The same thing has the same name everywhere.** The label, the docs, the message, and the activity log use one word for one concept.
5. **Follow Jellyfin.** Use Jellyfin's own markup and theme, so the plugin looks like part of the dashboard.

## Writing

### The Rules

These rules apply to every string a user can see. That includes HTML, JS, C# messages the API returns, and activity log entries.

* **No em dashes or en dashes as punctuation.** Split the sentence, or join it with a comma and "and". An en dash in a numeric range, such as `22–6`, is fine.
* **No semicolons.** Split the sentence.
* **Avoid colons.** Never end a label with one. In a sentence, rewrite it. "How fast files move: downloads and uploads." becomes "Sets how fast files move in each direction."
* **One sentence.** Two only when the second gives the fix or the consequence.
* **End every sentence with a period.** Labels, buttons, titles, tabs, and table cells are not sentences and take no period.
* **No "please", "successfully", "simply", or "just".**
* **No exclamation marks.**
* **No ALL CAPS for emphasis.** Say why it matters instead.
* **No "(s)".** Write the right plural for the count.
* **No contractions.** Write "cannot", not "can't".
* **No "e.g." or "i.e.".** Write "such as".
* **American spelling.** Color, behavior, favorite, center.
* **Use the reader's words.** "Server" and "library", not "peer" and "collection folder", unless the page is about the protocol.

### Casing

| Text | Case | Example |
| :--- | :--- | :--- |
| Tab | Title Case, one word | Servers |
| Section and collapsible title | Title Case | Sync Parameters |
| Field label | Title Case | Scheduled Speed |
| Checkbox label | Title Case | Sync Watched State |
| Button | Title Case, verb first | Add Server |
| Dialog title | Title Case | Remove Server |
| Badge and status chip | Title Case | Pending |
| Column header | Title Case | Last Sync |
| Activity log name | Title Case | 12 Items Synced |
| Description, message, placeholder sentence | Sentence case | Copies played and unplayed marks. |

Title Case capitalizes every word except short articles, conjunctions, and prepositions in the middle, such as "a", "and", "of", "to", and "in". Keep acronyms as written, such as "API Key", "URL", "Mbps", and "GB".

### Words to Use

Pick the word on the left every time.

| Use | Not | When |
| :--- | :--- | :--- |
| Remove | Delete | Takes an entry out of a list or the config, and nothing on disk is lost |
| Delete | Remove | Destroys data, files, or history that cannot come back |
| Clear | Empty, Reset | Removes every entry in a list or log |
| Reset | Restore | Puts settings back to their defaults |
| Save | Apply, Update | Writes changes to the config |
| Done | OK, Close | Closes an editor and keeps its changes |
| Cancel | Close, No | Closes without keeping changes |
| Add | New, Create | Puts a new entry into a list on the page |
| New | Add, Create | Starts a new record in a record picker |
| Test Connection | Test, Check | Calls the other side and reports the result |
| Run Now | Start, Trigger | Starts a scheduled task right away |
| Select | Pick, Choose, Tick, Click | Any instruction to make a choice |
| Turn on, Turn off | Enable, Disable, Tick, Untick | An instruction about a checkbox or toggle |
| Leave blank | Leave empty | An empty field means the default |
| API Key | Api Key, API key, Token | The Jellyfin key for another server |
| Server URL | Address, Host | The base address of a Jellyfin server |
| Could not | Failed to, Unable to | An error message |

The control names its own result. "Remove Server" leaves the server's media alone, so it is Remove. "Delete Cached Files" frees disk space, so it is Delete.

### Labels

* Two to four words. Name the thing the field holds, not the action.
* Put a unit at the end in lowercase parentheses, such as **Send Changes After (seconds)** or **Cache Size (GB)**. Use symbols for standard units, such as `GB` and `Mbps`.
* No colons, periods, or question marks.
* A checkbox label names the feature it turns on, never "Enable X". Write **Sync Watched State**, not **Enable Watched State Sync**. The one exception is the master switch at the top of a section, which reads **Enable** followed by the section name, such as **Enable History Sync**.

### Descriptions

A `fieldDescription` sits under every field that is not obvious from its label.

* One sentence that says what the setting does, starting with a verb. "Copies played and unplayed marks."
* Add a second sentence only for the default or a consequence. "0 is no limit."
* Describe the current behavior only. Never "now" or "no longer".
* Never repeat the label.
* Put units, ranges, and what blank means here, never in the label or the placeholder.

| Bad | Good |
| :--- | :--- |
| How fast files move: downloads to this server, and what it sends to others. | Sets how fast files move in each direction. |
| Run in order; drag or use the arrows to reorder. | Runs in order. Drag a step to move it. |
| Server time. 22 to 6 runs overnight; equal hours run all day | Uses the server's clock. Equal hours run all day. |
| This setting enables the syncing of watched state between servers. | Copies played and unplayed marks. |

A section intro is one `fieldDescription` right under the section title. It says what the section is for in one sentence.

### Placeholders

A placeholder shows an example of a valid value. It is never the only place an instruction lives, since it disappears once the reader types.

* Show a realistic example with no prefix. `https://jellyfin.example.com`, not `e.g. https://...`.
* Use `example.com` addresses and generic names only.
* For an override that falls back to a default, show the value it falls back to. When that value is not known, write `Default`.
* For a search box, write "Search" and the noun, with no ellipsis. `Search items`.
* For a secret already stored, Base's `bindSecretField` writes "Saved, leave blank to keep". Leave it as is.
* Never put a sentence in a placeholder. Move it to the description.

### Buttons

* A verb, or a verb and a noun. **Save**, **Add Server**, **Run Now**.
* Name the object when the page has more than one of that action. **Add Library Mapping** and **Add User Mapping** sit on the same page.
* A primary Save is **Save**. Use **Save** and a noun only when one page has several saves, such as **Save Bandwidth**.
* An icon only button carries a `title` of one to three words, such as `Remove`, `Move Up`, or `Retry`. Never a sentence.
* A button that runs for a while shows progress in place. See [Progress](#progress).

### Messages

A message reports what happened in one sentence that ends with a period.

| Kind | Pattern | Example |
| :--- | :--- | :--- |
| Saved | `<Thing> saved.` or `Saved.` | Bandwidth saved. |
| Done | `<Thing> <past verb>.` | History cleared. |
| Copied | `<Thing> copied.` | Link copied. |
| Working | `<Verb>ing…` | Saving… |
| Could not | `Could not <verb> the <thing>.` | Could not save the bandwidth. |
| Could not, with a fix | Add one sentence with the fix | Could not load the settings. Reload the page. |
| Missing input | `Enter <the thing>.` or `Select <the thing>.` | Enter the Server URL and API Key. |
| Nothing to do | `No <things> to <verb>.` | No items to retry. |

* Use the ellipsis character `…`, never three dots, and only for work in progress.
* When the server gave a reason, show it with `shared.errorText(err, 'Could not save the server.')`.
* Name the count with a correct plural. "Deleted 3 items." and "Deleted 1 item."
* Never say "error occurred". Say what could not happen.

### Confirm Dialogs

A confirm asks one question and states the consequence.

```js
confirmDialog('Remove source-a? Its library mappings are removed too.', {
    title: 'Remove Server',
    confirmText: 'Remove',
    destructive: true
});
```

* The message is a question, then one sentence on the consequence. "Delete 3 items? This cannot be undone."
* The title is the action in Title Case, such as **Remove Server**. Never leave the default **Confirm**.
* The confirm button repeats the action verb, such as **Remove** or **Delete**. Never **OK** or **Yes**.
* Set `destructive: true` for anything that loses data.
* Never "Are you sure you want to". Ask the question directly.
* Never use `window.confirm` or `Dashboard.confirm`.

### Empty States

| Where | Pattern | Example |
| :--- | :--- | :--- |
| Record picker | `No <things> yet. Select New to add one.` | No profiles yet. Select New to add one. |
| List in a form | `No <things> yet. Add one below.` | No servers yet. Add one below. |
| Live list | `No <things> right now.` | Nothing is moving between servers right now. |
| Filtered list | `No <things> match.` | No items match. |
| Feature off | `<Feature> is off. Turn it on above to <result>.` | Live changes are off. Turn them on above to see the queue. |

Use `shared.emptySection(text)` for a region and `shared.emptySection(text, { inline: true })` for a list.

### Activity Log

Use `ActivitySummary` and `ActivityText` from Base.

* The name is short and Title Case. "12 Items Synced".
* The overview is one sentence with no closing period, as Base writes it. "12 items were synced from source-a (9), source-b (3)".
* Use the same verb the UI uses for the action.

### Messages From C#

The API returns text that the page shows as is, so it follows the same rules.

* A full sentence that starts with a capital and ends with a period. "Server source-a is not configured on this server."
* Say what is wrong and, when it helps, what to do. "The entry for this server is off or has no Server URL and API Key."
* Never return an exception message, a stack trace, or a type name.

## Color

### Tokens

Use Base tokens. They follow the dashboard's theme, light themes included. Never write a hex value for something these cover.

| Token | Use |
| :--- | :--- |
| `--jpk-accent` | Links, focus rings, primary buttons, progress |
| `--jpk-accent-hover` | Hover on accent elements |
| `--jpk-fg` | Washes on elements that fade their own text |
| `--jpk-surface` | Panel and dialog backgrounds |
| `--jpk-border` | Every border and divider |
| `--jpk-field-gap` | Space between field rows |
| `--jpk-mono` | Code, ids, paths, and raw errors |
| `--jpk-success`, `--jpk-warning`, `--jpk-error`, `--jpk-info`, `--jpk-purple` | Solid status colors |
| `--jpk-<color>-bg`, `--jpk-<color>-fg` | Tinted badge and card backgrounds and text, for green, red, orange, purple, blue, and gray |

### Meaning

A color means the same thing on every page and in every plugin.

| Color | Means | States |
| :--- | :--- | :--- |
| Green | Done and healthy | Active, Synced, Downloaded, Succeeded |
| Red | Failed, or about to lose data | Error, Errored, Failed, Deleting, Pending Deletion |
| Orange | Waiting, or needs attention soon | Queued, Expired, Warning |
| Purple | Working right now | Pending, Downloading, Locked, Pending Download |
| Blue | Informational | Admin, Imported, Pending Replacement |
| Gray | Off or not applicable | Disabled, Ignored, Skipped, Group |

* Map a status to a color through `BADGE_STATES`. Never keep a second map in the plugin.
* Disabled is gray when it means off. Red is only for failure.
* Use "orange" for the warning color in class names, never "amber", "yellow", or "warning".
* Never use color alone. A badge always has a word, and a dot always has a `title`.

## Page Structure

### Shell

Every config page uses Jellyfin's page shell.

```html
<div data-role="page" class="page type-interior pluginConfigurationPage withTabs"
     data-require="emby-input,emby-button,emby-select,emby-checkbox">
    <div>
        <div class="content-primary">
            <div class="verticalSection">
                <div class="sectionTitleContainer flex align-items-center">
                    <h2 class="sectionTitle">Servers</h2>
                </div>
                <div class="fieldDescription">Connects this server to the others in the pool.</div>
            </div>
        </div>
    </div>
    <link rel="stylesheet" href="configurationpage?name=serversync_jpkribs_shared.css">
</div>
```

* List `data-require` in the order `emby-input,emby-button,emby-select,emby-checkbox`, dropping any the page does not use.
* Use no inline `style` attributes. Add a class to the plugin CSS, or to Base when it is reusable.

### Tabs

* One registered page per tab, wired with `createConfigPage` and `setTabs`.
* Tab names are one Title Case word.
* Keep the tab list in one place, the plugin's shared module, never copied into each page.
* Order tabs from what is used most to what is set once. ServerSync is **Sync**, **Servers**, **Storage**, **Settings**.

### Sections

| Need | Component |
| :--- | :--- |
| A page with several independent groups of settings | `jpk-collapsible-section`, one per group |
| A group that is always open | `jpk-section` with `jpk-section-title` |
| A heading inside a section | `jpk-subsection-title` |
| An ordered setup, such as a server editor | `jpk-collapsible-section` with `jpk-step-title` and `jpk-step-number` |
| Settings that only matter when a checkbox is on | `jpk-indent` under the checkbox, shown with `shared.bindReveal` |

* A collapsible header is a `button.jpk-collapsible-header` holding `jpk-collapsible-title` and `jpk-collapsible-icon`. Base replaces the icon with `expand_more`.
* Start collapsibles closed, except the first one on a page someone opens to act on.

### Saving

Pick one model per page and keep it.

| Model | Use it when | Save button |
| :--- | :--- | :--- |
| One Save per page | Settings depend on each other | `raised jpk-button-submit block` at the bottom |
| One Save per section | Sections are independent and long | `raised jpk-button-submit jpk-button-small` in `jpk-collapsible-actions` |
| Editor dialog | Each record is edited on its own | **Done** in `openEditorDialog` |

* Track unsaved changes with `createChangeTracker` and the `jpk-unsaved` indicator, which reads "Unsaved".
* Guard leaving the page with `page.guardUnsaved`.
* Report the result with `shared.setStatus` next to the button.
* Use `shared.saveConfigWith` for get, change, save, and report.

## Components

Every component below is in Base. Use it as Base ships it. When it does not fit, change Base.

### Fields

| Need | Use |
| :--- | :--- |
| Text, number, or URL | `inputContainer` with `<label for>`, `<input is="emby-input">`, and a `fieldDescription` |
| Choice from a short list | `selectContainer` with `label.selectLabel` and `<select is="emby-select">` |
| On or off | `checkboxContainer checkboxContainer-withDescription` with `emby-checkbox` and `fieldDescription checkboxFieldDescription` |
| A mode that needs a sentence per option | `createChoiceGroup` with `jpk-choice` cards |
| Several on or off options in a row | `jpk-toggle-group` with `jpk-toggle` pills |
| Many checkboxes | `jpk-check-grid` |
| Many items from a long or searchable list | `createCheckboxList` |
| Typeahead with removable chips | `createChipSelect`, with `single: true` for one choice |
| One user | `createUserSelector` |
| Several users | `createUserMultiSelector` |
| Several fields side by side | `jpk-field-row` with `jpk-field` |
| A secret | `bindSecretField`, `readSecretField`, and `clearSecretField` |
| A template with `{name}` tokens | `placeholderHint` and `bindPlaceholders` |
| Code or JSON, read only | `jpk-code-block` |

* Use `emby-select`, never `jpk-selector-dropdown`, for a field. `jpk-selector-dropdown` is only for the record picker.
* Every input has a `<label for>`.

### Buttons

| Need | Class |
| :--- | :--- |
| Primary action | `raised jpk-button-submit` |
| Destructive action | `raised jpk-button-destructive` |
| Secondary action | `raised jpk-secondary` |
| Compact action in a section | add `jpk-button-small` |
| Icon and label in a toolbar | `jpk-icon-btn` |
| Icon only in a row | `jpk-row-btn`, with `.dirty` for unsaved and `.success` after saving |

* Every button is `<button is="emby-button" type="button">`.
* Icons are `<span class="material-icons" aria-hidden="true">name</span>`.
* Close a dialog with `jpk-dialog-close` and `&times;`. Never "✕" or another glyph.

| Action | Icon |
| :--- | :--- |
| Add | `add` |
| Edit | `edit` |
| Remove or delete | `delete` |
| Copy | `content_copy` |
| Move up and down | `keyboard_arrow_up`, `keyboard_arrow_down` |
| Drag | `drag_indicator` |
| Expand | `expand_more` |
| Retry | `refresh` |
| Open | `chevron_right` |
| Import and export | `upload`, `download` |
| Show | `visibility` |

### Records

| Need | Use |
| :--- | :--- |
| One record at a time from a list | `jpk-selector` with New, Rename, and Remove |
| Records in priority order | `createSortableCardList` |
| A plain list of records | `jpk-card-list` with `jpk-card-item-*` |
| Edit one record in a dialog | `openEditorDialog` |
| A large searchable list | `createPaginatedTable` |
| Edit table rows in place | `jpk-bulk-edit-bar` and `jpk-edit-row` |
| Uploaded files | `jpk-asset-list` |

* A record picker's actions read **New**, **Rename**, **Remove**, then **Import** and **Export**, in that order.
* Use `createPaginatedTable` for any list that can grow past one screen. Rows open with `actions.onRowClick`.

### Status

| Need | Use |
| :--- | :--- |
| The state of one item | `shared.statusBadge(text, state)` |
| A label on an item | `shared.badge(color, label)` |
| Counts at the top of a page | `shared.renderCards` with `jpk-cards jpk-cards-rows` |
| Label and value pairs | `jpk-record-card` |
| A notice in the page | `jpk-info`, `jpk-success`, `jpk-warn`, `jpk-error`, `jpk-neutral` |
| Raw failure text | `jpk-error-message` |
| The result of an action | `shared.setStatus` |
| A row of reset actions | `jpk-reset-group` with `jpk-reset-row` |

* Never use `Dashboard.alert` for a result. It blocks the page. Use `setStatus`.
* A notice holds one sentence. Use a callout for something the reader must know before acting, not for a result.

### Progress

* A task started from a button shows progress in that button with `shared.pollTaskProgress`.
* Progress inside a row uses `jpk-progress` and `jpk-progress-bar`.
* Never block the page with `Dashboard.showLoadingMsg`.

### Dialogs

| Need | Use |
| :--- | :--- |
| A yes or no question | `confirmDialog` |
| A few values | `promptDialog` |
| A record editor | `openEditorDialog`, with `wide: true` for a large one |
| A custom dialog | `openDialog` |
| A dialog already in the page markup | `bindDialog` |

* Build dialogs in JS with `openDialog` and its wrappers. Use `bindDialog` only when the markup is large and static.
* A dialog title is Title Case and names what the dialog does.
* Buttons read, from left to right, the destructive action, then **Cancel**, then the primary action.

## Layout

* Use Base's breakpoints, 768 px for tablet and 600 px for phone. Use 700 px only where Base does, for dialogs.
* Test every page at 375 px wide. Nothing scrolls sideways except a table.
* Spacing comes from `--jpk-field-gap`. Never add margins between fields.
* Never set a width in pixels on a field. Let it fill its container.

## Accessibility

* Every input has a label, and every icon only button has a `title` and an `aria-label`.
* Every icon span has `aria-hidden="true"`.
* Rows that open on click also open with Enter and Space. Base's table and card list do this already.
* Never rely on color alone.

## CSS in This Plugin

* Plugin classes use the `serversync-` prefix in kebab case. `jpk-` is reserved for Base.
* Keep plugin CSS for layout that only this plugin has. A component another plugin could use goes to Base.
* Never override a `jpk-` class. Change Base instead.
* Never define a color token. Use Base's.

## Consolidation

These ServerSync pieces duplicate Base or another plugin. Replace each with the listed component, and promote the ones Base lacks.

### Replace With Base

| ServerSync today | Replace with |
| :--- | :--- |
| `LibraryMenu.setTabs` and a `getTabs` copy in each page | `createConfigPage` and one tab list in the shared module |
| `Dashboard.alert` through `showAlert` and `saveSection` | `shared.setStatus` and `shared.saveConfigWith` |
| `window.confirm` in `serversync_settings.js` | `confirmDialog` |
| Inline `<span class="text-error">` errors in `serversync_servers.js` | `shared.setStatus` |
| `.serversync-queue-chip` and `.serversync-storage-mode-badge` | `shared.badge` |
| Hand written `<span class="jpk-badge">` strings | `shared.badge` |
| `.text-success`, `.text-error`, `.text-warn` | `jpk-ok`, `jpk-bad`, or a callout |
| `.serversync-health-card` panel | `jpk-cards` or `jpk-record-card` |
| `.serversync-filter-item*` and `.serversync-queue-row*` thumbnail rows | `jpk-table-row` with `jpk-table-row-thumb` |
| `#mainStatusRow .jpk-card.hidden` | `jpk-cards-rows` |
| `.jpk-collapsible-actions` border override | Base default, or a Base change |
| `jpk-modal-field-value`, which Base does not define | `jpk-record-card` |
| "✕" close glyph in the queue | `&times;` in `jpk-row-btn` |
| Sentence long button titles in `serversync_sync.html` | One to three word titles |

### Promote to Base

These are built two or more times across the plugins and belong in Base.

| Component | Built today in |
| :--- | :--- |
| `jpk-panel`, a bordered sub card | ServerSync `.serversync-panel` and `.serversync-mapping`, plus Live Channels, Remote Access, User Management |
| `jpk-static-table`, a simple scrolling table | ServerSync `.serversync-compare-table`, `.serversync-form-table`, `.serversync-role-table`, plus Remote Access, Artwork Generator |
| A single choice pill group | ServerSync `.serversync-filter-pills`, plus YouTube Audio, Remote Access |
| `.btn-progress` styling for `pollTaskProgress` | ServerSync `.serversync-action-button` |
| `jpk-section-intro` | ServerSync `fieldDescription mb-md`, plus Scripts, Artwork Generator, Remote Access |
| A JS plural helper | ServerSync and YouTube Audio "(s)", plus hand written plurals in four plugins |
| A light ordered list with up and down buttons | User Management, YouTube Audio, Requests |
| An inline editor footer | ServerSync `.serversync-editor-footer` |
| `flashSaved` on `createChangeTracker` | Artwork Generator, four copies |
| Date input styling in `form.css` | User Management |
| `.sr-only` | Remote Access |

## Checklist

Run every new or changed string through this list.

* [ ] No em dashes, en dashes as punctuation, or semicolons.
* [ ] No colon at the end of a label, and none in a sentence unless it introduces a list.
* [ ] One sentence, or two when the second is the fix or the consequence.
* [ ] Sentences end with a period. Labels, buttons, and titles do not.
* [ ] Title Case for labels, buttons, titles, tabs, and badges. Sentence case for sentences.
* [ ] No "please", "successfully", exclamation marks, ALL CAPS, "(s)", or contractions.
* [ ] The word matches [Words to Use](#words-to-use).
* [ ] The component comes from Base, unchanged.
* [ ] Colors come from Base tokens and mean what [Meaning](#meaning) says.
* [ ] The docs use the same label.

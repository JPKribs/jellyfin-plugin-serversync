# Code

How to write, comment, and test code in this plugin. It covers C#, JavaScript, HTML, CSS, Python, shell, and YAML.

## Layout

Folders inside `Jellyfin.Plugin.ServerSync/` set the namespace, so a file in `Services/Queue/` is in `Jellyfin.Plugin.ServerSync.Services.Queue`.

| Path | Holds |
| :--- | :--- |
| `Plugin.cs` | The plugin class and its pages. |
| `PluginServiceRegistrator.cs` | DI registration. |
| `Api/` | Controllers, their attributes, and their guards. |
| `Configuration/` | `PluginConfiguration` and the C# classes it is made of. |
| `Web/` | The dashboard pages, as HTML, JS, and CSS, embedded by a wildcard. |
| `Assets/` | The logo. |
| `Localization/` | The plugin's own strings, one file per language. See [translations.md](translations.md). |
| `Models/` | Data classes, records, and enums, in one subfolder per sync type. |
| `Services/` | Stores, clients, merge services, links, the queue, and peers, in one subfolder per area. |
| `Tasks/` | Scheduled tasks. |
| `Utilities/` | Static helpers, extension methods, and streams. |

At the repository root, `tools/integration/` holds the Docker pool and matrix, `docs/` the documentation site, and `skills/` the guides.

## Base First

[JPKribs.Jellyfin.Base](https://github.com/JPKribs/jellyfin-plugin-base) holds everything two plugins could share. Its README lists every component and helper. Read it before you write anything new.

### Use What Base Has

* Use the Base type or component when one fits. Never write a local version, a wrapper that renames it, or a CSS override of a `base-` class.
* When Base almost fits, change Base. Never patch around it locally.
* Delete a local copy as soon as Base offers the same thing. Do it in the commit that moves to that Base version.
* Never shadow a Base name. A local `FormatUtilities` beside Base's `FormatUtilities` is ambiguous.
* Read Base from the package this plugin builds against. The C# source is in `~/.nuget/packages/jpkribs.jellyfin.base/<version>/src/`, and the JS typings are in `assets/jpkribs_shared.d.ts` beside it.
* Treat the local Base checkout as read only. Base has its own owner and its own release, so report a gap and wait for the version that closes it.
* When Base differs from the local code it replaces, keep the local behavior in a small step around the Base call, and cover it with a test. `ConfigurationUtilities` refuses `..` in a Server URL before it asks `SsrfGuard`.

| Need | Use from Base |
| :--- | :--- |
| Plugin class and config lock | `PluginBase<TPlugin, TConfiguration>` |
| Scheduled task | `PluginScheduledTask` |
| Outbound HTTP | `BaseHttp` and `HttpResult` |
| HTTP handlers | `BaseRetryHandler` with `BaseRetryOptions`, and `RedirectRefusingHandler` |
| Addresses another server or a user supplied | `SsrfGuard`, with `requireHttpsForPublic: true` |
| Retry and backoff | `RetryPolicy`, `BackoffPolicy`, `CircuitBreaker` |
| Parallel work | `ConcurrentTaskRunner` |
| Secrets | `SecretProtector`, `AddPluginSecretProtector` |
| Comparing a presented secret | `SecretCompare.FixedTimeEquals` |
| Writing a file whole or not at all | `AtomicFile`, with `ownerOnly: true` for a key or secret |
| Paths inside a folder | `PathContainment` |
| Streams | `StreamUtilities.CopyWithIdleTimeoutAsync` and `CopyThrottledAsync`, `SpeedBucket`, `ThrottledReadStream`, `CappedReadStream`, `ProgressReportingStream`, `StreamLimits` |
| Hashes and sizes | `HashUtilities`, `ByteSize` |
| The clock | `TimeProvider`, and `UtcTime` for stored times |
| Activity log | `ActivityLogger`, `ActivitySummary`, `ActivityText` |
| Paged lists | `PagedResult<T>`, `PagedQuery` |
| SQLite versioning | `DatabaseSchema` |
| JSON state file | `JsonFileStore<T>` |
| Formatting and names | `FormatUtilities`, `ItemTitles`, `RateUtilities`, `FileNameSanitizer` |
| Config page lifecycle | `createConfigPage`, through `createServerSyncPage` in `serversync_shared.js` |
| Page helpers | `createShared` |
| Tables | `createPaginatedTable` |
| Lists and row editors | `createCheckboxList`, `createRepeater`, `createSortableCardList` |
| Charts | `createPieChart`, `createUsageMeter`, `createBarChart` |
| Dialogs | `openDialog`, `confirmDialog`, `promptDialog`, `openEditorDialog`, `bindDialog` |

### Move Code Into Base

Code belongs in Base when another plugin could use it unchanged. A helper with no ServerSync types in its signature is a candidate.

1. Add it to `../Shared/jellyfin-plugin-base/src/`, one file per component, following Base's [code guide](https://github.com/JPKribs/jellyfin-plugin-base/blob/main/skills/code.md).
2. Add tests in `JPKribs.Jellyfin.Base.Tests/`, and JS tests in `JPKribs.Jellyfin.Base.Tests/js/`.
3. Document it in Base's docs site inventory.
4. Pack it locally with `scripts/pack.sh` and build this plugin against it with `-p:RestoreAdditionalProjectSources=<base>/nupkg`.
5. Commit and push to Base, then run Base's **Release** workflow.
6. Raise the `JPKribs.Jellyfin.Base` version in this plugin's csproj, delete the local copy, and add a `developer` changelog line naming the Base version.

Never commit this plugin against an unreleased Base package.

## Comments

### The Rules

* Every class, method, function, and component gets a header comment.
* The header is the name, then a summary of one or two sentences, leaning to one.
* List inputs and outputs when there are any. One line each, as `name [type]: What or why`.
* Write no inline comments, with one exception. A branch whose reason is not obvious gets one short line above it.
* A branch comment says why, not what. Keep it under 15 words.
* Never comment what the code already says. `// Check if values differ` is noise.
* Never leave commented out code, TODO, FIXME, or HACK. Open an issue instead.
* Never describe history. "Used to", "now", and "was changed to" belong in commit messages.
* Use no em dashes or semicolons in comments.

```text
<Name>
<What it does, in one sentence.>

Inputs
name [type]: What it is or why it is needed.

Output
[type]: What comes back.
```

Leave out **Inputs** when there are none, and **Output** when nothing comes back.

### C#

C# uses XML docs so the compiler and IntelliSense read them. The member name is the title. The type comes from the signature, so a `param` gives only the what or why.

```csharp
/// <summary>
/// Decides what to do with a value a peer offers.
/// </summary>
/// <param name="local">The version this server holds, or null when the object was never versioned here.</param>
/// <param name="incoming">The version the peer's value carries.</param>
/// <param name="valuesEqual">Whether the two values already match.</param>
/// <returns>The decision to apply.</returns>
public static VersionDecision Decide(ObjectVersion? local, ObjectVersion incoming, bool valuesEqual)
{
    ArgumentNullException.ThrowIfNull(incoming);

    if (valuesEqual)
    {
        return VersionDecision.Adopt;
    }

    if (local is null)
    {
        return VersionDecision.Apply;
    }

    // Same edit with different values means a copy drifted, so the arrival wins.
    if (SameVersion(local, incoming))
    {
        return VersionDecision.Apply;
    }

    return IsNewer(incoming, local) ? VersionDecision.Apply : VersionDecision.Keep;
}
```

* Every type and every member that is not private gets `<summary>`, every `<param>`, and `<returns>` when it returns a value.
* Private members get a `<summary>` when the name does not say enough.
* Use `<inheritdoc/>` on an interface or override member.
* Write `true` and `false` as `<c>true</c>` and `<c>false</c>`.
* A constructor summary is "Initializes a new instance of the <see cref="Name"/> class."
* Skip tautologies. `<param name="item">The item.</param>` says nothing, so write what the item is for.
* Put one type in each file, named after the type. A small enum used by one class may share its file.
* Match the namespace to the folder.

### JavaScript

```js
// createSyncTableModule
// Builds the Sync tab's content table and its filters.
//
// Inputs
// view [HTMLElement]: The page that holds the table.
// shared [object]: The helper bag from createShared.
//
// Output
// [object]: A handle with reload and destroy.
export function createSyncTableModule(view, shared) {
```

* Comment every top level function, every function assigned to `shared`, and every exported function.
* Do not comment the methods inside a returned handle. List them in the Output line instead.
* Open each file with a header naming the file and saying what it holds in one sentence. Use no `// ====` banners.
* Only `serversync_shared.js` imports Base, by the relative path `./configurationpage?name=serversync_jpkribs_shared.js`. It exports each Base builder a page uses, and every page imports from `serversync_shared.js`.

### HTML

```html
<!-- Library Mapping -->
<!-- Pairs each source library with a local one. -->
<div class="verticalSection">
```

* One comment line naming each section or dialog where it starts, and an optional second line saying what it is for.
* Use no inline `style` attributes.

### CSS

```css
/* healthCard */
/* Rounded card showing one sync type's last run and counts. */
/* Example: <div class="healthCard"><span class="healthCard-count">5</span></div> */
.healthCard { ... }
```

* Three lines above each component, the name, what it looks like, and one example.
* Prefix every class with `serversync-` in kebab case. `base-` is reserved for Base.
* Use Base tokens such as `--base-border` and `--base-fg` for colors. Never write a hex value for something Base already colors.

### Python, Shell, and YAML

* A Python module opens with a docstring that names it and lists its commands. Each function gets a one line docstring.
* A shell script opens with a comment saying what it does and how to run it.
* A YAML file opens with a comment saying what it is for. Comment a step only when its reason is not obvious.

## C# Conventions

* Target `net10.0` with `Nullable` on, `TreatWarningsAsErrors` on, and `AnalysisMode` set to `AllEnabledByDefault`.
* Keep `jellyfin.ruleset` identical across plugins. Raise a rule change in every plugin at once.
* Suppress a rule only at the narrowest scope, with a reason. `#pragma warning disable CA2100 // SQL is internal and parameterized.`
* Use file scoped namespaces, `sealed` classes, and records for data.
* Name private fields `_camelCase`.
* End every async method name in `Async`, pass the `CancellationToken` through, and use `ConfigureAwait(false)` on every await.
* Guard public arguments with `ArgumentNullException.ThrowIfNull`.
* Log through `ILogger<T>` with static, named templates. Pass untrusted text through `FormatUtilities.SanitizeForLog`.
* Catch a specific exception, or filter with `when`. Rethrow `OperationCanceledException` when the token was cancelled.
* Read the clock once in a method and reuse the value. Two reads can straddle a second and disagree.
* Take a `TimeProvider` wherever time decides an outcome, so a test can move it.
* Register services with `[PluginService]`.
* Never take a source item id to mean the same bytes. A file replaced in place keeps its id, so anything kept by that id, such as a cached file, is dropped when the source announces a change.

## JavaScript Conventions

* Each config page is an ES module whose default export calls `createServerSyncPage`, which wraps Base's `createConfigPage` and holds the tab list.
* Keep one handle for each Base component a page draws. Call `destroy` before drawing it again, and `setValue` when only its numbers change.
* Drop an answer that arrives after the page moved on. Remember the picked key before the request and compare it when the answer lands.
* Use `const` and `let`, never `var`. Arrow functions are fine for callbacks.
* Escape every value placed in HTML with `escapeHtml`.
* Report results with `shared.setStatus`, and errors with `shared.errorText`.
* Register every poller and observer with the page handle so `viewhide` stops it.

## Security

* Store every credential with `SecretProtector`, built by `AddPluginSecretProtector`. Never write a secret to the config, a file, or a log in plain text.
* Send a stored secret to the browser only as `SECRET_KEPT`. Read it back with `readSecretField` and resolve it with `SecretProtector.ResolveIncoming`.
* Pass every value from a request, a peer, or a file through `FormatUtilities.SanitizeForLog` before logging it.
* Call another server only through a `SourceServerClient` from `SourceServerClientFactory`. Its named `HttpClient`s carry the SSRF guard, the redirect refusal, and the retry handler.
* Never follow a redirect to another host, and never call an address a peer supplied without the SSRF guard.
* Check a Server URL with `ConfigurationUtilities.ValidateServerUrlForSsrfAsync`, which refuses plain HTTP to a public address.
* Compare a secret a caller presents with `SecretCompare.FixedTimeEquals`, never `==`.
* Write a key or secret file with `AtomicFile` and `ownerOnly: true`, so it is never readable by others, even for a moment.
* Guard every peer route with `PeerCallerGuardAttribute`, check the sender with `PeerController.Sender`, and add `RequireCompatibleProtocolAttribute` to every route but `Capabilities`. Guard every operator route with `[Authorize(Policy = "RequiresElevation")]`.
* Check the caller's user mapping before returning or streaming anything for a user.
* Build SQL with parameters only. Never put a value into SQL text.
* Keep files inside the folders they belong to with `PathContainment` and `FileNameSanitizer`.
* Escape every value placed in HTML with `escapeHtml`.
* Never put a secret or a user's data in a URL, except the link secret a `.strm` must carry.

## Tests

Every source file has a test file. See [testing.md](testing.md) for each layer, how to write tests, fakes, the Docker pool, and the matrix.

## Checklist

* [ ] Base's source and typings were searched, nothing here duplicates Base, and anything reusable went to Base first.
* [ ] Every class, method, function, and component has a header.
* [ ] Inline comments appear only above branches, and say why.
* [ ] Every changed source file has tests that pass, per [testing.md](testing.md).
* [ ] The [security](#security) rules hold.
* [ ] `dotnet build` shows no warnings.

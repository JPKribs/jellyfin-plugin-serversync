# Logging

How this plugin writes to Jellyfin's activity log and to the server log. The same rules apply to every JPKribs plugin.

The activity log is for the admin. It says what happened, to whom, in one short line. The server log is for diagnosis. It says enough to find a fault, and stays quiet on a normal day.

## Activity Log

Write every entry through Base's `ActivityLogger`, or `ActivitySummary` for events that come in bursts. Every name and overview comes from a key in `Localization/`, per [translations.md](translations.md).

### When to Write One

* A person did something an admin would want to see, such as a request approved or a user disabled.
* The plugin changed something on its own, such as sessions logged out or items synced.
* Something failed that the admin has to fix, such as a server that cannot be reached.
* Never for a run that changed nothing, a page load, or a setting read.
* Never one entry per item in a busy run. Sum them with `ActivitySummary`.

### The Name

The name says what kind of thing happened. It is generic, so the same event always has the same name.

* Write it as a noun and a past tense verb, in sentence case, such as "Issue reported" or "Sessions logged out".
* Put no person, title, server, or count in it. Those go in the overview.
* Use the plural form when the entry covers more than one, such as "Session logged out" and "Sessions logged out".

### The Overview

The overview says what the event was. It is one short sentence with every detail the admin needs, and nothing else.

* Name who did it, what it was done to, and where, such as the user, the title, and the client or server.
* Say each fact once. Nothing in the name is repeated, and no fact appears twice.
* Leave out notes, comments, and other free text a person typed. They are bloat, and the plugin's own page shows them.
* Leave out a count in brackets when it is 1, such as "Jellyfin Web" rather than "Jellyfin Web (1)".
* End with no full stop, as Jellyfin's own entries do. `ActivityText.Sentence` does this.
* Pass the user's id as `userId` when the entry is about one user.

### Examples

| Name | Overview |
| :--- | :--- |
| Issue reported | Johnny reported a video issue on Toy Story (1995) |
| Issue rescinded | Johnny rescinded their issue on Toy Story (1995) |
| Session logged out | Logged out Johnny's stale session on Jellyfin Web |
| Sessions logged out | Logged out 3 stale sessions for Johnny on Infuse (2) and Sarah on Swiftfin |
| Requests not sent | 2 requests could not be sent to Radarr |

These break the rules.

| Name | Overview | What is wrong |
| :--- | :--- | :--- |
| Issue reported by Johnny | Johnny reported an issue with Toy Story (1995) (Video), noting: TEST | The name holds the user, the user appears twice, and the note is bloat. |
| 1 Session Logged Out | 1 stale session was logged out from Jellyfin Web (1) | The name holds a count and uses title case, the count appears twice, and the user is missing. |

### Severity

| Severity | Use it when |
| :--- | :--- |
| Error | Something failed and stays failed until the admin acts. |
| Warning | Something failed or was skipped, and the plugin will retry or carry on. |
| Information | Anything else. |

## Server Log

Log through `ILogger<T>`. Jellyfin shows Information and above by default, so the level decides what the admin sees.

### Levels

| Level | Use it when | Example |
| :--- | :--- | :--- |
| Error | An operation failed and was not recovered. A feature is broken until someone acts. | Could not reach Radarr at {Url}, so 3 requests were not sent |
| Warning | Something went wrong and the plugin handled it, by retrying, skipping, or falling back. It is worth a look if it repeats. | Skipped {Path} because its metadata could not be read |
| Information | A milestone the admin would want in the log, such as a task finishing or the plugin changing something outside itself. | Synced 12 items from {Server} in {Elapsed} |
| Debug | Anything that helps diagnose a fault: each decision, each item, each call, timings, and why something was skipped. | Skipped {Item} because it is already on {Server} |
| Trace | Raw payloads and step by step detail. Rarely needed. | Response from {Url}: {Body} |

* A normal day leaves a handful of Information lines. Never log Information per item, per request, or per loop pass.
* A failure is logged once, where it is handled. Never log and rethrow, and never log the same failure at two layers.
* A retry logs Debug on each attempt and one Warning or Error when it gives up.
* An expected condition, such as an item with no artwork, is Debug, not Warning.
* Cancellation is not an error. Log it at Debug, or not at all.

### Messages

* Use a static template with named placeholders in PascalCase, such as `"Synced {Count} items from {Server}"`. Never build the message with interpolation.
* Write a short sentence in sentence case with no full stop.
* Name the thing it is about, such as the item, user, server, or path, so a reader can find it.
* Pass the exception as the first argument. Never put `ex.Message` in the template.
* Never log a secret, an API key, a token, or a password. Sanitize text from a request, a remote service, or a file before logging it.
* Server log messages stay in English. Only activity entries are translated.

## Checklist

* [ ] Every activity name is generic, in sentence case, with no person, title, or count.
* [ ] Every activity overview names who, what, and where, once each, with no note and no full stop.
* [ ] Bursts go through `ActivitySummary`, not one entry each.
* [ ] Each server log line has the level the table above gives it.
* [ ] Information lines are rare. Per item and per call lines are Debug.
* [ ] Each failure is logged once, with its exception, and no secret.

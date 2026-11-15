# Review

The check to run on your own changes before you call them done. People and AI assistants run it the same way.

## How to Review

1. Read the whole diff, every hunk, as a reviewer who did not write it.

    ```bash
    git diff
    ```

    ```bash
    git status
    ```

2. Go through each section below. Fix what fails, then read the diff again.
3. Run the checks. Read their output. A check you did not run has not passed.
4. Report what you found in the form under [Reporting](#reporting).

## Scope

* [ ] Every change serves the task. Nothing unrelated was reformatted, renamed, or moved.
* [ ] No new files exist that the task did not need, including scratch files and logs.
* [ ] Nothing is left half done, such as a setting with no UI or a route with no caller.
* [ ] Nothing on the [stop and ask](../starthere.md#stop-and-ask) list changed without approval.

## Base

* [ ] Nothing duplicates a Base component, helper, or style.
* [ ] No `jpk-` class is overridden.
* [ ] Anything another plugin could use went to Base, per [code.md](code.md#move-code-into-base).

## Words

* [ ] Every new or changed string passes the [style checklist](style.md#checklist).
* [ ] Every term matches [glossary.md](glossary.md). A new term was added there first.
* [ ] The UI, docs, messages, and code use the same word for each concept.

## UI

* [ ] Each component is the one [style.md](style.md#components) names for the need.
* [ ] Colors come from Base tokens and keep their [meaning](style.md#meaning).
* [ ] The page works at 375 px wide.
* [ ] Results use `setStatus`, confirms use `confirmDialog`, and nothing uses `Dashboard.alert` or `window.confirm`.

## Code

* [ ] Every class, method, function, and component has a header, per [code.md](code.md#comments).
* [ ] Inline comments sit only above branches and say why.
* [ ] No commented out code, TODO, or debug output.
* [ ] The [security](code.md#security) rules hold.
* [ ] `dotnet build` shows no warnings.

## Tests

* [ ] Every changed source file has tests in the mirrored folder, per [testing.md](testing.md).
* [ ] A bug fix has a test that fails without it.
* [ ] Every layer the change touches was run, and passed.

## Docs

* [ ] Every page the change affects describes the current release only.
* [ ] Labels in the docs match the page exactly.
* [ ] Screenshots of anything that changed are retaken.
* [ ] [Architecture](../docs/Architecture.md) still matches the code.
* [ ] `build.yaml` has a changelog entry for any change a user would notice.
* [ ] `mkdocs build --strict` passes.

## Reporting

Report in this order.

1. **What changed.** One line per change, with the files.
2. **What was checked.** Each command that ran and its result. Quote the failure when one failed.
3. **What was not checked.** Anything left unverified, and why, such as a pool scenario that was not run.
4. **What needs a decision.** Anything on the stop and ask list, or any rule this change could not follow.

Never report a check as passed without running it. Never call work done while a check fails.

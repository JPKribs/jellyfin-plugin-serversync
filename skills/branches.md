# Branches

How work moves between `unstable` and `master`.

## The Two Branches

| Branch | Holds | Releases from it |
| :--- | :--- | :--- |
| `unstable` | Feature work, until it is tested. | None |
| `master` | What is released, and patch fixes. | Every release |

No other branch lives on the remote. Work on a local branch, then merge it into one of these two.

## Which Branch

* **Feature.** Anything new, changed behavior, a refactor, or a Base upgrade goes to `unstable`. It ships in a feature release, which raises `F` in the version.
* **Patch.** A fix for a released bug goes straight to `master`. It ships in a bug release, which raises `B`.

## Dependencies

Renovate opens its dependency pull requests against `unstable`, so a dependency raise ships with the next feature release.

## Sync

`.github/workflows/sync-branches.yaml` merges `master` into `unstable` on every push to `master`. A patch and the manifest update after a release reach `unstable` with no step by hand.

A merge that conflicts opens a pull request from `sync/master-into-unstable` into `unstable`. Resolve it there, merge it, and delete the branch.

To sync by hand, start **Sync Branches** from the Actions tab with `sync`.

## Feature Release

1. Test `unstable` on a server.
2. Set the release on `unstable`, per [release.md](release.md).
3. Start **Sync Branches** from the Actions tab with `promote`. It moves `master` to `unstable` and starts the release draft, build, and test runs.
4. Publish the draft release as usual.

`promote` refuses when `unstable` is missing a commit from `master`. Run `sync` first, and merge its pull request if it opens one.

## Patch Release

1. Commit the fix on `master`.
2. Set the release on `master`, per [release.md](release.md), and publish it.
3. The sync carries the fix and the manifest update to `unstable`.

# Release

How to cut a release of this plugin, from the version number to the manifest every server reads.

## Versions

A version is `JJ.JJ.F.B`.

| Part | Means | Raise it when |
| :--- | :--- | :--- |
| `JJ.JJ` | The Jellyfin version the release targets, such as `12.4` | The plugin moves to a new Jellyfin version |
| `F` | The feature release | The release adds or changes behavior a user will notice |
| `B` | The bug release within that feature | The release only fixes bugs |

Raising a part sets every part after it to `0`. `12.4.13.2` becomes `12.4.14.0` for a feature and `12.4.13.3` for a fix.

## Branch

A feature release is cut on `unstable`, then **Sync Branches** with `promote` moves `master` to it. A bug release is cut on `master`, and the sync carries it to `unstable`. See [branches.md](branches.md).

## Before the Release

1. **Base.** When the release needs a Base change, release Base first with its **Release** workflow, then raise `JPKribs.Jellyfin.Base` in `Jellyfin.Plugin.ServerSync/Jellyfin.Plugin.ServerSync.csproj` and delete any local copy Base replaces. Wait until NuGet serves the version, and never release against a local package. Raise Base on a branch, and merge it once every check below has passed on it.
2. **Protocol.** When a hint, a peer route, or a conflict rule changed in a way an older server would misread, raise `HintProtocol.ProtocolVersion`. Raise `HintProtocol.MinProtocolVersion` only when this release can no longer talk to the protocol before it.
3. **Database.** When the schema changed, raise `SyncSchema.CurrentSchemaVersion`. Raise `SyncSchema.MinReaderVersion` only when a migration changes or removes something older releases rely on.
4. **Build.**

    ```bash
    dotnet build
    ```

5. **Tests.**

    ```bash
    dotnet test
    ```

    ```bash
    node --test 'Jellyfin.Plugin.ServerSync.Tests/js/*.test.mjs'
    ```

6. **Pool.** Run the full pool from a clean state.

    ```bash
    python3 tools/integration/run.py reset
    ```

    ```bash
    python3 tools/integration/run.py test
    ```

    Run the matrix too when the release changes how content or links follow file changes, or how files are downloaded or written. See [testing.md](testing.md#matrix).

    Every check must run on the commit that is released. Run them again after any later change.

7. **Docs.** Every page describes this release. Retake any screenshot whose subject changed, and all of them when the Base version changed. Update [Versioning](../docs/Versioning.md) when the protocol, the schema reader, or the upgrade path changed, and [Architecture](../docs/Architecture.md) when the parts or their connections changed.

    ```bash
    mkdocs build --strict
    ```

8. **Review.** Run [review.md](review.md) over everything since the last release.

## Set the Release

Edit `build.yaml`.

1. Set `version` to the new version.
2. Replace the whole `changelog` with this release's entries. Each release lists only its own changes. See [documentation.md](documentation.md#release-notes) for how to write them.
3. Say in `changed` when the protocol changed, so a user knows to update every server in the pool.
4. Name the Base version in `developer` when it changed, such as "Uses JPKribs.Jellyfin.Base 2026.11.17", with what it replaced locally.
5. Keep `description` and `overview` true for this release. `manifest.json` takes its own description, so update both together.

Commit it on its own.

```bash
git commit -am "Set the release to 12.4.14.0"
```

```bash
git push
```

## Publish

1. **Draft.** The **Create/Update Release Draft** workflow builds the zip and writes a draft release named `v<version>` with the changelog as its notes. A push that only changes `build.yaml` does not start it, so start it from the Actions tab.
2. **Check.** Open the draft on GitHub. The notes must match `build.yaml`, and the zip and its `.md5` must be attached.
3. **Release.** Publish the draft. The **Publish Plugin** workflow takes the attached zip's checksum, adds the version to `manifest.json`, and commits "Update manifest for release v<version>".
4. **Pull.**

    ```bash
    git pull
    ```

5. **Verify.** On a test server, add the repository URL, install the release from the catalog, restart, and open each tab.

## When a Release Is Wrong

* Never edit or reuse a published version. Fix it in a new bug release.
* When a release breaks servers, publish the fix as the next bug release at once.
* When the manifest entry itself is wrong, run **Publish Plugin** by hand with the release tag after fixing the cause.

## Checklist

* [ ] The version follows `JJ.JJ.F.B`, and later parts are `0`.
* [ ] Base is released and referenced, with local copies deleted.
* [ ] The protocol and schema versions are raised when needed.
* [ ] `dotnet build`, `dotnet test`, and the JS tests pass on the released commit.
* [ ] The pool passes from `reset`, and the matrix when needed.
* [ ] Docs, screenshots, Versioning, and Architecture describe this release.
* [ ] `mkdocs build --strict` passes.
* [ ] The changelog lists only this release, in the four keys.
* [ ] The draft matches `build.yaml` before it is published.
* [ ] The manifest commit is pulled.

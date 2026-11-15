# Testing

How to test this plugin, from a single method to a pool of real servers. A change is done when the tests for every layer it touches pass.

## The Rule

Every source file has a test file, and every test proves that given inputs produce the expected output. A bug fix starts with a test that fails without the fix.

## Layers

Test each behavior at the lowest layer that can prove it. Go up a layer only for what the lower one cannot reach.

| Layer | Proves | Runs | Time |
| :--- | :--- | :--- | :--- |
| Unit | One class or function gives the right output for each input | `dotnet test` | Seconds |
| Store | SQL, schema, and migrations against a real SQLite file | `dotnet test` | Seconds |
| Controller | A route's status codes, guards, and response shape | `dotnet test` | Seconds |
| JavaScript | Page logic, such as formatting, filters, and state | `node --test` | Seconds |
| API integration | Every call to another server works against a real Jellyfin | `dotnet test --filter` | Minutes |
| Pool scenario | Servers sync with each other end to end | `integration/run.py` | Minutes to an hour |
| Matrix | Every pool shape ends up holding what the main server holds after file changes | `integration/matrix.py` | Hours |

| A change to | Needs |
| :--- | :--- |
| A utility, model, or decider | Unit |
| A store, table, or the schema | Store |
| A controller or route attribute | Controller |
| A config page script | JavaScript |
| A `SourceServerClient` call | API integration |
| Hints, peers, links, or pairing | Pool scenario |
| How content or links follow file changes | Matrix |

## Layout

Mirror the source folders inside the test project so a missing test is easy to spot.

| Source | Test |
| :--- | :--- |
| `Jellyfin.Plugin.ServerSync/Services/Queue/VersionDecider.cs` | `Jellyfin.Plugin.ServerSync.Tests/Services/Queue/VersionDeciderTests.cs` |
| `Jellyfin.Plugin.ServerSync/Controllers/HintsController.cs` | `Jellyfin.Plugin.ServerSync.Tests/Controllers/HintsControllerTests.cs` |
| `Jellyfin.Plugin.ServerSync/Configuration/serversync_shared.js` | `Jellyfin.Plugin.ServerSync.Tests/js/serversync_shared.test.mjs` |

* Shared fakes live in `Jellyfin.Plugin.ServerSync.Tests/Fakes/`.
* The API integration suite lives in `Jellyfin.Plugin.ServerSync.Tests/ApiIntegration/`.

## C# Tests

Tests use xUnit with plain `Assert`. There is no mocking library.

```csharp
/// <summary>
/// Equal values adopt the incoming version without a write.
/// True: a copy that already matches raises nothing and ends the loop.
/// False: every hint would be written back and re-hinted forever.
/// </summary>
[Fact]
public void Decide_EqualValues_Adopts()
{
    var local = Version("source-a", 1);
    var incoming = Version("source-b", 2);

    var result = VersionDecider.Decide(local, incoming, valuesEqual: true);

    Assert.Equal(VersionDecision.Adopt, result);
}
```

### Naming and Comments

* Name the class `<Subject>Tests` and each method `Method_Scenario_Expectation`.
* Give the class a one sentence summary of what its subject must guarantee.
* Give each test the three line summary. What it proves, what passing means, and what failing would cause.

### Writing a Test

* Arrange, act, and assert, separated by blank lines.
* Assert one behavior per test. Several asserts are fine when they check one outcome.
* Use `[Theory]` with `[InlineData]` for one rule over several inputs.
* Test the edges. Null, empty, the largest value, a cancelled token, a peer that answers with an error, and a peer that never answers.
* Never sleep. Pass a clock or a delay in, and control it from the test.
* A test never reaches the network. Use a stub `HttpMessageHandler`.

### Stores

* Use a real SQLite file in a temp folder, and delete it in `Dispose`.
* Open it through `SyncSchema` so the test runs the same create and migrate as the plugin.
* Test a migration by writing the older schema by hand, opening it, and checking the result.

### Fakes

Write fakes by hand and share them.

| Fake | Stands in for |
| :--- | :--- |
| `FakeConfigManager` | The plugin configuration |
| `FakeClientFactory` | The factory that builds a `SourceServerClient` |
| `StubHandler` | An `HttpMessageHandler` that answers from a table |
| `ScriptedHandler` | An `HttpMessageHandler` that answers in sequence |

* Keep each fake in `Fakes/`, one per file, with a header comment like any other class.
* Never copy a fake into a test file. Extend the shared one.
* A fake records what it was asked so the test can assert on it.

```bash
dotnet test
```

## JavaScript Tests

JS tests run on node's own test runner with Base's fake DOM and no dependencies.

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatLag } from '../../Jellyfin.Plugin.ServerSync/Configuration/serversync_shared.js';

test('formatLag shows minutes for a lag under an hour', () => {
    assert.equal(formatLag(90), '2 minutes');
});
```

```bash
node --test 'Jellyfin.Plugin.ServerSync.Tests/js/*.test.mjs'
```

* Name each test as a sentence about the behavior.
* Test logic, not layout. Move logic out of event handlers into exported functions so a test can call them.
* Use Base's `fake-dom.mjs` when a function needs elements.

## API Integration Tests

The suite exercises every call the plugin makes to another server against a real Jellyfin. It only sends GET and HEAD requests, so it never changes the server. It is skipped unless both variables are set.

```bash
SERVERSYNC_TEST_SERVER_URL=http://localhost:8111 SERVERSYNC_TEST_API_KEY=<key> dotnet test --filter "FullyQualifiedName~ApiIntegration"
```

* Add a test here for every new call in `SourceServerClient`.
* A test that finds nothing to check fails and says what to add to the server. It never passes on nothing.

## Pool Scenarios

`integration/run.py` runs three Jellyfin servers, `source-a`, `source-b`, and `local`, with the plugin built from this checkout. See [documentation.md](documentation.md#the-docker-pool) for addresses, credentials, and its rules.

```bash
python3 integration/run.py up
```

```bash
python3 integration/run.py scenarios links chaos
```

```bash
python3 integration/run.py reset
```

```bash
python3 integration/run.py test
```

`scenarios` runs every scenario whose title holds any of the words. `test` builds, seeds, runs every scenario, then runs the API suite against `source-a`. A full run takes about 45 minutes.

### Writing a Scenario

```python
def scenario_reorder_rehomes(creds, apis):
    local = apis["local"]
    configure_local(creds, local, ["source-b", "source-a"])
    run_task(local, "ServerSyncUpdateTables")
    rows = content_rows(local)
    expect(rows["Shared Movie (2020).mp4"]["ServerName"] == "source-b", "shared title re-homed to the new first server")
```

* Name the function `scenario_<area>_<behavior>` and give it a one line docstring.
* Add it to `SCENARIOS` as `("<what it proves>", scenario_<name>)`. Start the title with its area, since `scenarios` filters on the words in the title.
* Write each `expect` message as the thing that should be true, in lowercase.
* Leave the pool as you found it. Put back any setting the scenario changed.
* Use the seeded titles and users. Add seed data to `seed_media` rather than inside a scenario.

### Rules

* Run `reset` before a full `test`. Leftover link rows trip the prune safety limit.
* Never sign in as admin from another script during a run. It revokes the harness token.
* Never open `sync.db` with sqlite on the host while the pool is up.
* Run `reset`, then `test`, before every release.

## Matrix

`integration/matrix.py` seeds films on `n1`, syncs them out, then changes them the ways a real library changes. A re-encode, a new container, a rename, a moved folder, and a removal. Each other server must end up holding exactly what `n1` holds.

| Shape | Pool |
| :--- | :--- |
| `1:1` | `n2` pulls from `n1` |
| `1=1` | `n1` and `n2` sync with each other |
| `1:2` | `n2` and `n3` each pull from `n1` |
| `1:1:1` | `n1`, `n2`, and `n3` all sync with each other |
| `1+3` | `n2`, `n3`, and `n4` pull from `n1` and sync with each other |

```bash
python3 integration/matrix.py run 1:1 1=1 link hints
```

```bash
python3 integration/matrix.py reset
```

* With no arguments every shape runs as copies and as links with live changes on, and `1:1` also runs with live changes off.
* `interrupt`, `holders`, `own`, and `idle` run single rounds.
* The matrix servers answer on ports 8121 to 8124.
* Run the matrix for any change to how content or links follow file changes.

## Checklist

* [ ] Every changed source file has tests, in the mirrored folder.
* [ ] A bug fix has a test that fails without it.
* [ ] Edges are covered, including null, empty, cancelled, and a failing peer.
* [ ] Fakes come from `Fakes/`.
* [ ] `dotnet test` passes.
* [ ] The JS tests pass.
* [ ] Matching pool scenarios pass for any change between servers.
* [ ] The matrix passes for any change to how files follow the source.

# Tests

## Unit Tests

Run with no setup:

```
dotnet test
```

The tests use xUnit with plain `Assert` and no mocking library. See [skills/testing.md](../skills/testing.md) for how to write them.

### Layout

Each test file sits in the folder that mirrors the source file it tests, so `Jellyfin.Plugin.ServerSync/Services/Queue/VersionDecider.cs` is tested by `Services/Queue/VersionDeciderTests.cs`. The test namespace follows the folder.

| Folder | Tests |
| :--- | :--- |
| `Configuration/` | `PluginConfiguration` |
| `Controllers/` | Routes and their attributes |
| `Models/` | Models, DTOs, and comparators, in the source's subfolders |
| `Services/` | Stores, merge services, clients, links, queue, peers, and playlists, in the source's subfolders |
| `Tasks/` | Scheduled tasks and their base classes |
| `Utilities/` | Helpers and streams |
| `Fakes/` | Shared fakes, one per file |
| `ApiIntegration/` | The read-only suite against a real server |
| `js/` | JavaScript tests, run with `node --test` |

`PluginTests.cs` covers `Plugin.cs`, and `StableSecretProtectionTests.cs` covers the secret protector the plugin builds on Base.

### Fakes

`Fakes/` holds the stand-ins tests share.

| Fake | Stands in for |
| :--- | :--- |
| `FakeConfigManager` | The plugin configuration manager |
| `FakeClientFactory` | The client factory, for tests that never reach a source |
| `StubPeerClientFactory` | The client factory, building clients over a `StubPeerHandler` |
| `FixedProvider` | The database provider, handing back one database |
| `StubHandler` | An `HttpMessageHandler` that answers from a function |
| `ScriptedHandler` | An `HttpMessageHandler` that answers in sequence |
| `StubPeerHandler` | A peer that answers the connection test and the playlist listing |
| `InterfaceStub` | A large Jellyfin interface, recording every call |
| `PlaylistTestHarness` | A playlist mirror over a real store and recording Jellyfin stubs |

## API Integration Tests

A read-only suite that exercises every source server call the plugin makes (pagination, person catalog parity, image probes, downloads) against a real Jellyfin server. These tests are skipped unless you point them at a server:

```
export SERVERSYNC_TEST_SERVER_URL=http://localhost:8096
export SERVERSYNC_TEST_API_KEY=<api key from that server>
dotnet test --filter "FullyQualifiedName~ApiIntegration"
```

The integration tests only issue GET and HEAD requests and never modify the target server. The person catalog parity test downloads the full person list twice, so expect a few minutes of runtime against large servers. A test that finds nothing on the server to check, such as no item with an image or an external subtitle, fails and says what to add, rather than passing on nothing.

## Docker Pool

`integration/run.py` runs three Jellyfin servers in Docker, source-a, source-b, and local, with the plugin built from this checkout, and drives every module across them, links included.

```
python3 integration/run.py test        # build, start the pool, run every scenario, then the API tests against source-a
python3 integration/run.py scenarios links chaos   # scenarios whose title has any of these words, on the running pool
python3 integration/run.py up          # build and start the pool, signed in and seeded
python3 integration/run.py reset       # throw the pool away and start fresh
python3 integration/run.py down        # stop it
```

The servers answer on ports 8111, 8112, and 8113, and the admin password is in `integration/.state/credentials.json`. Local has a small 64 MB disk at `/media-tiny`, so a scenario can fill the free space floor for real. The `links` scenarios cover playback, seeking, transcoding, subtitles inside a file, media info and trickplay images from the source, direct play per user, the link check, the link cache and its rules, a user's own stream limit, speed limits, versions under a bitrate limit, hostile requests, a source going down, restarts, renames, removals, and more. The `chaos` scenarios kill the source mid-stream, tamper with links, lose the link key, fuzz every route, and link three hundred films at once.

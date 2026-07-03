# Tests

## Unit Tests

Run with no setup:

```
dotnet test
```

## API Integration Tests

A read-only suite that exercises every source server call the plugin makes (pagination, person catalog parity, image probes, downloads) against a real Jellyfin server. These tests are skipped unless you point them at a server:

```
export SERVERSYNC_TEST_SERVER_URL=http://localhost:8096
export SERVERSYNC_TEST_API_KEY=<api key from that server>
dotnet test --filter "FullyQualifiedName~ApiIntegration"
```

The integration tests only issue GET and HEAD requests and never modify the target server. The person catalog parity test downloads the full person list twice, so expect a few minutes of runtime against large servers.

# Profiling

How to find and fix slow C# in this plugin. It covers CPU, memory, allocations, locks, SQLite, and HTTP. Every other language is out of scope.

## When to Profile

Profile only when there is a reason. Never profile to look for something to improve.

Start when one of these is true.

* A user or a test reports that something is slow, uses too much memory, or starves playback.
* A change adds or alters a loop, a query, or a call to another server on a path that runs for every item, such as a Scan, a Sync task, a sync table, or a hint worker.
* A number in a scenario or a task run grew from one release to the next.

Without one of these, do not profile. Code that has no symptom and no change is not a problem.

## The Tiers

Profiling has three tiers. Start at Tier 1. Go up a tier only when the one below could not answer the question. Most issues end at Tier 1 or Tier 2.

| Tier | Answers | Budget | Needs the pool |
| :--- | :--- | :--- | :--- |
| 1. Time One Method or Query | Is this code or this query slow by itself | 10 minutes | No |
| 2. Trace One Task | Which code dominates one slow task or route | 30 minutes | Yes |
| 3. Go Deeper | Why memory grows, or what a second path costs | Ask first | Yes |

* **Tier 1** is a temporary timing test and `EXPLAIN QUERY PLAN` on a small copy of `sync.db`. It needs no pool and no seed.
* **Tier 2** is one CPU trace and counters of one task or route. Pick the one path the issue points at, and seed the pool once with the [recipe](#seeding-the-pool).
* **Tier 3** is a heap dump, a scaled copy of the database, or a second path. Stop and ask before you start it.

### The Budget

* Each tier has a time budget. When it runs out, stop and report what you have, even when it is inconclusive.
* Profile one path per issue. Never sweep every path "to be sure".
* Stop as soon as the goal is met or the cause is proven.
* Write the tier, the path, and the stop reason in the report.

## The Rule

Measure first, change second, measure again. A change made for speed is not done until a number shows it is faster and the tests still pass.

* Never optimize from a guess. A hot path you cannot show is not a hot path.
* Never trade correctness, a safety limit, or a [security](code.md#security) rule for speed.
* Keep the simplest code that meets the goal. Stop when the goal is met.

## Goals

State the goal before you measure. A goal names the operation, the data size, and the limit.

| Good | Bad |
| :--- | :--- |
| A Scan of 50,000 items finishes in under 60 seconds | Make the Scan faster |
| `/Hints` handles 200 hints a second under 100 MB | Reduce memory |

The plugin runs inside Jellyfin on the user's server. Treat its CPU, memory, and disk as shared. A task that is fast but starves playback has failed.

## Check These First

These causes are common. Check each against the code before you measure anything.

| Check | Usual cause |
| :--- | :--- |
| A loop that asks Jellyfin or SQLite the same question for every item or every user and item pair | The answer does not change inside a run, so ask once per run |
| A sync table query that sorts a whole table | The sort has no index, so a deep page reads and sorts every row |
| A request to a source that asks for fields nothing reads | The payload is larger than the work needs |
| Work repeated for items that have not changed | The Stamp or modified time is never checked |
| A log string built in a hot loop | The string is built even when the level is off |

If the code shows the cause, a Tier 1 test is enough to prove it.

## Seeding the Pool

Tier 2 needs data that is big enough to show the problem and no bigger.

* Seed once, with about 5,000 small files in a source library, then scan. Never reseed between runs.
* Run the pool scenarios that fill the tables the path reads, such as `content`, `history`, or `hints`.
* Write down the data size in the report.
* Never build a bigger seed to prove a scaling claim. Copy `sync.db` out and scale the copy at Tier 3.

## Tools

Use only the tools Microsoft ships for .NET. They need the .NET 10 SDK, the same as the repo, so check it first.

```bash
dotnet --list-sdks
```

Install the tools once. Update them with `dotnet tool update --global` and the same name.

```bash
dotnet tool install --global dotnet-trace
```

```bash
dotnet tool install --global dotnet-counters
```

```bash
dotnet tool install --global dotnet-gcdump
```

The tools install to `~/.dotnet/tools`. On macOS that folder can be missing from `PATH`, because the dotnet installer writes the literal text `~/.dotnet/tools` to `/etc/paths.d`, and a shell does not expand it. Add this line to `~/.zshrc` and open a new shell.

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
```

Check that each tool runs.

```bash
dotnet-trace --version
```

| Question | Tool |
| :--- | :--- |
| Which methods use the CPU | `dotnet-trace` |
| Is the process healthy over time | `dotnet-counters` |
| What holds the memory | `dotnet-gcdump` |
| How fast is this one method | A temporary test with `Stopwatch` and `GC.GetAllocatedBytesForCurrentThread()` |
| How long did this run take | A `Stopwatch` in a temporary test, or `dotnet test` timing |
| Which SQL is slow | `EXPLAIN QUERY PLAN` and a timed test against a real SQLite file |

A trace is a `.nettrace` file, which `dotnet-trace` writes by default. Open it in PerfView on Windows, or turn it into a speedscope file and open that at [speedscope.app](https://www.speedscope.app). Pass `--format Speedscope` to `collect` to get both files in one step, or convert a trace later.

```bash
dotnet-trace convert run.nettrace --format Speedscope -o run
```

This writes `run.speedscope.json`. Keep every trace, dump, and test output outside the repository.

## Tier 1: Time One Method or Query

A method with no I/O is best measured alone. For a query, run `EXPLAIN QUERY PLAN` on a copy of `sync.db`, as in the last step of Tier 2, and time the query in a test against a real SQLite file.

* Write a temporary xUnit test in the test project that times the method with `Stopwatch` and reads `GC.GetAllocatedBytesForCurrentThread()` before and after. Take the difference on the same thread, since the counter covers only the current thread. Write the numbers with `ITestOutputHelper`, and read them with `--logger "console;verbosity=detailed"`. Never commit the test.
* Build and run in `Release`, with `dotnet test -c Release`, and without a debugger.
* Warm up first. Call the method a few times before you measure, so the JIT is not in the number.
* Run it many times and take the median. One run proves nothing.
* Run it before and after the change, on the same machine, with nothing else busy.
* Give it realistic input. A list of three items hides what a list of 50,000 shows.
* Never commit the test, its output, or any trace.

To see where the time goes inside the method, trace the test host. Make the test loop for at least 10 seconds, so a trace has time to attach. Do not use `dotnet-trace collect -- dotnet test`. It traces the `dotnet test` command and not the test code, and it can hang. Run the tests in one shell and attach from another.

```bash
dotnet test -c Release --filter FullyQualifiedName~MyTimingTest
```

Find the test host process id, which is the newest `testhost` process.

```bash
pgrep -f testhost
```

```bash
dotnet-trace collect --process-id <pid> --duration 00:00:08 --format Speedscope -o ~/Desktop/method.nettrace
```

For what holds the memory, take a heap dump of the same process, and read it.

```bash
dotnet-gcdump collect --process-id <pid> -o ~/Desktop/method.gcdump
```

```bash
dotnet-gcdump report ~/Desktop/method.gcdump
```

## Tier 2: Trace One Task

The plugin runs in Jellyfin's process, so attach to that process. Use the Docker pool so the data is real and the run repeats. Trace one task or one route, the one the issue points at.

The Jellyfin image is Linux and runs Jellyfin as process 1 of each container, as root. It is self contained, so it has no `dotnet` and no diagnostic tools. A tool on the Mac cannot reach a process inside the Docker VM. So the tool must run inside the container, from the same `/tmp` as Jellyfin, where Jellyfin opens its diagnostic socket.

1. Bring up the pool and run the scenario that is slow. The pool builds the plugin in `Release`.

    ```bash
    python3 tools/integration/run.py up
    ```

2. Install the tools in the container that runs the slow work, which is usually `serversync-it-local`. Use the standalone Linux builds that match the container CPU, which `docker exec serversync-it-local uname -m` prints. Use `linux-arm64` for `aarch64` and `linux-x64` for `x86_64` in the address.

    ```bash
    docker exec serversync-it-local curl -sL -o /tmp/dotnet-trace https://aka.ms/dotnet-trace/linux-arm64
    ```

    ```bash
    docker exec serversync-it-local chmod +x /tmp/dotnet-trace
    ```

    Repeat both for `dotnet-counters` and `dotnet-gcdump`. These builds are not self contained, and they stop with "You must install .NET" because the Jellyfin image has no runtime they can find. Unpack a .NET 8 runtime beside them and point `DOTNET_ROOT` at it on every call.

    ```bash
    docker exec serversync-it-local sh -c 'mkdir -p /tmp/dotnet-rt && curl -sL https://aka.ms/dotnet/8.0/dotnet-runtime-linux-arm64.tar.gz | tar -xz -C /tmp/dotnet-rt'
    ```

    ```bash
    docker exec -e DOTNET_ROOT=/tmp/dotnet-rt serversync-it-local /tmp/dotnet-trace ps
    ```

    The list shows `jellyfin` as process 1.
3. Record a CPU trace of Jellyfin, which is process 1, while the slow run goes. Use the `dotnet-sampled-thread-time` profile. The default profile adds JIT and loader events and makes a trace of 100 MB or more for a few minutes.

    ```bash
    docker exec -e DOTNET_ROOT=/tmp/dotnet-rt serversync-it-local /tmp/dotnet-trace collect --process-id 1 --profile dotnet-sampled-thread-time --duration 00:01:00 -o /tmp/run.nettrace
    ```

    The trace ends by itself after the duration, so pick a duration longer than the run. To end it sooner, send `SIGINT` to the tool. The image has no `ps` or `pkill`, so find the process in `/proc` and name it in `kill`.

    ```bash
    docker exec serversync-it-local sh -c 'for p in /proc/[0-9]*; do case "$(tr "\0" " " < $p/cmdline)" in /tmp/dotnet-trace*) kill -INT ${p#/proc/};; esac; done'
    ```

    Stopping can take seconds to minutes, so wait for the tool to exit before copying the file. A trace copied while the tool still runs is cut short and has no method names.
4. Copy the trace out to a folder outside the repo, then convert it on the Mac.

    ```bash
    docker cp serversync-it-local:/tmp/run.nettrace ~/Desktop/run.nettrace
    ```

    ```bash
    dotnet-trace convert ~/Desktop/run.nettrace --format Speedscope -o ~/Desktop/run
    ```

5. Read the `.speedscope.json` file in speedscope, or sum it with a script. Every thread is sampled, so an idle thread shows as `UNMANAGED_CODE_TIME`, and a thread running managed code shows as `CPU_TIME`. Native work such as SQLite and file calls also shows as `UNMANAGED_CODE_TIME`, so credit it to the nearest plugin frame above it. Ignore `System.GC.RunFinalizers`, which reports the idle finalizer thread as busy. A plugin frame that wraps Jellyfin's own work, such as an action filter, is inclusive of that work, so look at its callees before blaming it.
6. For counters, record while the run goes. The output is `.json` and ends without its closing brackets when the tool is stopped with `SIGINT`, so add them before parsing. Read allocation (`dotnet.gc.heap.total_allocated`), collections, thread pool queue length, and lock contentions (`dotnet.monitor.lock_contentions`). Counters named `/ 1 sec` are per second, so add them up over the run.

    ```bash
    docker exec -e DOTNET_ROOT=/tmp/dotnet-rt serversync-it-local /tmp/dotnet-counters collect --process-id 1 --counters System.Runtime --format json --duration 00:01:00 -o /tmp/counters
    ```

7. For what holds the memory, take a heap dump in the middle of the run and again after it, and read both. The report lists types by size, so sort it by size times count.

    ```bash
    docker exec -e DOTNET_ROOT=/tmp/dotnet-rt serversync-it-local /tmp/dotnet-gcdump collect --process-id 1 -o /tmp/mid.gcdump
    ```

    ```bash
    dotnet-gcdump report ~/Desktop/mid.gcdump
    ```

8. To time the Sync tab routes, use the API key in `tools/integration/.state/credentials.json` under `servers`, not the admin login. Send it in an `Authorization: MediaBrowser Token="<key>"` header. The `X-Emby-Token` header and the `api_key` query are refused.

    ```bash
    curl -s -o /dev/null -w "%{time_total}" -H 'Authorization: MediaBrowser Token="<key>"' "http://localhost:8113/Sync/Items?take=50"
    ```

9. To read the query plans, copy the whole data folder out, since the database runs in WAL mode, and open the copy only.

    ```bash
    docker cp serversync-it-local:/config/data/serversync/. ~/Desktop/dbcopy
    ```

A tool inside a container is lost when the container is recreated, so install it again after `down` or after `up` recreates the pool.

Follow the pool [rules](testing.md#rules). Never sign in as admin from another script during a run, and never open `sync.db` with sqlite on the host while the pool is up. Copy it out of the container first when you need to inspect it.

## Tier 3: Go Deeper

Stop and ask before you start Tier 3. Say what Tier 2 left unanswered and what you will do.

* Take a heap dump in the middle of the run and after it, per the memory step of Tier 2.
* Scale a copy of `sync.db` to the size the issue needs, and run the queries against the copy. Never open the live database.
* Trace a second path only when the first trace points at it.

## Reading a Trace

* **Inclusive time** is a method plus everything it calls. **Exclusive time** is the method alone. Start from the largest inclusive plugin frame and walk down to the largest exclusive one.
* A frame in `System.Threading` or a wait is time spent blocked. Find what it waits on, since more CPU will not help.
* Many small frames in the allocator or the GC point to allocation pressure. Read the allocation counters, then find the allocating call.
* Time spent in a `Task` that never shows CPU is waiting on I/O. Count the calls before you tune one call.

## Common Fixes

Try these in order. Each is cheaper and safer than the next.

1. **Do less.** Skip an item whose [Stamp](glossary.md#syncing) or modified time has not changed. Stop a pass at the first answer.
2. **Do it once.** Hoist work out of a loop. Cache a result for the length of one run, never across runs unless it is invalidated.
3. **Do it in bulk.** One query for a set, not one per row. One transaction for a batch of writes. One page, not the whole table.
4. **Do it together.** Run independent calls with `ConcurrentTaskRunner` from Base, with a limit that respects the other server.
5. **Allocate less.** Use `Span<T>`, `StringBuilder`, pooled buffers, and `static` lambdas where a trace shows allocation pressure. Never do this without a number.
6. **Stream, never buffer.** Read a file or a response as a stream. Never load a large file into memory.

### SQLite

* Run `EXPLAIN QUERY PLAN` on any query that reads a large table. A `SCAN` of a big table needs an index or a different query.
* An index is a schema change. It is a [stop and ask](../starthere.md#stop-and-ask) item.
* Batch writes in one transaction, and keep a transaction short so it does not block readers.
* Select only the columns you use.

### Async and Threads

* Never block on a task with `.Result` or `.Wait()`. It starves the thread pool.
* Pass the `CancellationToken` through, so a stopped run stops working.
* Hold a lock for the shortest time, and never across an `await`.
* Bound every queue and every degree of parallelism.

### HTTP

* Reuse the named `HttpClient`s in `PluginServiceRegistrator`. Never build a new one per call.
* Make fewer, larger calls over many small ones when the source offers a bulk route.
* Never loosen the SSRF guard, the redirect refusal, or the retry handler for speed.
* Copy a download with Base's `StreamUtilities.CopyThrottledAsync` inside `CopyWithIdleTimeoutAsync`. Never write a copy loop, a speed limit, or a stall check by hand.

### Logging

* Use the `ILogger` templates in [code.md](code.md#c-conventions). Never build the message with string interpolation.
* A log in a hot loop costs even when the level is off. Move it out of the loop or guard it with `IsEnabled`.

## Proving the Result

1. Measure the same scenario before and after, with the same data and the same machine. Record both numbers.
2. Run the tests for every layer the change touches, per [testing.md](testing.md#layers).
3. For a change to how content, links, hints, or peers behave, run the matching pool scenarios. Run the matrix for a change to how files follow the source. A faster wrong answer is a bug.
4. Check that memory and the thread count stay flat across a long run.
5. Report the numbers in the commit message or the report, as before and after for the same operation.

## What Not to Do

* Never ship a profiler, a timing test, a `Stopwatch`, an allocation counter, or a timing log in the plugin.
* Never commit trace files, gcdumps, or timing output.
* Never raise a timeout or lower a retry count to hide a slow call.
* Never remove a [prune safety limit](glossary.md#housekeeping), a check, or a guard to gain speed.
* Never profile a Debug build. Use `Release`.
* Never change a [stop and ask](../starthere.md#stop-and-ask) item without approval, even when it is the cause.

## Checklist

* [ ] The reason to profile is a symptom, a change to a hot path, or a number that grew.
* [ ] The tier, the path, and the stop reason are in the report.
* [ ] The goal names the operation, the data size, and the limit.
* [ ] A trace, a counter, or a timed test shows where the time or memory goes.
* [ ] The fix is the simplest one that meets the goal.
* [ ] Before and after numbers exist for the same operation and data.
* [ ] A `Release` build was measured.
* [ ] The tests for every touched layer pass, and the matching pool scenarios and matrix pass.
* [ ] No profiler code, trace file, timing test, or output is in the diff.
* [ ] Anything on the stop and ask list, such as an index, was approved first.

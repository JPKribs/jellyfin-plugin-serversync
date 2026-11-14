#!/usr/bin/env python3
"""What happens on every other server when files change on one, across four pool shapes.

Usage:
  matrix.py run [shape ...] [copy|link] [hints|scan]   build, start the pool, run the chosen rounds
  matrix.py run interrupt [copy|link]                  only the rounds that kill a server partway
  matrix.py run holders                                only the round where two servers hold one film
  matrix.py run own                                    only the round for films a server already had
  matrix.py down                                        stop the pool, keeping its state
  matrix.py reset                                       stop the pool and delete its state

Shapes:
  1:1     n1 is the main server and n2 pulls from it
  1=1     n1 and n2 are equal partners, each syncing with the other
  1:2     n1 is the main server and n2 and n3 each pull from it
  1:1:1   n1, n2, and n3 are equal partners, each syncing with the other two
  1+3     n1 is the main server, n2, n3, and n4 each pull from it and sync with each other

Every round seeds a set of films on n1, syncs them out, then changes them on n1 the ways a library
really changes: a re-encode under the same name, a new container under the same name, a new file name,
a moved folder, a removal, and a new container that sits beside the old file for a while. Each other
server is then expected to end up holding exactly what n1 holds, as copies or as links, with every
film playing the bytes n1 has now.

With no arguments every shape runs as copies and as links, with live changes on, and 1:1 also runs
with live changes off so the scheduled scan is exercised alone. Only the standard library is used.
"""
import json
import os
import shutil
import subprocess
import sys
import time
import uuid

import run as it

HERE = os.path.dirname(os.path.abspath(__file__))
STATE = os.path.join(it.STATE, "matrix")
NODES = ["n1", "n2", "n3", "n4"]
ORIGIN = "n1"

it.STATE = STATE
it.CREDS = os.path.join(STATE, "credentials.json")
it.SERVERS = {n: {"port": 8120 + i, "internal": f"http://{n}:8096", "role": "node"} for i, n in enumerate(NODES, 1)}
it.LIBRARIES = {n: {"Movies": ("movies", [])} for n in NODES}

# shape -> (the servers that take part, the servers expected to follow n1, whether n1 pulls too,
#           whether the followers sync with each other)
SHAPES = {
    "1:1": (["n1", "n2"], ["n2"], False, False),
    "1=1": (["n1", "n2"], ["n2"], True, True),
    "1:2": (["n1", "n2", "n3"], ["n2", "n3"], False, False),
    "1:1:1": (["n1", "n2", "n3"], ["n2", "n3"], True, True),
    "1+3": (["n1", "n2", "n3", "n4"], ["n2", "n3", "n4"], False, True),
}

VIDEO = (".mkv", ".mp4", ".strm")
log, expect, wait_until = it.log, it.expect, it.wait_until


def compose(*args):
    env = dict(os.environ, JELLYFIN_IMAGE=it.IMAGE)
    meta = os.path.join(it.PLUGIN_DIR, "meta.json")
    if os.path.exists(meta):
        with open(meta, encoding="utf-8") as f:
            m = json.load(f)
        env["PLUGIN_FOLDER"] = f"{m['name']}_{m['version']}"
    subprocess.run(["docker", "compose", "-p", "serversync-mx", "-f", "docker-compose.matrix.yml", *args], cwd=HERE, check=True, env=env)


def setup_pool():
    """Completes each wizard once, creates the Movies library and an API key, and records everything."""
    creds = it.load_creds()
    # Saved before any wizard runs, so a run that stops partway can still sign in on the next.
    it.save_creds(creds)
    apis = {}
    for name in NODES:
        api = it.connect(name)
        info = api.wait_ready()
        entry = creds["servers"].setdefault(name, {})
        if not info.get("StartupWizardCompleted"):
            log(f"== {name}: completing startup wizard")
            api.post("/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}, expect=204)
            api.get("/Startup/User")
            api.post("/Startup/User", {"Name": "admin", "Password": creds["password"]}, expect=204)
            api.post("/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False}, expect=204)
            api.post("/Startup/Complete", expect=204)
            time.sleep(2)
        _, auth = api.post("/Users/AuthenticateByName", {"Username": "admin", "Pw": creds["password"]}, expect=200)
        api.token = auth["AccessToken"]
        entry["userId"] = auth["User"]["Id"]
        entry["serverId"] = info.get("Id") or api.get("/System/Info")["Id"]
        sysconfig = api.get("/System/Configuration")
        if sysconfig.get("ServerName") != name:
            sysconfig["ServerName"] = name
            api.post("/System/Configuration", sysconfig, expect=204)
        entry["libraries"] = {}
        folders = api.get("/Library/VirtualFolders")
        found = next((v for v in folders if v["Name"] == "Movies"), None)
        if found is None:
            log(f"== {name}: creating Movies library")
            options = json.loads(json.dumps(it.LIBRARY_OPTIONS))
            api.post("/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false&paths=%2Fmedia%2FMovies", options, expect=204)
            found = next(v for v in api.get("/Library/VirtualFolders") if v["Name"] == "Movies")
        entry["libraries"]["Movies"] = found["ItemId"]
        viewer = next((u for u in api.get("/Users") if u["Name"] == "viewer"), None)
        if viewer is None:
            _, viewer = api.post("/Users/New", {"Name": "viewer"}, expect=200)
        entry["viewerId"] = viewer["Id"]
        keys = api.get("/Auth/Keys")["Items"]
        if not keys:
            api.post("/Auth/Keys?app=ServerSyncMX", expect=204)
            keys = api.get("/Auth/Keys")["Items"]
        entry["apiKey"] = keys[0]["AccessToken"]
        apis[name] = api
    it.save_creds(creds)
    it.POOL_APIS.clear()
    it.POOL_APIS.update(apis)
    return creds, apis


# --------------------------------------------------------------------------------------------------
# Films
# --------------------------------------------------------------------------------------------------

def movies_dir(node):
    return os.path.join(STATE, "media", node, "Movies")


def encode(node, folder, name, codec, seconds=2, size="128x96"):
    """Writes one film file on a server. hevc and av1 differ in size and in what a player must decode."""
    out = os.path.join(movies_dir(node), folder)
    os.makedirs(out, exist_ok=True)
    video = ["-c:v", "libx265", "-x265-params", "log-level=error"] if codec == "hevc" else ["-c:v", "libsvtav1", "-svtav1-params", "lp=1"]
    it.ffmpeg(out, "-f", "lavfi", "-i", f"testsrc=duration={seconds}:size={size}:rate=10", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono",
              "-t", str(seconds), *video, "-pix_fmt", "yuv420p", "-c:a", "aac", f"/out/{name}")
    return os.path.join(out, name)


def video_files(node, tag):
    """Every film file a server holds for this round, as paths under its Movies folder."""
    found = []
    root = movies_dir(node)
    for dirpath, _, files in os.walk(root):
        for f in files:
            if tag in dirpath and f.endswith(VIDEO):
                found.append(os.path.relpath(os.path.join(dirpath, f), root))
    return sorted(found)


def folders(node, tag):
    root = movies_dir(node)
    return sorted(d for d in os.listdir(root) if tag in d) if os.path.isdir(root) else []


def items(api, tag):
    listed = api.get("/Items?IncludeItemTypes=Movie&Recursive=true&Fields=Path,MediaSources")["Items"]
    return [i for i in listed if tag in (i.get("Path") or "")]


def codec_of(api, item):
    user = api.get("/Users")[0]["Id"]
    full = api.get(f"/Items/{item['Id']}?userId={user}")
    for source in full.get("MediaSources") or []:
        for stream in source.get("MediaStreams") or []:
            if stream.get("Type") == "Video":
                return stream.get("Codec")
    return None


def played_bytes(api, user, item):
    status, body = api.call("POST", f"/Items/{item['Id']}/PlaybackInfo?userId={user}", {}, raw=True)
    if status != 200:
        return status, None
    sources = json.loads(body).get("MediaSources") or []
    if not sources:
        return 404, None
    status, _, data = it.http(api, "GET", f"/Videos/{item['Id']}/stream?static=true&mediaSourceId={sources[0].get('Id')}")
    return status, data


def rows(api, tag):
    return [r for r in it.all_rows(api) if tag in (r.get("SourcePath") or "") or tag in (r.get("LocalPath") or "")]


# --------------------------------------------------------------------------------------------------
# Pool shapes
# --------------------------------------------------------------------------------------------------

CONTENT = dict(EnableContentSync=True, DownloadNewContentMode="Enabled", ReplaceExistingContentMode="Enabled",
               MissingContentAction="Delete", DeleteMissingContentMode="Enabled", RemoveEmptyFoldersOnDelete=True,
               MinimumFreeDiskSpaceGb=0, LinkCacheSizeGb=0, FullScanIntervalHours=24, EnableRecyclingBin=False)


def configure(creds, apis, shape, storage, hints):
    """Writes every server's peer list for a shape. The main server sends to its children and pulls
    nothing. Equal partners and siblings sync with each other."""
    members, followers, origin_pulls, siblings = SHAPES[shape]
    for name in NODES:
        if name not in members:
            it.configure_server(creds, apis[name], name, [])
    partner = "Sync"
    if origin_pulls:
        order = {n: [m for m in members if m != n] for n in members}
        modes = {n: {m: partner for m in order[n]} for n in members}
    else:
        order = {ORIGIN: list(followers)}
        modes = {ORIGIN: {f: ("Push" if hints else "Pull") for f in followers}}
        for f in followers:
            others = [s for s in followers if s != f] if siblings else []
            order[f] = [ORIGIN] + others
            modes[f] = {ORIGIN: "Pull", **{s: partner for s in others}}
    for name in members:
        pulls = origin_pulls or name != ORIGIN
        flags = dict(CONTENT) if pulls else {}
        it.configure_server(creds, apis[name], name, order[name], modes[name], **flags)
        config = it.plugin_config(apis[name])
        for entry in config["Servers"]:
            if storage == "link":
                entry["AllowLinking"] = True
                if entry.get("Mode") in ("Pull", 0) and name == ORIGIN:
                    entry["Mode"] = "Push"
            for mapping in entry["LibraryMappings"]:
                mapping["StorageMode"] = "Link" if storage == "link" and pulls else "Copy"
        it.save_plugin_config(apis[name], config)
    return members, followers, origin_pulls


def set_flag(apis, names, **flags):
    for name in names:
        config = it.plugin_config(apis[name])
        config.update(flags)
        it.save_plugin_config(apis[name], config)


def quiet(apis, members, timeout=120):
    """Waits for every hint queue in the round to drain."""
    def empty():
        return all(not it.hints(apis[n])["Outbound"] and not it.hints(apis[n])["Inbound"] and it.hints(apis[n])["Pending"] == 0 for n in members)
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            if empty():
                return True
        except Exception:
            pass
        time.sleep(2)
    return False


def sync_pass(apis, names):
    for name in names:
        it.wait_scans_idle(apis[name])
        it.run_task(apis[name], "ServerSyncUpdateTables")
        it.run_task(apis[name], "ServerSyncDownloadContent")
    for name in names:
        it.wait_scans_idle(apis[name])


def rescan(api, check, message, timeout=180):
    """Scans a library until it shows what is on disk."""
    deadline = time.time() + timeout
    next_kick = 0
    while time.time() < deadline:
        if time.time() >= next_kick:
            it.wait_scans_idle(api)
            api.post("/Library/Refresh", None, expect=204)
            next_kick = time.time() + 25
        time.sleep(3)
        try:
            if check():
                return True
        except Exception:
            pass
    expect(False, message + f" (within {timeout}s)")
    return False


def clean(creds, apis):
    """Empties every server's media, library, sync table, and peer list, so rounds do not meet."""
    for name in NODES:
        it.configure_server(creds, apis[name], name, [])
    for name in NODES:
        root = movies_dir(name)
        os.makedirs(root, exist_ok=True)
        for entry in os.listdir(root):
            path = os.path.join(root, entry)
            shutil.rmtree(path, ignore_errors=True) if os.path.isdir(path) else os.remove(path)
        # Jellyfin leaves a library alone while its folder is empty, so something always sits in it.
        with open(os.path.join(root, "placeholder.txt"), "w", encoding="utf-8") as f:
            f.write("keeps the folder from being empty\n")
        apis[name].post("/Sync/ResetContentSyncDatabase", None)
    for name in NODES:
        rescan(apis[name], lambda n=name: not apis[n].get("/Items?IncludeItemTypes=Movie&Recursive=true")["Items"], f"{name} emptied its library")
    quiet(apis, NODES, 60)


# --------------------------------------------------------------------------------------------------
# One round
# --------------------------------------------------------------------------------------------------

def expected_files(tag, storage):
    """What a follower should hold: n1's files, under the same names, as copies or as links."""
    held = video_files(ORIGIN, tag)
    if storage == "copy":
        return held
    return sorted(os.path.splitext(p)[0] + ".strm" for p in held)


def describe(apis, node, tag):
    lines = []
    for r in rows(apis[node], tag):
        lines.append(f"      row {os.path.basename(r.get('SourcePath') or '')} -> {os.path.basename(r.get('LocalPath') or '')}: {r.get('StorageMode')} {r.get('Status')} {r.get('PendingType') or ''} {r.get('ErrorMessage') or ''}".rstrip())
    return "\n".join(lines)


def check(apis, creds, node, tag, storage, stage, final):
    """Compares one follower with n1. Before the full scan only what must already hold is asserted:
    every film n1 holds now is here and plays n1's bytes. After it, nothing else may be left."""
    api = apis[node]
    user = creds["servers"][node]["userId"]
    label = f"{node} {stage}"
    want = expected_files(tag, storage)
    have = video_files(node, tag)
    missing = [p for p in want if p not in have]
    if not final:
        # While an old container still sits beside its replacement on n1, Jellyfin lists the two as one
        # film with two versions, and a copy library takes one version. Either file is n1's film.
        stems = {os.path.splitext(p)[0] for p in have}
        missing = [p for p in missing if os.path.splitext(p)[0] not in stems]
    extra = [p for p in have if p not in want]
    expect(not missing, f"{label}: holds every film n1 holds (missing {missing})" if missing else f"{label}: holds every film n1 holds")
    if final:
        expect(not extra, f"{label}: holds nothing n1 no longer has (extra {extra})" if extra else f"{label}: holds nothing n1 no longer has")
        stray = [f for f in folders(node, tag) if f not in folders(ORIGIN, tag)]
        expect(not stray, f"{label}: no folder is left behind (left {stray})" if stray else f"{label}: no folder is left behind")
    bad = [r for r in rows(api, tag) if r.get("Status") not in ("Synced",)]
    if final or not bad:
        expect(not bad, f"{label}: every row is synced" + (f" ({[(os.path.basename(r['SourcePath']), r['Status'], r.get('ErrorMessage')) for r in bad]})" if bad else ""))
    elif bad:
        log(f"   note {label}: rows not settled yet: {[(os.path.basename(r['SourcePath']), r['Status'], (r.get('ErrorMessage') or '')[:80]) for r in bad]}")

    # Every film n1 holds plays here, with n1's bytes and n1's codec.
    origin_items = {os.path.splitext(os.path.basename(i["Path"]))[0]: i for i in items(apis[ORIGIN], tag)}
    # A sync run asks Jellyfin to refresh the folders it changed, which Jellyfin does after its own delay.
    wait = 180
    deadline = time.time() + wait
    local_items = {}
    while time.time() < deadline:
        local_items = {os.path.splitext(os.path.basename(i["Path"]))[0]: i for i in items(api, tag)}
        if all(os.path.splitext(os.path.basename(p))[0] in local_items for p in want if p in have) and (not final or len(local_items) == len(want)):
            break
        time.sleep(3)
    if final:
        expect(len(local_items) == len(want), f"{label}: the library lists {len(want)} films ({sorted(local_items)})" if len(local_items) != len(want) else f"{label}: the library lists {len(want)} films")
    # While an old container still sits beside its replacement on n1, either one is n1's file.
    by_stem = {}
    for relative in video_files(ORIGIN, tag):
        by_stem.setdefault(os.path.splitext(os.path.basename(relative))[0], []).append(relative)
    for stem, relatives in sorted(by_stem.items()):
        item = local_items.get(stem)
        if item is None:
            expect(False, f"{label}: {stem} is in the library")
            continue
        originals = []
        for relative in relatives:
            with open(os.path.join(movies_dir(ORIGIN), relative), "rb") as f:
                originals.append(f.read())
        status, data = played_bytes(api, user, item)
        if status != 200 or data not in originals:
            # A file just replaced is still listed under its old item until Jellyfin has scanned the
            # folder, which a sync run asks for and Jellyfin does after its own delay.
            it.wait_scans_idle(api)
            api.post("/Library/Refresh", None, expect=204)
            it.wait_scans_idle(api)
            time.sleep(8)
            fresh = [i for i in items(api, tag) if os.path.splitext(os.path.basename(i["Path"]))[0] == stem]
            for candidate in fresh or [item]:
                status, data = played_bytes(api, user, candidate)
                if status == 200 and data in originals:
                    item = candidate
                    break
        expect(status == 200 and data in originals, f"{label}: {stem} plays the file n1 has now ({status}, {len(data or b'')} bytes, n1 has {[len(o) for o in originals]})")
        if final:
            want_codec = codec_of(apis[ORIGIN], origin_items[stem]) if stem in origin_items else None
            got = codec_of(api, item)
            if got != want_codec:
                api.post("/Library/Refresh", None, expect=204)
                it.wait_scans_idle(api)
                time.sleep(5)
                got = codec_of(api, item)
            expect(got == want_codec, f"{label}: {stem} shows n1's codec ({got} here, {want_codec} on n1)")


def round_(creds, apis, shape, storage, hints):
    tag = uuid.uuid4().hex[:6]
    title = f"{shape} {storage} {'hints' if hints else 'scan'}"
    log(f"== round: {title} ({tag})")
    before = len(it._failures)
    clean(creds, apis)
    members, followers, origin_pulls = configure(creds, apis, shape, storage, hints)
    syncing = followers + ([ORIGIN] if origin_pulls else [])
    origin = apis[ORIGIN]

    def name(kind, suffix=""):
        return f"{kind} {tag}{suffix} (2024)"

    # Seed: every film as HEVC in a Matroska file.
    for kind in ("Keep", "Reenc", "Ext", "Folder", "Gone", "Overlap"):
        encode(ORIGIN, name(kind), name(kind) + ".mkv", "hevc")
    encode(ORIGIN, name("Name"), name("Name") + " [HEVC].mkv", "hevc")
    with open(os.path.join(movies_dir(ORIGIN), name("Ext"), name("Ext") + ".en.srt"), "w", encoding="utf-8") as f:
        f.write("1\n00:00:00,000 --> 00:00:01,000\nExt\n")
    rescan(origin, lambda: len(items(origin, tag)) == 7, "n1 lists the seeded films")
    if hints:
        quiet(apis, members)
    sync_pass(apis, syncing)
    sync_pass(apis, syncing)
    for node in followers:
        rescan(apis[node], lambda n=node: len(items(apis[n], tag)) == 7, f"{node} lists the seeded films")
        check(apis, creds, node, tag, storage, "seeded", final=True)

    # Change everything on n1 at once.
    log("   -- changing the files on n1")
    encode(ORIGIN, name("Reenc"), name("Reenc") + ".mkv", "av1")
    os.remove(os.path.join(movies_dir(ORIGIN), name("Ext"), name("Ext") + ".mkv"))
    encode(ORIGIN, name("Ext"), name("Ext") + ".mp4", "av1")
    os.remove(os.path.join(movies_dir(ORIGIN), name("Name"), name("Name") + " [HEVC].mkv"))
    encode(ORIGIN, name("Name"), name("Name") + " [AV1].mkv", "av1")
    os.makedirs(os.path.join(movies_dir(ORIGIN), name("Folder", " Moved")))
    shutil.move(os.path.join(movies_dir(ORIGIN), name("Folder"), name("Folder") + ".mkv"),
                os.path.join(movies_dir(ORIGIN), name("Folder", " Moved"), name("Folder", " Moved") + ".mkv"))
    shutil.rmtree(os.path.join(movies_dir(ORIGIN), name("Folder")))
    shutil.rmtree(os.path.join(movies_dir(ORIGIN), name("Gone")))
    encode(ORIGIN, name("Overlap"), name("Overlap") + ".mp4", "av1")

    def origin_settled(count):
        listed = items(origin, tag)
        paths = sorted(os.path.relpath(i["Path"], "/media/Movies") for i in listed)
        sources = sorted(os.path.relpath(s["Path"], "/media/Movies") for i in listed for s in i.get("MediaSources") or [] if s.get("Path"))
        on_disk = video_files(ORIGIN, tag)
        reenc = next((i for i in listed if "Reenc" in i["Path"]), None)
        return len(on_disk) == count and (paths == on_disk or sources == on_disk) and reenc is not None and codec_of(origin, reenc) == "av1"

    rescan(origin, lambda: origin_settled(7), "n1's library shows the changed files")
    if hints:
        quiet(apis, members)

    sync_pass(apis, syncing)
    for node in followers:
        check(apis, creds, node, tag, storage, "after one sync", final=False)
        # A removal is settled without waiting for the full scan: by n1's announcement where live
        # changes are on, and otherwise by the record n1 keeps of what left its library, which this
        # scan asks for. A copy is left for the full scan where a sibling also covers its folder.
        if storage == "link" or not SHAPES[shape][3]:
            lingering = [p for p in video_files(node, tag) if p.startswith(name("Gone") + "/")]
            expect(not lingering, f"{node} after one sync: the removed film is gone without a full scan" + (f" ({lingering})" if lingering else ""))

    # The old container goes once the new one has been seen beside it.
    log("   -- removing the old container that sat beside its replacement")
    os.remove(os.path.join(movies_dir(ORIGIN), name("Overlap"), name("Overlap") + ".mkv"))
    rescan(origin, lambda: origin_settled(6), "n1's library drops the old container")
    if hints:
        quiet(apis, members)
    sync_pass(apis, syncing)
    for node in followers:
        check(apis, creds, node, tag, storage, "after two syncs", final=False)

    # A full scan is when removals are settled. Two passes, since a removal is marked and then run.
    log("   -- full scan")
    set_flag(apis, syncing, FullScanIntervalHours=0)
    sync_pass(apis, syncing)
    sync_pass(apis, syncing)
    for node in followers:
        api = apis[node]
        it.wait_scans_idle(api)
        api.post("/Library/Refresh", None, expect=204)
        it.wait_scans_idle(api)
    for node in followers:
        check(apis, creds, node, tag, storage, "settled", final=True)
        if len(it._failures) > before:
            log(f"   state of {node}:\n      files {video_files(node, tag)}\n{describe(apis, node, tag)}")
    if origin_pulls:
        # An equal partner must not be handed back what was just changed on it.
        want = [p for p in video_files(ORIGIN, tag) if not p.endswith(".strm")]
        expect(len(want) == 6 and not [p for p in video_files(ORIGIN, tag) if p.endswith(".strm")] and len(folders(ORIGIN, tag)) == 6,
               f"n1 settled: its own changes were not undone by its partners (files {video_files(ORIGIN, tag)})")
    failed = len(it._failures) - before
    log(f"   round {title}: {'passed' if failed == 0 else str(failed) + ' failed'}")
    return title, failed


# --------------------------------------------------------------------------------------------------
# Interruptions
# --------------------------------------------------------------------------------------------------

def docker(*args):
    subprocess.run(["docker", *args], check=True, capture_output=True)


def start_task(api, key):
    task = next(t for t in api.get("/ScheduledTasks") if t["Key"] == key)
    api.post(f"/ScheduledTasks/Running/{task['Id']}", expect=204)


def pad(path, megabytes):
    """Makes a film big enough that a throttled download of it can be stopped partway."""
    with open(path, "ab") as f:
        f.write(os.urandom(megabytes * 1024 * 1024))


def leftovers(node, tag):
    """Files a download or replacement leaves beside a film only while it is in progress."""
    found = []
    for dirpath, _, files in os.walk(movies_dir(node)):
        if tag in dirpath:
            found += [f for f in files if ".syncing." in f or ".replacing." in f or f.endswith(".serversync-tmp") or f.endswith(".serversync-move")]
    return sorted(found)


def settle(apis, creds, tag, storage, stage):
    sync_pass(apis, ["n2"])
    sync_pass(apis, ["n2"])
    api = apis["n2"]
    it.wait_scans_idle(api)
    api.post("/Library/Refresh", None, expect=204)
    it.wait_scans_idle(api)
    check(apis, creds, "n2", tag, storage, stage, final=True)
    left = leftovers("n2", tag)
    expect(not left, f"n2 {stage}: nothing half done is left in the library" + (f" ({left})" if left else ""))


def interrupt_round(creds, apis, storage):
    """One main server and one child, with the child killed mid-download and mid-replacement, the main
    server gone for several runs, and links removed and emptied by hand. Nothing is retried by hand:
    every recovery is the next scheduled run's."""
    tag = uuid.uuid4().hex[:6]
    title = f"1:1 {storage} interrupted"
    log(f"== round: {title} ({tag})")
    before = len(it._failures)
    clean(creds, apis)
    configure(creds, apis, "1:1", storage, True)
    set_flag(apis, ["n2"], FullScanIntervalHours=0, MaxDownloadSpeed=1)
    origin, child = apis[ORIGIN], apis["n2"]

    def name(kind):
        return f"{kind} {tag} (2024)"

    def film(kind):
        return os.path.join(movies_dir(ORIGIN), name(kind), name(kind) + ".mkv")

    encode(ORIGIN, name("Small"), name("Small") + ".mkv", "hevc")
    encode(ORIGIN, name("Big"), name("Big") + ".mkv", "hevc")
    pad(film("Big"), 6)
    rescan(origin, lambda: len(items(origin, tag)) == 2, "n1 lists the seeded films")
    quiet(apis, ["n1", "n2"])

    if storage == "copy":
        # The child dies partway through a first download.
        log("   -- killing the child mid-download")
        it.run_task(child, "ServerSyncUpdateTables")
        start_task(child, "ServerSyncDownloadContent")
        time.sleep(6)
        docker("kill", "serversync-mx-n2")
        docker("start", "serversync-mx-n2")
        child.wait_ready()
        expect(not os.path.exists(os.path.join(movies_dir("n2"), name("Big"), name("Big") + ".mkv")) or
               os.path.getsize(os.path.join(movies_dir("n2"), name("Big"), name("Big") + ".mkv")) == os.path.getsize(film("Big")),
               "n2 after the kill: no half film sits under the film's own name")
        settle(apis, creds, tag, storage, "after a killed download")

        # The child dies partway through replacing a film it already holds.
        log("   -- killing the child mid-replacement")
        held = os.path.join(movies_dir("n2"), name("Big"), name("Big") + ".mkv")
        old_size = os.path.getsize(held)
        encode(ORIGIN, name("Big"), name("Big") + ".mkv", "av1")
        pad(film("Big"), 7)
        rescan(origin, lambda: any(codec_of(origin, i) == "av1" for i in items(origin, tag) if "Big" in i["Path"]), "n1 shows the replaced film")
        quiet(apis, ["n1", "n2"])
        it.run_task(child, "ServerSyncUpdateTables")
        start_task(child, "ServerSyncDownloadContent")
        time.sleep(6)
        docker("kill", "serversync-mx-n2")
        docker("start", "serversync-mx-n2")
        child.wait_ready()
        expect(os.path.exists(held) and os.path.getsize(held) in (old_size, os.path.getsize(film("Big"))),
               "n2 after the kill: the film it held is still there, whole")
        settle(apis, creds, tag, storage, "after a killed replacement")
    else:
        sync_pass(apis, ["n2"])
        sync_pass(apis, ["n2"])
        check(apis, creds, "n2", tag, storage, "seeded", final=True)

    # The main server is gone for more runs than a row has tries, then comes back.
    log("   -- the main server goes away for five runs")
    encode(ORIGIN, name("Late"), name("Late") + ".mkv", "hevc")
    rescan(origin, lambda: len(items(origin, tag)) == 3, "n1 lists the late film")
    quiet(apis, ["n1", "n2"], 30)
    if storage == "link":
        # A link removed by hand and one emptied, as a power loss leaves it. Both are this plugin's
        # own to write again, and writing one needs no source.
        strm = lambda kind: os.path.join(movies_dir("n2"), name(kind), name(kind) + ".strm")
        os.remove(strm("Small"))
        open(strm("Big"), "w").close()
    docker("stop", "serversync-mx-n1")
    for _ in range(5):
        try:
            it.run_task(child, "ServerSyncUpdateTables")
            it.run_task(child, "ServerSyncDownloadContent")
        except AssertionError:
            pass
    if storage == "link":
        from_route = lambda path: os.path.exists(path) and "/Sync/Link/" in open(path, encoding="utf-8").read()
        expect(from_route(strm("Small")) and from_route(strm("Big")), "n2 with n1 down: the removed link and the emptied link were written again")
    held_rows = [r for r in rows(child, tag) if "Small" in (r.get("SourcePath") or "")]
    on_disk = lambda r: os.path.exists(os.path.join(movies_dir("n2"), os.path.relpath(r["LocalPath"], "/media/Movies")))
    expect(held_rows and all(on_disk(r) for r in held_rows), "n2 with n1 down: what it already holds is untouched")
    docker("start", "serversync-mx-n1")
    origin.wait_ready()
    it.wait_scans_idle(origin)
    settle(apis, creds, tag, storage, "after the main server came back")

    failed = len(it._failures) - before
    log(f"   round {title}: {'passed' if failed == 0 else str(failed) + ' failed'}")
    return title, failed


def holders_round(creds, apis):
    """Two servers hold the same two films and a third links to them. Each link streams from the
    higher of the two while it is idle. With one film held open on it, the other film plays from
    the second server. When the first server removes a film, its link moves to the second and keeps
    playing, and when the second removes it too, the link goes. Nothing waits for a full scan."""
    import threading
    import urllib.request
    tag = uuid.uuid4().hex[:6]
    title = "two holders link"
    log(f"== round: {title} ({tag})")
    before = len(it._failures)
    clean(creds, apis)
    for node in NODES:
        if node not in ("n1", "n2", "n3"):
            it.configure_server(creds, apis[node], node, [])
    for holder in ("n1", "n3"):
        it.configure_server(creds, apis[holder], holder, ["n2"], {"n2": "Push"})
        config = it.plugin_config(apis[holder])
        for entry in config["Servers"]:
            entry["AllowLinking"] = True
        it.save_plugin_config(apis[holder], config)
    it.configure_server(creds, apis["n2"], "n2", ["n1", "n3"], {"n1": "Pull", "n3": "Pull"}, **CONTENT)
    config = it.plugin_config(apis["n2"])
    for entry in config["Servers"]:
        for mapping in entry["LibraryMappings"]:
            mapping["StorageMode"] = "Link"
    it.save_plugin_config(apis["n2"], config)

    shared = f"Shared {tag} (2024)"
    other = f"Other {tag} (2024)"
    films = {}
    for folder, megabytes in ((shared, 40), (other, 0)):
        first = encode("n1", folder, folder + ".mkv", "hevc")
        if megabytes:
            pad(first, megabytes)
        os.makedirs(os.path.join(movies_dir("n3"), folder), exist_ok=True)
        shutil.copy(first, os.path.join(movies_dir("n3"), folder, folder + ".mkv"))
        with open(first, "rb") as f:
            films[folder] = f.read()
    for holder in ("n1", "n3"):
        rescan(apis[holder], lambda h=holder: len(items(apis[h], tag)) == 2, f"{holder} lists both films")
    quiet(apis, ["n1", "n2", "n3"])
    sync_pass(apis, ["n2"])
    sync_pass(apis, ["n2"])
    child = apis["n2"]
    user = creds["servers"]["n2"]["userId"]
    link = os.path.join(movies_dir("n2"), shared, shared + ".strm")
    rescan(child, lambda: len(items(child, tag)) == 2, "n2 lists both films")

    def item_of(folder):
        return next((i for i in items(child, tag) if folder in i["Path"]), None)

    def row_of(folder):
        return next((r for r in rows(child, tag) if folder in (r.get("LocalPath") or "")), None)

    def server_of(folder):
        row = row_of(folder)
        return (row.get("ServerName") or row.get("ServerKey")) if row else None

    def plays(folder):
        item = item_of(folder)
        if item is None:
            return False
        status, data = played_bytes(child, user, item)
        return status == 200 and data == films[folder]

    expect(os.path.exists(link) and len(rows(child, tag)) == 2, "n2 holds one link for each film both servers hold")
    started_on = server_of(shared)
    # Nothing is played before the hold: a source counts a stream for 90 seconds after it ends, and
    # the first server has to be idle for the held play to start on it.

    # One film is played and its stream held open. The next play goes to the server doing nothing.
    log("   -- holding one stream open and playing the other film")
    held_item = item_of(shared)
    status, body = child.call("POST", f"/Items/{held_item['Id']}/PlaybackInfo?userId={user}", {}, raw=True)
    source = (json.loads(body).get("MediaSources") or [{}])[0] if status == 200 else {}
    stop = threading.Event()

    def hold():
        request = urllib.request.Request(child.base + f"/Videos/{held_item['Id']}/stream?static=true&mediaSourceId={source.get('Id')}", headers=child._headers())
        try:
            with urllib.request.urlopen(request, timeout=180) as response:
                while not stop.is_set() and response.read(4096):
                    time.sleep(0.25)
        except Exception:  # noqa: BLE001 the hold ends however the stream does
            pass

    holder_thread = threading.Thread(target=hold, daemon=True)
    holder_thread.start()
    time.sleep(6)
    since = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime())
    played = plays(other)
    time.sleep(2)
    logs = subprocess.run(["docker", "logs", "--since", since + "Z", "serversync-mx-n2"], capture_output=True, text=True)
    moved = [line for line in (logs.stdout + logs.stderr).splitlines() if other in line and "which has more room" in line]
    # With one link streaming and a limit of one in all, the next play is refused, and plays again
    # once the limit is lifted.
    set_flag(apis, ["n2"], LinkMaxStreamsTotal=1)
    refused = not plays(other)
    set_flag(apis, ["n2"], LinkMaxStreamsTotal=0)
    allowed_again = plays(other)
    stop.set()
    holder_thread.join(timeout=30)
    expect(refused, "with one link streaming and a limit of one in all, the next play is refused")
    expect(allowed_again, "with the limit lifted, it plays again")
    expect(played, "the other film plays while the first is held open")
    expect(bool(moved), "the other film played from the server that was doing nothing" + (f" ({moved[-1][-110:]})" if moved else ""))

    log("   -- the first holder removes the film")
    shutil.rmtree(os.path.join(movies_dir("n1"), shared))
    rescan(apis["n1"], lambda: len(items(apis["n1"], tag)) == 1, "n1 no longer lists the film")
    quiet(apis, ["n1", "n2", "n3"])
    sync_pass(apis, ["n2"])
    expect(os.path.exists(link), "the link is kept, since the other server still holds the film")
    held = row_of(shared)
    expect(held is not None and held.get("Status") == "Synced" and server_of(shared) != started_on,
           f"the link moved to the other holder ({held and (held.get('Status'), held.get('ErrorMessage'))}, from {started_on} to {server_of(shared)})")
    expect(plays(shared), "the link plays from the other holder")

    log("   -- the second holder removes it too")
    shutil.rmtree(os.path.join(movies_dir("n3"), shared))
    rescan(apis["n3"], lambda: len(items(apis["n3"], tag)) == 1, "n3 no longer lists the film")
    quiet(apis, ["n1", "n2", "n3"])
    sync_pass(apis, ["n2"])
    expect(not os.path.exists(link), "the link is removed once no server holds the film, without a full scan")
    expect(row_of(shared) is None, "no row is left for it")
    expect(plays(other), "the other film's link is untouched")

    failed = len(it._failures) - before
    log(f"   round {title}: {'passed' if failed == 0 else str(failed) + ' failed'}")
    return title, failed


def own_round(creds, apis):
    """One main server and one child kept as copies, with Removed files on Delete. A film the child
    already had before the two were matched is the child's own and stays when the main server
    removes its copy. A film the child downloaded from the main server is removed, as a mirror
    removes it. A film changed on the main server replaces the child's own, and is then the main
    server's file like any other."""
    tag = uuid.uuid4().hex[:6]
    title = "1:1 copy own files"
    log(f"== round: {title} ({tag})")
    before = len(it._failures)
    clean(creds, apis)
    configure(creds, apis, "1:1", "copy", True)
    origin, child = apis[ORIGIN], apis["n2"]

    def name(kind):
        return f"{kind} {tag} (2024)"

    def on(node, kind):
        return os.path.join(movies_dir(node), name(kind), name(kind) + ".mkv")

    for kind in ("Mine", "Theirs", "Swapped"):
        encode(ORIGIN, name(kind), name(kind) + ".mkv", "hevc")
    # One more film stays on n1 throughout, under another tag. A library that answers with no items
    # at all is never taken for a removal of everything, which is not what this round is about.
    anchor = f"Anchor {uuid.uuid4().hex[:6]} (2024)"
    encode(ORIGIN, anchor, anchor + ".mkv", "hevc")
    for kind in ("Mine", "Swapped"):
        os.makedirs(os.path.dirname(on("n2", kind)), exist_ok=True)
        shutil.copy(on(ORIGIN, kind), on("n2", kind))
    rescan(origin, lambda: len(items(origin, tag)) == 3, "n1 lists the films")
    rescan(child, lambda: len(items(child, tag)) == 2, "n2 lists the films it already had")
    quiet(apis, ["n1", "n2"])
    sync_pass(apis, ["n2"])
    sync_pass(apis, ["n2"])
    kept = {os.path.basename(r.get("LocalPath") or ""): r.get("Kept") for r in rows(child, tag)}
    expect(all(os.path.exists(on("n2", kind)) for kind in ("Mine", "Theirs", "Swapped")), "n2 holds all three films")
    expect(kept.get(name("Mine") + ".mkv") == "Own" and kept.get(name("Swapped") + ".mkv") == "Own" and kept.get(name("Theirs") + ".mkv") in (None, ""),
           f"the films n2 already had are marked as its own, and the one it downloaded is not ({kept})")

    log("   -- n1 replaces one film and removes the other two")
    encode(ORIGIN, name("Swapped"), name("Swapped") + ".mkv", "av1")
    rescan(origin, lambda: any(codec_of(origin, i) == "av1" for i in items(origin, tag) if "Swapped" in i["Path"]), "n1 shows the replaced film")
    quiet(apis, ["n1", "n2"])
    sync_pass(apis, ["n2"])
    with open(on(ORIGIN, "Swapped"), "rb") as f:
        replaced = f.read()
    with open(on("n2", "Swapped"), "rb") as f:
        expect(f.read() == replaced, "the changed film replaced the one n2 had")
    shutil.rmtree(os.path.join(movies_dir(ORIGIN), name("Mine")))
    shutil.rmtree(os.path.join(movies_dir(ORIGIN), name("Theirs")))
    shutil.rmtree(os.path.join(movies_dir(ORIGIN), name("Swapped")))
    rescan(origin, lambda: not items(origin, tag), "n1 no longer lists the films")
    quiet(apis, ["n1", "n2"])
    set_flag(apis, ["n2"], FullScanIntervalHours=0)
    sync_pass(apis, ["n2"])
    sync_pass(apis, ["n2"])
    expect(os.path.exists(on("n2", "Mine")), "n2's own film stays when n1 removes its copy")
    expect(not os.path.exists(on("n2", "Theirs")), "the film n2 downloaded from n1 is removed, as a mirror removes it")
    expect(not os.path.exists(on("n2", "Swapped")), "the film replaced from n1 is n1's file now, and is removed with it")
    left = [(os.path.basename(r.get("LocalPath") or ""), r.get("Status")) for r in rows(child, tag)]
    expect(not left, f"no row is left for any of them ({left})")

    failed = len(it._failures) - before
    log(f"   round {title}: {'passed' if failed == 0 else str(failed) + ' failed'}")
    return title, failed


def own_link_round(creds, apis):
    """A server that brings its own films into a Link library. A film it already has is its own:
    the other server's file at the same name is never downloaded over it, whether that file is the
    same, different, or changes later, and it stays when the other server removes theirs. A film
    only the other server has arrives as a link, and that link goes when the film does."""
    tag = uuid.uuid4().hex[:6]
    title = "1:1 link own films"
    log(f"== round: {title} ({tag})")
    before = len(it._failures)
    clean(creds, apis)
    configure(creds, apis, "1:1", "link", True)
    origin, child = apis[ORIGIN], apis["n2"]
    user = creds["servers"]["n2"]["userId"]

    def name(kind):
        return f"{kind} {tag} (2024)"

    def on(node, kind, extension=".mkv"):
        return os.path.join(movies_dir(node), name(kind), name(kind) + extension)

    def read(path):
        with open(path, "rb") as f:
            return f.read()

    for kind in ("Same", "Different", "Theirs"):
        encode(ORIGIN, name(kind), name(kind) + ".mkv", "hevc")
    # One more film stays on n1 throughout, under another tag. A library that answers with no items
    # at all is never taken for a removal of everything, which is not what this round is about.
    anchor = f"Anchor {uuid.uuid4().hex[:6]} (2024)"
    encode(ORIGIN, anchor, anchor + ".mkv", "hevc")
    os.makedirs(os.path.dirname(on("n2", "Same")), exist_ok=True)
    shutil.copy(on(ORIGIN, "Same"), on("n2", "Same"))
    encode("n2", name("Different"), name("Different") + ".mkv", "av1")
    mine = {kind: read(on("n2", kind)) for kind in ("Same", "Different")}
    rescan(origin, lambda: len(items(origin, tag)) == 3, "n1 lists the films")
    rescan(child, lambda: len(items(child, tag)) == 2, "n2 lists the films it brought")
    quiet(apis, ["n1", "n2"])
    sync_pass(apis, ["n2"])
    sync_pass(apis, ["n2"])

    def untouched(stage):
        for kind in ("Same", "Different"):
            expect(os.path.exists(on("n2", kind)) and read(on("n2", kind)) == mine[kind], f"n2 {stage}: its own {kind} film is the file it brought")
            expect(not os.path.exists(on("n2", kind, ".strm")), f"n2 {stage}: no link is written beside its own {kind} film")

    untouched("after syncing")
    kept = {os.path.basename(r.get("LocalPath") or ""): (r.get("Kept"), r.get("Status")) for r in rows(child, tag)}
    expect(kept.get(name("Same") + ".mkv") == ("Own", "Synced") and kept.get(name("Different") + ".mkv") == ("Own", "Synced"),
           f"both are tracked as its own and settled ({kept})")
    expect(os.path.exists(on("n2", "Theirs", ".strm")) and not os.path.exists(on("n2", "Theirs")), "the film only n1 has arrives as a link")
    rescan(child, lambda: len(items(child, tag)) == 3, "n2 lists all three")
    theirs = next(i for i in items(child, tag) if "Theirs" in i["Path"])
    status, data = played_bytes(child, user, theirs)
    expect(status == 200 and data == read(on(ORIGIN, "Theirs")), "the link plays n1's film")

    log("   -- n1 changes its copy of a film n2 owns")
    encode(ORIGIN, name("Different"), name("Different") + ".mkv", "hevc", seconds=3)
    encode(ORIGIN, name("Same"), name("Same") + ".mkv", "av1")
    rescan(origin, lambda: any(codec_of(origin, i) == "av1" for i in items(origin, tag) if "Same" in i["Path"]), "n1 shows its changed films")
    quiet(apis, ["n1", "n2"])
    sync_pass(apis, ["n2"])
    set_flag(apis, ["n2"], FullScanIntervalHours=0)
    sync_pass(apis, ["n2"])
    untouched("after n1 changed its copies")
    unsettled = [(os.path.basename(r.get("LocalPath") or ""), r.get("Status"), r.get("ErrorMessage")) for r in rows(child, tag) if r.get("Status") != "Synced"]
    expect(not unsettled, f"nothing is queued or waiting because of it ({unsettled})")

    log("   -- n1 removes all three")
    for kind in ("Same", "Different", "Theirs"):
        shutil.rmtree(os.path.join(movies_dir(ORIGIN), name(kind)))
    rescan(origin, lambda: not items(origin, tag), "n1 no longer lists the films")
    quiet(apis, ["n1", "n2"])
    sync_pass(apis, ["n2"])
    sync_pass(apis, ["n2"])
    untouched("after n1 removed its copies")
    expect(not os.path.exists(on("n2", "Theirs", ".strm")), "the link to n1's film is removed")
    left = [(os.path.basename(r.get("LocalPath") or ""), r.get("Status")) for r in rows(child, tag)]
    expect(not left, f"no row is left for any of them ({left})")

    failed = len(it._failures) - before
    log(f"   round {title}: {'passed' if failed == 0 else str(failed) + ' failed'}")
    return title, failed


def clash_round(creds, apis):
    """Two equal partners that each already hold the same title as a different file, and one film
    the other lacks. Both scan before either downloads, the worst case for a swap. Each keeps its
    own file for the title they share, and each gets the film it lacked. Taking the partner's file
    is a choice made by hand, and afterwards the partner neither takes it back nor loses its own."""
    tag = uuid.uuid4().hex[:6]
    title = "1=1 copy same title, different files"
    log(f"== round: {title} ({tag})")
    before = len(it._failures)
    clean(creds, apis)
    configure(creds, apis, "1=1", "copy", True)
    pair = ["n1", "n2"]

    def name(kind):
        return f"{kind} {tag} (2024)"

    def on(node, kind):
        return os.path.join(movies_dir(node), name(kind), name(kind) + ".mkv")

    def read(path):
        with open(path, "rb") as f:
            return f.read()

    encode("n1", name("Clash"), name("Clash") + ".mkv", "hevc")
    encode("n2", name("Clash"), name("Clash") + ".mkv", "av1")
    encode("n1", name("OnlyOne"), name("OnlyOne") + ".mkv", "hevc")
    encode("n2", name("OnlyTwo"), name("OnlyTwo") + ".mkv", "hevc")
    own = {node: read(on(node, "Clash")) for node in pair}
    expect(own["n1"] != own["n2"], "the two servers hold different files for the title")
    for node in pair:
        rescan(apis[node], lambda n=node: len(items(apis[n], tag)) == 2, f"{node} lists its two films")
    quiet(apis, pair)

    def both_scan_then_both_sync():
        for node in pair:
            it.wait_scans_idle(apis[node])
            it.run_task(apis[node], "ServerSyncUpdateTables")
        for node in pair:
            it.run_task(apis[node], "ServerSyncDownloadContent")
        for node in pair:
            it.wait_scans_idle(apis[node])

    def each_keeps_its_own(stage):
        for node in pair:
            expect(os.path.exists(on(node, "Clash")) and read(on(node, "Clash")) == own[node], f"{node} {stage}: the title they share is still its own file")

    both_scan_then_both_sync()
    both_scan_then_both_sync()
    each_keeps_its_own("after syncing")
    expect(os.path.exists(on("n2", "OnlyOne")) and read(on("n2", "OnlyOne")) == read(on("n1", "OnlyOne")), "n2 got the film only n1 had")
    expect(os.path.exists(on("n1", "OnlyTwo")) and read(on("n1", "OnlyTwo")) == read(on("n2", "OnlyTwo")), "n1 got the film only n2 had")
    for node in pair:
        clash = [r for r in rows(apis[node], tag) if "Clash" in (r.get("LocalPath") or "")]
        expect(len(clash) == 1 and clash[0].get("Kept") == "Own" and clash[0].get("Status") == "Synced" and "differs" in (clash[0].get("ErrorMessage") or clash[0].get("Reason") or ""),
               f"{node}: the shared title is settled as its own and says the two differ ({[(r.get('Kept'), r.get('Status'), r.get('ErrorMessage') or r.get('Reason')) for r in clash]})")

    log("   -- full scans change nothing")
    set_flag(apis, pair, FullScanIntervalHours=0)
    both_scan_then_both_sync()
    both_scan_then_both_sync()
    each_keeps_its_own("after full scans")
    for node in pair:
        waiting = [(os.path.basename(r.get("LocalPath") or ""), r.get("Status")) for r in rows(apis[node], tag) if r.get("Status") != "Synced"]
        expect(not waiting, f"{node}: nothing is queued, waiting, or errored ({waiting})")

    log("   -- n2 takes n1's file by hand")
    clash = next(r for r in rows(apis["n2"], tag) if "Clash" in (r.get("LocalPath") or ""))
    apis["n2"].post("/Sync/QueueItems", {"SourceItemIds": [clash["SourceItemId"]]}, expect=200)
    both_scan_then_both_sync()
    both_scan_then_both_sync()
    expect(read(on("n2", "Clash")) == own["n1"], "n2 now holds n1's file for the title")
    expect(os.path.exists(on("n1", "Clash")) and read(on("n1", "Clash")) == own["n1"], "n1 still holds its own file, and did not take n2's back")

    log("   -- n1 removes the title")
    shutil.rmtree(os.path.join(movies_dir("n1"), name("Clash")))
    rescan(apis["n1"], lambda: not [i for i in items(apis["n1"], tag) if "Clash" in i["Path"]], "n1 no longer lists the title")
    quiet(apis, pair)
    both_scan_then_both_sync()
    both_scan_then_both_sync()
    expect(not os.path.exists(on("n2", "Clash")), "the file n2 took from n1 goes with n1's, as a downloaded file does")
    expect(not os.path.exists(on("n1", "Clash")), "the title does not come back to n1")
    expect(os.path.exists(on("n1", "OnlyTwo")) and os.path.exists(on("n2", "OnlyOne")), "the films each got from the other are untouched")

    failed = len(it._failures) - before
    log(f"   round {title}: {'passed' if failed == 0 else str(failed) + ' failed'}")
    return title, failed


def cmd_run(args):
    shapes = [a for a in args if a in SHAPES] or list(SHAPES)
    storages = [a for a in args if a in ("copy", "link")] or ["copy", "link"]
    discovery = [a for a in args if a in ("hints", "scan")]
    only_interrupt = "interrupt" in args
    only_holders = "holders" in args
    only_own = "own" in args
    with_interrupt = only_interrupt or not args
    with_holders = only_holders or not args
    with_own = only_own or not args
    it.build_plugin()
    for node in NODES:
        os.makedirs(movies_dir(node), exist_ok=True)
    log("== starting the matrix pool")
    compose("up", "-d", "--remove-orphans")
    compose("restart")
    creds, apis = setup_pool()
    results = []
    for shape in ([] if only_interrupt or only_holders or only_own else shapes):
        for storage in storages:
            modes = discovery or (["hints", "scan"] if shape == "1:1" else ["hints"])
            for mode in modes:
                try:
                    results.append(round_(creds, apis, shape, storage, mode == "hints"))
                except Exception as ex:  # noqa: BLE001 report and go on to the next round
                    log(f"   ERROR {type(ex).__name__}: {ex}")
                    it._failures.append(f"{shape} {storage} {mode}: {ex}")
                    results.append((f"{shape} {storage} {mode}", -1))
    for own in ((own_round, own_link_round, clash_round) if with_own else ()):
        try:
            results.append(own(creds, apis))
        except Exception as ex:  # noqa: BLE001 report and go on to the next round
            log(f"   ERROR {type(ex).__name__}: {ex}")
            it._failures.append(f"{own.__name__}: {ex}")
            results.append((own.__name__, -1))
    if with_holders:
        try:
            results.append(holders_round(creds, apis))
        except Exception as ex:  # noqa: BLE001 report and go on to the next round
            log(f"   ERROR {type(ex).__name__}: {ex}")
            it._failures.append(f"two holders link: {ex}")
            results.append(("two holders link", -1))
    for storage in (storages if with_interrupt else []):
        try:
            results.append(interrupt_round(creds, apis, storage))
        except Exception as ex:  # noqa: BLE001 report and go on to the next round
            log(f"   ERROR {type(ex).__name__}: {ex}")
            it._failures.append(f"1:1 {storage} interrupted: {ex}")
            results.append((f"1:1 {storage} interrupted", -1))
            for node in ("n1", "n2"):
                subprocess.run(["docker", "start", f"serversync-mx-{node}"], capture_output=True)
    log("== rounds")
    for title, failed in results:
        log(f"   {'passed' if failed == 0 else 'ERROR ' if failed < 0 else 'FAILED'}  {title}" + (f" ({failed} checks)" if failed > 0 else ""))
    log(f"== {it._checks - len(it._failures)} of {it._checks} checks passed")
    for failure in it._failures:
        log(f"   - {failure}")
    return 1 if it._failures else 0


def main():
    command = sys.argv[1] if len(sys.argv) > 1 else "run"
    if command == "run":
        sys.exit(cmd_run(sys.argv[2:]))
    if command == "down":
        compose("down", "--remove-orphans")
    elif command == "reset":
        compose("down", "--remove-orphans", "-v")
        shutil.rmtree(STATE, ignore_errors=True)
        log("== matrix state removed")
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main()

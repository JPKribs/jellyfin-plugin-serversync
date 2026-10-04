#!/usr/bin/env python3
"""Integration tests for Server Sync against a three server Jellyfin pool in Docker.

Usage:
  run.py test        build the plugin, start the pool, seed it, run every scenario
  run.py up          build, start, and seed only, leaving the pool running for manual use
  run.py down        stop the pool, keeping its state
  run.py reset       stop the pool and delete all state, media, and credentials
  run.py scenarios   run the scenarios against an already seeded pool

Only the standard library is used. Docker and the dotnet SDK must be on the path.
"""
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
STATE = os.path.join(HERE, ".state")
PLUGIN_DIR = os.path.join(HERE, ".plugin")
PUBLISH_DIR = os.path.join(HERE, ".publish")
CREDS = os.path.join(STATE, "credentials.json")
IMAGE = os.environ.get("JELLYFIN_IMAGE", "jellyfin/jellyfin:12.1")
PLUGIN_GUID = "ebd650b5-6f4c-4ccb-b10d-23dffb3a7286"

SERVERS = {
    "source-a": {"port": 8111, "internal": "http://source-a:8096", "role": "source"},
    "source-b": {"port": 8112, "internal": "http://source-b:8096", "role": "source"},
    "local": {"port": 8113, "internal": "http://local:8096", "role": "local"},
}

# Each server's libraries, by name: the collection type and the files it holds as (relative path,
# seconds). Source A holds movies only. Source B holds movies and a show, so sync is exercised per
# type. The shared movie differs in length per server so the winning copy is recognizable by size.
LIBRARIES = {
    "source-a": {
        "Movies": ("movies", [("Shared Movie (2020)/Shared Movie (2020).mp4", 2), ("Only A (2021)/Only A (2021).mp4", 2)]),
    },
    "source-b": {
        "Movies": ("movies", [("Shared Movie (2020)/Shared Movie (2020).mp4", 3), ("Only B (2022)/Only B (2022).mp4", 2)]),
        "Shows": ("tvshows", [
            ("Test Show (2023)/Season 01/Test Show (2023) - S01E01 - Pilot.mp4", 2),
            ("Test Show (2023)/Season 01/Test Show (2023) - S01E02 - Second.mp4", 2),
        ]),
    },
    "local": {"Movies": ("movies", []), "Shows": ("tvshows", [])},
}

# Metadata seeded on the sources after their scans, so every module has something to carry over.
ONLY_A_METADATA = {
    "Overview": "A film that exists only on source A.",
    "Genres": ["Drama", "Testing"],
    "Tags": ["serversync"],
    "Studios": [{"Name": "Test Studio"}],
    "People": [{"Name": "Alice Actor", "Type": "Actor", "Role": "Lead"}, {"Name": "Bob Director", "Type": "Director"}],
}
PILOT_METADATA = {
    "Overview": "The pilot episode, which exists only on source B.",
    "People": [{"Name": "Carol Actor", "Type": "Actor", "Role": "Guest"}],
}
ALICE_BIO = "Alice Actor was born to star in integration tests."

LIBRARY_OPTIONS = {
    "LibraryOptions": {
        "EnableInternetProviders": False,
        "EnableRealtimeMonitor": False,
        "EnableChapterImageExtraction": False,
        "EnableTrickplayImageExtraction": False,
        "ExtractTrickplayImagesDuringLibraryScan": False,
        "SaveLocalMetadata": False,
        "TypeOptions": [{"Type": "Movie", "MetadataFetchers": [], "MetadataFetcherOrder": [], "ImageFetchers": [], "ImageFetcherOrder": []}],
    }
}


# --------------------------------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------------------------------

def log(msg):
    print(msg, flush=True)


def run(cmd, cwd=None, check=True, capture=False):
    result = subprocess.run(cmd, cwd=cwd or HERE, check=False, text=True, capture_output=capture)
    if check and result.returncode != 0:
        raise SystemExit(f"command failed ({result.returncode}): {' '.join(cmd)}\n{result.stdout if capture else ''}{result.stderr if capture else ''}")
    return result


def read_build_yaml():
    info = {"artifacts": []}
    in_artifacts = False
    with open(os.path.join(REPO, "build.yaml"), encoding="utf-8") as f:
        for raw in f:
            line = raw.rstrip("\n")
            if line.startswith("artifacts:"):
                in_artifacts = True
                continue
            if in_artifacts:
                if line.startswith("- "):
                    info["artifacts"].append(line[2:].strip().strip('"'))
                    continue
                if line and not line.startswith(" "):
                    in_artifacts = False
            for key in ("name", "guid", "version", "targetAbi"):
                if line.startswith(key + ":"):
                    info[key] = line.split(":", 1)[1].strip().strip('"')
    return info


class Api:
    """Minimal Jellyfin client over urllib, one per server."""

    def __init__(self, name, port, device):
        self.name = name
        self.base = f"http://127.0.0.1:{port}"
        self.device = device
        self.token = None

    def _headers(self):
        auth = f'MediaBrowser Client="ServerSyncIT", Device="runner", DeviceId="{self.device}", Version="1.0"'
        if self.token:
            auth += f', Token="{self.token}"'
        return {"Authorization": auth, "Content-Type": "application/json", "Accept": "application/json"}

    def call(self, method, path, body=None, expect=None, raw=False):
        data = None if body is None else json.dumps(body).encode()
        # A server that just started answers its public info endpoint a moment before it accepts
        # other requests, so a dropped connection is retried a few times before it counts.
        for attempt in range(6):
            req = urllib.request.Request(self.base + path, data=data, method=method, headers=self._headers())
            try:
                with urllib.request.urlopen(req, timeout=60) as resp:
                    content = resp.read()
                    status = resp.status
                break
            except urllib.error.HTTPError as e:
                content = e.read()
                status = e.code
                break
            except (urllib.error.URLError, ConnectionError, TimeoutError, OSError):
                if attempt == 5:
                    raise
                time.sleep(3)
        if expect is not None and status != expect:
            raise AssertionError(f"{self.name}: {method} {path} returned {status}: {content[:300]!r}")
        if raw:
            return status, content
        if not content:
            return status, None
        try:
            return status, json.loads(content)
        except ValueError:
            return status, content

    def get(self, path, expect=200):
        return self.call("GET", path, expect=expect)[1]

    def post(self, path, body=None, expect=None):
        return self.call("POST", path, body, expect=expect)

    def wait_ready(self, timeout=240):
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                status, info = self.call("GET", "/System/Info/Public")
                if status == 200 and info:
                    # Startup finishes a few seconds after the public endpoint first answers.
                    time.sleep(6)
                    return info
            except (urllib.error.URLError, ConnectionError, TimeoutError, OSError):
                pass
            time.sleep(2)
        raise SystemExit(f"{self.name} did not come up within {timeout}s")


# --------------------------------------------------------------------------------------------------
# Build and pool lifecycle
# --------------------------------------------------------------------------------------------------

def build_plugin():
    info = read_build_yaml()
    folder = f"{info['name']}_{info['version']}"
    log(f"== building plugin {info['version']} for ABI {info['targetAbi']}")
    project = os.path.join(REPO, "Jellyfin.Plugin.ServerSync", "Jellyfin.Plugin.ServerSync.csproj")
    shutil.rmtree(PUBLISH_DIR, ignore_errors=True)
    run(["dotnet", "publish", project, "-c", "Release", "-o", PUBLISH_DIR, f"-p:Version={info['version']}", "--nologo", "-v", "quiet"], cwd=REPO)
    shutil.rmtree(PLUGIN_DIR, ignore_errors=True)
    os.makedirs(PLUGIN_DIR)
    for artifact in info["artifacts"]:
        shutil.copy(os.path.join(PUBLISH_DIR, artifact), PLUGIN_DIR)
    logo = os.path.join(REPO, "Jellyfin.Plugin.ServerSync", "Assets", "Logo.png")
    if os.path.exists(logo):
        shutil.copy(logo, os.path.join(PLUGIN_DIR, "Logo.png"))
    meta = {
        "guid": info["guid"],
        "name": info["name"],
        "version": info["version"],
        "targetAbi": info["targetAbi"],
        "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "status": "Active",
        "autoUpdate": True,
        "imagePath": "Logo.png",
        "assemblies": [a for a in info["artifacts"] if a.endswith(".dll")],
    }
    with open(os.path.join(PLUGIN_DIR, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2)
    return folder


def compose(*args, folder=None):
    env = dict(os.environ, JELLYFIN_IMAGE=IMAGE)
    if folder:
        env["PLUGIN_FOLDER"] = folder
    elif os.path.exists(os.path.join(PLUGIN_DIR, "meta.json")):
        with open(os.path.join(PLUGIN_DIR, "meta.json"), encoding="utf-8") as f:
            m = json.load(f)
        env["PLUGIN_FOLDER"] = f"{m['name']}_{m['version']}"
    subprocess.run(["docker", "compose", *args], cwd=HERE, check=True, env=env)


def ffmpeg(out_dir, *args):
    """Runs the ffmpeg inside the Jellyfin image with out_dir mounted at /out, so the host needs nothing."""
    run([
        "docker", "run", "--rm", "-v", f"{out_dir}:/out", "--entrypoint", "/usr/lib/jellyfin-ffmpeg/ffmpeg", IMAGE,
        "-loglevel", "error", "-y", *args,
    ])


def seed_media():
    """Generates every library's files and a few solid color images for image sync."""
    media_root = os.path.join(STATE, "media")
    for server, libraries in LIBRARIES.items():
        for library, (_, files) in libraries.items():
            os.makedirs(os.path.join(media_root, server, library), exist_ok=True)
            for relative, seconds in files:
                target = os.path.join(media_root, server, library, relative)
                if os.path.exists(target):
                    continue
                folder = os.path.dirname(target)
                os.makedirs(folder, exist_ok=True)
                log(f"   generating {server}/{library}/{relative} ({seconds}s)")
                ffmpeg(folder,
                       "-f", "lavfi", "-i", f"testsrc=duration={seconds}:size=64x64:rate=10",
                       "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono", "-t", str(seconds),
                       "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", f"/out/{os.path.basename(target)}")
    images = os.path.join(STATE, "images")
    os.makedirs(images, exist_ok=True)
    for color in ("red", "blue", "green"):
        if not os.path.exists(os.path.join(images, f"{color}.png")):
            ffmpeg(images, "-f", "lavfi", "-i", f"color=c={color}:s=96x144", "-frames:v", "1", f"/out/{color}.png")


def image_base64(color):
    import base64
    with open(os.path.join(STATE, "images", f"{color}.png"), "rb") as f:
        return base64.b64encode(f.read()).decode()


def load_creds():
    if os.path.exists(CREDS):
        with open(CREDS, encoding="utf-8") as f:
            return json.load(f)
    return {"password": uuid.uuid4().hex[:16], "servers": {}}


def save_creds(creds):
    os.makedirs(STATE, exist_ok=True)
    with open(CREDS, "w", encoding="utf-8") as f:
        json.dump(creds, f, indent=2)


def connect(name):
    return Api(name, SERVERS[name]["port"], f"it-{name}")


def setup_pool():
    """Completes each wizard once, creates the Movies library and an API key, and records everything."""
    creds = load_creds()
    apis = {}
    for name, spec in SERVERS.items():
        api = connect(name)
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
        status, auth = api.post("/Users/AuthenticateByName", {"Username": "admin", "Pw": creds["password"]}, expect=200)
        api.token = auth["AccessToken"]
        entry["userId"] = auth["User"]["Id"]
        entry["serverId"] = info["Id"]
        sysconfig = api.get("/System/Configuration")
        if sysconfig.get("ServerName") != name:
            sysconfig["ServerName"] = name
            api.post("/System/Configuration", sysconfig, expect=204)
        entry["libraries"] = {}
        for library, (kind, _) in LIBRARIES[name].items():
            folders = api.get("/Library/VirtualFolders")
            found = next((v for v in folders if v["Name"] == library), None)
            if found is None:
                log(f"== {name}: creating {library} library")
                options = json.loads(json.dumps(LIBRARY_OPTIONS))
                options["LibraryOptions"]["TypeOptions"] = [{"Type": t, "MetadataFetchers": [], "MetadataFetcherOrder": [], "ImageFetchers": [], "ImageFetcherOrder": []} for t in ("Movie", "Series", "Season", "Episode")]
                api.post(f"/Library/VirtualFolders?name={library}&collectionType={kind}&refreshLibrary=false&paths=%2Fmedia%2F{library}", options, expect=204)
                folders = api.get("/Library/VirtualFolders")
                found = next(v for v in folders if v["Name"] == library)
            entry["libraries"][library] = found["ItemId"]
        api.post("/Library/Refresh", None, expect=204)
        viewer = next((u for u in api.get("/Users") if u["Name"] == "viewer"), None)
        if viewer is None:
            log(f"== {name}: creating viewer user")
            status, viewer = api.post("/Users/New", {"Name": "viewer"}, expect=200)
        entry["viewerId"] = viewer["Id"]
        keys = api.get("/Auth/Keys")["Items"]
        if not keys:
            api.post("/Auth/Keys?app=ServerSyncIT", expect=204)
            keys = api.get("/Auth/Keys")["Items"]
        entry["apiKey"] = keys[0]["AccessToken"]
        apis[name] = api
    save_creds(creds)
    for name, libraries in LIBRARIES.items():
        for library, (kind, files) in libraries.items():
            if files:
                wait_items(apis[name], "Movie" if kind == "movies" else "Episode", len(files))
    seed_metadata(creds, apis)
    return creds, apis


def wait_items(api, item_type, count, timeout=180):
    """Waits for a library to list the expected items, kicking a fresh scan if the first one was skipped."""
    deadline = time.time() + timeout
    next_kick = time.time() + 30
    while time.time() < deadline:
        result = api.get(f"/Items?IncludeItemTypes={item_type}&Recursive=true")
        if result.get("TotalRecordCount", 0) >= count:
            return
        if time.time() > next_kick:
            api.post("/Library/Refresh", None, expect=204)
            next_kick = time.time() + 30
        time.sleep(2)
    raise AssertionError(f"{api.name}: library did not reach {count} items of type {item_type}")


def find_item(api, item_type, needle):
    """Finds an item whose name starts with the needle, or whose path contains it. Episodes with no
    provider carry the series name, so they are found by path."""
    items = api.get(f"/Items?IncludeItemTypes={item_type}&Recursive=true&Fields=Overview,Genres,Tags,Studios,People,Path")["Items"]
    return next((i for i in items if (i.get("Name") or "").startswith(needle) or needle in (i.get("Path") or "")), None)


def update_item(api, item, changes):
    """Edits an item the way the dashboard does: fetch the full DTO, change fields, post it back."""
    dto = api.get(f"/Items/{item['Id']}")
    dto.update(changes)
    api.post(f"/Items/{item['Id']}", dto, expect=204)


def upload_image(api, path, color):
    data = image_base64(color).encode()
    req = urllib.request.Request(api.base + path, data=data, method="POST", headers={**api._headers(), "Content-Type": "image/png"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        assert resp.status in (200, 204), f"image upload to {path} returned {resp.status}"


def seed_metadata(creds, apis):
    """Gives the sources metadata, people, images, and user settings worth syncing. Safe to repeat."""
    a, b = apis["source-a"], apis["source-b"]
    only_a = find_item(a, "Movie", "Only A")
    if only_a and only_a.get("Overview") != ONLY_A_METADATA["Overview"]:
        log("== source-a: seeding metadata, people, and an image on Only A")
        update_item(a, only_a, ONLY_A_METADATA)
        upload_image(a, f"/Items/{only_a['Id']}/Images/Primary", "red")
    alice = find_item(a, "Person", "Alice Actor")
    if alice and alice.get("Overview") != ALICE_BIO:
        log("== source-a: seeding Alice Actor's bio and image")
        update_item(a, alice, {"Overview": ALICE_BIO})
        upload_image(a, f"/Items/{alice['Id']}/Images/Primary", "blue")
    pilot = find_item(b, "Episode", "Pilot")
    if pilot and pilot.get("Overview") != PILOT_METADATA["Overview"]:
        log("== source-b: seeding metadata and people on the pilot episode")
        update_item(b, pilot, PILOT_METADATA)
    viewer_id = creds["servers"]["source-a"]["viewerId"]
    viewer = a.get(f"/Users/{viewer_id}")
    if viewer["Configuration"].get("SubtitleMode") != "Always":
        log("== source-a: seeding the viewer user's policy, configuration, and avatar")
        policy = viewer["Policy"]
        policy["MaxActiveSessions"] = 3
        policy["EnableContentDownloading"] = True
        a.post(f"/Users/{viewer_id}/Policy", policy, expect=204)
        config = viewer["Configuration"]
        config["SubtitleMode"] = "Always"
        config["AudioLanguagePreference"] = "fra"
        a.post(f"/Users/{viewer_id}/Configuration", config, expect=204)
        upload_image(a, f"/Users/{viewer_id}/Images/Primary", "green")


# --------------------------------------------------------------------------------------------------
# Plugin driving
# --------------------------------------------------------------------------------------------------

def plugin_config(api):
    return api.get(f"/Plugins/{PLUGIN_GUID}/Configuration")


def save_plugin_config(api, config):
    api.post(f"/Plugins/{PLUGIN_GUID}/Configuration", config, expect=204)


def server_entry(creds, name, key=None, local="local", mode="Pull"):
    """Builds the entry one server (local) keeps for a peer (name): internal URL, the key that peer issued,
    and mappings by library name and username between the two."""
    e = creds["servers"][name]
    mine = creds["servers"][local]
    return {
        "Key": key or uuid.uuid4().hex,
        "Name": name,
        "Url": SERVERS[name]["internal"],
        "ExternalUrl": "",
        "AllowPrivateNetwork": True,
        "ApiKey": e["apiKey"],
        "AuthenticatedUser": "",
        "AuthenticatedUserId": "",
        "ServerName": name,
        "ServerId": e["serverId"],
        "Mode": mode,
        "IsEnabled": True,
        "LibraryMappings": [{
            "SourceLibraryId": lib_id, "SourceLibraryName": library, "SourceRootPath": f"/media/{library}",
            "LocalLibraryId": mine["libraries"][library], "LocalLibraryName": library, "LocalRootPath": f"/media/{library}",
            "IsEnabled": True, "FilterMode": "AllowAll", "FilteredItems": [],
        } for library, lib_id in e["libraries"].items() if library in mine["libraries"]],
        "UserMappings": [
            {"SourceUserId": e["userId"], "SourceUserName": "admin", "LocalUserId": mine["userId"], "LocalUserName": "admin", "IsEnabled": True},
            {"SourceUserId": e["viewerId"], "SourceUserName": "viewer", "LocalUserId": mine["viewerId"], "LocalUserName": "viewer", "IsEnabled": True},
        ],
    }


def configure_server(creds, api, local, order, modes=None, **flags):
    """Writes one server's peer list in priority order, keeping stored entries so keys round trip, and sets
    each peer's mode. All modules are off unless a flag turns one on."""
    modes = modes or {}
    config = plugin_config(api)
    existing = {s["Name"]: s for s in config.get("Servers", [])}
    servers = []
    for n in order:
        # Mappings are rebuilt from the credentials every time; only the entry key is kept so stored keys
        # and sync rows keep pointing at the same entry.
        entry = server_entry(creds, n, local=local, key=(existing.get(n) or {}).get("Key"))
        entry["Mode"] = modes.get(n, "Pull")
        servers.append(entry)
    config["Servers"] = servers
    config.update({
        "EnableContentSync": False, "EnableMetadataSync": False, "EnableHistorySync": False,
        "EnablePeopleSync": False, "EnableUserSync": False,
    })
    config.update(flags)
    save_plugin_config(api, config)
    stored = plugin_config(api)
    assert [s["Name"] for s in stored["Servers"]] == list(order), "server order did not persist"
    assert all(s["ApiKey"] for s in stored["Servers"]), "a server entry lost its key on save"


def configure_local(creds, api, order, **flags):
    """Writes the server list in the given priority order, keeping stored entries so keys round trip."""
    config = plugin_config(api)
    existing = {s["Name"]: s for s in config.get("Servers", [])}
    servers = [server_entry(creds, n, key=(existing.get(n) or {}).get("Key")) for n in order]
    for entry in servers:
        entry["Mode"] = "Pull"
    config["Servers"] = servers
    config.update({
        "EnableContentSync": True, "DownloadNewContentMode": "Enabled", "ReplaceExistingContentMode": "Enabled",
        "DeleteMissingContentMode": "Disabled", "MinimumFreeDiskSpaceGb": 0,
        "EnableMetadataSync": False, "EnableHistorySync": False, "EnablePeopleSync": False, "EnableUserSync": False,
    })
    config.update(flags)
    save_plugin_config(api, config)
    stored = plugin_config(api)
    assert [s["Name"] for s in stored["Servers"]] == list(order), "server order did not persist"
    assert all(s["ApiKey"] for s in stored["Servers"]), "a server entry lost its key on save"


def run_task(api, key, timeout=300):
    tasks = api.get("/ScheduledTasks")
    task = next(t for t in tasks if t["Key"] == key)
    api.post(f"/ScheduledTasks/Running/{task['Id']}", expect=204)
    deadline = time.time() + timeout
    while time.time() < deadline:
        time.sleep(1)
        state = api.get(f"/ScheduledTasks/{task['Id']}")
        if state["State"] == "Idle":
            result = state.get("LastExecutionResult") or {}
            if result.get("Status") not in ("Completed", None):
                raise AssertionError(f"task {key} ended {result.get('Status')}: {result.get('ErrorMessage')}")
            return
    raise AssertionError(f"task {key} did not finish within {timeout}s")


def content_rows(api):
    rows = api.get("/ServerSync/Items?take=200")["Items"]
    return {os.path.basename(r["SourcePath"]): r for r in rows}


def local_files():
    out = {}
    root = os.path.join(STATE, "media", "local")
    for dirpath, _, files in os.walk(root):
        for f in files:
            if f.endswith(".mp4"):
                out[f] = os.path.getsize(os.path.join(dirpath, f))
    return out


def user_data(api, user_id, item_id):
    return api.get(f"/Items/{item_id}?userId={user_id}")["UserData"]


def movie_id(api, title):
    items = api.get("/Items?IncludeItemTypes=Movie&Recursive=true")["Items"]
    return next(i["Id"] for i in items if i["Name"].startswith(title.split(" (")[0]))


# --------------------------------------------------------------------------------------------------
# Scenarios
# --------------------------------------------------------------------------------------------------

def scenario_content_priority(creds, apis):
    local = apis["local"]
    configure_local(creds, local, ["source-a", "source-b"])
    run_task(local, "ServerSyncUpdateTables")
    rows = content_rows(local)
    expect(rows["Shared Movie (2020).mp4"]["ServerName"] == "source-a", "shared title comes from the first server")
    expect(rows["Only A (2021).mp4"]["ServerName"] == "source-a", "unique title from A is tracked")
    expect(rows["Only B (2022).mp4"]["ServerName"] == "source-b", "unique title from B is tracked")
    expect(rows["Test Show (2023) - S01E01 - Pilot.mp4"]["ServerName"] == "source-b", "the show's episodes come from B")
    expected = {"Shared Movie (2020).mp4", "Only A (2021).mp4", "Only B (2022).mp4",
                "Test Show (2023) - S01E01 - Pilot.mp4", "Test Show (2023) - S01E02 - Second.mp4"}
    # A reused pool may also carry the film the content hint scenario adds, so the check is a superset.
    expect(expected <= set(rows), f"the five seeded files are tracked, got {len(rows)} rows")
    run_task(local, "ServerSyncDownloadContent")
    files = local_files()
    expect(expected <= set(files), "all five seeded files downloaded")
    expect(os.path.exists(os.path.join(STATE, "media", "local", "Shows", "Test Show (2023)", "Season 01", "Test Show (2023) - S01E01 - Pilot.mp4")),
           "episodes landed under the Shows library in the series and season folders")
    size_a = os.path.getsize(os.path.join(STATE, "media", "source-a", "Movies", "Shared Movie (2020)", "Shared Movie (2020).mp4"))
    expect(files["Shared Movie (2020).mp4"] == size_a, "the shared file is A's copy")
    run_task(local, "ServerSyncUpdateTables")
    rows = content_rows(local)
    expect(all(r["Status"] == "Synced" for r in rows.values()), "a second refresh leaves every row Synced")
    wait_items(local, "Movie", 3)
    wait_items(local, "Episode", 2)
    expect(local.get("/Items?IncludeItemTypes=Series&Recursive=true")["TotalRecordCount"] == 1, "local recognizes the series")
    expect(local.get("/Items?IncludeItemTypes=Episode&Recursive=true")["TotalRecordCount"] == 2, "local recognizes both episodes")


def scenario_reorder_rehomes(creds, apis):
    local = apis["local"]
    configure_local(creds, local, ["source-b", "source-a"])
    run_task(local, "ServerSyncUpdateTables")
    rows = content_rows(local)
    expect(rows["Shared Movie (2020).mp4"]["ServerName"] == "source-b", "shared title re-homed to the new first server")
    expect(rows["Shared Movie (2020).mp4"]["Status"] in ("Queued", "Pending"), "the different copy is queued for replacement")
    expect(all(r.get("PendingType") != "Deletion" and r["Status"] != "Deleting" for r in rows.values()), "no row is scheduled for deletion")
    expect(rows["Only A (2021).mp4"]["Status"] == "Synced" and rows["Only B (2022).mp4"]["Status"] == "Synced", "unique titles untouched")
    expect(all(rows[k]["Status"] == "Synced" for k in rows if "Test Show" in k), "episodes untouched")
    run_task(local, "ServerSyncDownloadContent")
    size_b = os.path.getsize(os.path.join(STATE, "media", "source-b", "Movies", "Shared Movie (2020)", "Shared Movie (2020).mp4"))
    expect(local_files()["Shared Movie (2020).mp4"] == size_b, "the shared file is now B's copy")
    configure_local(creds, local, ["source-a", "source-b"])


def scenario_history_negotiation(creds, apis):
    local, a = apis["local"], apis["source-a"]
    configure_local(creds, local, ["source-a", "source-b"], EnableHistorySync=True, HistorySyncNegotiate=True)
    lu, au = creds["servers"]["local"]["userId"], creds["servers"]["source-a"]["userId"]
    li, ai = movie_id(local, "Only A"), movie_id(a, "Only A")
    local.post(f"/UserPlayedItems/{li}?userId={lu}&datePlayed=2026-09-30T10:00:00Z", expect=200)
    a.post(f"/UserFavoriteItems/{ai}?userId={au}", expect=200)
    run_task(local, "ServerSyncRefreshHistoryTable")
    run_task(local, "ServerSyncMissingHistory")
    ud_a, ud_l = user_data(a, au, ai), user_data(local, lu, li)
    expect(ud_a["Played"] and ud_a["IsFavorite"], "the play made locally reached the source and its favorite stayed")
    expect(ud_l["Played"] and ud_l["IsFavorite"], "the favorite made on the source reached local and its play stayed")
    # Stale path: the source changes between refresh and sync.
    run_task(local, "ServerSyncRefreshHistoryTable")
    local.call("DELETE", f"/UserFavoriteItems/{li}?userId={lu}", expect=200)
    run_task(local, "ServerSyncRefreshHistoryTable")
    a.call("DELETE", f"/UserPlayedItems/{ai}?userId={au}", expect=200)
    run_task(local, "ServerSyncMissingHistory")
    ud_a, ud_l = user_data(a, au, ai), user_data(local, lu, li)
    expect(not ud_a["Played"] and not ud_a["IsFavorite"], "after a stale answer the source holds the re-merged state")
    expect(not ud_l["Played"] and not ud_l["IsFavorite"], "local holds the same re-merged state")
    # A change made only locally reaches the source.
    local.post(f"/UserFavoriteItems/{li}?userId={lu}", expect=200)
    run_task(local, "ServerSyncRefreshHistoryTable")
    run_task(local, "ServerSyncMissingHistory")
    expect(user_data(a, au, ai)["IsFavorite"], "a favorite set only locally reached the source")
    configure_local(creds, local, ["source-a", "source-b"])


def scenario_metadata_by_type(creds, apis):
    local = apis["local"]
    configure_local(creds, local, ["source-a", "source-b"], EnableMetadataSync=True, MetadataSyncMetadata=True, MetadataSyncGenres=True,
                    MetadataSyncTags=True, MetadataSyncStudios=True, MetadataSyncPeople=True, MetadataSyncImages=True)
    run_task(local, "ServerSyncRefreshMetadataTable")
    run_task(local, "ServerSyncMissingMetadata")
    movie = find_item(local, "Movie", "Only A")
    expect(movie is not None and movie.get("Overview") == ONLY_A_METADATA["Overview"], "movie overview arrived from A")
    expect(movie is not None and set(movie.get("Genres") or []) == set(ONLY_A_METADATA["Genres"]), "movie genres arrived")
    expect(movie is not None and set(movie.get("Tags") or []) == set(ONLY_A_METADATA["Tags"]), "movie tags arrived")
    expect(movie is not None and [s["Name"] for s in movie.get("Studios") or []] == ["Test Studio"], "movie studio arrived")
    expect(movie is not None and {p["Name"] for p in movie.get("People") or []} == {"Alice Actor", "Bob Director"}, "movie cast and crew arrived")
    expect(movie is not None and "Primary" in (movie.get("ImageTags") or {}), "movie primary image arrived")
    pilot = find_item(local, "Episode", "Pilot")
    expect(pilot is not None and pilot.get("Overview") == PILOT_METADATA["Overview"], "episode overview arrived from B")
    expect(pilot is not None and {p["Name"] for p in pilot.get("People") or []} == {"Carol Actor"}, "episode guest cast arrived")


def scenario_people_sync(creds, apis):
    local = apis["local"]
    configure_local(creds, local, ["source-a", "source-b"], EnablePeopleSync=True, PeopleSyncImages=True)
    run_task(local, "ServerSyncRefreshPeopleTable")
    run_task(local, "ServerSyncMissingPeople")
    alice = find_item(local, "Person", "Alice Actor")
    expect(alice is not None, "Alice Actor exists locally after the people pass")
    expect(alice is not None and alice.get("Overview") == ALICE_BIO, "Alice's bio arrived from A")
    expect(alice is not None and "Primary" in (alice.get("ImageTags") or {}), "Alice's image arrived from A")


def scenario_user_sync(creds, apis):
    local = apis["local"]
    configure_local(creds, local, ["source-a", "source-b"], EnableUserSync=True, UserSyncPolicy=True, UserSyncConfiguration=True, UserSyncProfileImage=True)
    run_task(local, "ServerSyncRefreshUserTable")
    run_task(local, "ServerSyncMissingUserData")
    viewer = local.get(f"/Users/{creds['servers']['local']['viewerId']}")
    # The hint scenarios move the session limit later on, so compare against what source-a holds now.
    limit_on_a = apis["source-a"].get(f"/Users/{creds['servers']['source-a']['viewerId']}")["Policy"].get("MaxActiveSessions")
    expect(viewer["Configuration"].get("SubtitleMode") == "Always", "viewer's subtitle mode arrived from A")
    expect(viewer["Configuration"].get("AudioLanguagePreference") == "fra", "viewer's audio language arrived from A")
    expect(viewer["Policy"].get("MaxActiveSessions") == limit_on_a, "viewer's session limit arrived from A")
    expect(viewer["Policy"].get("EnableContentDownloading") is True, "viewer's download permission arrived from A")
    expect(bool(viewer.get("PrimaryImageTag")), "viewer's avatar arrived from A")
    configure_local(creds, local, ["source-a", "source-b"])


def hints(api):
    return api.get("/ServerSync/Hints")


def wait_until(check, timeout, message):
    """Polls a condition for up to timeout seconds and records it as one expectation."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            if check():
                expect(True, message)
                return True
        except AssertionError:
            raise
        except Exception:
            pass
        time.sleep(1)
    expect(False, message + f" (within {timeout}s)")
    return False


def scenario_hints_three_servers(creds, apis):
    """Every server tells the other two about history changes as they happen, and each pulls the change.
    Movies exist on all three after the content scenarios, so one shared title is watched from each side."""
    names = ["source-a", "source-b", "local"]
    both = {n: "Sync" for n in names}
    for n in names:
        peers = [p for p in names if p != n]
        configure_server(creds, apis[n], n, peers, both, EnableHistorySync=True, HistorySyncNegotiate=True)
    ids = {n: (creds["servers"][n]["userId"], movie_id(apis[n], "Shared Movie")) for n in names}

    def played(n):
        return bool(user_data(apis[n], *ids[n])["Played"])

    def favorite(n):
        return bool(user_data(apis[n], *ids[n])["IsFavorite"])

    def queues_empty():
        return all(not hints(apis[n])["Outbound"] and not hints(apis[n])["Inbound"] and hints(apis[n])["Pending"] == 0 for n in names)

    for n in names:
        apis[n].call("DELETE", f"/UserPlayedItems/{ids[n][1]}?userId={ids[n][0]}", expect=200)
        apis[n].call("DELETE", f"/UserFavoriteItems/{ids[n][1]}?userId={ids[n][0]}", expect=200)
    wait_until(queues_empty, 60, "the pool is quiet before the test")

    # A play on local reaches both sources through hints alone. No scheduled task runs.
    apis["local"].post(f"/UserPlayedItems/{ids['local'][1]}?userId={ids['local'][0]}&datePlayed=2026-10-01T10:00:00Z", expect=200)
    wait_until(lambda: played("source-a") and played("source-b"), 60, "a play on local reached both sources without a scan")
    wait_until(queues_empty, 60, "every queue drained after the play")

    # A favorite on source-a reaches the other two.
    apis["source-a"].post(f"/UserFavoriteItems/{ids['source-a'][1]}?userId={ids['source-a'][0]}", expect=200)
    wait_until(lambda: favorite("local") and favorite("source-b"), 60, "a favorite on source-a reached local and source-b")
    wait_until(queues_empty, 60, "every queue drained after the favorite")

    # Nothing echoes: a quiet pool stays quiet and every server agrees.
    time.sleep(12)
    expect(queues_empty(), "no hint was raised by an apply, so the pool is loop free")
    expect(all(played(n) and favorite(n) for n in names), "all three servers hold the same state")
    expect(all(len(activity(apis[n], "watch history for Shared Movie")) >= 1 for n in names), "every server wrote activity entries for the hints it applied")

    # A later edit on source-b reverses the play everywhere, through the three way merge.
    apis["source-b"].call("DELETE", f"/UserPlayedItems/{ids['source-b'][1]}?userId={ids['source-b'][0]}", expect=200)
    wait_until(lambda: not played("local") and not played("source-a"), 60, "unmarking on source-b reversed the play on both other servers")
    expect(all(favorite(n) for n in names), "the favorite survived the reversal")
    wait_until(queues_empty, 60, "every queue drained after the reversal")

    for n in ("source-a", "source-b"):
        configure_server(creds, apis[n], n, [])
    configure_local(creds, apis["local"], ["source-a", "source-b"])


INFO_FLAGS = dict(EnableMetadataSync=True, MetadataSyncMetadata=True, MetadataSyncGenres=True, MetadataSyncTags=True,
                  MetadataSyncStudios=True, MetadataSyncPeople=True, MetadataSyncImages=True,
                  EnablePeopleSync=True, PeopleSyncImages=True)


def overview(api, item_type, needle):
    item = find_item(api, item_type, needle)
    return item.get("Overview") if item else None


def set_overview(api, item_type, needle, text):
    item = find_item(api, item_type, needle)
    assert item is not None, f"{api.name} has no {item_type} matching {needle}"
    update_item(api, item, {"Overview": text})


def scenario_item_hints(creds, apis):
    """Metadata and people edits travel as hints, the newest edit wins across the pool, and a full scan
    against a peer that carries versions keeps a newer local edit instead of taking the peer's."""
    names = ["source-a", "source-b", "local"]
    both = {n: "Sync" for n in names}
    for n in names:
        configure_server(creds, apis[n], n, [p for p in names if p != n], both, **INFO_FLAGS)

    def queues_empty():
        return all(not hints(apis[n])["Outbound"] and not hints(apis[n])["Inbound"] and hints(apis[n])["Pending"] == 0 for n in names)

    wait_until(queues_empty, 60, "the pool is quiet before the test")

    # An overview edit on source-a reaches local, which holds the same film. Source-b has no copy and drops it.
    set_overview(apis["source-a"], "Movie", "Only A", "Edited on source-a")
    wait_until(lambda: overview(apis["local"], "Movie", "Only A") == "Edited on source-a", 60, "a metadata edit on source-a reached local without a scan")
    wait_until(queues_empty, 60, "every queue drained after the metadata edit")
    only_a = find_item(apis["local"], "Movie", "Only A")
    status, version = apis["local"].call("GET", f"/ServerSync/Hints/Version?kind=Metadata&localItemId={only_a['Id']}")
    expect(status == 200 and version and version["ServerId"] == creds["servers"]["source-a"]["serverId"] and not version["IsThisServer"],
           "local records source-a as the editor of the hinted overview")

    # The newest edit wins everywhere, whichever server made it.
    set_overview(apis["local"], "Movie", "Shared Movie", "First, from local")
    wait_until(lambda: all(overview(apis[n], "Movie", "Shared Movie") == "First, from local" for n in ("source-a", "source-b")), 60, "an edit on local reached both sources")
    shared = find_item(apis["local"], "Movie", "Shared Movie")
    status, version = apis["local"].call("GET", f"/ServerSync/Hints/Version?kind=Metadata&localItemId={shared['Id']}")
    expect(status == 200 and version and version["IsThisServer"], "local records itself as the editor of its own overview")
    status, version = apis["source-a"].call("GET", f"/ServerSync/Hints/Version?kind=Metadata&localItemId={find_item(apis['source-a'], 'Movie', 'Shared Movie')['Id']}")
    expect(status == 200 and version and version["ServerId"] == creds["servers"]["local"]["serverId"], "source-a records local as the editor of the copy it pulled")
    wait_until(queues_empty, 60, "every queue drained after the first shared edit")
    set_overview(apis["source-b"], "Movie", "Shared Movie", "Second, from source-b")
    wait_until(lambda: all(overview(apis[n], "Movie", "Shared Movie") == "Second, from source-b" for n in ("source-a", "local")), 60, "a later edit on source-b replaced it on the other two")
    wait_until(queues_empty, 60, "every queue drained after the second shared edit")
    time.sleep(8)
    expect(queues_empty() and all(overview(apis[n], "Movie", "Shared Movie") == "Second, from source-b" for n in names), "the pool is quiet and agrees on the newest overview")

    # A person's bio edited on local reaches source-a, which knows Alice. Source-b never cast her and drops it.
    set_overview(apis["local"], "Person", "Alice Actor", "Alice, edited on local")
    wait_until(lambda: overview(apis["source-a"], "Person", "Alice Actor") == "Alice, edited on local", 60, "a people edit on local reached source-a without a scan")
    wait_until(queues_empty, 60, "every queue drained after the people edit")

    # The full scan against a peer that carries versions keeps a newer local edit. Source-b is silenced
    # so its older edit only becomes visible through local's scan, and source-b is put first so the
    # shared film's row comes from it.
    configure_server(creds, apis["source-b"], "source-b", [])
    configure_server(creds, apis["source-a"], "source-a", ["local"], {"local": "Sync"}, **INFO_FLAGS)
    configure_server(creds, apis["local"], "local", ["source-b", "source-a"], {"source-b": "Pull", "source-a": "Sync"}, **INFO_FLAGS)
    set_overview(apis["source-b"], "Movie", "Shared Movie", "Older, on source-b")
    time.sleep(2)
    set_overview(apis["local"], "Movie", "Shared Movie", "Newer, on local")
    wait_until(lambda: overview(apis["source-a"], "Movie", "Shared Movie") == "Newer, on local", 60, "the newer local edit reached source-a by hint")
    run_task(apis["local"], "ServerSyncRefreshMetadataTable")
    run_task(apis["local"], "ServerSyncMissingMetadata")
    expect(overview(apis["local"], "Movie", "Shared Movie") == "Newer, on local", "the scan kept the newer local overview instead of taking source-b's older one")
    expect(overview(apis["source-a"], "Movie", "Shared Movie") == "Newer, on local", "source-a still holds the newer overview after the scan")

    for n in ("source-a", "source-b"):
        configure_server(creds, apis[n], n, [])
    configure_local(creds, apis["local"], ["source-a", "source-b"])


def scenario_content_and_user_hints(creds, apis):
    """A file that appears on a source is downloaded by local on a hint, and a user's policy change
    travels both ways with the newest edit winning. Sources send without pulling content themselves."""
    user_flags = dict(EnableUserSync=True, UserSyncPolicy=True, UserSyncConfiguration=True, UserSyncProfileImage=True)
    configure_server(creds, apis["local"], "local", ["source-a", "source-b"], {"source-a": "Sync", "source-b": "Sync"},
                     EnableContentSync=True, DownloadNewContentMode="Enabled", ReplaceExistingContentMode="Enabled",
                     DeleteMissingContentMode="Disabled", MinimumFreeDiskSpaceGb=0, **user_flags)
    configure_server(creds, apis["source-a"], "source-a", ["local"], {"local": "Sync"}, **user_flags)
    configure_server(creds, apis["source-b"], "source-b", ["local"], {"local": "Sync"}, **user_flags)
    names = ["source-a", "source-b", "local"]

    def queues_empty():
        return all(not hints(apis[n])["Outbound"] and not hints(apis[n])["Inbound"] and hints(apis[n])["Pending"] == 0 for n in names)

    wait_until(queues_empty, 60, "the pool is quiet before the test")

    # A new film lands on source-a. Its library scan raises the hint and local downloads it with no task run.
    title = "Only A Two (2024)"
    folder = os.path.join(STATE, "media", "source-a", "Movies", title)
    os.makedirs(folder, exist_ok=True)
    ffmpeg(folder, "-f", "lavfi", "-i", "testsrc=duration=2:size=64x64:rate=10", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono",
           "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", f"/out/{title}.mp4")
    apis["source-a"].post("/Library/Refresh", None, expect=204)
    target = os.path.join(STATE, "media", "local", "Movies", title, f"{title}.mp4")
    wait_until(lambda: os.path.exists(target) and os.path.getsize(target) == os.path.getsize(os.path.join(folder, f"{title}.mp4")),
               150, "a file added on source-a was downloaded by local on a hint alone")
    wait_until(queues_empty, 60, "every queue drained after the download")
    wait_until(lambda: find_item(apis["local"], "Movie", "Only A Two") is not None, 120, "local's library picked the new film up through the queued scan")

    # A policy change on source-a reaches local, and a later change on local wins everywhere.
    viewer_a, viewer_l, viewer_b = (creds["servers"][n]["viewerId"] for n in ("source-a", "local", "source-b"))
    policy = apis["source-a"].get(f"/Users/{viewer_a}")["Policy"]
    policy["MaxActiveSessions"] = 5
    apis["source-a"].post(f"/Users/{viewer_a}/Policy", policy, expect=204)
    wait_until(lambda: apis["local"].get(f"/Users/{viewer_l}")["Policy"].get("MaxActiveSessions") == 5, 120, "a policy change on source-a reached local without a scan")
    wait_until(queues_empty, 90, "every queue drained after the policy change")
    policy = apis["local"].get(f"/Users/{viewer_l}")["Policy"]
    policy["MaxActiveSessions"] = 7
    apis["local"].post(f"/Users/{viewer_l}/Policy", policy, expect=204)
    wait_until(lambda: apis["source-a"].get(f"/Users/{viewer_a}")["Policy"].get("MaxActiveSessions") == 7
               and apis["source-b"].get(f"/Users/{viewer_b}")["Policy"].get("MaxActiveSessions") == 7, 120, "a later policy change on local reached both sources")
    wait_until(queues_empty, 90, "every queue drained after the second policy change")
    time.sleep(8)
    expect(queues_empty(), "the pool is quiet after the user changes")

    for n in ("source-a", "source-b"):
        configure_server(creds, apis[n], n, [])
    configure_local(creds, apis["local"], ["source-a", "source-b"])


def activity(api, needle, limit=40):
    """Server Sync activity log entries whose name contains the needle, newest first."""
    items = api.get(f"/System/ActivityLog/Entries?limit={limit}")["Items"]
    return [e for e in items if e["Name"].startswith("Server Sync") and needle in e["Name"]]


def restart_container(name, api):
    """Restarts one pool container, which clears paused peers held in memory."""
    run(["docker", "restart", f"serversync-it-{name}"])
    api.wait_ready(180)
    time.sleep(6)


def scenario_peer_endpoint_auth(creds, apis):
    """The peer and operator endpoints sit behind Jellyfin's own authentication: no token is refused,
    a standard user's token is refused, and an administrator's key is accepted."""
    local = apis["local"]
    e = creds["servers"]["local"]
    anon = Api("local", SERVERS["local"]["port"], "it-anon")
    for method, path in (("POST", "/ServerSync/Peer/Queue"), ("GET", "/ServerSync/Peer/Status"), ("GET", "/ServerSync/Peer/Link?serverId=x"),
                         ("POST", "/ServerSync/Peer/History"), ("GET", "/ServerSync/Hints"), ("POST", "/ServerSync/Hints/CheckPeer"),
                         ("GET", "/ServerSync/Hints/Version?kind=People&name=x"), ("DELETE", "/ServerSync/Servers/x/Rows")):
        status, _ = anon.call(method, path, {} if method == "POST" else None)
        expect(status == 401, f"{method} {path} without a token is refused ({status})")
    status, auth = local.post("/Users/AuthenticateByName", {"Username": "viewer", "Pw": ""}, expect=200)
    viewer = Api("local", SERVERS["local"]["port"], "it-viewer")
    viewer.token = auth["AccessToken"]
    for method, path in (("POST", "/ServerSync/Peer/Queue"), ("GET", "/ServerSync/Peer/Status"), ("GET", "/ServerSync/Peer/Capabilities"), ("GET", "/ServerSync/Hints")):
        status, _ = viewer.call(method, path, {} if method == "POST" else None)
        expect(status == 403, f"{method} {path} with a standard user's token is refused ({status})")
    admin = Api("local", SERVERS["local"]["port"], "it-key")
    admin.token = e["apiKey"]
    expect(admin.call("GET", "/ServerSync/Peer/Capabilities")[0] == 200, "an administrator's API key reaches the peer endpoints")
    expect(admin.call("GET", "/ServerSync/Hints")[0] == 200, "an administrator's API key reaches the operator endpoint")
    status, removed = admin.call("DELETE", "/ServerSync/Servers/no-such-key/Rows")
    expect(status == 200 and removed and all(v == 0 for v in removed.values()), "forgetting an unknown server removes nothing and answers with the counts")


def scenario_push_responses(creds, apis):
    """Every answer a peer can give to a push is handled: a peer that does not list the sender pauses
    with the reason, a refused key pauses, a server without the plugin pauses, a receiver with the module
    off declines and the sender drops the row, and each pause writes an activity entry."""
    local = apis["local"]
    e = creds["servers"]["local"]
    # 409: source-b does not list local. 403: a bad key for source-a. 404: a path with no plugin behind it.
    configure_server(creds, apis["source-b"], "source-b", [])
    configure_server(creds, apis["source-a"], "source-a", ["local"], {"local": "Sync"}, EnableHistorySync=True, HistorySyncNegotiate=True,
                     EnableMetadataSync=False, EnablePeopleSync=True, PeopleSyncImages=True)
    configure_server(creds, local, "local", ["source-a", "source-b"], {"source-a": "Sync", "source-b": "Sync"}, **INFO_FLAGS)
    config = plugin_config(local)
    for srv in config["Servers"]:
        if srv["Name"] == "source-a":
            srv["ApiKey"] = "not-a-real-key"
    config["Servers"].append({**server_entry(creds, "source-b", key=uuid.uuid4().hex), "Name": "no-plugin", "Url": "http://source-b:8096/web", "Mode": "Push"})
    save_plugin_config(local, config)

    set_overview(local, "Movie", "Shared Movie", f"Push response test {time.strftime('%H:%M:%S')}")
    local.post("/ServerSync/Hints/Run", None, expect=204)
    time.sleep(8)
    local.post("/ServerSync/Hints/Run", None, expect=204)
    h = hints(local)
    peers = {p["Name"]: p for p in h["Peers"]}
    expect("does not list this server" in (peers.get("source-b", {}).get("Reason") or ""), "a peer that does not list this server pauses with that reason (409)")
    expect("refused this server's key" in (peers.get("source-a", {}).get("Reason") or ""), "a peer that refuses the key pauses with that reason (401/403)")
    expect("not installed" in (peers.get("no-plugin", {}).get("Reason") or ""), "a URL with no Server Sync behind it pauses as not installed (404)")
    paused_rows = [r for r in h["Outbound"] if r["PeerName"] in ("source-a", "source-b", "no-plugin")]
    expect(paused_rows and all(r["State"] == "Pending" and r["Attempts"] >= 1 for r in paused_rows), "paused rows stay pending with an attempt recorded, nothing is lost")
    expect(len(activity(local, "paused hints to")) >= 3, "each pause wrote an activity entry")

    # Clear the pauses, give source-a its real key, list local on source-b, and send a metadata hint that
    # source-a declines because its metadata module is off while source-b applies it.
    restart_container("local", local)
    configure_server(creds, apis["source-b"], "source-b", ["local"], {"local": "Sync"}, **INFO_FLAGS)
    configure_server(creds, local, "local", ["source-a", "source-b"], {"source-a": "Sync", "source-b": "Sync"}, **INFO_FLAGS)
    # The rows deferred by the pauses above wait fifteen minutes; discard them so only the new hint counts.
    for r in hints(local)["Outbound"]:
        local.call("DELETE", f"/ServerSync/Hints/Outbound/{r['Id']}", expect=204)
    applied_before = len(activity(apis["source-a"], "metadata for Shared Movie"))
    set_overview(local, "Movie", "Shared Movie", f"Declined test {time.strftime('%H:%M:%S')}")
    wait_until(lambda: not [r for r in hints(local)["Outbound"] if r["PeerName"] == "source-a"], 60, "a hint the receiver declined (module off) was dropped by the sender after a 200")
    wait_until(lambda: not hints(local)["Outbound"], 60, "the same hint was accepted and completed by the peer whose module is on")
    expect(len(activity(apis["source-a"], "metadata for Shared Movie")) == applied_before, "the declining receiver applied nothing")

    for n in ("source-a", "source-b"):
        configure_server(creds, apis[n], n, [])
    configure_local(creds, local, ["source-a", "source-b"])


def scenario_check_link_by_direction(creds, apis):
    """Check Link judges the pairing by the chosen direction: Pull is ready when the source pushes to us
    and warns when it does not or lacks the plugin, Push needs the destination to list us as Pull or Sync,
    and Sync needs the peer to list us as Sync."""
    local = apis["local"]
    configure_server(creds, local, "local", ["source-a", "source-b"], {"source-a": "Pull", "source-b": "Pull"})
    entry_a = next(s for s in plugin_config(local)["Servers"] if s["Name"] == "source-a")

    def check(entry, mode):
        status, r = local.post("/ServerSync/Hints/CheckPeer", {"ServerUrl": entry["Url"], "ApiKey": "__JPK_SECRET_KEPT__",
                                                            "ServerKey": entry["Key"], "AllowPrivateNetwork": True, "Mode": mode}, expect=200)
        return r["Severity"], r["Message"]

    # Every server in the pool runs the plugin, so the "not installed" warning is covered by the unit level
    # message test rather than here.
    configure_server(creds, apis["source-a"], "source-a", [])
    sev, msg = check(entry_a, "Pull")
    expect(sev == "warn" and "will not arrive as they happen" in msg, "Pull against a server that does not push to us warns, and still pulls")
    sev, _ = check(entry_a, "Push")
    expect(sev == "error", "Push against a server that does not list us is an error")
    configure_server(creds, apis["source-a"], "source-a", ["local"], {"local": "Pull"})
    sev, msg = check(entry_a, "Push")
    expect(sev == "ok", "Push is ready once the destination lists us as Pull")
    sev, _ = check(entry_a, "Sync")
    expect(sev == "error", "Sync against a peer that lists us only as Pull is an error")
    configure_server(creds, apis["source-a"], "source-a", ["local"], {"local": "Sync"})
    sev, msg = check(entry_a, "Sync")
    expect(sev == "ok", "Sync is ready once the peer lists us as Sync")
    sev, msg = check(entry_a, "Pull")
    expect(sev == "ok" and "also arrive as they happen" in msg, "Pull against a server that pushes to us is ready with live changes")
    _, full = local.post("/ServerSync/Hints/CheckPeer", {"ServerUrl": entry_a["Url"], "ApiKey": "__JPK_SECRET_KEPT__",
                                                      "ServerKey": entry_a["Key"], "AllowPrivateNetwork": True, "Mode": "Pull"}, expect=200)
    expect(abs(full.get("ClockSkewSeconds", 999)) < 30 and "clock" not in full["Message"], "Check Link reads the peer's clock and finds it in step")
    configure_server(creds, apis["source-a"], "source-a", [])
    configure_local(creds, local, ["source-a", "source-b"])


def scenario_non_admin_peer_key(creds, apis):
    """Local holds source-a with a standard user's token (viewer). Changes on source-a still arrive on
    local through hints for every category, applied with that user's access: history for that user only
    and one way, metadata, people, content, and the user's own settings. Source-a announces with the
    administrator's key it holds for local, which is what the inbound queue needs."""
    local, a = apis["local"], apis["source-a"]
    ea, el = creds["servers"]["source-a"], creds["servers"]["local"]
    status, auth = a.post("/Users/AuthenticateByName", {"Username": "viewer", "Pw": ""}, expect=200)
    viewer_token = auth["AccessToken"]
    user_flags = dict(EnableUserSync=True, UserSyncPolicy=True, UserSyncConfiguration=True, UserSyncProfileImage=True)
    configure_server(creds, local, "local", ["source-a"], {"source-a": "Sync"}, EnableContentSync=True, DownloadNewContentMode="Enabled",
                     ReplaceExistingContentMode="Enabled", DeleteMissingContentMode="Disabled", MinimumFreeDiskSpaceGb=0,
                     EnableHistorySync=True, HistorySyncNegotiate=True, **INFO_FLAGS, **user_flags)
    config = plugin_config(local)
    entry = next(s for s in config["Servers"] if s["Name"] == "source-a")
    entry["ApiKey"] = viewer_token
    entry["AuthenticatedUser"] = "viewer"
    entry["AuthenticatedUserId"] = ea["viewerId"]
    entry["UserMappings"] = [{"SourceUserId": ea["viewerId"], "SourceUserName": "viewer", "LocalUserId": el["viewerId"], "LocalUserName": "viewer", "IsEnabled": True}]
    save_plugin_config(local, config)
    configure_server(creds, a, "source-a", ["local"], {"local": "Sync"}, EnableHistorySync=True, HistorySyncNegotiate=True, **INFO_FLAGS, **user_flags)

    status, r = local.post("/ServerSync/Hints/CheckPeer", {"ServerUrl": entry["Url"], "ApiKey": "__JPK_SECRET_KEPT__", "ServerKey": entry["Key"], "AllowPrivateNetwork": True, "Mode": "Pull"}, expect=200)
    expect(r["Severity"] == "warn" and "standard user" in r["Message"], "Check Link names a standard user's key and still allows Pull")
    status, t = local.post("/ServerSync/TestConnection", {"ServerUrl": entry["Url"], "ApiKey": "__JPK_SECRET_KEPT__", "ServerKey": entry["Key"], "AllowPrivateNetwork": True}, expect=200)
    expect(t["Success"] and t.get("IsAdministrator") is False, "the connection test reports the key as a standard user's")

    def queues_empty():
        h = hints(local)
        return not h["Inbound"] and not hints(a)["Outbound"]

    # History: a play by the viewer on source-a reaches local's viewer, one way.
    ai = movie_id(a, "Shared Movie"); li = movie_id(local, "Shared Movie")
    a.call("DELETE", f"/UserPlayedItems/{ai}?userId={ea['viewerId']}", expect=200)
    local.call("DELETE", f"/UserPlayedItems/{li}?userId={el['viewerId']}", expect=200)
    wait_until(queues_empty, 60, "queues are quiet before the history check")
    a.post(f"/UserPlayedItems/{ai}?userId={ea['viewerId']}&datePlayed=2026-10-02T10:00:00Z", expect=200)
    wait_until(lambda: user_data(local, el["viewerId"], li)["Played"], 90, "history: a play by the viewer on source-a reached local with the viewer's key")
    wait_until(lambda: not hints(local)["Inbound"], 60, "history: local finished the hint")
    # Local cannot report completion with a standard user's key; source-a reads it from local's status on
    # its next check, so its row stays Sent rather than failing or looping.
    a_rows = hints(a)["Outbound"]
    expect(all(r["State"] == "Sent" for r in a_rows), "history: source-a's row waits as Sent for the status check, since local's key cannot report completion")
    status, st = local.call("GET", "/ServerSync/Peer/Status")
    expect(status == 200 and (not a_rows or any(r["HintId"] in st.get("Completed", []) for r in a_rows)), "history: local's status lists the finished hint for source-a to read")
    expect(len(activity(local, "watch history for Shared Movie")) >= 1, "history: local wrote an activity entry")

    # Metadata.
    stamp = f"Non admin metadata {time.strftime('%H:%M:%S')}"
    set_overview(a, "Movie", "Only A", stamp)
    wait_until(lambda: overview(local, "Movie", "Only A") == stamp, 90, "metadata: an edit on source-a reached local with the viewer's key")
    expect(len(activity(local, "metadata for Only A")) >= 1, "metadata: local wrote an activity entry")

    # People.
    bio = f"Alice non admin {time.strftime('%H:%M:%S')}"
    set_overview(a, "Person", "Alice Actor", bio)
    wait_until(lambda: overview(local, "Person", "Alice Actor") == bio, 90, "people: an edit on source-a reached local with the viewer's key")
    expect(len(activity(local, "person Alice Actor")) >= 1, "people: local wrote an activity entry")

    # Users: the viewer's own policy.
    policy = a.get(f"/Users/{ea['viewerId']}")["Policy"]
    policy["MaxActiveSessions"] = 9
    a.post(f"/Users/{ea['viewerId']}/Policy", policy, expect=204)
    wait_until(lambda: local.get(f"/Users/{el['viewerId']}")["Policy"].get("MaxActiveSessions") == 9, 120, "users: the viewer's own policy change on source-a reached local with the viewer's key")
    expect(len(activity(local, "user viewer")) >= 1, "users: local wrote an activity entry")

    # Content: a new film on source-a, downloaded with the viewer's key.
    # A fresh title each run, so a reused pool still has to download something.
    title = f"Only A Three {time.strftime('%H%M%S')} (2025)"
    folder = os.path.join(STATE, "media", "source-a", "Movies", title)
    os.makedirs(folder, exist_ok=True)
    ffmpeg(folder, "-f", "lavfi", "-i", "testsrc=duration=2:size=64x64:rate=10", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono",
           "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", f"/out/{title}.mp4")
    a.post("/Library/Refresh", None, expect=204)
    target = os.path.join(STATE, "media", "local", "Movies", title, f"{title}.mp4")
    wait_until(lambda: os.path.exists(target), 150, "content: a file added on source-a was downloaded by local with the viewer's key")
    expect(len(activity(local, f"file Only A Three {title.split(' ')[3]}")) >= 1, "content: local wrote an activity entry")

    configure_server(creds, a, "source-a", [])
    configure_local(creds, local, ["source-a", "source-b"])


SCENARIOS = [
    ("content priority across two sources, movies and a show", scenario_content_priority),
    ("reordering re-homes a shared item without deleting", scenario_reorder_rehomes),
    ("history negotiates both ways with the source", scenario_history_negotiation),
    ("metadata, images, and people carry over by type", scenario_metadata_by_type),
    ("people sync carries bios and images", scenario_people_sync),
    ("user sync carries policy, configuration, and avatar", scenario_user_sync),
    ("history hints travel across three servers without loops", scenario_hints_three_servers),
    ("metadata and people hints, newest edit wins, scan keeps newer local", scenario_item_hints),
    ("content and user hints across the pool", scenario_content_and_user_hints),
    ("peer and operator endpoints require Jellyfin authentication", scenario_peer_endpoint_auth),
    ("check link judges the pairing by direction", scenario_check_link_by_direction),
    ("push responses: paused peers, declined hints, activity entries", scenario_push_responses),
    ("a standard user's key still receives every category", scenario_non_admin_peer_key),
]

_failures = []
_checks = 0


def expect(condition, message):
    global _checks
    _checks += 1
    if condition:
        log(f"   ok   {message}")
    else:
        log(f"   FAIL {message}")
        _failures.append(message)


def run_scenarios(creds, apis):
    """Runs every scenario, or only those whose title contains one of the words given after the command."""
    only = [w.lower() for w in sys.argv[2:]]
    for title, fn in SCENARIOS:
        if only and not any(w in title.lower() for w in only):
            continue
        log(f"== scenario: {title}")
        before = len(_failures)
        try:
            fn(creds, apis)
        except Exception as ex:  # noqa: BLE001 report and continue to the next scenario
            log(f"   ERROR {type(ex).__name__}: {ex}")
            _failures.append(f"{title}: {ex}")
        if len(_failures) == before:
            log("   passed")
    log(f"== {_checks - len(_failures)} of {_checks} checks passed")
    if _failures:
        log("== failures:")
        for f in _failures:
            log(f"   - {f}")
        return 1
    return 0


# --------------------------------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------------------------------

def plugin_fingerprint():
    dll = os.path.join(PLUGIN_DIR, "Jellyfin.Plugin.ServerSync.dll")
    if not os.path.exists(dll):
        return None
    import hashlib
    with open(dll, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def cmd_up():
    before = plugin_fingerprint()
    folder = build_plugin()
    os.makedirs(STATE, exist_ok=True)
    seed_media()
    log("== starting the pool")
    compose("up", "-d", "--remove-orphans", folder=folder)
    if before is not None and before != plugin_fingerprint():
        # The DLL is bind mounted, so a rebuilt plugin only loads after a restart.
        log("== plugin changed, restarting the pool")
        compose("restart")
    creds, apis = setup_pool()
    log("== pool ready")
    for name, spec in SERVERS.items():
        log(f"   {name}: http://localhost:{spec['port']}  admin / (password in {CREDS})")
    return creds, apis


def cmd_test():
    creds, apis = cmd_up()
    return run_scenarios(creds, apis)


def cmd_scenarios():
    creds = load_creds()
    apis = {}
    for name in SERVERS:
        api = connect(name)
        api.wait_ready(60)
        status, auth = api.post("/Users/AuthenticateByName", {"Username": "admin", "Pw": creds["password"]}, expect=200)
        api.token = auth["AccessToken"]
        apis[name] = api
    return run_scenarios(creds, apis)


def cmd_down():
    compose("down", "--remove-orphans")


def cmd_reset():
    compose("down", "--remove-orphans", "-v")
    shutil.rmtree(STATE, ignore_errors=True)
    shutil.rmtree(PLUGIN_DIR, ignore_errors=True)
    shutil.rmtree(PUBLISH_DIR, ignore_errors=True)
    log("== state removed")


def main():
    command = sys.argv[1] if len(sys.argv) > 1 else "test"
    commands = {"test": cmd_test, "up": cmd_up, "down": cmd_down, "reset": cmd_reset, "scenarios": cmd_scenarios}
    if command not in commands:
        raise SystemExit(__doc__)
    result = commands[command]()
    sys.exit(result if isinstance(result, int) else 0)


if __name__ == "__main__":
    main()

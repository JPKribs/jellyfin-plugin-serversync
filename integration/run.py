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
import re
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

PLUGIN_VERSION = next(
    line.split(":", 1)[1].strip().strip('"')
    for line in open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "build.yaml"))
    if line.startswith("version:"))

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
        self.version = PLUGIN_VERSION

    def _headers(self):
        auth = f'MediaBrowser Client="ServerSyncIT", Device="runner", DeviceId="{self.device}", Version="1.0"'
        if self.token:
            auth += f', Token="{self.token}"'
        return {"Authorization": auth, "Content-Type": "application/json", "Accept": "application/json", "X-ServerSync-Version": self.version}

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
        entry["serverId"] = info.get("Id") or api.get("/System/Info")["Id"]
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


# API keys may call the peer routes from anywhere, so the host and servers that do not list each other can.
def open_grants(creds, name):
    return [{"UserId": "apikey", "UserName": "API keys", "Locations": []},
            {"UserId": creds["servers"][name]["userId"], "UserName": "admin", "Locations": []}]

# Every server in the pool once the scenarios run, so a configuration change on one can be announced to the rest.
POOL_APIS = {}


def save_plugin_config(api, config):
    api.post(f"/Plugins/{PLUGIN_GUID}/Configuration", config, expect=204)
    # A sender re-reads what a peer accepts once a minute. The scenarios change modules far faster than
    # that, so every server is asked to re-read now, the way Run Now on the dashboard does.
    for other in POOL_APIS.values():
        try:
            other.post("/Sync/Hints/Run", None, expect=204)
        except Exception:
            pass


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
        # Changes are sent once an object has gone untouched this long. The pool keeps it short.
        "HintDebounceSeconds": 5,
        "PeerCallerGrants": open_grants(creds, local),
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
        "PeerCallerGrants": open_grants(creds, "local"),
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
    rows = api.get("/Sync/Items?take=200")["Items"]
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


def restamp_seeds(apis):
    """Saves the seeded values again on the sources, so each carries an edit newer than any local edit
    an earlier run left behind. The pool keeps its databases between runs, and a scenario that expects a
    source's value to arrive needs the source to hold the newest edit, not merely the same text."""
    a, b = apis["source-a"], apis["source-b"]
    for api, kind, needle, values in ((a, "Movie", "Only A", ONLY_A_METADATA), (a, "Person", "Alice Actor", {"Overview": ALICE_BIO}), (b, "Episode", "Pilot", PILOT_METADATA)):
        item = find_item(api, kind, needle)
        if item:
            update_item(api, item, values)


def scenario_metadata_by_type(creds, apis):
    local = apis["local"]
    restamp_seeds(apis)
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
    restamp_seeds(apis)
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
    overview = api.get("/Sync/Hints")
    rows = overview.get("Inbound") or []
    overview["Parked"] = [r for r in rows if r.get("Parked")]
    overview["Inbound"] = [r for r in rows if not r.get("Parked")]
    return overview


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


def playlists_of(api, user_id, name):
    items = api.get(f"/Items?userId={user_id}&IncludeItemTypes=Playlist&Recursive=true")["Items"]
    return [i for i in items if i["Name"] == name]


def playlist_paths(api, user_id, playlist_id):
    items = api.get(f"/Playlists/{playlist_id}/Items?userId={user_id}&Fields=Path")["Items"]
    return [i.get("Path") for i in items]


def scenario_playlist_hints(creds, apis):
    """A playlist made, edited, or deleted on one server of a Sync pair reaches the other through hints alone."""
    names = ["source-a", "local"]
    for n in names:
        peers = [p for p in names if p != n]
        configure_server(creds, apis[n], n, peers, {p: "Sync" for p in peers}, EnablePlaylistSync=True)
    admin = {n: creds["servers"][n]["userId"] for n in names}
    title = "IT Hint Playlist"

    def queues_empty():
        return all(not hints(apis[n])["Outbound"] and not hints(apis[n])["Inbound"] and hints(apis[n])["Pending"] == 0 for n in names)

    for n in names:
        for p in playlists_of(apis[n], admin[n], title):
            apis[n].call("DELETE", f"/Items/{p['Id']}", expect=204)
    wait_until(queues_empty, 60, "the pool is quiet before the test")
    for n in names:
        for p in playlists_of(apis[n], admin[n], title):
            apis[n].call("DELETE", f"/Items/{p['Id']}", expect=204)

    ids = [movie_id(apis["local"], "Only A (2021)"), movie_id(apis["local"], "Shared Movie")]
    _, made = apis["local"].post("/Playlists", {"Name": title, "Ids": ids, "UserId": admin["local"], "MediaType": "Video"}, expect=200)
    local_paths = playlist_paths(apis["local"], admin["local"], made["Id"])

    def mirrored_paths():
        found = playlists_of(apis["source-a"], admin["source-a"], title)
        return playlist_paths(apis["source-a"], admin["source-a"], found[0]["Id"]) if len(found) == 1 else None

    wait_until(lambda: mirrored_paths() == local_paths, 60, "a playlist made on local reached source-a with its items in order")
    wait_until(queues_empty, 60, "every queue drained after the playlist was made")

    copy = playlists_of(apis["source-a"], admin["source-a"], title)[0]
    entries = apis["source-a"].get(f"/Playlists/{copy['Id']}/Items?userId={admin['source-a']}")["Items"]
    apis["source-a"].call("DELETE", f"/Playlists/{copy['Id']}/Items?entryIds={entries[0]['PlaylistItemId']}", expect=204)
    wait_until(lambda: playlist_paths(apis["local"], admin["local"], made["Id"]) == local_paths[1:], 60, "removing an item on source-a removed it on local")
    wait_until(queues_empty, 60, "every queue drained after the edit")

    time.sleep(12)
    expect(queues_empty(), "no playlist hint was raised by an apply, so the pair is loop free")
    expect(len(playlists_of(apis["local"], admin["local"], title)) == 1, "local still holds exactly one copy")

    apis["local"].call("DELETE", f"/Items/{made['Id']}", expect=204)
    wait_until(lambda: not playlists_of(apis["source-a"], admin["source-a"], title), 60, "deleting the playlist on local deleted it on source-a")
    wait_until(queues_empty, 60, "every queue drained after the deletion")

    configure_server(creds, apis["source-a"], "source-a", [])
    configure_local(creds, apis["local"], ["source-a", "source-b"])


def scenario_playlist_pull(creds, apis):
    """On Pull the other server's playlist always wins: a copy is made, follows edits, and is deleted with it."""
    local, a = apis["local"], apis["source-a"]
    admin = {"local": creds["servers"]["local"]["userId"], "source-a": creds["servers"]["source-a"]["userId"]}
    title = "IT Pull Playlist"
    configure_server(creds, a, "source-a", [])
    configure_local(creds, local, ["source-a", "source-b"], EnablePlaylistSync=True, EnableContentSync=False)
    for name, api in (("local", local), ("source-a", a)):
        for p in playlists_of(api, admin[name], title):
            api.call("DELETE", f"/Items/{p['Id']}", expect=204)

    ids = [movie_id(a, "Only A (2021)"), movie_id(a, "Shared Movie")]
    if not playlists_of(a, admin["source-a"], "IT Pull Anchor"):
        a.post("/Playlists", {"Name": "IT Pull Anchor", "Ids": ids[:1], "UserId": admin["source-a"], "MediaType": "Video"}, expect=200)
    _, made = a.post("/Playlists", {"Name": title, "Ids": ids, "UserId": admin["source-a"], "MediaType": "Video"}, expect=200)
    run_task(local, "ServerSyncInformation")
    copies = playlists_of(local, admin["local"], title)
    expect(len(copies) == 1 and len(playlist_paths(local, admin["local"], copies[0]["Id"])) == 2, "a Pull copies the playlist with both items")

    local_entries = local.get(f"/Playlists/{copies[0]['Id']}/Items?userId={admin['local']}")["Items"]
    local.call("DELETE", f"/Playlists/{copies[0]['Id']}/Items?entryIds={local_entries[0]['PlaylistItemId']}", expect=204)
    run_task(local, "ServerSyncInformation")
    expect(len(playlist_paths(local, admin["local"], copies[0]["Id"])) == 2, "a local edit to a pulled copy is replaced by the parent's copy")

    a.call("DELETE", f"/Items/{made['Id']}", expect=204)
    run_task(local, "ServerSyncInformation")
    expect(not playlists_of(local, admin["local"], title), "deleting the playlist on the parent deletes the pulled copy")

    configure_local(creds, local, ["source-a", "source-b"])


def peer_playlists(api, name):
    return [p for p in api.get("/Sync/Peer/Playlists")["Playlists"] if p["Name"] == name]


def clear_playlists(apis, names, title):
    for n in names:
        api = apis[n]
        for p in peer_playlists(api, title):
            status, body = api.call("DELETE", f"/Items/{p['Id']}", raw=True)
            if status == 404:
                owner = api.get(f"/Users/{p['OwnerUserId']}")["Name"]
                _, auth = api.post("/Users/AuthenticateByName", {"Username": owner, "Pw": ""}, expect=200)
                as_owner = Api(api.name, api.base.rsplit(":", 1)[1], api.device + "-owner")
                as_owner.token = auth["AccessToken"]
                status, body = as_owner.call("DELETE", f"/Items/{p['Id']}", raw=True)
            assert status in (204, 404), f"{n}: deleting playlist {p['Id']} returned {status}: {body[:300]!r}"


def scenario_playlist_push(creds, apis):
    """On Push the sender's copy always overrides the receiver's, and deletions follow."""
    local, a = apis["local"], apis["source-a"]
    admin = {"local": creds["servers"]["local"]["userId"], "source-a": creds["servers"]["source-a"]["userId"]}
    title = "IT Push Playlist"
    configure_server(creds, local, "local", ["source-a"], {"source-a": "Push"}, EnablePlaylistSync=True)
    configure_server(creds, a, "source-a", ["local"], {"local": "Pull"}, EnablePlaylistSync=True)
    clear_playlists(apis, ["local", "source-a"], title)

    def queues_empty():
        return all(not hints(api)["Outbound"] and not hints(api)["Inbound"] and hints(api)["Pending"] == 0 for api in (local, a))

    wait_until(queues_empty, 60, "the pair is quiet before the test")
    ids = [movie_id(local, "Only A (2021)"), movie_id(local, "Shared Movie")]
    _, made = local.post("/Playlists", {"Name": title, "Ids": ids, "UserId": admin["local"], "MediaType": "Video"}, expect=200)
    wait_until(lambda: len(playlists_of(a, admin["source-a"], title)) == 1, 60, "a playlist made on local was pushed to source-a")
    copy = playlists_of(a, admin["source-a"], title)[0]

    entries = a.get(f"/Playlists/{copy['Id']}/Items?userId={admin['source-a']}")["Items"]
    a.call("DELETE", f"/Playlists/{copy['Id']}/Items?entryIds={entries[0]['PlaylistItemId']}", expect=204)
    time.sleep(8)
    local_entries = local.get(f"/Playlists/{made['Id']}/Items?userId={admin['local']}")["Items"]
    local.call("DELETE", f"/Playlists/{made['Id']}/Items?entryIds={local_entries[1]['PlaylistItemId']}", expect=204)
    expected = playlist_paths(local, admin["local"], made["Id"])
    wait_until(lambda: playlist_paths(a, admin["source-a"], copy["Id"]) == expected, 60, "a later edit on local overrides the copy on source-a")
    expect(len(playlist_paths(local, admin["local"], made["Id"])) == 1, "the edit on source-a's copy never came back to local")

    local.call("DELETE", f"/Items/{made['Id']}", expect=204)
    wait_until(lambda: not playlists_of(a, admin["source-a"], title), 60, "deleting on local deletes the pushed copy")
    wait_until(queues_empty, 60, "every queue drained")

    configure_server(creds, a, "source-a", [])
    configure_local(creds, local, ["source-a", "source-b"])


def scenario_playlist_sharing(creds, apis):
    """With Sharing on, shares map to the mapped users and the public flag is copied."""
    local, a = apis["local"], apis["source-a"]
    ea, el = creds["servers"]["source-a"], creds["servers"]["local"]
    title = "IT Shared Playlist"
    configure_server(creds, local, "local", ["source-a"], {"source-a": "Sync"}, EnablePlaylistSync=True, PlaylistSyncShares=True)
    configure_server(creds, a, "source-a", ["local"], {"local": "Sync"}, EnablePlaylistSync=True, PlaylistSyncShares=True)
    clear_playlists(apis, ["local", "source-a"], title)

    ids = [movie_id(a, "Only A (2021)")]
    a.post("/Playlists", {"Name": title, "Ids": ids, "UserId": ea["userId"], "MediaType": "Video", "IsPublic": False,
                          "Users": [{"UserId": ea["viewerId"], "CanEdit": True}]}, expect=200)

    def copied():
        found = peer_playlists(local, title)
        return found[0] if len(found) == 1 else None

    wait_until(lambda: copied() is not None, 60, "a shared playlist made on source-a reached local")
    p = copied()
    expect(p["OwnerUserId"] == el["userId"].replace("-", ""), "the copy belongs to the mapped owner")
    expect(p["OpenAccess"] is False, "the copy keeps the playlist private")
    expect([(s["UserId"], s["CanEdit"]) for s in p["Shares"]] == [(el["viewerId"].replace("-", ""), True)], "the copy is shared with the mapped viewer, who may edit it")

    clear_playlists(apis, ["source-a"], title)
    wait_until(lambda: copied() is None, 60, "deleting the shared playlist on source-a deletes the copy")
    configure_server(creds, a, "source-a", [])
    configure_local(creds, local, ["source-a", "source-b"])


def scenario_playlist_standard_user(creds, apis):
    """A standard user's key copies only the playlists that user can see, all owned by that user here."""
    local, a = apis["local"], apis["source-a"]
    ea, el = creds["servers"]["source-a"], creds["servers"]["local"]
    names = ["IT Viewer Own", "IT Viewer Shared", "IT Admin Private"]
    clear_playlists(apis, ["local", "source-a"], names[0])
    clear_playlists(apis, ["local", "source-a"], names[1])
    clear_playlists(apis, ["local", "source-a"], names[2])
    ids = [movie_id(a, "Only A (2021)")]
    a.post("/Playlists", {"Name": names[0], "Ids": ids, "UserId": ea["viewerId"], "MediaType": "Video", "IsPublic": False}, expect=200)
    a.post("/Playlists", {"Name": names[1], "Ids": ids, "UserId": ea["userId"], "MediaType": "Video", "IsPublic": False,
                          "Users": [{"UserId": ea["viewerId"], "CanEdit": False}]}, expect=200)
    a.post("/Playlists", {"Name": names[2], "Ids": ids, "UserId": ea["userId"], "MediaType": "Video", "IsPublic": False}, expect=200)

    _, auth = a.post("/Users/AuthenticateByName", {"Username": "viewer", "Pw": ""}, expect=200)
    configure_local(creds, local, ["source-a"], EnablePlaylistSync=True, PlaylistSyncShares=True, EnableContentSync=False)
    config = plugin_config(local)
    entry = next(s for s in config["Servers"] if s["Name"] == "source-a")
    entry["ApiKey"] = auth["AccessToken"]
    entry["AccessLevel"] = "User"
    entry["AuthenticatedUser"] = "viewer"
    entry["AuthenticatedUserId"] = ea["viewerId"]
    save_plugin_config(local, config)
    config = plugin_config(a)
    config["PeerCallerGrants"] = open_grants(creds, "source-a") + [{"UserId": ea["viewerId"], "UserName": "viewer", "Locations": []}]
    save_plugin_config(a, config)

    run_task(local, "ServerSyncInformation")
    own = peer_playlists(local, names[0])
    shared = peer_playlists(local, names[1])
    private = peer_playlists(local, names[2])
    viewer = el["viewerId"].replace("-", "")
    expect(len(own) == 1 and own[0]["OwnerUserId"] == viewer, "the viewer's own playlist is copied to the local viewer")
    expect(len(shared) == 1 and shared[0]["OwnerUserId"] == viewer, "a playlist shared with the viewer is copied, owned by the local viewer")
    expect(not private, "an administrator's private playlist is not copied")

    for n in names:
        clear_playlists(apis, ["local", "source-a"], n)
    config = plugin_config(a)
    config["PeerCallerGrants"] = open_grants(creds, "source-a")
    save_plugin_config(a, config)
    configure_local(creds, local, ["source-a", "source-b"])


def scenario_whitelist(creds, apis):
    """The peer routes refuse callers the whitelist does not name, with a reason the connection test reports."""
    api = apis["source-a"]

    def capabilities():
        status, body = api.call("GET", "/Sync/Peer/Capabilities", raw=True)
        return status, json.loads(body) if status == 403 and body else None

    def grants(value):
        config = plugin_config(api)
        config["PeerCallerGrants"] = value
        save_plugin_config(api, config)

    grants([])
    status, body = capabilities()
    expect(status == 403 and body and body["Code"] == "IpNotWhitelisted", "an API key from an address not on the Servers tab is refused as an IP not whitelisted")

    admin = creds["servers"]["source-a"]["userId"]
    grants([{"UserId": admin, "UserName": "admin", "Locations": ["203.0.113.0/24", "example.invalid"]}])
    status, body = capabilities()
    expect(status == 403 and body and body["Code"] == "UserNotWhitelistedForIp", "a user listed for other locations is refused for this IP")

    grants([{"UserId": admin, "UserName": "admin", "Locations": []}])
    status, _ = capabilities()
    expect(status == 200, "a user with no locations is allowed from anywhere")

    local = apis["local"]
    entry = next(s for s in plugin_config(local)["Servers"] if s["Name"] == "source-a")
    grants([])
    _, t = local.post("/Sync/TestConnection", {"ServerUrl": entry["Url"], "ApiKey": "__JPK_SECRET_KEPT__", "ServerKey": entry["Key"], "AllowPrivateNetwork": True}, expect=200)
    expect(t.get("SyncAccess") == "IpNotWhitelisted", "Test Connection reports that this server is not whitelisted there")
    grants(open_grants(creds, "source-a"))
    _, t = local.post("/Sync/TestConnection", {"ServerUrl": entry["Url"], "ApiKey": "__JPK_SECRET_KEPT__", "ServerKey": entry["Key"], "AllowPrivateNetwork": True}, expect=200)
    expect(t.get("SyncAccess") == "Allowed", "Test Connection reports access once whitelisted")


def scenario_history_stale_base(creds, apis):
    """A base two servers agreed on goes stale when one of them takes a change from a third. Source-b and
    source-a first agree through a favorite from source-b, then local plays the title, which both take
    from local. When source-b unmarks it, source-a's base with source-b still says unplayed, and a three
    way merge would read source-a's play as its own and push it back. The versions say the unmark is the
    newer edit, so it wins everywhere and stays."""
    names = ["source-a", "source-b", "local"]
    mesh = {n: "Sync" for n in names}
    for n in names:
        configure_server(creds, apis[n], n, [p for p in names if p != n], mesh, EnableHistorySync=True, HistorySyncNegotiate=True)
    ids = {n: (creds["servers"][n]["userId"], movie_id(apis[n], "Shared Movie")) for n in names}

    def state(n):
        data = user_data(apis[n], *ids[n])
        return bool(data["Played"]), bool(data["IsFavorite"])

    def queues_empty():
        return all(not hints(apis[n])["Outbound"] and not hints(apis[n])["Inbound"] and hints(apis[n])["Pending"] == 0 for n in names)

    for n in names:
        apis[n].call("DELETE", f"/UserPlayedItems/{ids[n][1]}?userId={ids[n][0]}", expect=200)
        apis[n].call("DELETE", f"/UserFavoriteItems/{ids[n][1]}?userId={ids[n][0]}", expect=200)
    wait_until(queues_empty, 90, "the pool is quiet before the test")

    apis["source-b"].post(f"/UserFavoriteItems/{ids['source-b'][1]}?userId={ids['source-b'][0]}", expect=200)
    wait_until(lambda: all(state(n) == (False, True) for n in names), 90, "a favorite on source-b reached both others, so source-a and source-b agree on unplayed")
    wait_until(queues_empty, 90, "every queue drained after the favorite")

    apis["local"].post(f"/UserPlayedItems/{ids['local'][1]}?userId={ids['local'][0]}&datePlayed=2026-10-02T10:00:00Z", expect=200)
    wait_until(lambda: all(state(n) == (True, True) for n in names), 90, "a play on local reached both sources")
    wait_until(queues_empty, 90, "every queue drained after the play")

    apis["source-b"].call("DELETE", f"/UserPlayedItems/{ids['source-b'][1]}?userId={ids['source-b'][0]}", expect=200)
    wait_until(lambda: all(state(n) == (False, True) for n in names), 90, "the unmark on source-b won on every server, the stale base notwithstanding")
    wait_until(queues_empty, 90, "every queue drained after the unmark")
    time.sleep(12)
    expect(all(state(n) == (False, True) for n in names), "nothing pushed the play back: all three still hold unplayed with the favorite")

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

    # An overview edit on source-a reaches local, which holds the same film. Source-b has no copy and parks it.
    set_overview(apis["source-a"], "Movie", "Only A", "Edited on source-a")
    wait_until(lambda: overview(apis["local"], "Movie", "Only A") == "Edited on source-a", 60, "a metadata edit on source-a reached local without a scan")
    wait_until(queues_empty, 60, "every queue drained after the metadata edit")
    only_a = find_item(apis["local"], "Movie", "Only A")
    status, version = apis["local"].call("GET", f"/Sync/Hints/Version?kind=Metadata&localItemId={only_a['Id']}")
    expect(status == 200 and version and version["ServerId"] == creds["servers"]["source-a"]["serverId"] and not version["IsThisServer"],
           "local records source-a as the editor of the hinted overview")

    # The newest edit wins everywhere, whichever server made it.
    set_overview(apis["local"], "Movie", "Shared Movie", "First, from local")
    wait_until(lambda: all(overview(apis[n], "Movie", "Shared Movie") == "First, from local" for n in ("source-a", "source-b")), 60, "an edit on local reached both sources")
    shared = find_item(apis["local"], "Movie", "Shared Movie")
    status, version = apis["local"].call("GET", f"/Sync/Hints/Version?kind=Metadata&localItemId={shared['Id']}")
    expect(status == 200 and version and version["IsThisServer"], "local records itself as the editor of its own overview")
    status, version = apis["source-a"].call("GET", f"/Sync/Hints/Version?kind=Metadata&localItemId={find_item(apis['source-a'], 'Movie', 'Shared Movie')['Id']}")
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

    # User settings are never announced live, since Jellyfin raises no event for them. A policy change
    # on source-a stays put until the scheduled task runs, then arrives.
    viewer_a, viewer_l = (creds["servers"][n]["viewerId"] for n in ("source-a", "local"))
    before = apis["local"].get(f"/Users/{viewer_l}")["Policy"].get("MaxActiveSessions")
    # A value the pool has not used before, so a reused pool still sees a change.
    limit = 10 + (int(time.time()) % 80)
    policy = apis["source-a"].get(f"/Users/{viewer_a}")["Policy"]
    policy["MaxActiveSessions"] = limit
    apis["source-a"].post(f"/Users/{viewer_a}/Policy", policy, expect=204)
    time.sleep(12)
    expect(apis["local"].get(f"/Users/{viewer_l}")["Policy"].get("MaxActiveSessions") == before and queues_empty(), "a policy change raises no hint and the pool stays quiet")
    run_task(apis["local"], "ServerSyncInformation")
    expect(apis["local"].get(f"/Users/{viewer_l}")["Policy"].get("MaxActiveSessions") == limit, "the scheduled Sync Information task carries the policy change")

    for n in ("source-a", "source-b"):
        configure_server(creds, apis[n], n, [])
    configure_local(creds, apis["local"], ["source-a", "source-b"])


def activity(api, needle, limit=40):
    """Server Sync activity log entries whose name contains the needle, newest first."""
    items = api.get(f"/System/ActivityLog/Entries?limit={limit}")["Items"]
    return [e for e in items if (e.get("Type") or "").startswith("ServerSync")
            and needle.lower() in (e["Name"] + " " + (e.get("ShortOverview") or "")).lower()]


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
    routes = (("GET", "/Sync/Peer/Capabilities"), ("GET", "/Sync/Peer/Link?serverId=x"), ("POST", "/Sync/Peer/Queue"),
              ("POST", "/Sync/Peer/Complete"), ("GET", "/Sync/Peer/Status"), ("POST", "/Sync/Peer/Versions"), ("POST", "/Sync/Peer/History"),
              ("GET", "/Sync/Peer/Interest?serverId=x"), ("POST", "/Sync/Peer/Lookup"),
              ("GET", "/Sync/Hints"), ("POST", "/Sync/Hints/Run"), ("POST", "/Sync/Hints/CheckPeer"),
              ("GET", "/Sync/Hints/Version?kind=People&name=x"), ("DELETE", "/Sync/Hints/Outbound/1"), ("DELETE", "/Sync/Hints/Inbound/1"),
              ("DELETE", "/Sync/Servers/x/Rows"),
              ("GET", "/Sync/Items"), ("GET", "/Sync/ImageProxy?itemId=00000000000000000000000000000000"), ("GET", "/Sync/MetadataItems"),
              ("GET", "/Sync/HistoryItems"), ("GET", "/Sync/PeopleItems"), ("GET", "/Sync/UserSyncUsers"), ("POST", "/Sync/TestConnection"),
              ("POST", "/Sync/TriggerSync"), ("POST", "/Sync/ResetSyncDatabase"))
    refused = 0
    for method, path in routes:
        status, _ = anon.call(method, path, {} if method == "POST" else None)
        if status == 401:
            refused += 1
        else:
            expect(False, f"{method} {path} without a token answered {status} instead of 401")
    expect(refused == len(routes), f"every route refuses a caller with no token ({refused} of {len(routes)})")
    status, auth = local.post("/Users/AuthenticateByName", {"Username": "viewer", "Pw": ""}, expect=200)
    viewer = Api("local", SERVERS["local"]["port"], "it-viewer")
    viewer.token = auth["AccessToken"]
    for method, path in (("POST", "/Sync/Peer/Queue"), ("GET", "/Sync/Peer/Status"), ("GET", "/Sync/Peer/Capabilities"), ("GET", "/Sync/Hints")):
        status, _ = viewer.call(method, path, {} if method == "POST" else None)
        expect(status == 403, f"{method} {path} with a standard user's token is refused ({status})")
    admin = Api("local", SERVERS["local"]["port"], "it-key")
    admin.token = e["apiKey"]
    status, caps = admin.call("GET", "/Sync/Peer/Capabilities")
    expect(status == 200, "an administrator's API key reaches the peer endpoints")
    expect(isinstance(caps.get("Accepts"), list) and "ServerTime" in caps, "capabilities list the kinds this server applies and its clock")
    expect(admin.call("GET", "/Sync/Hints")[0] == 200, "an administrator's API key reaches the operator endpoint")
    status, removed = admin.call("DELETE", "/Sync/Servers/no-such-key/Rows")
    expect(status == 200 and removed and all(v == 0 for v in removed.values()), "forgetting an unknown server removes nothing and answers with the counts")

    # An administrator's key alone does not make a caller a peer. A request that names a configured
    # server as its sender must carry the secret that server was issued, so a forged batch, a forged
    # completion, and a forged history negotiation are refused with 428 until the real server pairs.
    configure_server(creds, local, "local", ["source-a"], {"source-a": "Sync"})
    a_id = creds["servers"]["source-a"]["serverId"]
    status, body = admin.call("POST", "/Sync/Peer/Queue", {"SenderServerId": a_id, "Items": []})
    expect(status == 428, f"a batch naming source-a as sender without its pairing secret is refused with 428 ({status})")
    status, body = admin.call("POST", "/Sync/Peer/Complete", {"SenderServerId": a_id, "Items": [{"HintId": f"{a_id}:1", "VersionTimestamp": "2999-01-01T00:00:00Z"}]})
    expect(status == 428, f"a completion naming source-a as sender without its pairing secret is refused with 428 ({status})")
    status, body = admin.call("POST", "/Sync/Peer/Complete", {"Items": []})
    expect(status == 400, f"a completion with no sender is malformed ({status})")
    status, body = admin.call("POST", "/Sync/Peer/History", {"SenderServerId": a_id, "Items": []})
    expect(status == 428, f"a history negotiation naming source-a as sender without its pairing secret is refused with 428 ({status})")
    status, body = admin.call("POST", "/Sync/Peer/History", {"SenderServerId": "not-a-configured-server", "Items": []})
    expect(status == 200, f"a history negotiation from a server this one does not list still negotiates, with nothing recorded ({status})")
    status, body = admin.call("GET", "/Sync/Peer/Status")
    expect(status == 400, f"a status read that does not name the asking server is malformed ({status})")
    other = Api("local", SERVERS["local"]["port"], "it-key")
    other.token = e["apiKey"]
    other.version = "0.0.0.0"
    status, body = other.call("POST", "/Sync/Peer/Queue", {"SenderServerId": a_id, "Items": []})
    expect(status == 426 and "same version" in (body or ""), f"a peer call from another Server Sync version is refused with 426 ({status})")
    status, body = other.call("POST", "/Sync/Peer/Pair", {"ServerId": a_id, "Secret": "x"})
    expect(status == 426, f"a pairing from another Server Sync version is refused with 426 ({status})")
    status, body = admin.call("GET", f"/Sync/Peer/Interest?serverId={a_id}")
    expect(status == 428, f"an interest read naming source-a without its pairing secret is refused with 428 ({status})")
    status, body = admin.call("POST", "/Sync/Peer/Lookup", {"Path": "/no/such/file.mkv"})
    expect(status == 200 and body and body.get("Found") is False, f"a lookup for a path this server does not hold answers not found ({status})")
    status, body = admin.call("POST", "/Sync/Peer/Lookup", {})
    expect(status == 400, f"a lookup with neither a path nor a name is malformed ({status})")
    status, caps = other.call("GET", "/Sync/Peer/Capabilities")
    expect(status == 200 and caps.get("PluginVersion") == PLUGIN_VERSION, "capabilities still answer another version, so Check Link can name both versions")
    status, body = admin.call("POST", "/Sync/Peer/Pair", {"ServerId": "not-a-configured-server", "Secret": "x"})
    expect(status == 409, f"a pairing from a server this one does not list is refused ({status})")

    # A name is held to the same rule as an address when private networks are disallowed.
    status, v = admin.call("POST", "/Sync/TestConnection", {"ServerUrl": "https://localhost:8096", "ApiKey": "x", "AllowPrivateNetwork": False})
    expect(status == 200 and not v["Success"] and "resolves" in v["Message"], "a name that resolves to loopback is refused when private networks are disallowed")
    status, v = admin.call("POST", "/Sync/TestConnection", {"ServerUrl": "https://source-a:8096", "ApiKey": "x", "AllowPrivateNetwork": False})
    expect(status == 200 and not v["Success"] and "resolves" in v["Message"], "a pool name that resolves to a private address is refused when private networks are disallowed")
    status, v = admin.call("POST", "/Sync/TestConnection", {"ServerUrl": "http://source-a:8096", "ApiKey": "x", "AllowPrivateNetwork": True})
    message = (v or {}).get("Message") or ""
    expect(status == 200 and "resolves" not in message and "Plain HTTP" not in message, f"the same name over plain HTTP passes the URL check when private networks are allowed ({message})")


def scenario_push_responses(creds, apis):
    """Every answer a peer can give to a push is handled: a peer that does not list the sender pauses
    with the reason, a refused key pauses, a server without the plugin pauses, a receiver with the module
    off declines and the sender drops the row, and each pause writes an activity entry."""
    local = apis["local"]
    e = creds["servers"]["local"]
    # 409: source-b wants metadata but does not list local. 403: a bad key for source-a. 404: a path with no plugin behind it.
    configure_server(creds, apis["source-b"], "source-b", [], **INFO_FLAGS)
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
    local.post("/Sync/Hints/Run", None, expect=204)
    time.sleep(8)
    local.post("/Sync/Hints/Run", None, expect=204)
    h = hints(local)
    peers = {p["Name"]: p for p in h["Peers"]}
    expect("does not list this server" in (peers.get("source-b", {}).get("Reason") or ""), "a peer that does not list this server pauses with that reason (409)")
    expect("refused this server's key" in (peers.get("source-a", {}).get("Reason") or ""), "a peer that refuses the key pauses with that reason (401/403)")
    expect("not installed" in (peers.get("no-plugin", {}).get("Reason") or ""), "a URL with no Server Sync behind it pauses as not installed (404)")
    paused_rows = [r for r in h["Outbound"] if r["PeerName"] in ("source-a", "source-b", "no-plugin")]
    expect(paused_rows and all(r["State"] == "Pending" and r["Attempts"] >= 1 for r in paused_rows), "paused rows stay pending with an attempt recorded, nothing is lost")
    expect(len(activity(local, "paused changes to")) >= 3, "each pause wrote an activity entry")

    # Clear the pauses, give source-a its real key, list local on source-b, and send a metadata hint that
    # source-a declines because its metadata module is off while source-b applies it.
    restart_container("local", local)
    configure_server(creds, apis["source-b"], "source-b", ["local"], {"local": "Sync"}, **INFO_FLAGS)
    configure_server(creds, local, "local", ["source-a", "source-b"], {"source-a": "Sync", "source-b": "Sync"}, **INFO_FLAGS)
    # The rows deferred by the pauses above wait fifteen minutes; discard them so only the new hint counts.
    for r in hints(local)["Outbound"]:
        local.call("DELETE", f"/Sync/Hints/Outbound/{r['Id']}", expect=204)
    applied_before = len(activity(apis["source-a"], "metadata for Shared Movie"))
    set_overview(local, "Movie", "Shared Movie", f"Declined test {time.strftime('%H:%M:%S')}")
    wait_until(lambda: not [r for r in hints(local)["Outbound"] if r["PeerName"] == "source-a"], 60, "a hint for a module the receiver has off is never owed to it, or is dropped once it declines")
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
        status, r = local.post("/Sync/Hints/CheckPeer", {"ServerUrl": entry["Url"], "ApiKey": "__JPK_SECRET_KEPT__",
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
    _, full = local.post("/Sync/Hints/CheckPeer", {"ServerUrl": entry_a["Url"], "ApiKey": "__JPK_SECRET_KEPT__",
                                                      "ServerKey": entry_a["Key"], "AllowPrivateNetwork": True, "Mode": "Pull"}, expect=200)
    expect(abs(full.get("ClockSkewSeconds", 999)) < 30 and "clock" not in full["Message"], "Check Link reads the peer's clock and finds it in step")
    expect(full.get("Paired") is True and "could not pair" not in full["Message"], "Check Link pairs the two servers, since the peer lists this one and holds an administrator's key for it")
    _, moved = local.post("/Sync/Hints/CheckPeer", {"ServerUrl": "http://source-b:8096", "ApiKey": "__JPK_SECRET_KEPT__",
                                                       "ServerKey": entry_a["Key"], "AllowPrivateNetwork": True, "Mode": "Pull"}, expect=200)
    expect(not moved.get("Reachable") and "only sent to the address saved" in moved["Message"], "the stored key is never sent to an address other than the one saved on the entry")
    status, t = local.post("/Sync/TestConnection", {"ServerUrl": "http://source-b:8096", "ApiKey": "__JPK_SECRET_KEPT__", "ServerKey": entry_a["Key"], "AllowPrivateNetwork": True}, expect=200)
    expect(not t["Success"] and "only sent to the address saved" in (t.get("Message") or ""), "the connection test refuses the stored key for an edited address too")
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

    status, t = local.post("/Sync/TestConnection", {"ServerUrl": entry["Url"], "ApiKey": "__JPK_SECRET_KEPT__", "ServerKey": entry["Key"], "AllowPrivateNetwork": True}, expect=200)
    expect(t["Success"] and t.get("IsAdministrator") is False, "the connection test reports the key as a standard user's")
    expect(t.get("SyncAccess") == "UserNotWhitelisted", "the connection test reports that the viewer is not whitelisted on source-a")

    config = plugin_config(a)
    config["PeerCallerGrants"] = open_grants(creds, "source-a") + [{"UserId": ea["viewerId"], "UserName": "viewer", "Locations": []}]
    save_plugin_config(a, config)
    status, t = local.post("/Sync/TestConnection", {"ServerUrl": entry["Url"], "ApiKey": "__JPK_SECRET_KEPT__", "ServerKey": entry["Key"], "AllowPrivateNetwork": True}, expect=200)
    expect(t.get("SyncAccess") == "Allowed", "once source-a whitelists the viewer, the connection test reports access")
    status, r = local.post("/Sync/Hints/CheckPeer", {"ServerUrl": entry["Url"], "ApiKey": "__JPK_SECRET_KEPT__", "ServerKey": entry["Key"], "AllowPrivateNetwork": True, "Mode": "Sync"}, expect=200)
    expect(r["Severity"] in ("ok", "warn"), "Check Link accepts Sync with a whitelisted standard user's key")

    def queues_empty():
        h = hints(local)
        return not h["Inbound"] and not hints(a)["Outbound"]

    # History: a play by the viewer on source-a reaches local's viewer, one way.
    ai = movie_id(a, "Shared Movie"); li = movie_id(local, "Shared Movie")
    a.call("DELETE", f"/UserPlayedItems/{ai}?userId={ea['viewerId']}", expect=200)
    local.call("DELETE", f"/UserPlayedItems/{li}?userId={el['viewerId']}", expect=200)
    wait_until(queues_empty, 60, "queues are quiet before the history check")
    a.post(f"/UserPlayedItems/{ai}?userId={ea['viewerId']}&datePlayed={time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())}", expect=200)
    wait_until(lambda: user_data(local, el["viewerId"], li)["Played"], 90, "history: a play by the viewer on source-a reached local with the viewer's key")
    wait_until(lambda: not hints(local)["Inbound"], 60, "history: local finished the hint")
    wait_until(lambda: not hints(a)["Outbound"], 60, "history: local reported completion with the whitelisted viewer's key")
    wait_until(lambda: len(activity(local, "watch history for Shared Movie")) >= 1, 15, "history: local wrote an activity entry")

    # Metadata.
    stamp = f"Non admin metadata {time.strftime('%H:%M:%S')}"
    set_overview(a, "Movie", "Only A", stamp)
    wait_until(lambda: overview(local, "Movie", "Only A") == stamp, 90, "metadata: an edit on source-a reached local with the viewer's key")
    wait_until(lambda: len(activity(local, "metadata for Only A")) >= 1, 15, "metadata: local wrote an activity entry")

    # People.
    bio = f"Alice non admin {time.strftime('%H:%M:%S')}"
    set_overview(a, "Person", "Alice Actor", bio)
    wait_until(lambda: overview(local, "Person", "Alice Actor") == bio, 90, "people: an edit on source-a reached local with the viewer's key")
    wait_until(lambda: len(activity(local, "person Alice Actor")) >= 1, 15, "people: local wrote an activity entry")

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
    wait_until(lambda: len(activity(local, f"file Only A Three {title.split(' ')[3]}")) >= 1, 15, "content: local wrote an activity entry")

    configure_server(creds, a, "source-a", [])
    configure_local(creds, local, ["source-a", "source-b"])


def scenario_dashboard_endpoints(creds, apis):
    """The dashboard's bulk and status endpoints answer as the pages expect: every module's Retry errors
    queues errored rows without listing them, an unknown status is refused, a peer reads only its own
    queue, and the queue overview answers with its parts."""
    local = apis["local"]
    for route in ("HistoryItems/Queue", "MetadataItems/Queue", "PeopleItems/Queue", "UserItems/Queue"):
        status, body = local.call("POST", f"/Sync/{route}", {"Ids": [], "Status": "Errored"})
        expect(status == 200 and isinstance((body or {}).get("Updated"), int), f"{route} queues every errored row from a status alone ({status})")
        status, _ = local.call("POST", f"/Sync/{route}", {"Ids": [], "Status": "NotAStatus"})
        expect(status == 400, f"{route} refuses a status it does not know ({status})")
    status, st = local.call("GET", "/Sync/Peer/Status?serverId=no-such-server")
    expect(status == 409, f"a status read for a server this one does not list is refused ({status})")
    overview = hints(local)
    expect(all(k in overview for k in ("Gathering", "Outbound", "Inbound", "Peers", "OutboundCounts")), "the queue overview answers with gathering, both queues, peers, and counts")


def make_film(server, title):
    folder = os.path.join(STATE, "media", server, "Movies", title)
    os.makedirs(folder, exist_ok=True)
    ffmpeg(folder, "-f", "lavfi", "-i", "testsrc=duration=2:size=64x64:rate=10", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono",
           "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", f"/out/{title}.mp4")
    return folder


def rescan_until(api, item_type, needle, present, message):
    api.post("/Library/Refresh", None, expect=204)
    return wait_until(lambda: (find_item(api, item_type, needle) is not None) == present, 150, message)


def scenario_removed_files_ignore(creds, apis):
    """Ignore hides a whole title folder with an empty .ignore, never a season, shows it again when it
    returns or the setting changes, leaves a user's own .ignore alone, and cancels waiting deletions."""
    local, a, b = apis["local"], apis["source-a"], apis["source-b"]
    title = "Hidden Title (2019)"
    file_name = f"{title}.mp4"
    source = os.path.join(STATE, "media", "source-a", "Movies", title)
    copy = os.path.join(STATE, "media", "local", "Movies", title)
    marker = os.path.join(copy, ".ignore")
    shutil.rmtree(copy, ignore_errors=True)
    make_film("source-a", title)
    rescan_until(a, "Movie", "Hidden Title", True, "source-a lists the new film")
    configure_local(creds, local, ["source-a", "source-b"], FullScanIntervalHours=0)

    def row():
        return content_rows(local).get(file_name)

    run_task(local, "ServerSyncUpdateTables")
    if (row() or {}).get("Status") != "Queued":
        local.post("/Sync/UpdateItemStatus", {"SourceItemId": row()["SourceItemId"], "Status": "Queued"}, expect=200)
    run_task(local, "ServerSyncDownloadContent")
    expect(os.path.exists(os.path.join(copy, file_name)), "local downloaded the film")

    def set_action(action, delete_mode):
        config = plugin_config(local)
        config["MissingContentAction"] = action
        config["DeleteMissingContentMode"] = delete_mode
        config["FullScanIntervalHours"] = 0
        save_plugin_config(local, config)

    def remove_from_source():
        shutil.rmtree(source, ignore_errors=True)
        rescan_until(a, "Movie", "Hidden Title", False, "source-a no longer lists the film")

    set_action("Ignore", "Disabled")
    remove_from_source()
    run_task(local, "ServerSyncUpdateTables")
    expect(os.path.exists(marker) and os.path.getsize(marker) == 0, "Ignore writes an empty .ignore in the title folder")
    expect(os.path.exists(os.path.join(copy, file_name)), "the hidden file is kept")
    expect((row() or {}).get("Status") == "Ignored", "the hidden row is Ignored")
    run_task(local, "ServerSyncDownloadContent")
    expect(os.path.exists(os.path.join(copy, file_name)), "a Sync run keeps the hidden file")

    make_film("source-a", title)
    rescan_until(a, "Movie", "Hidden Title", True, "the film is back on source-a")
    run_task(local, "ServerSyncUpdateTables")
    expect(not os.path.exists(marker), "the .ignore is removed once the title is back on the source")
    expect((row() or {}).get("Status") == "Synced", "the returned row is Synced again")

    remove_from_source()
    run_task(local, "ServerSyncUpdateTables")
    expect(os.path.exists(marker), "the title is hidden again once it is gone")
    set_action("Keep", "Disabled")
    run_task(local, "ServerSyncUpdateTables")
    expect(not os.path.exists(marker), "switching to Keep shows the title again")
    expect((row() or {}).get("Status") == "Synced", "Keep leaves the row Synced")

    with open(marker, "w", encoding="utf-8") as f:
        f.write("*.nfo\n")
    set_action("Ignore", "Disabled")
    run_task(local, "ServerSyncUpdateTables")
    with open(marker, encoding="utf-8") as f:
        expect(f.read() == "*.nfo\n", "a .ignore the user wrote is left as it is")
    expect((row() or {}).get("Status") == "Synced", "a title with the user's own .ignore is not marked hidden")
    set_action("Keep", "Disabled")
    run_task(local, "ServerSyncUpdateTables")
    expect(os.path.exists(marker), "showing titles never removes the user's .ignore")
    os.remove(marker)

    set_action("Delete", "Enabled")
    run_task(local, "ServerSyncUpdateTables")
    expect((row() or {}).get("Status") == "Deleting", "Delete marks the gone film for deletion")
    set_action("Ignore", "Disabled")
    run_task(local, "ServerSyncUpdateTables")
    expect((row() or {}).get("Status") != "Deleting", "switching to Ignore cancels the waiting deletion")
    run_task(local, "ServerSyncDownloadContent")
    expect(os.path.exists(os.path.join(copy, file_name)), "the file survives a Sync run after the switch")
    run_task(local, "ServerSyncUpdateTables")
    expect(os.path.exists(marker), "the next refresh hides the title instead")

    episode = os.path.join(STATE, "media", "source-b", "Shows", "Test Show (2023)", "Season 01", "Test Show (2023) - S01E02 - Second.mp4")
    parked = os.path.join(STATE, "parked-episode.mp4")
    show = os.path.join(STATE, "media", "local", "Shows", "Test Show (2023)")
    shutil.move(episode, parked)
    try:
        rescan_until(b, "Episode", "S01E02", False, "source-b no longer lists the second episode")
        run_task(local, "ServerSyncUpdateTables")
        expect(not os.path.exists(os.path.join(show, ".ignore")) and not os.path.exists(os.path.join(show, "Season 01", ".ignore")),
               "a single gone episode never hides the show or its season")
        expect(content_rows(local)["Test Show (2023) - S01E02 - Second.mp4"]["Status"] != "Ignored", "the gone episode is not marked hidden")
    finally:
        shutil.move(parked, episode)
        rescan_until(b, "Episode", "S01E02", True, "the second episode is back on source-b")

    set_action("Keep", "Disabled")
    run_task(local, "ServerSyncUpdateTables")
    expect(not os.path.exists(marker), "leaving Ignore shows the title again")
    shutil.rmtree(copy, ignore_errors=True)
    configure_local(creds, local, ["source-a", "source-b"], FullScanIntervalHours=24)
    run_task(local, "ServerSyncUpdateTables")


def rewrite_settings(name, api, edit):
    """Stops a pool server, rewrites its saved Server Sync settings, and starts it again."""
    container = f"serversync-it-{name}"
    path = os.path.join(STATE, f"{name}-settings.xml")
    run(["docker", "stop", container])
    run(["docker", "cp", f"{container}:/config/plugins/configurations/Jellyfin.Plugin.ServerSync.xml", path])
    with open(path, encoding="utf-8") as f:
        text = f.read()
    with open(path, "w", encoding="utf-8") as f:
        f.write(edit(text))
    run(["docker", "cp", path, f"{container}:/config/plugins/configurations/Jellyfin.Plugin.ServerSync.xml"])
    run(["docker", "start", container])
    api.wait_ready(180)
    time.sleep(6)
    os.remove(path)


def scenario_whitelist_upgrade(creds, apis):
    """Updating from 12.2.7.0 keeps its whitelist as it was, and updating from before it adds
    API keys and every administrator once so pulling servers keep working."""
    local = apis["local"]
    unseeded = lambda text: re.sub(r"\s*<PeerCallerWhitelistSeeded>[^<]*</PeerCallerWhitelistSeeded>", "", text)
    no_grants = lambda text: re.sub(r"\s*<PeerCallerGrants\s*/>|\s*<PeerCallerGrants>.*?</PeerCallerGrants>", "", text, flags=re.S)

    config = plugin_config(local)
    config["PeerCallerGrants"] = []
    save_plugin_config(local, config)
    rewrite_settings("local", local, unseeded)
    local.token = local.post("/Users/AuthenticateByName", {"Username": "admin", "Pw": creds["password"]}, expect=200)[1]["AccessToken"]
    config = plugin_config(local)
    expect(config["PeerCallerGrants"] == [], "a 12.2.7.0 install keeps its empty whitelist after updating")
    expect(config["PeerCallerWhitelistSeeded"] is True, "the update is recorded so it runs once")

    rewrite_settings("local", local, lambda text: no_grants(unseeded(text)))
    local.token = local.post("/Users/AuthenticateByName", {"Username": "admin", "Pw": creds["password"]}, expect=200)[1]["AccessToken"]
    grants = plugin_config(local)["PeerCallerGrants"]
    admin = creds["servers"]["local"]["userId"].replace("-", "")
    expect(any(g["UserId"] == "apikey" for g in grants), "an install from before 12.2.7.0 gets an API keys row")
    expect(any(g["UserId"].replace("-", "") == admin for g in grants), "it also gets a row for the administrator")
    expect(all(not g["Locations"] for g in grants), "the added rows allow every location")

    config = plugin_config(local)
    config["PeerCallerGrants"] = open_grants(creds, "local")
    save_plugin_config(local, config)


SCENARIOS = [
    ("content priority across two sources, movies and a show", scenario_content_priority),
    ("reordering re-homes a shared item without deleting", scenario_reorder_rehomes),
    ("history negotiates both ways with the source", scenario_history_negotiation),
    ("metadata, images, and people carry over by type", scenario_metadata_by_type),
    ("people sync carries bios and images", scenario_people_sync),
    ("user sync carries policy, configuration, and avatar", scenario_user_sync),
    ("history hints travel across three servers without loops", scenario_hints_three_servers),
    ("history: a base made stale by a third server does not revert a newer edit", scenario_history_stale_base),
    ("metadata and people hints, newest edit wins, scan keeps newer local", scenario_item_hints),
    ("content hints across the pool, user settings by task", scenario_content_and_user_hints),
    ("peer and operator endpoints require Jellyfin authentication", scenario_peer_endpoint_auth),
    ("check link judges the pairing by direction", scenario_check_link_by_direction),
    ("push responses: paused peers, declined hints, activity entries", scenario_push_responses),
    ("a standard user's key still receives every category", scenario_non_admin_peer_key),
    ("dashboard bulk and status endpoints", scenario_dashboard_endpoints),
    ("playlist changes and deletions travel by hints", scenario_playlist_hints),
    ("the whitelist guards the peer routes", scenario_whitelist),
    ("playlists on pull follow the parent", scenario_playlist_pull),
    ("playlists on push override the receiver", scenario_playlist_push),
    ("playlist sharing and public access are copied", scenario_playlist_sharing),
    ("a standard user's key copies the playlists it can see", scenario_playlist_standard_user),
    ("removed files set to Ignore hide whole titles only", scenario_removed_files_ignore),
    ("updating keeps a 12.2.7.0 whitelist and seeds older ones", scenario_whitelist_upgrade),
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
    POOL_APIS.clear()
    POOL_APIS.update(apis)
    # The scenarios call peer routes from the host and between servers that do not list each other, so the
    # pool starts open. The whitelist scenario locks a server down on its own.
    for name, api in apis.items():
        config = plugin_config(api)
        config["PeerCallerGrants"] = open_grants(creds, name)
        api.post(f"/Plugins/{PLUGIN_GUID}/Configuration", config, expect=204)
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

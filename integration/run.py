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
import urllib.parse
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
STATE = os.path.join(HERE, ".state")
PLUGIN_DIR = os.path.join(HERE, ".plugin")
PUBLISH_DIR = os.path.join(HERE, ".publish")
CREDS = os.path.join(STATE, "credentials.json")
IMAGE = os.environ.get("JELLYFIN_IMAGE", "jellyfin/jellyfin:12.2.20261005-225228")
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
        self.protocol = "1"

    def _headers(self):
        auth = f'MediaBrowser Client="ServerSyncIT", Device="runner", DeviceId="{self.device}", Version="1.0"'
        if self.token:
            auth += f', Token="{self.token}"'
        return {"Authorization": auth, "Content-Type": "application/json", "Accept": "application/json", "X-ServerSync-Version": self.version, "X-ServerSync-Protocol": self.protocol, "X-ServerSync-Min-Protocol": self.protocol}

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
    # An external subtitle, so companion downloads and the API tests have one to fetch.
    subtitle = os.path.join(media_root, "source-a", "Movies", "Only A (2021)", "Only A (2021).en.srt")
    if not os.path.exists(subtitle):
        with open(subtitle, "w", encoding="utf-8") as f:
            f.write("1\n00:00:00,000 --> 00:00:01,000\nOnly A\n")
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


def all_rows(api):
    """Every content sync row, read a page at a time, since the route answers at most 200 per request
    and a reused pool can hold more."""
    rows, skip = [], 0
    while True:
        page = api.get(f"/Sync/Items?skip={skip}&take=200")["Items"]
        rows.extend(page)
        if len(page) < 200:
            return rows
        skip += 200


def content_rows(api):
    return {os.path.basename(r["SourcePath"]): r for r in all_rows(api)}


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
    # Since 12.2.9.0 peers are matched by protocol, not plugin version, so the old peer here speaks a
    # protocol older than this build's oldest.
    other.version = "0.0.0.0"
    other.protocol = "0"
    status, body = other.call("POST", "/Sync/Peer/Queue", {"SenderServerId": a_id, "Items": []})
    expect(status == 426, f"a peer call speaking an older protocol is refused with 426 ({status})")
    status, body = other.call("POST", "/Sync/Peer/Pair", {"ServerId": a_id, "Secret": "x"})
    expect(status == 426, f"a pairing speaking an older protocol is refused with 426 ({status})")
    other.protocol = "1"
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


def make_film(server, title, seconds=2, size="64x64", extension="mp4"):
    folder = os.path.join(STATE, "media", server, "Movies", title)
    os.makedirs(folder, exist_ok=True)
    ffmpeg(folder, "-f", "lavfi", "-i", f"testsrc=duration={seconds}:size={size}:rate=10", "-f", "lavfi", "-i", "anullsrc=r=8000:cl=mono",
           "-t", str(seconds), "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", f"/out/{title}.{extension}")
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


def set_linking(api, peer, allow):
    """Turns Allow Linking on or off on one server's entry for a peer."""
    config = plugin_config(api)
    for entry in config["Servers"]:
        if entry["Name"] == peer:
            entry["AllowLinking"] = allow
    save_plugin_config(api, config)


def set_storage(api, server, library, mode):
    """Sets the Storage of one library mapping on local's entry for a server."""
    config = plugin_config(api)
    for entry in config["Servers"]:
        if entry["Name"] == server:
            for mapping in entry["LibraryMappings"]:
                if mapping["SourceLibraryName"] == library:
                    mapping["StorageMode"] = mode
    save_plugin_config(api, config)


def scenario_links(creds, apis):
    """A Link mapping writes a .strm that streams from the source through both servers, while copies
    already there stay copies, and history matches the link in both directions."""
    local, a = apis["local"], apis["source-a"]
    lu, au = creds["servers"]["local"]["userId"], creds["servers"]["source-a"]["userId"]
    # Jellyfin derives an item's id from its path, and a pool that keeps removed rows would hand a
    # re-created film its earlier copy row, so every run links a film of its own.
    needle = f"Linked Film {uuid.uuid4().hex[:6]}"
    title = f"{needle} (2024)"
    folder = os.path.join(STATE, "media", "local", "Movies", title)
    link = os.path.join(folder, f"{title}.strm")
    shutil.rmtree(folder, ignore_errors=True)

    def forget_film():
        shutil.rmtree(os.path.join(STATE, "media", "source-a", "Movies", title), ignore_errors=True)
        shutil.rmtree(folder, ignore_errors=True)
        rescan_until(a, "Movie", needle, False, "source-a no longer lists the linked film")
        local.post("/Library/Refresh", None, expect=204)
        configure_local(creds, local, ["source-a", "source-b"])
        run_task(local, "ServerSyncUpdateTables")

    source_folder = make_film("source-a", title)
    source_file = os.path.join(source_folder, f"{title}.mp4")
    with open(os.path.join(source_folder, f"{title}.en.srt"), "w", encoding="utf-8") as f:
        f.write("1\n00:00:00,000 --> 00:00:01,000\nHello\n")
    rescan_until(a, "Movie", needle, True, "source-a lists the film to link")
    plot = "A linked film & its <details>, written from the source."
    update_item(a, find_item(a, "Movie", needle), {"Overview": plot, "Genres": ["Drama"], "ProductionYear": 2024})

    def row():
        return content_rows(local).get(f"{title}.mp4")

    try:
        configure_server(creds, a, "source-a", ["local"], {"local": "Pull"})
        configure_local(creds, local, ["source-a"])
        set_storage(local, "source-a", "Movies", "Link")

        run_task(local, "ServerSyncUpdateTables")
        expect(row() is None, "with linking off on the source, no link row is made")
        expect(content_rows(local)["Only A (2021).mp4"]["StorageMode"] == "Copy", "a copy already here stays a copy under a Link mapping")

        set_linking(a, "local", True)
        expect(a.get("/Sync/Peer/Capabilities").get("Features") == ["Link"], "source-a offers Link once linking is allowed")
        run_task(local, "ServerSyncUpdateTables")
        expect((row() or {}).get("StorageMode") == "Link" and (row() or {}).get("Status") == "Queued", "the new film is queued as a link")
        run_task(local, "ServerSyncDownloadContent")
        expect(os.path.exists(link) and not os.path.exists(os.path.join(folder, f"{title}.mp4")), "a .strm is written where the copy would go, and nothing is downloaded")
        expect(os.path.exists(os.path.join(folder, f"{title}.en.srt")), "the subtitle is downloaded beside the link")
        nfo = os.path.join(folder, f"{title}.nfo")
        expect(os.path.exists(nfo), "an .nfo is written beside the link")
        if os.path.exists(nfo):
            import xml.etree.ElementTree as ET
            movie = ET.parse(nfo).getroot()
            expect(movie.tag == "movie" and movie.findtext("plot") == plot and movie.findtext("year") == "2024" and movie.findtext("genre") == "Drama",
                   "the .nfo carries the source's plot, year, and genre")
        with open(link, encoding="utf-8") as f:
            url = f.read().strip()
        expect(url.startswith("http://127.0.0.1:8096/Sync/Link/") and "?k=" in url, "the .strm holds a loopback URL with a secret")
        expect(os.path.exists(os.path.join(STATE, "media", "local", "Movies", "Only A (2021)", "Only A (2021).mp4")), "the copied film is untouched")

        wait_until(lambda: find_item(local, "Movie", needle) is not None, 150, "local lists the linked film")
        item = find_item(local, "Movie", needle)
        wait_until(lambda: (local.get(f"/Items/{item['Id']}?userId={lu}").get("RunTimeTicks") or 0) > 0, 150, "the link gets its media info without being played")
        expect(local.get(f"/Items/{item['Id']}?userId={lu}").get("Overview") == plot, "local reads the link's plot from the .nfo")

        info = local.post(f"/Items/{item['Id']}/PlaybackInfo?userId={lu}", {}, expect=200)[1]
        source = info["MediaSources"][0]
        expect(source.get("Path") is None and source.get("IsRemote") is False and source.get("Protocol") == "File",
               "PlaybackInfo hides the loopback URL so clients play through local")
        status, body = local.call("GET", f"/Videos/{item['Id']}/stream?static=true&mediaSourceId={source['Id']}", raw=True)
        with open(source_file, "rb") as f:
            original = f.read()
        expect(status in (200, 206) and body == original, "the static stream through local is the source's file, byte for byte")
        status, _ = local.call("GET", "/" + url.split("/", 3)[3], raw=True)
        expect(status == 403, "the link URL refuses a caller that is not local itself")

        # Per user: the source's own rules decide, and a refusal shows at PlaybackInfo.
        details = local.get(f"/Users/{lu}/Items/{item['Id']}")
        expect(all("/Sync/Link/" not in (m.get("Path") or "") for m in details.get("MediaSources") or []),
               "an item's details never show the link's URL")
        wrong = run(["docker", "exec", "serversync-it-local", "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}", url.split("?k=")[0] + "?k=wrong"], capture=True, check=False)
        expect(wrong.stdout.strip() == "403", "the link URL refuses local itself when the secret is wrong")
        proxied = run(["docker", "exec", "serversync-it-local", "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}", "-H", "X-Forwarded-For: 203.0.113.9", url], capture=True, check=False)
        expect(proxied.stdout.strip() == "403", "the link URL refuses a request that came through a proxy, even with the right secret")
        for route in ("File", "Download"):
            status, body = local.call("GET", f"/Items/{item['Id']}/{route}", raw=True)
            expect(status == 403 and b"Sync/Link" not in body, f"/Items/{{id}}/{route} never hands out the link or its secret")
        viewer = Api("local-viewer", SERVERS["local"]["port"], "viewer-device")
        _, auth = viewer.post("/Users/AuthenticateByName", {"Username": "viewer", "Pw": ""}, expect=200)
        viewer.token = auth["AccessToken"]
        lv, av = creds["servers"]["local"]["viewerId"], creds["servers"]["source-a"]["viewerId"]
        status, _ = viewer.call("GET", f"/Videos/{item['Id']}/stream?static=true&mediaSourceId={source['Id']}", raw=True)
        expect(status == 403, "a stream from a user who never pressed play is refused, though someone else is playing the link")
        policy = a.get(f"/Users/{av}")["Policy"]
        try:
            a.post(f"/Users/{av}/Policy", dict(policy, EnableMediaPlayback=False), expect=204)
            status, body = viewer.call("POST", f"/Items/{item['Id']}/PlaybackInfo?userId={lv}", {}, raw=True)
            expect(status == 403 and b"may not play media" in body, "a user the source may not let play is refused at PlaybackInfo with the reason")
        finally:
            a.post(f"/Users/{av}/Policy", policy, expect=204)

        # The limit is checked before the viewer streams at all, since a probe at PlaybackInfo already
        # counts as their stream for a while.
        config = plugin_config(a)
        next(e for e in config["Servers"] if e["Name"] == "local")["LinkMaxStreams"] = 1
        save_plugin_config(a, config)
        local.call("GET", f"/Videos/{item['Id']}/stream?static=true&mediaSourceId={source['Id']}", raw=True)
        status, body = viewer.call("POST", f"/Items/{item['Id']}/PlaybackInfo?userId={lv}", {}, raw=True)
        expect(status == 403 and b"all are in use" in body, "a second user is refused while the source's stream limit is reached")
        status, _ = local.post(f"/Items/{item['Id']}/PlaybackInfo?userId={lu}", {})
        expect(status == 200, "the user already streaming may go on, since seeking is not a new stream")
        next(e for e in config["Servers"] if e["Name"] == "local")["LinkMaxStreams"] = 0
        save_plugin_config(a, config)
        status, _ = viewer.call("POST", f"/Items/{item['Id']}/PlaybackInfo?userId={lv}", {}, raw=True)
        expect(status == 200, "once the source allows them, the same user may play")

        # Playback on the link shows on the source as a session and marks its file played there once.
        source_item = find_item(a, "Movie", needle)
        runtime = local.get(f"/Items/{item['Id']}?userId={lv}").get("RunTimeTicks") or 0
        play = {"ItemId": item["Id"], "MediaSourceId": source["Id"], "PlaySessionId": "it-link", "PositionTicks": 0, "CanSeek": True, "PlayMethod": "DirectPlay"}
        viewer.post("/Sessions/Playing", play, expect=204)

        def playing_on_a():
            return [x for x in a.get("/Sessions") if x.get("Client") == "Server Sync"
                    and (x.get("NowPlayingItem") or {}).get("Id", "").replace("-", "") == source_item["Id"].replace("-", "")]
        wait_until(lambda: len(playing_on_a()) == 1, 30, "playing the link shows on source-a as a Server Sync session")
        expect(all(x.get("UserId", "").replace("-", "") == av.replace("-", "") for x in playing_on_a()), "the session is the source's mapped user")
        expect(all(x.get("SupportsMediaControl") for x in playing_on_a()), "source-a's dashboard can control the session")
        a.post(f"/Sessions/{playing_on_a()[0]['Id']}/Playing/Stop", None, expect=204)
        wait_until(lambda: "Passed a remote control command from 'source-a'" in run(["docker", "logs", "--since", "2m", "serversync-it-local"], capture=True, check=False).stdout
                   + run(["docker", "logs", "--since", "2m", "serversync-it-local"], capture=True, check=False).stderr, 30,
                   "pressing Stop on source-a reaches the viewer's session on local")
        viewer.post("/Sessions/Playing/Stopped", dict(play, PositionTicks=runtime), expect=204)
        wait_until(lambda: user_data(a, av, source_item["Id"])["Played"], 30, "stopping at the end marks the source's file played")
        wait_until(lambda: not playing_on_a(), 30, "the session on source-a ends when playback stops")
        expect(user_data(a, av, source_item["Id"]).get("PlayCount") == 1, "the source counts the play once")

        links = local.get("/Sync/Peer/Links")["Links"]
        expect(any(l["LinkPath"].endswith(f"{title}.strm") and l["CopiedPath"].endswith(f"{title}.mp4") for l in links),
               "local lists its link with the path its copy would have")

        configure_local(creds, local, ["source-a"], EnableHistorySync=True, HistorySyncNegotiate=True, EnableContentSync=False)
        local.post(f"/UserPlayedItems/{item['Id']}?userId={lu}&datePlayed=2026-10-01T10:00:00Z", expect=200)
        run_task(local, "ServerSyncRefreshHistoryTable")
        history = [r for r in local.get("/Sync/HistoryItems?take=500")["Items"] if title in (r.get("ItemName") or "")]
        expect(any(r.get("LocalItemId", "").replace("-", "") == item["Id"].replace("-", "") for r in history), "local's history matches the source film to the link")
        run_task(local, "ServerSyncMissingHistory")
        expect(user_data(a, au, source_item["Id"])["Played"], "a play recorded on the link reaches the source's real file")
        expect(user_data(a, av, source_item["Id"]).get("PlayCount") == 1 and user_data(local, lv, item["Id"]).get("PlayCount") == 1,
               "history sync does not double a play the source already counted")

        configure_server(creds, a, "source-a", ["local"], {"local": "Pull"}, EnableHistorySync=True)
        set_linking(a, "local", True)
        run_task(a, "ServerSyncRefreshHistoryTable")
        reverse = [r for r in a.get("/Sync/HistoryItems?take=500")["Items"] if title in (r.get("ItemName") or "")]
        expect(any(r.get("LocalItemId", "").replace("-", "") == source_item["Id"].replace("-", "") for r in reverse),
               "reading local, source-a matches local's link to its own file")
        expect(all(not (r.get("SourcePath") or "").endswith(".strm") for r in reverse), "source-a records the link by the copy's path")

        set_linking(a, "local", False)
        status, _ = local.call("GET", f"/Videos/{item['Id']}/stream?static=true&mediaSourceId={source['Id']}", raw=True)
        expect(status >= 400, "turning linking off on the source stops the stream")

        # Promotion: the link becomes a full copy, downloaded the normal way, and then the .strm goes.
        configure_local(creds, local, ["source-a"])
        _, result = local.post("/Sync/PromoteItems", {"SourceItemIds": [row()["SourceItemId"]]}, expect=200)
        expect(result.get("Promoted") == 1 and row()["StorageMode"] == "Promoting", "the Sync tab marks the link to become a copy")
        copy = os.path.join(folder, f"{title}.mp4")
        run_task(local, "ServerSyncDownloadContent")
        expect(os.path.exists(copy) and not os.path.exists(link), "the copy is in place and the link is gone")
        with open(copy, "rb") as f:
            expect(f.read() == original, "the copy is the source's file")
        run_task(local, "ServerSyncUpdateTables")
        expect(row()["StorageMode"] == "Copy" and row()["Status"] == "Synced", "the row is a synced copy")
        wait_until(lambda: (find_item(local, "Movie", needle) or {}).get("Path", "").endswith(".mp4"), 150, "local lists the film as the copy")
    finally:
        configure_server(creds, a, "source-a", [])
        forget_film()



# --------------------------------------------------------------------------------------------------
# Link edge cases
# --------------------------------------------------------------------------------------------------

def http(api, method, path, headers=None, timeout=60):
    """One raw request with extra headers. Returns the status, the headers, and the body."""
    merged = dict(api._headers())
    merged.update(headers or {})
    req = urllib.request.Request(api.base + path, method=method, headers=merged)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, dict(resp.headers), resp.read()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers or {}), e.read()


def in_local(*args):
    """Runs curl inside the local container, so the request comes from local itself. Returns the status."""
    result = run(["docker", "exec", "serversync-it-local", "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}", *args], capture=True, check=False)
    return result.stdout.strip()


def wait_scans_idle(api, timeout=300):
    """Waits until the server is not scanning its libraries, so a scenario does not start on top of the
    last one's clean up."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if all(t.get("State") != "Running" for t in api.get("/ScheduledTasks") if t.get("Key") in ("RefreshLibrary", "ServerSyncUpdateTables", "ServerSyncDownloadContent")):
            return
        time.sleep(2)


class LinkedFilm:
    """A film on source-a held on local as a link, for one scenario. Leaves the pool as it found it."""

    def __init__(self, creds, apis, needle=None, seconds=2, size="64x64", local_flags=None):
        self.creds, self.apis = creds, apis
        self.a, self.local = apis["source-a"], apis["local"]
        self.lu = creds["servers"]["local"]["userId"]
        self.needle = needle or f"Edge {uuid.uuid4().hex[:6]}"
        self.title = f"{self.needle} (2024)"
        self.folder = os.path.join(STATE, "media", "local", "Movies", self.title)
        self.source_folder = os.path.join(STATE, "media", "source-a", "Movies", self.title)
        self.link = os.path.join(self.folder, f"{self.title}.strm")
        self.seconds, self.size = seconds, size
        self.local_flags = dict(dict(FullScanIntervalHours=0, MissingContentAction="Keep", DeleteMissingContentMode="Disabled"), **(local_flags or {}))

    def setup(self, write=True):
        wait_scans_idle(self.local)
        wait_scans_idle(self.a)
        make_film("source-a", self.title, self.seconds, self.size)
        self.source_file = os.path.join(self.source_folder, f"{self.title}.mp4")
        rescan_until(self.a, "Movie", self.needle, True, f"source-a lists {self.needle}")
        configure_server(self.creds, self.a, "source-a", ["local"], {"local": "Pull"})
        set_linking(self.a, "local", True)
        self.configure()
        if write:
            self.write()
        return self

    def configure(self, **flags):
        configure_local(self.creds, self.local, ["source-a"], **dict(self.local_flags, **flags))
        set_storage(self.local, "source-a", "Movies", "Link")

    def write(self):
        run_task(self.local, "ServerSyncUpdateTables")
        run_task(self.local, "ServerSyncDownloadContent")
        wait_until(lambda: find_item(self.local, "Movie", self.needle) is not None, 150, f"local lists {self.needle}")
        self.item = find_item(self.local, "Movie", self.needle)

    def row(self):
        return content_rows(self.local).get(f"{self.title}.mp4")

    def play_info(self, api=None, user=None):
        api = api or self.local
        status, body = api.call("POST", f"/Items/{self.item['Id']}/PlaybackInfo?userId={user or self.lu}", {}, raw=True)
        self.source = json.loads(body)["MediaSources"][0] if status == 200 else None
        return status, body

    def original(self):
        with open(self.source_file, "rb") as f:
            return f.read()

    def teardown(self):
        configure_server(self.creds, self.a, "source-a", [])
        shutil.rmtree(self.source_folder, ignore_errors=True)
        shutil.rmtree(self.folder, ignore_errors=True)
        rescan_until(self.a, "Movie", self.needle, False, f"source-a no longer lists {self.needle}")
        self.local.post("/Library/Refresh", None, expect=204)
        # Back to the pool's own defaults, so no scenario's settings carry into the next.
        configure_local(self.creds, self.local, ["source-a", "source-b"], FullScanIntervalHours=24, MissingContentAction=None)
        run_task(self.local, "ServerSyncUpdateTables")


def scenario_link_ranges(creds, apis):
    """Seeking reads exactly the bytes asked for, through local's stream route, the proxy, and source-a."""
    film = LinkedFilm(creds, apis, seconds=20, size="320x240").setup()
    try:
        film.play_info()
        data = film.original()
        base = f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={film.source['Id']}"
        for label, header, expected in (
            ("a middle range", "bytes=1000-1999", data[1000:2000]),
            ("an open ended range", f"bytes={len(data) - 500}-", data[-500:]),
            ("a suffix range", "bytes=-300", data[-300:]),
            ("the first byte", "bytes=0-0", data[:1]),
        ):
            status, headers, body = http(film.local, "GET", base, {"Range": header})
            expect(status == 206 and body == expected, f"{label} ({header}) is the source's bytes, as 206 ({status}, {len(body)} bytes)")
        status, headers, body = http(film.local, "GET", base, {"Range": f"bytes={len(data) + 10}-"})
        expect(status == 416, f"a range past the end is refused as 416 ({status})")
        status, headers, body = http(film.local, "GET", base)
        expect(status == 200 and body == data, "the whole file reads back unchanged")
        ranges = [http(film.local, "GET", base, {"Range": f"bytes={i * 4096}-{i * 4096 + 4095}"})[2] for i in range(len(data) // 4096)]
        expect(b"".join(ranges) == data[:len(ranges) * 4096], "reading the file in 4 KB pieces, as a player seeking would, gives the same bytes")
    finally:
        film.teardown()


def scenario_link_hls(creds, apis):
    """Transcoding a link: Jellyfin's ffmpeg reads the link under the user's grant and makes HLS segments."""
    film = LinkedFilm(creds, apis, seconds=12, size="320x240").setup()
    try:
        film.play_info()
        query = (f"MediaSourceId={film.source['Id']}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=150000&AudioBitrate=64000"
                 f"&MaxWidth=160&SegmentContainer=ts&PlaySessionId=it-hls-{uuid.uuid4().hex[:6]}&TranscodingMaxAudioChannels=2")
        status, _, master = http(film.local, "GET", f"/Videos/{film.item['Id']}/master.m3u8?{query}")
        expect(status == 200 and b"#EXTM3U" in master, f"the HLS master playlist for a link is served ({status})")
        variant = next((l for l in master.decode().splitlines() if l and not l.startswith("#")), None)
        status, _, playlist = http(film.local, "GET", f"/Videos/{film.item['Id']}/{variant}") if variant else (0, {}, b"")
        segment = next((l for l in playlist.decode(errors="ignore").splitlines() if l and not l.startswith("#")), None)
        expect(status == 200 and segment is not None, f"the variant playlist lists segments ({status})")
        status, _, ts = http(film.local, "GET", f"/Videos/{film.item['Id']}/{segment}", timeout=120) if segment else (0, {}, b"")
        expect(status == 200 and len(ts) > 188 and ts[0] == 0x47, f"the first segment is MPEG-TS transcoded from the link ({status}, {len(ts)} bytes)")
        film.local.call("DELETE", f"/Videos/ActiveEncodings?deviceId=it-local&playSessionId=it-hls", raw=True)
    finally:
        film.teardown()


def scenario_link_hostile(creds, apis):
    """Requests built to misuse links or their routes are refused, and none of them errors."""
    film = LinkedFilm(creds, apis).setup()
    try:
        film.play_info()
        with open(film.link, encoding="utf-8") as f:
            url = f.read().strip()
        link_id = url.split("/Sync/Link/")[1].split("?")[0]
        secret = url.split("?k=")[1]
        prefix = url.split("/Sync/Link/")[0]
        cases = {
            "a path climbing out of the route": (prefix + "/Sync/Link/..%2F..%2FSystem%2FInfo?k=" + secret, "403"),
            "a link id that is not a row": (prefix + "/Sync/Link/" + "0" * 32 + "?k=" + secret, "403"),
            "a very long link id": (prefix + "/Sync/Link/" + "a" * 3000 + "?k=x", "403"),
            "no secret": (prefix + "/Sync/Link/" + link_id, "403"),
            "the secret of another link": (prefix + "/Sync/Link/" + link_id[::-1] + "?k=" + secret, "403"),
            "an empty secret": (prefix + "/Sync/Link/" + link_id + "?k=", "403"),
        }
        for label, (target, code) in cases.items():
            got = in_local(target)
            expect(got == code, f"{label} is refused with {code} ({got})")
        for header in ("X-Real-IP: 1.2.3.4", "Forwarded: for=1.2.3.4", "X-Forwarded-Host: evil"):
            got = in_local("-H", header, url)
            expect(got == "403", f"a request carrying '{header.split(':')[0]}' is refused ({got})")
        got = in_local("-X", "POST", url)
        expect(got in ("404", "405"), f"POST to a link is not served ({got})")
        got = in_local("-H", "Range: bytes=abc", url)
        expect(got in ("200", "206", "416"), f"a malformed Range is answered, not failed ({got})")
        got = in_local("-I", url)
        expect(got in ("200", "206"), f"HEAD on a link with a live grant answers ({got})")
        status, _, _ = http(film.local, "GET", "/Sync/Link/" + link_id + "?k=" + secret)
        expect(status == 403, f"the right link and secret from outside the server are refused ({status})")
        status, body = film.local.call("POST", "/Sync/Peer/Stream/Check", {"ServerId": "nobody", "ItemId": link_id})
        expect(status == 409, f"a stream check from a server not configured here is refused ({status})")
        status, body = film.local.call("POST", "/Sync/Peer/Playback", None)
        expect(status in (400, 415), f"an empty playback report is malformed ({status})")
        status, body = film.local.call("POST", "/Sync/Peer/Control", {"ServerId": film.creds["servers"]["source-a"]["serverId"], "SessionKey": "x" * 300})
        expect(status in (400, 428), f"a control request with an oversized session is refused ({status})")
        status, _, _ = http(film.local, "GET", f"/Items/{film.item['Id']}/Download")
        expect(status == 403, f"downloading a link is refused ({status})")
        logs = run(["docker", "logs", "--since", "3m", "serversync-it-local"], capture=True, check=False)
        errors = [l for l in (logs.stdout + logs.stderr).splitlines() if "[ERR]" in l and ("Link" in l or "ServerSync" in l)]
        expect(not errors, f"none of those requests logged an error ({errors[:2]})")
    finally:
        film.teardown()


def scenario_link_source_down(creds, apis):
    """With source-a stopped, playing a link is refused at once with a reason, and nothing else breaks."""
    film = LinkedFilm(creds, apis).setup()
    try:
        other = find_item(film.local, "Movie", "Only B")
        run(["docker", "stop", "serversync-it-source-a"])
        started = time.time()
        status, body = film.play_info()
        took = time.time() - started
        expect(status == 403 and b"could not be reached" in body, f"PlaybackInfo on a link names the unreachable source ({status})")
        expect(took < 40, f"the refusal comes within the check's timeout ({took:.0f}s)")
        if other:
            status, _ = film.local.call("POST", f"/Items/{other['Id']}/PlaybackInfo?userId={film.lu}", {}, raw=True)
            expect(status == 200, f"other items still play while a link's source is down ({status})")
        status, _ = film.local.call("GET", f"/Users/{film.lu}/Items/{film.item['Id']}", raw=True)
        expect(status == 200, f"the link's details still load ({status})")
    finally:
        run(["docker", "start", "serversync-it-source-a"])
        film.a.wait_ready(180)
        time.sleep(4)
    try:
        status, _ = film.play_info()
        expect(status == 200, f"once source-a is back, the link plays again ({status})")
    finally:
        film.teardown()


def scenario_link_restarts(creds, apis):
    """A restart of either server keeps links as they are, and they play on."""
    film = LinkedFilm(creds, apis).setup()
    try:
        with open(film.link, encoding="utf-8") as f:
            before = f.read()
        restart_container("local", film.local)
        with open(film.link, encoding="utf-8") as f:
            expect(f.read() == before, "the .strm is unchanged after local restarts, since its key is kept")
        run_task(film.local, "ServerSyncUpdateTables")
        expect(film.row()["Status"] == "Synced", "a refresh after the restart leaves the link settled")
        status, _ = film.play_info()
        status2, _, body = http(film.local, "GET", f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={(film.source or {}).get('Id')}")
        expect(status == 200 and body == film.original(), f"the link plays after local restarts ({status}, {status2})")
        restart_container("source-a", film.a)
        status, _ = film.play_info()
        status2, _, body = http(film.local, "GET", f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={(film.source or {}).get('Id')}")
        expect(status == 200 and body == film.original(), f"the link plays after source-a restarts, pairing again on its own ({status}, {status2})")
    finally:
        film.teardown()


def scenario_link_rename(creds, apis):
    """A rename on the source moves the link to the new name, and it plays from there."""
    film = LinkedFilm(creds, apis).setup()
    renamed_needle = film.needle + " Renamed"
    renamed = f"{renamed_needle} (2024)"
    new_source = os.path.join(STATE, "media", "source-a", "Movies", renamed)
    new_local = os.path.join(STATE, "media", "local", "Movies", renamed)
    try:
        os.makedirs(new_source, exist_ok=True)
        shutil.move(film.source_file, os.path.join(new_source, f"{renamed}.mp4"))
        shutil.rmtree(film.source_folder, ignore_errors=True)
        rescan_until(film.a, "Movie", renamed_needle, True, "source-a lists the renamed film")
        rescan_until(film.a, "Movie", film.needle + " (", False, "source-a no longer lists the old name")
        # Jellyfin ids follow the path, so a rename reaches Server Sync as one film gone and one new.
        run_task(film.local, "ServerSyncUpdateTables")
        run_task(film.local, "ServerSyncDownloadContent")
        moved = os.path.join(new_local, f"{renamed}.strm")
        expect(os.path.exists(moved), "the renamed film gets a link under its new name")
        rows = content_rows(film.local)
        expect(rows.get(f"{renamed}.mp4", {}).get("StorageMode") == "Link", "the new name's row is a link")
        expect(os.path.exists(film.link), "the old name's link is kept, as Keep does for any file gone from the source")
        wait_until(lambda: find_item(film.local, "Movie", renamed_needle) is not None, 150, "local lists the renamed link")
        item = find_item(film.local, "Movie", renamed_needle)
        status, body = film.local.call("POST", f"/Items/{item['Id']}/PlaybackInfo?userId={film.lu}", {}, raw=True)
        source = json.loads(body)["MediaSources"][0] if status == 200 else {}
        status, _, data = http(film.local, "GET", f"/Videos/{item['Id']}/stream?static=true&mediaSourceId={source.get('Id')}")
        with open(os.path.join(new_source, f"{renamed}.mp4"), "rb") as f:
            expect(status == 200 and data == f.read(), f"the renamed link plays ({status})")
    finally:
        shutil.rmtree(new_source, ignore_errors=True)
        shutil.rmtree(new_local, ignore_errors=True)
        film.a.post("/Library/Refresh", None, expect=204)
        film.teardown()


def scenario_link_deleted(creds, apis):
    """A film removed from the source takes its link with it when deletions are on."""
    film = LinkedFilm(creds, apis, local_flags=dict(DeleteMissingContentMode="Enabled", MissingContentAction="Delete")).setup()
    try:
        shutil.rmtree(film.source_folder, ignore_errors=True)
        rescan_until(film.a, "Movie", film.needle, False, "source-a no longer lists the film")
        run_task(film.local, "ServerSyncUpdateTables")
        expect((film.row() or {}).get("Status") == "Deleting", "the link is marked for deletion")
        run_task(film.local, "ServerSyncDownloadContent")
        expect(not os.path.exists(film.link), "the Sync run removes the .strm")
        wait_until(lambda: find_item(film.local, "Movie", film.needle) is None, 150, "local no longer lists the linked film")
    finally:
        film.teardown()


def scenario_link_collision(creds, apis):
    """Two source files that differ only by extension: one becomes the link, the other is refused with a reason."""
    film = LinkedFilm(creds, apis)
    try:
        film.setup(write=False)
        ffmpeg(film.source_folder, "-i", f"/out/{film.title}.mp4", "-c", "copy", f"/out/{film.title}.mkv")
        film.a.post("/Library/Refresh", None, expect=204)
        wait_until(lambda: len([i for i in film.a.get("/Items?IncludeItemTypes=Movie&Recursive=true&Fields=Path")["Items"]
                                if film.needle in (i.get("Path") or "")]) >= 1, 150, "source-a scanned both files")
        time.sleep(5)
        run_task(film.local, "ServerSyncUpdateTables")
        run_task(film.local, "ServerSyncDownloadContent")
        rows = [r for r in all_rows(film.local) if film.needle in r["SourcePath"]]
        linked = [r for r in rows if r["Status"] == "Synced"]
        refused = [r for r in rows if r["Status"] == "Errored"]
        if len(rows) >= 2:
            expect(len(linked) == 1 and len(refused) == len(rows) - 1, f"one file is linked and the rest refused ({[r['Status'] for r in rows]})")
            expect(all("differ only by their extension" in (r.get("ErrorMessage") or "") for r in refused), "the refusal says why")
        else:
            expect(len(rows) == 1 and rows[0]["Status"] == "Synced", "Jellyfin merged the two files into one item, which is linked once")
        expect(os.path.exists(film.link), "one .strm is written")
    finally:
        film.teardown()


def scenario_link_users_strm(creds, apis):
    """A .strm the user put in a linked library is never taken for a link: not rewritten, not refused, not hidden."""
    film = LinkedFilm(creds, apis).setup()
    own_folder = os.path.join(STATE, "media", "local", "Movies", f"Users Own {film.needle} (2024)")
    own = os.path.join(own_folder, f"Users Own {film.needle} (2024).strm")
    try:
        os.makedirs(own_folder, exist_ok=True)
        with open(own, "w", encoding="utf-8") as f:
            f.write("http://example.invalid/stream.mp4\n")
        film.local.post("/Library/Refresh", None, expect=204)
        wait_until(lambda: find_item(film.local, "Movie", f"Users Own {film.needle}") is not None, 150, "local lists the user's own .strm")
        run_task(film.local, "ServerSyncUpdateTables")
        run_task(film.local, "ServerSyncDownloadContent")
        with open(own, encoding="utf-8") as f:
            expect(f.read() == "http://example.invalid/stream.mp4\n", "the user's .strm is untouched")
        item = find_item(film.local, "Movie", f"Users Own {film.needle}")
        status, _, _ = http(film.local, "GET", f"/Items/{item['Id']}/File")
        expect(status == 200, f"the user's .strm still downloads as Jellyfin serves any file ({status})")
        status, body = film.local.call("POST", f"/Items/{item['Id']}/PlaybackInfo?userId={film.lu}", {}, raw=True)
        sources = json.loads(body).get("MediaSources") if status == 200 else []
        expect(status == 200 and sources and sources[0].get("Path") == "http://example.invalid/stream.mp4",
               f"its PlaybackInfo is exactly Jellyfin's, path and all ({status})")
        expect(all(film.needle not in r["SourcePath"] or "Users Own" not in r["SourcePath"] for r in all_rows(film.local)),
               "no sync row tracks the user's .strm")
    finally:
        shutil.rmtree(own_folder, ignore_errors=True)
        film.teardown()


def scenario_link_full_disk(creds, apis):
    """On a disk below the free space floor, a run with only links goes ahead, and a copy waits."""
    a, local = apis["source-a"], apis["local"]
    needle = f"Tiny {uuid.uuid4().hex[:6]}"
    title = f"{needle} (2024)"
    make_film("source-a", title)
    source_folder = os.path.join(STATE, "media", "source-a", "Movies", title)
    options = {"LibraryOptions": {"EnableInternetProviders": False, "EnableRealtimeMonitor": False, "SaveLocalMetadata": False}}

    def exists(path):
        return run(["docker", "exec", "serversync-it-local", "test", "-f", path], check=False).returncode == 0

    def set_aside(rows):
        # This scenario's rows are copies queued on a full disk, which would hold back the next run.
        ids = [r["SourceItemId"] for r in rows if r["Status"] != "Ignored"]
        if ids:
            local.post("/Sync/IgnoreItems", {"SourceItemIds": ids}, expect=200)

    set_aside([r for r in all_rows(local) if "/Tiny " in r["SourcePath"]])
    try:
        rescan_until(a, "Movie", needle, True, f"source-a lists {needle}")
        free = run(["docker", "exec", "serversync-it-local", "df", "-m", "/media-tiny"], capture=True, check=False).stdout
        expect("media-tiny" in free, "local has the small disk")
        if not any(f["Name"] == "Tiny" for f in local.get("/Library/VirtualFolders")):
            local.post("/Library/VirtualFolders?name=Tiny&collectionType=movies&refreshLibrary=false&paths=%2Fmedia-tiny", options, expect=204)
        tiny = next(f for f in local.get("/Library/VirtualFolders") if f["Name"] == "Tiny")["ItemId"]
        configure_server(creds, a, "source-a", ["local"], {"local": "Pull"})
        set_linking(a, "local", True)
        configure_local(creds, local, ["source-a"], FullScanIntervalHours=0, MinimumFreeDiskSpaceGb=1)
        config = plugin_config(local)
        entry = next(e for e in config["Servers"] if e["Name"] == "source-a")
        mapping = next(m for m in entry["LibraryMappings"] if m["SourceLibraryName"] == "Movies")
        mapping.update({"LocalLibraryId": tiny, "LocalLibraryName": "Tiny", "LocalRootPath": "/media-tiny", "StorageMode": "Link"})
        entry["LibraryMappings"] = [mapping]
        save_plugin_config(local, config)
        disk = [d for d in local.get("/Sync/DiskSpace") if d["Path"] == "/media-tiny"]
        expect(disk and not disk[0]["IsSufficient"], f"the small disk reads as below the 1 GB floor ({disk})")

        link = f"/media-tiny/{title}/{title}.strm"
        copy = f"/media-tiny/{title}/{title}.mp4"
        run_task(local, "ServerSyncUpdateTables")
        run_task(local, "ServerSyncDownloadContent")
        expect(exists(link), "a run with only links goes ahead below the floor and writes the link")

        mapping["StorageMode"] = "Copy"
        save_plugin_config(local, config)
        run_task(local, "ServerSyncUpdateTables")
        row = content_rows(local).get(f"{title}.mp4") or {}
        expect(row.get("StorageMode") == "Promoting", f"the mapping now copies, so the link is marked to become a copy ({row.get('StorageMode')})")
        try:
            run_task(local, "ServerSyncDownloadContent")
        except AssertionError:
            pass
        expect(exists(link) and not exists(copy), "the copy waits for disk space, and the link stays and keeps playing")
    finally:
        set_aside([r for r in all_rows(local) if needle in r["SourcePath"]])
        configure_server(creds, a, "source-a", [])
        shutil.rmtree(source_folder, ignore_errors=True)
        rescan_until(a, "Movie", needle, False, f"source-a no longer lists {needle}")
        local.call("DELETE", "/Library/VirtualFolders?name=Tiny&refreshLibrary=false", raw=True)
        run(["docker", "exec", "serversync-it-local", "sh", "-c", "rm -rf /media-tiny/*"], check=False)
        configure_local(creds, local, ["source-a", "source-b"], FullScanIntervalHours=24)
        run_task(local, "ServerSyncUpdateTables")


def scenario_link_parallel_limit(creds, apis):
    """With a limit of one stream, two users starting at the same moment: exactly one streams."""
    film = LinkedFilm(creds, apis, seconds=20, size="320x240").setup()
    viewer = Api("local-viewer-edge", SERVERS["local"]["port"], "viewer-edge")
    _, auth = viewer.post("/Users/AuthenticateByName", {"Username": "viewer", "Pw": ""}, expect=200)
    viewer.token = auth["AccessToken"]
    lv = creds["servers"]["local"]["viewerId"]
    try:
        film.play_info()
        viewer_status, _ = film.play_info(viewer, lv)
        expect(viewer_status == 200, "both users may play before the limit is set")
        config = plugin_config(film.a)
        next(e for e in config["Servers"] if e["Name"] == "local")["LinkMaxStreams"] = 1
        save_plugin_config(film.a, config)
        # The probes at PlaybackInfo count as each user's stream for 90 seconds after their last read, which
        # can end a few seconds after the limit is saved, and a stream still counted is not a new one.
        time.sleep(120)
        import concurrent.futures
        path = f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={film.source['Id']}"
        with concurrent.futures.ThreadPoolExecutor(2) as pool:
            results = list(pool.map(lambda api: http(api, "GET", path)[0], [film.local, viewer]))
        expect(sorted(results)[0] == 200 and sorted(results)[1] >= 400, f"exactly one of two simultaneous users streams ({results})")
    finally:
        config = plugin_config(film.a)
        for e in config["Servers"]:
            e["LinkMaxStreams"] = 0
        save_plugin_config(film.a, config)
        film.teardown()


def scenario_link_fallback(creds, apis):
    """Both sources hold the same film and both allow linking. Local links it once, from source-a, its
    first server. When source-a is down, refuses, or dies between PlaybackInfo and the stream, source-b
    stands in with the same file, gets the playback report, and may control the session. A different file
    at the same path on source-b is never used, and when nobody allows it the refusal names both."""
    film = LinkedFilm(creds, apis, seconds=6, size="160x120")
    b = apis["source-b"]
    b_folder = os.path.join(STATE, "media", "source-b", "Movies", film.title)
    since = now_stamp()
    try:
        film.setup(write=False)
        os.makedirs(b_folder, exist_ok=True)
        shutil.copyfile(film.source_file, os.path.join(b_folder, f"{film.title}.mp4"))
        rescan_until(b, "Movie", film.needle, True, f"source-b lists {film.needle}")
        configure_server(creds, b, "source-b", ["local"], {"local": "Pull"})
        set_linking(b, "local", True)
        configure_local(creds, film.local, ["source-a", "source-b"], **film.local_flags)
        set_storage(film.local, "source-a", "Movies", "Link")
        set_storage(film.local, "source-b", "Movies", "Link")
        film.write()
        rows = [r for r in all_rows(film.local) if film.needle in r["SourcePath"]]
        expect(len(rows) == 1 and rows[0]["ServerName"] == "source-a", f"one row, from source-a, the first server ({[(r['ServerName'], r['Status']) for r in rows]})")
        expect(os.path.exists(film.link) and not os.path.exists(os.path.join(film.folder, f"{film.title}.mp4")), "local holds one link and no copy")

        original = film.original()
        status, _ = film.play_info()
        expect(status == 200, f"the link plays from source-a ({status})")
        path = f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={film.source['Id']}"

        # source-a dies between PlaybackInfo and the stream: the link route itself falls over to source-b.
        run(["docker", "stop", "serversync-it-source-a"])
        status, _, body = http(film.local, "GET", path, timeout=120)
        expect(status == 200 and body == original, f"a stream begun on source-a finishes from source-b, byte for byte ({status}, {len(body)} of {len(original)})")

        # source-a down before play: PlaybackInfo picks source-b straight away.
        started = time.time()
        status, body = film.play_info()
        expect(status == 200, f"PlaybackInfo falls back to source-b while source-a is down ({status} {body[:200]!r})")
        expect(time.time() - started < 40, "the fallback answers within the check's timeout")
        status, _, body = http(film.local, "GET", path, timeout=120)
        expect(status == 200 and body == original, f"the fallback streams the same bytes ({status})")
        logs = run(["docker", "logs", "--since", since, "serversync-it-local"], capture=True, check=False)
        expect(f"from 'source-b' instead" in logs.stdout + logs.stderr, "local logs that source-b stood in")

        # The playback report and remote control follow the server actually streaming.
        source_item = find_item(b, "Movie", film.needle)
        play = {"ItemId": film.item["Id"], "MediaSourceId": film.source["Id"], "PlaySessionId": "it-fallback", "PositionTicks": 0, "CanSeek": True, "PlayMethod": "DirectPlay"}
        film.local.post("/Sessions/Playing", play, expect=204)

        def playing_on_b():
            return [x for x in b.get("/Sessions") if x.get("Client") == "Server Sync"
                    and (x.get("NowPlayingItem") or {}).get("Id", "").replace("-", "") == source_item["Id"].replace("-", "")]
        wait_until(lambda: len(playing_on_b()) == 1, 30, "the fallback's playback shows on source-b as a Server Sync session")
        b.post(f"/Sessions/{playing_on_b()[0]['Id']}/Playing/Pause", None, expect=204)
        wait_until(lambda: "Passed a remote control command from 'source-b'" in "".join(
            (lambda r: r.stdout + r.stderr)(run(["docker", "logs", "--since", since, "serversync-it-local"], capture=True, check=False))), 30,
            "source-b may control the viewer it streams to")
        film.local.post("/Sessions/Playing/Stopped", dict(play, PositionTicks=10_000_000), expect=204)
        wait_until(lambda: not playing_on_b(), 30, "the session on source-b ends when playback stops")
    finally:
        run(["docker", "start", "serversync-it-source-a"])
        film.a.wait_ready(180)
        time.sleep(4)
    try:
        # source-a is back but refuses: source-b stands in.
        set_linking(film.a, "local", False)
        status, body = film.play_info()
        expect(status == 200, f"a refusing source-a falls back to source-b ({status} {body[:200]!r})")
        set_linking(film.a, "local", True)
        status, _ = film.play_info()
        expect(status == 200, "with source-a allowing again, the link plays")

        # Nobody allows it: the refusal is source-a's own reason, naming source-b as tried.
        set_linking(film.a, "local", False)
        set_linking(b, "local", False)
        status, body = film.play_info()
        expect(status == 403 and b"linking is not allowed" in body and b"also tried source-b" in body, f"when no server allows it, the refusal says why and what was tried ({status} {body[:300]!r})")
        set_linking(b, "local", True)

        # A different file at the same path on source-b is not the same file, so it is never used.
        os.remove(os.path.join(b_folder, f"{film.title}.mp4"))
        ffmpeg(b_folder, "-f", "lavfi", "-i", "testsrc=duration=3:size=64x64:rate=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", f"/out/{film.title}.mp4")
        b.post("/Library/Refresh", None, expect=204)
        time.sleep(10)
        status, body = film.play_info()
        expect(status == 403 and b"also tried source-b" in body, f"a different file on source-b is not played in its place ({status} {body[:300]!r})")
        set_linking(film.a, "local", True)
        status, _ = film.play_info()
        expect(status == 200, "the link plays from source-a again")
        # Jellyfin probes a link in the background after a play, so a probe still running when both
        # sources stopped allowing it fails, as it should.
        expect(not plugin_errors("serversync-it-local", since, allowed=("Error in Probe Provider",)), "local logged no unexpected errors")
    finally:
        set_linking(film.a, "local", True)
        configure_server(creds, b, "source-b", [])
        shutil.rmtree(b_folder, ignore_errors=True)
        rescan_until(b, "Movie", film.needle, False, f"source-b no longer lists {film.needle}")
        film.teardown()


def scenario_link_universal_audio(creds, apis):
    """An audio link plays through universal audio without the client ever being sent to the link's URL."""
    a, local = apis["source-a"], apis["local"]
    lu = creds["servers"]["local"]["userId"]
    track = f"Tone {uuid.uuid4().hex[:6]}"
    source_dir = os.path.join(STATE, "media", "source-a", "Music", "Test Artist", "Test Album")
    local_dir = os.path.join(STATE, "media", "local", "Music")
    os.makedirs(source_dir, exist_ok=True)
    os.makedirs(local_dir, exist_ok=True)
    ffmpeg(source_dir, "-f", "lavfi", "-i", "sine=frequency=440:duration=8", "-c:a", "libmp3lame", "-b:a", "128k",
           "-metadata", f"title={track}", "-metadata", "artist=Test Artist", "-metadata", "album=Test Album", f"/out/01 - {track}.mp3")
    options = {"LibraryOptions": {"EnableInternetProviders": False, "EnableRealtimeMonitor": False, "SaveLocalMetadata": False}}

    def music_library(api):
        folder = next((f for f in api.get("/Library/VirtualFolders") if f["Name"] == "Music"), None)
        if folder is None:
            api.post("/Library/VirtualFolders?name=Music&collectionType=music&refreshLibrary=true&paths=%2Fmedia%2FMusic", options, expect=204)
            folder = next(f for f in api.get("/Library/VirtualFolders") if f["Name"] == "Music")
        return folder["ItemId"]

    def find_track(api):
        items = api.get("/Items?IncludeItemTypes=Audio&Recursive=true&Fields=Path")["Items"]
        return next((i for i in items if track in (i.get("Name") or "") or track in (i.get("Path") or "")), None)

    try:
        a_music, local_music = music_library(a), music_library(local)
        a.post("/Library/Refresh", None, expect=204)
        wait_until(lambda: find_track(a) is not None, 150, "source-a lists the track")
        configure_server(creds, a, "source-a", ["local"], {"local": "Pull"})
        set_linking(a, "local", True)
        configure_local(creds, local, ["source-a"], FullScanIntervalHours=0)
        config = plugin_config(local)
        entry = next(e for e in config["Servers"] if e["Name"] == "source-a")
        entry["LibraryMappings"].append({
            "SourceLibraryId": a_music, "SourceLibraryName": "Music", "SourceRootPath": "/media/Music",
            "LocalLibraryId": local_music, "LocalLibraryName": "Music", "LocalRootPath": "/media/Music",
            "IsEnabled": True, "FilterMode": "AllowAll", "FilteredItems": [], "StorageMode": "Link"})
        save_plugin_config(local, config)
        run_task(local, "ServerSyncUpdateTables")
        run_task(local, "ServerSyncDownloadContent")
        # Jellyfin plays only video from a .strm, so a track in a Link mapping is copied instead.
        link = os.path.join(local_dir, "Test Artist", "Test Album", f"01 - {track}.strm")
        copy = os.path.join(local_dir, "Test Artist", "Test Album", f"01 - {track}.mp3")
        expect(not os.path.exists(link) and os.path.exists(copy), "a track in a Link mapping is copied, never linked")
        rows = [r for r in all_rows(local) if track in r["SourcePath"]]
        expect(rows and rows[0]["StorageMode"] == "Copy", f"its row is a copy ({[r['StorageMode'] for r in rows]})")
        local.post("/Library/Refresh", None, expect=204)
        wait_until(lambda: find_track(local) is not None, 150, "local lists the copied track")
        item = find_track(local)
        with open(os.path.join(source_dir, f"01 - {track}.mp3"), "rb") as f:
            original = f.read()
        for label, extra in (("with redirection left out", ""), ("with redirection asked for", "&EnableRedirection=true")):
            status, headers, body = http(local, "GET", f"/Audio/{item['Id']}/universal?UserId={lu}&Container=mp3&EnableRemoteMedia=true{extra}")
            location = headers.get("Location") or headers.get("location") or ""
            expect(status in (200, 206) and body == original and "Sync/Link" not in location,
                   f"universal audio {label} plays the track itself ({status}, {len(body)} of {len(original)} bytes)")
    finally:
        configure_server(creds, a, "source-a", [])
        shutil.rmtree(os.path.join(STATE, "media", "source-a", "Music"), ignore_errors=True)
        shutil.rmtree(local_dir, ignore_errors=True)
        for api in (a, local):
            # Removing a library while Jellyfin scans it trips Jellyfin's own database constraints.
            wait_until(lambda api=api: all(t.get("State") != "Running" for t in api.get("/ScheduledTasks") if t.get("Key") == "RefreshLibrary"), 120, f"{api.name} has finished scanning")
            api.call("DELETE", "/Library/VirtualFolders?name=Music&refreshLibrary=false", raw=True)
        configure_local(creds, local, ["source-a", "source-b"], FullScanIntervalHours=24)
        run_task(local, "ServerSyncUpdateTables")


# --------------------------------------------------------------------------------------------------
# Link chaos: trying to break it
# --------------------------------------------------------------------------------------------------

def now_stamp():
    """A point in time Docker's --since understands, taken in UTC."""
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def plugin_errors(container, since, allowed=()):
    """Errors a server logged since a point in time, leaving out the browser's expired session and any the
    scenario causes on purpose."""
    logs = run(["docker", "logs", "--since", since, container], capture=True, check=False)
    noise = ("Insufficient disk space", "pre-flight aborted", "Invalid token", "Token is required", "whitelist entry", "paused until", *allowed)
    return [l for l in (logs.stdout + logs.stderr).splitlines()
            if ("[ERR]" in l or "[FTL]" in l) and not any(n in l for n in noise)]


def healthy(api):
    status, info = api.call("GET", "/System/Info/Public")
    return status == 200 and bool(info)


def scenario_chaos_kill_source_midstream(creds, apis):
    """Killing source-a while local is part way through proxying a link leaves local healthy and quiet."""
    since = now_stamp()
    film = LinkedFilm(creds, apis, seconds=90, size="1280x720").setup()
    try:
        film.play_info()
        merged = dict(film.local._headers())
        req = urllib.request.Request(film.local.base + f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={film.source['Id']}", headers=merged)
        resp = urllib.request.urlopen(req, timeout=60)
        first = resp.read(64 * 1024)
        run(["docker", "kill", "serversync-it-source-a"])
        rest = b""
        try:
            while True:
                chunk = resp.read(256 * 1024)
                if not chunk:
                    break
                rest += chunk
        except Exception:
            pass
        total = len(first) + len(rest)
        expect(total <= os.path.getsize(film.source_file), f"the stream ended when the source died ({total} bytes read)")
        expect(healthy(film.local), "local keeps answering after its source died mid-stream")
        status, body = film.play_info()
        expect(status == 403, f"the next play is refused with a reason rather than hanging ({status})")
        expect(not plugin_errors("serversync-it-local", since), f"local logged no unhandled error ({plugin_errors('serversync-it-local', since)[:2]})")
    finally:
        run(["docker", "start", "serversync-it-source-a"])
        film.a.wait_ready(180)
        time.sleep(4)
        film.teardown()


def scenario_chaos_tampered_links(creds, apis):
    """A .strm deleted, emptied, filled with garbage, or pointed elsewhere: deleted ones come back, and nothing that is no longer Server Sync's own is overwritten."""
    since = now_stamp()
    film = LinkedFilm(creds, apis).setup()
    try:
        with open(film.link, encoding="utf-8") as f:
            good = f.read()

        os.remove(film.link)
        run_task(film.local, "ServerSyncUpdateTables")
        run_task(film.local, "ServerSyncDownloadContent")
        expect(os.path.exists(film.link) and open(film.link, encoding="utf-8").read() == good, "a deleted link is written again on the next sync")

        stale = good.replace("?k=", "?k=stale")
        with open(film.link, "w", encoding="utf-8") as f:
            f.write(stale)
        run_task(film.local, "ServerSyncUpdateTables")
        run_task(film.local, "ServerSyncDownloadContent")
        expect(open(film.link, encoding="utf-8").read() == good, "a link with a stale URL to this route is repaired")

        for label, content in (("garbage", "\x00\x01not a url at all"), ("an outside address", "http://example.invalid/movie.mp4\n"), ("nothing", "")):
            with open(film.link, "w", encoding="utf-8") as f:
                f.write(content)
            run_task(film.local, "ServerSyncUpdateTables")
            run_task(film.local, "ServerSyncDownloadContent")
            row = film.row() or {}
            expect(open(film.link, encoding="utf-8").read() == content, f"a .strm holding {label} is left as it is, since Server Sync no longer owns it")
            expect(row.get("Status") == "Errored" and "did not write" in (row.get("ErrorMessage") or ""), f"its row says why ({row.get('Status')})")
            film.local.post("/Sync/UpdateItemStatus", {"SourceItemId": row.get("SourceItemId"), "Status": "Synced"}, expect=200)
        unexpected = plugin_errors("serversync-it-local", since, allowed=("apply failed for record",))
        expect(healthy(film.local) and not unexpected, f"local stayed healthy and logged nothing beyond the refusals it was meant to ({unexpected[:2]})")
    finally:
        film.teardown()


def scenario_chaos_key_lost(creds, apis):
    """Losing the link key invalidates every link's secret: they refuse until the next sync writes them again, and then they play."""
    film = LinkedFilm(creds, apis).setup()
    try:
        with open(film.link, encoding="utf-8") as f:
            before = f.read()
        run(["docker", "exec", "serversync-it-local", "rm", "-f", "/config/data/serversync-link.key"])
        restart_container("local", film.local)
        status, _ = film.play_info()
        streamed, _, _ = http(film.local, "GET", f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={(film.source or {}).get('Id')}") if film.source else (0, {}, b"")
        expect(streamed != 200 or status != 200, f"with the key gone, the old link no longer streams ({status}, {streamed})")
        run_task(film.local, "ServerSyncUpdateTables")
        expect(film.row()["Status"] == "Queued", "the refresh sees the link is out of date and queues it")
        run_task(film.local, "ServerSyncDownloadContent")
        with open(film.link, encoding="utf-8") as f:
            after = f.read()
        expect(after != before and after.split("?k=")[0] == before.split("?k=")[0], "the link is written again with a new secret")
        status, _ = film.play_info()
        _, _, body = http(film.local, "GET", f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={film.source['Id']}")
        expect(status == 200 and body == film.original(), f"the rewritten link plays ({status})")
        key = run(["docker", "exec", "serversync-it-local", "stat", "-c", "%a %s", "/config/data/serversync-link.key"], capture=True, check=False).stdout.split()
        expect(key and key[0] == "600" and key[1] == "32", f"the new key is 32 bytes, readable by the server's account only ({key})")
    finally:
        film.teardown()


def scenario_chaos_linking_off_midstream(creds, apis):
    """Turning linking off on the source while a link plays stops new reads at once, with the reason, and leaves local healthy."""
    since = now_stamp()
    film = LinkedFilm(creds, apis, seconds=60, size="640x360").setup()
    try:
        film.play_info()
        path = f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={film.source['Id']}"
        status, _, _ = http(film.local, "GET", path, {"Range": "bytes=0-1023"})
        expect(status == 206, f"a range reads while linking is on ({status})")
        set_linking(film.a, "local", False)
        status, _, _ = http(film.local, "GET", path, {"Range": "bytes=1024-2047"})
        expect(status >= 400, f"the next range read is refused once linking is off ({status})")
        status, body = film.play_info()
        expect(status == 403 and b"not allowed" in body, f"pressing play again says linking is not allowed ({status})")
        set_linking(film.a, "local", True)
        status, _ = film.play_info()
        expect(status == 200, f"turning it back on lets it play again ({status})")
        expect(healthy(film.local) and not plugin_errors("serversync-it-local", since), "local logged no unhandled error")
    finally:
        film.teardown()


def scenario_chaos_storm(creds, apis):
    """Fifty PlaybackInfo calls and two hundred range reads at once on one link: every answer right, nothing dropped, nothing logged."""
    since = now_stamp()
    import concurrent.futures
    film = LinkedFilm(creds, apis, seconds=30, size="640x360").setup()
    try:
        film.play_info()
        data = film.original()
        started = time.time()
        with concurrent.futures.ThreadPoolExecutor(25) as pool:
            infos = list(pool.map(lambda _: film.local.call("POST", f"/Items/{film.item['Id']}/PlaybackInfo?userId={film.lu}", {}, raw=True)[0], range(50)))
        expect(infos.count(200) == 50, f"fifty PlaybackInfo calls at once all answer ({sorted(set(infos))}, {time.time() - started:.1f}s)")
        path = f"/Videos/{film.item['Id']}/stream?static=true&mediaSourceId={film.source['Id']}"
        random_ranges = []
        rng = __import__("random").Random(7)
        for _ in range(200):
            start = rng.randrange(0, len(data) - 1)
            end = min(len(data) - 1, start + rng.randrange(1, 65536))
            random_ranges.append((start, end))
        started = time.time()
        with concurrent.futures.ThreadPoolExecutor(32) as pool:
            results = list(pool.map(lambda r: http(film.local, "GET", path, {"Range": f"bytes={r[0]}-{r[1]}"}), random_ranges))
        wrong = [r for r, (status, _, body) in zip(random_ranges, results) if status != 206 or body != data[r[0]:r[1] + 1]]
        expect(not wrong, f"two hundred random ranges at once are all the source's bytes ({len(wrong)} wrong, {time.time() - started:.1f}s)")
        expect(healthy(film.local) and not plugin_errors("serversync-it-local", since), "local logged no unhandled error under the storm")
    finally:
        film.teardown()


def scenario_chaos_fuzz_routes(creds, apis):
    """Hundreds of malformed requests at every link and peer route, on both servers: none fails with a server error, and both stay up."""
    since = now_stamp()
    import random as _random
    rng = _random.Random(99)
    targets = ["/Sync/Peer/Stream/Check", "/Sync/Peer/Playback", "/Sync/Peer/Control", "/Sync/PromoteItems"]
    gets = ["/Sync/Peer/Links", "/Sync/Peer/Stream/{g}", "/Sync/Peer/Stream/not-a-guid", "/Sync/Link/{g}", "/Sync/Link/{g}?k={s}", "/Sync/Peer/Stream/{g}?probe=maybe&userId={s}"]
    values = [None, 0, -1, 2**63, 1.5, True, "", " ", "x" * 70000, "\u0000‮", "../../etc/passwd", [], {}, [1, "a"], {"a": {"b": {"c": []}}}, "00000000-0000-0000-0000-000000000000"]
    fields = ["ServerId", "ItemId", "UserId", "SourceUserId", "SessionKey", "Event", "PositionTicks", "Command", "SeekPositionTicks", "Text", "Header", "TimeoutMs", "SourceItemIds", "PlayMethod", "IsPaused"]
    statuses = {}
    for name in ("local", "source-a"):
        api = apis[name]
        for i in range(150):
            body = {f: rng.choice(values) for f in rng.sample(fields, rng.randrange(0, len(fields)))}
            raw = rng.random() < 0.15
            target = rng.choice(targets)
            if raw:
                req = urllib.request.Request(api.base + target, data=rng.choice([b"{", b"null", b"[]", b"\xff\xfe", b"{\"a\":" * 500]), method="POST", headers=api._headers())
                try:
                    with urllib.request.urlopen(req, timeout=30) as resp:
                        status = resp.status
                except urllib.error.HTTPError as e:
                    status = e.code
            else:
                status, _ = api.call("POST", target, body, raw=True)
            statuses[status] = statuses.get(status, 0) + 1
        for i in range(60):
            path = rng.choice(gets).format(g=uuid.uuid4().hex, s=rng.choice(["", "x", "%00", "a" * 5000]))
            status, _, _ = http(api, rng.choice(["GET", "HEAD"]), path)
            statuses[status] = statuses.get(status, 0) + 1
    expect(all(code < 500 for code in statuses), f"no request got a server error ({statuses})")
    expect(healthy(apis["local"]) and healthy(apis["source-a"]), "both servers still answer")
    expect(not plugin_errors("serversync-it-local", since) and not plugin_errors("serversync-it-source-a", since),
           f"nothing was logged as an unhandled error ({(plugin_errors('serversync-it-local', since) + plugin_errors('serversync-it-source-a', since))[:3]})")


def scenario_chaos_scale(creds, apis):
    """Three hundred films linked at once: all written, all probed through the proxy, normal playback no
    slower, and local's memory steady."""
    since = now_stamp()
    a, local = apis["source-a"], apis["local"]
    lu = creds["servers"]["local"]["userId"]
    tag = f"Bulk {uuid.uuid4().hex[:4]}"
    count = 300
    template = make_film("source-a", f"{tag} 000 (2024)")
    template_file = os.path.join(template, f"{tag} 000 (2024).mp4")
    titles = [f"{tag} {i:03d} (2024)" for i in range(count)]
    for title in titles[1:]:
        folder = os.path.join(STATE, "media", "source-a", "Movies", title)
        os.makedirs(folder, exist_ok=True)
        shutil.copyfile(template_file, os.path.join(folder, f"{title}.mp4"))

    # Counted by path: Jellyfin's search does not promise every match, so it cannot count them.
    def bulk_items(api):
        items = api.get("/Items?IncludeItemTypes=Movie&Recursive=true&Fields=Path")["Items"]
        return [i for i in items if f"/{tag} " in (i.get("Path") or "")]

    def listed(api):
        return len(bulk_items(api))

    def memory():
        out = run(["docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", "serversync-it-local"], capture=True, check=False).stdout.split("/")[0].strip()
        number = float(re.sub(r"[^0-9.]", "", out) or 0)
        return number * (1024 if "GiB" in out else 1)

    def normal_play_ms(samples=20):
        # A film held as a full copy, so this measures ordinary playback rather than a link.
        other = next(i for i in local.get("/Items?IncludeItemTypes=Movie,Episode&Recursive=true&Fields=Path")["Items"]
                     if not (i.get("Path") or "").endswith(".strm"))
        started = time.time()
        for _ in range(samples):
            local.call("POST", f"/Items/{other['Id']}/PlaybackInfo?userId={lu}", {}, raw=True)
        return (time.time() - started) * 1000 / samples

    try:
        a.post("/Library/Refresh", None, expect=204)
        wait_until(lambda: listed(a) >= count, 600, f"source-a lists all {count} films")
        baseline_ms = normal_play_ms()
        memory_before = memory()
        configure_server(creds, a, "source-a", ["local"], {"local": "Pull"})
        set_linking(a, "local", True)
        configure_local(creds, local, ["source-a"], FullScanIntervalHours=0, MissingContentAction="Keep", DeleteMissingContentMode="Disabled")
        set_storage(local, "source-a", "Movies", "Link")
        started = time.time()
        run_task(local, "ServerSyncUpdateTables", timeout=900)
        refreshed = time.time() - started
        started = time.time()
        run_task(local, "ServerSyncDownloadContent", timeout=900)
        synced = time.time() - started
        written = sum(os.path.exists(os.path.join(STATE, "media", "local", "Movies", t, f"{t}.strm")) for t in titles)
        expect(written == count, f"all {count} links are written ({written}; refresh {refreshed:.0f}s, sync {synced:.0f}s)")
        expect(synced < 120, f"writing {count} links takes under two minutes ({synced:.0f}s)")
        wait_until(lambda: listed(local) >= count, 600, f"local lists all {count} links")

        def probed():
            return sum(1 for i in bulk_items(local) if (i.get("RunTimeTicks") or 0) > 0)
        # The label is built before the wait, so the count is reported after it.
        try:
            wait_until(lambda: probed() >= count, 900, "every link gets its media info through the proxy")
        finally:
            log(f"        {probed()} of {count} links have their media info")
        with_links_ms = normal_play_ms()
        expect(with_links_ms < max(baseline_ms * 3, 250), f"PlaybackInfo on a normal film is not slowed by {count} links ({baseline_ms:.0f} ms before, {with_links_ms:.0f} ms after)")
        memory_after = memory()
        expect(memory_after - memory_before < 400, f"local's memory grows by less than 400 MiB ({memory_before:.0f} -> {memory_after:.0f} MiB)")
        unexpected = plugin_errors("serversync-it-local", since)
        expect(not unexpected, f"nothing was logged as an unhandled error ({unexpected[:2]})")
    finally:
        configure_server(creds, a, "source-a", [])
        for title in titles:
            shutil.rmtree(os.path.join(STATE, "media", "source-a", "Movies", title), ignore_errors=True)
            shutil.rmtree(os.path.join(STATE, "media", "local", "Movies", title), ignore_errors=True)
        a.post("/Library/Refresh", None, expect=204)
        local.post("/Library/Refresh", None, expect=204)
        wait_until(lambda: listed(a) == 0, 600, "source-a no longer lists the bulk films")
        configure_local(creds, local, ["source-a", "source-b"], FullScanIntervalHours=24, MissingContentAction=None)
        run_task(local, "ServerSyncUpdateTables", timeout=900)


def scenario_link_embedded_subtitles(creds, apis):
    """An MKV with subtitles inside it, held as a link: the tracks are listed, text extracts through the
    link for a user who pressed play and not for one who did not, and a subtitle burns into a transcode."""
    a, local = apis["source-a"], apis["local"]
    lu = creds["servers"]["local"]["userId"]
    needle = f"Subbed {uuid.uuid4().hex[:6]}"
    title = f"{needle} (2024)"
    folder = make_film("source-a", title, seconds=12, size="320x240")
    with open(os.path.join(folder, "track.srt"), "w", encoding="utf-8") as f:
        f.write("1\n00:00:00,500 --> 00:00:10,000\nHello from inside the link\n")
    with open(os.path.join(folder, "track.ass"), "w", encoding="utf-8") as f:
        f.write("[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
                "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                "Dialogue: 0,0:00:00.50,0:00:10.00,Default,,0,0,0,,Styled hello\n")
    ffmpeg(folder, "-i", f"/out/{title}.mp4", "-i", "/out/track.srt", "-i", "/out/track.ass", "-map", "0", "-map", "1", "-map", "2",
           "-c:v", "copy", "-c:a", "copy", "-c:s:0", "srt", "-c:s:1", "ass", "-metadata:s:s:0", "language=eng", "-metadata:s:s:1", "language=fre", f"/out/{title}.mkv")
    for leftover in (f"{title}.mp4", "track.srt", "track.ass"):
        os.remove(os.path.join(folder, leftover))
    local_folder = os.path.join(STATE, "media", "local", "Movies", title)
    wait_scans_idle(local)
    try:
        rescan_until(a, "Movie", needle, True, "source-a lists the MKV")
        configure_server(creds, a, "source-a", ["local"], {"local": "Pull"})
        set_linking(a, "local", True)
        configure_local(creds, local, ["source-a"], FullScanIntervalHours=0, MissingContentAction="Keep", DeleteMissingContentMode="Disabled", IncludeCompanionFiles=True)
        set_storage(local, "source-a", "Movies", "Link")
        run_task(local, "ServerSyncUpdateTables")
        run_task(local, "ServerSyncDownloadContent")
        expect(os.path.exists(os.path.join(local_folder, f"{title}.strm")), "the MKV is held as a link")
        wait_until(lambda: find_item(local, "Movie", needle) is not None, 150, "local lists the linked MKV")
        item = find_item(local, "Movie", needle)
        wait_until(lambda: (local.get(f"/Items/{item['Id']}?userId={lu}").get("RunTimeTicks") or 0) > 0, 150, "the link is probed")
        status, body = local.call("POST", f"/Items/{item['Id']}/PlaybackInfo?userId={lu}", {}, raw=True)
        source = json.loads(body)["MediaSources"][0]
        subs = [m for m in source.get("MediaStreams") or [] if m.get("Type") == "Subtitle"]
        expect(len(subs) == 2 and all(not m.get("IsExternal") for m in subs), f"both tracks inside the MKV are listed from the link ({[(m.get('Codec'), m.get('Language')) for m in subs]})")
        srt = next((m for m in subs if m.get("Language") == "eng"), subs[0] if subs else {})
        ass = next((m for m in subs if m.get("Language") == "fre"), subs[-1] if subs else {})
        status, _, text = http(local, "GET", f"/Videos/{item['Id']}/{source['Id']}/Subtitles/{srt.get('Index')}/Stream.vtt", timeout=120)
        expect(status == 200 and b"Hello from inside the link" in text, f"the text track extracts through the link ({status})")
        status, _, styled = http(local, "GET", f"/Videos/{item['Id']}/{source['Id']}/Subtitles/{ass.get('Index')}/Stream.ass", timeout=120)
        expect(status == 200 and b"Styled hello" in styled, f"the styled track extracts as it is ({status})")

        viewer = Api("local-viewer-subs", SERVERS["local"]["port"], "viewer-subs")
        _, auth = viewer.post("/Users/AuthenticateByName", {"Username": "viewer", "Pw": ""}, expect=200)
        viewer.token = auth["AccessToken"]
        status, _, _ = http(viewer, "GET", f"/Videos/{item['Id']}/{source['Id']}/Subtitles/{srt.get('Index')}/0/Stream.srt")
        expect(status == 403, f"a user who never pressed play cannot have the link read for subtitles ({status})")

        query = (f"MediaSourceId={source['Id']}&VideoCodec=h264&AudioCodec=aac&VideoBitrate=200000&MaxWidth=320&SegmentContainer=ts"
                 f"&SubtitleStreamIndex={srt.get('Index')}&SubtitleMethod=Encode&PlaySessionId=it-subs-{uuid.uuid4().hex[:6]}")
        status, _, master = http(local, "GET", f"/Videos/{item['Id']}/master.m3u8?{query}")
        variant = next((l for l in master.decode(errors="ignore").splitlines() if l and not l.startswith("#")), None)
        status, _, playlist = http(local, "GET", f"/Videos/{item['Id']}/{variant}") if variant else (0, {}, b"")
        segment = next((l for l in playlist.decode(errors="ignore").splitlines() if l and not l.startswith("#")), None)
        status, _, ts = http(local, "GET", f"/Videos/{item['Id']}/{segment}", timeout=120) if segment else (0, {}, b"")
        expect(status == 200 and len(ts) > 188 and ts[0] == 0x47, f"a transcode with the subtitle burned in reads the link ({status}, {len(ts)} bytes)")
    finally:
        configure_server(creds, a, "source-a", [])
        shutil.rmtree(folder, ignore_errors=True)
        shutil.rmtree(local_folder, ignore_errors=True)
        rescan_until(a, "Movie", needle, False, "source-a no longer lists the MKV")
        local.post("/Library/Refresh", None, expect=204)
        configure_local(creds, local, ["source-a", "source-b"], FullScanIntervalHours=24, MissingContentAction=None)
        run_task(local, "ServerSyncUpdateTables")

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
    ("links stream from the source and match across servers", scenario_links),
    ("links: seeking reads exactly the bytes asked for", scenario_link_ranges),
    ("links: transcoding a link to HLS", scenario_link_hls),
    ("links: hostile requests are refused", scenario_link_hostile),
    ("links: a source that is down", scenario_link_source_down),
    ("links: restarts keep links working", scenario_link_restarts),
    ("links: a rename on the source moves the link", scenario_link_rename),
    ("links: a removed film takes its link", scenario_link_deleted),
    ("links: two files differing only by extension", scenario_link_collision),
    ("links: a user's own .strm is left alone", scenario_link_users_strm),
    ("links: a full disk never stops links", scenario_link_full_disk),
    ("links: parallel streams respect the limit", scenario_link_parallel_limit),
    ("links: a second source stands in when the first cannot", scenario_link_fallback),
    ("links: universal audio never redirects to a link", scenario_link_universal_audio),
    ("links: subtitles inside an MKV", scenario_link_embedded_subtitles),
    ("chaos: the source is killed mid-stream", scenario_chaos_kill_source_midstream),
    ("chaos: tampered, emptied, and deleted links", scenario_chaos_tampered_links),
    ("chaos: the link key is lost", scenario_chaos_key_lost),
    ("chaos: linking is turned off mid-stream", scenario_chaos_linking_off_midstream),
    ("chaos: a storm of plays and seeks", scenario_chaos_storm),
    ("chaos: malformed requests at every route", scenario_chaos_fuzz_routes),
    ("chaos: three hundred links at once", scenario_chaos_scale),
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


def run_api_tests(creds):
    """Runs the plugin's live API tests against source-a, which the seeding gives an image, a
    subtitle, and downloadable files."""
    log("== API tests against source-a")
    env = dict(os.environ, SERVERSYNC_TEST_SERVER_URL=f"http://127.0.0.1:{SERVERS['source-a']['port']}",
               SERVERSYNC_TEST_API_KEY=creds["servers"]["source-a"]["apiKey"])
    result = subprocess.run(["dotnet", "test", os.path.join(REPO, "Jellyfin.Plugin.ServerSync.Tests"), "--filter", "FullyQualifiedName~ApiIntegration"],
                            cwd=REPO, env=env, text=True, capture_output=True, check=False)
    summary = [line.strip() for line in result.stdout.splitlines() if "Passed!" in line or "Failed!" in line or line.strip().startswith("Failed ")]
    for line in summary:
        log(f"   {line}")
    if result.returncode != 0 or not any("Passed!" in line for line in summary):
        log(result.stdout[-3000:])
        return 1
    return 0


def cmd_test():
    creds, apis = cmd_up()
    scenarios = run_scenarios(creds, apis)
    api = run_api_tests(creds)
    return scenarios or api


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

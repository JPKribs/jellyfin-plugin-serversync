"""Server Sync screenshot server: serves the plugin pages, Base and jellyfin-web the way Jellyfin does, and the plugin's
strings the way its Translations route does.

Usage: python3 -I server.py 8874
Base comes from the NuGet cache at the version the plugin's csproj names. JELLYFIN_WEB picks the jellyfin-web folder.
"""
import http.server, json, os, re, sys, urllib.parse
HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN = os.path.join(HERE, '..', '..', 'Jellyfin.Plugin.ServerSync')
PROJECT = os.path.join(PLUGIN, 'Jellyfin.Plugin.ServerSync.csproj')
REPO = os.path.join(PLUGIN, 'Web')
JF = os.environ.get('JELLYFIN_WEB', '/Applications/Jellyfin.app/Contents/Resources/jellyfin-web')
BASEVER = re.search(r'Include="JPKribs\.Jellyfin\.Base"\s+Version="([^"]+)"', open(PROJECT).read()).group(1)
BASEROOT = os.path.expanduser('~/.nuget/packages/jpkribs.jellyfin.base/%s' % BASEVER)
BASE = os.path.join(BASEROOT, 'assets')
STRINGS = os.path.join(BASEROOT, 'jellyfin')
BASESTRINGS = os.path.join(BASEROOT, 'strings')
TYPES = {'.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.json': 'application/json', '.woff2': 'font/woff2', '.woff': 'font/woff', '.svg': 'image/svg+xml', '.png': 'image/png'}

for path, what in ((BASE, 'Base %s (run dotnet restore on the plugin)' % BASEVER), (os.path.join(JF, 'index.html'), 'jellyfin-web (set JELLYFIN_WEB)')):
    if not os.path.exists(path):
        sys.exit('Missing %s: %s' % (what, path))


def first():
    """The stylesheets jellyfin-web's index.html links, in its order."""
    return re.findall(r'href="([^"?]+\.css)', open(os.path.join(JF, 'index.html')).read())


def stylesheets():
    """jellyfin-web's stylesheets in load order: index.html's two first, the dark theme, then every chunk. A chunk that
    sets the root font to anything but the dashboard's 93% belongs to another layout, such as TV, and is left out."""
    head = first()
    rest = []
    for name in sorted(os.listdir(JF)):
        if not name.endswith('.css') or name in head:
            continue
        sizes = re.findall(r'html\{[^}]*?font-size:([^;}!]+)', open(os.path.join(JF, name), encoding='utf-8', errors='ignore').read())
        if any(size.strip() != '93%' for size in sizes):
            continue
        rest.append(name)
    return head + ['themes/dark/theme.css'] + rest


def links(names):
    return ''.join('<link rel="stylesheet" href="/jf/%s">' % n for n in names)


DEFAULT_CASING = ['Jellyfin', 'TV', 'ID', 'API', 'URL', 'HTTP', 'HTTPS', 'IP', 'DNS', 'HDR', 'SDR', '4K']


def casing():
    """Base's default casing list and the plugin's Localization/casing.json, as the build embeds them."""
    path = os.path.join(PLUGIN, 'Localization', 'casing.json')
    return DEFAULT_CASING + (json.load(open(path, encoding='utf-8')) if os.path.isfile(path) else [])


def recase(text, case, words):
    """Writes English text in sentence or title case, keeping the casing list and words with a capital inside."""
    keep = {w.lower(): w for w in words}
    small = {'a', 'an', 'and', 'as', 'at', 'but', 'by', 'for', 'from', 'in', 'into', 'nor', 'of', 'on', 'or', 'per', 'the', 'to', 'via', 'vs', 'with'}
    parts = text.split(' ')
    out = []
    for i, word in enumerate(parts):
        if word.lower() in keep:
            out.append(keep[word.lower()])
        elif re.search(r'[a-z][A-Z]|\d|^\{', word) or (len(word) > 1 and word.isupper()):
            out.append(word)
        elif case == 'title' and (i == 0 or i == len(parts) - 1 or word.lower() not in small):
            out.append(word[:1].upper() + word[1:].lower())
        else:
            out.append(word[:1].upper() + word[1:].lower() if i == 0 else word.lower())
    return ' '.join(out)


def declared():
    """Each Jellyfin string Base and the plugin declare, with its Source and Case. The plugin's own declaration wins."""
    base = json.load(open(os.path.join(BASESTRINGS, 'jellyfin.json'), encoding='utf-8'))
    found = {e['key']: (e.get('source'), e.get('case')) for e in base['strings']}
    for attrs in re.findall(r'<JellyfinString ([^>]*?)/?>', open(PROJECT).read()):
        values = dict(re.findall(r'(\w+)="([^"]*)"', attrs))
        found[values['Include']] = (values.get('Source'), values.get('Case'))
    return found


def translations(language):
    """The plugin's strings as its Translations route serves them, { language, strings, casing }: Base's own strings,
    the declared Jellyfin strings in the language, else in US English, with any Case applied to English, and the local
    strings from Localization."""
    words = casing()
    strings = declared()
    out = dict(json.load(open(os.path.join(BASESTRINGS, 'en-us.json'), encoding='utf-8')))
    for code in ('en-us', language):
        if not code:
            continue
        sources = {}
        for source in ('server', 'web'):
            path = os.path.join(STRINGS, source, code + '.json')
            sources[source] = json.load(open(path, encoding='utf-8')) if os.path.isfile(path) else {}
        for key, (source, case) in strings.items():
            text = sources[source].get(key) if source in sources else (sources['web'].get(key) or sources['server'].get(key))
            if text:
                out[key] = recase(text, case, words) if case and code.startswith('en') else text
        local = os.path.join(PLUGIN, 'Localization', code + '.json')
        if os.path.isfile(local):
            out.update({k: v for k, v in json.load(open(local, encoding='utf-8')).items() if v})
    return json.dumps({'language': language or 'en-us', 'strings': out, 'casing': words}).encode()


class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)
        if u.path == '/Sync/Translations':
            return self.send(translations(re.sub(r'[^a-z-]', '', q.get('language', ['en-us'])[0].lower())), 'application/json')
        if u.path.endswith('configurationpage') and 'name' in q:
            name = q['name'][0]
            if 'jpkribs_shared' in name:
                path = os.path.join(BASE, 'jpkribs_shared' + os.path.splitext(name)[1])
            else:
                path = os.path.join(REPO, name if '.' in name else name + '.html')
        elif u.path.startswith('/jf/'):
            path = os.path.join(JF, urllib.parse.unquote(u.path[4:]))
        else:
            path = os.path.join(HERE, u.path.lstrip('/') or 'index.html')
        if not os.path.isfile(path):
            self.send_error(404); return
        data = open(path, 'rb').read()
        if path.endswith('.html') and os.path.dirname(path) == HERE:
            data = data.replace(b'<!--STYLESHEETS-->', links(stylesheets()).encode())
        self.send(data, TYPES.get(os.path.splitext(path)[1], 'application/octet-stream'))

    def send(self, data, kind):
        self.send_response(200); self.send_header('Content-Type', kind); self.send_header('Cache-Control', 'no-store'); self.end_headers(); self.wfile.write(data)


http.server.ThreadingHTTPServer(('127.0.0.1', int(sys.argv[1]) if len(sys.argv) > 1 else 8874), H).serve_forever()

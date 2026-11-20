// loader.mjs
// A node module loader hook that lets the Server Sync config pages load outside Jellyfin. The pages import their
// shared module and Base's bundle by a specifier that only Jellyfin resolves, so this maps each one to its file.
// Base's bundle is its built dist/jpkribs_shared.js.

import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

var configDir = '';
var baseEntry = '';

// initialize
// Receives the folders the hook maps to, from register in support.mjs.
//
// Inputs
// data [object]: configDir is the plugin's Web folder. baseEntry is the URL of Base's built bundle.
export async function initialize(data) {
    configDir = data.configDir;
    baseEntry = data.baseEntry;
}

// resolve
// Maps './configurationpage?name=<file>' to the plugin's own file, or to Base's built bundle for the shared one.
//
// Inputs
// specifier [string]: What the importing module asked for.
// context [object]: The importer and the import conditions.
// nextResolve [function]: The default resolver.
//
// Output
// [Promise]: The resolved URL.
export async function resolve(specifier, context, nextResolve) {
    var match = /^\.\/configurationpage\?name=(.+)$/.exec(specifier);
    if (!match) return nextResolve(specifier, context);
    if (match[1] === 'serversync_jpkribs_shared.js') return { url: baseEntry, shortCircuit: true };
    return { url: pathToFileURL(join(configDir, match[1])).href, shortCircuit: true };
}

// load
// Loads the plugin and Base scripts as ES modules, since neither folder has a package file that says so.
//
// Inputs
// url [string]: The module to load.
// context [object]: The load context.
// nextLoad [function]: The default loader.
//
// Output
// [Promise]: The loaded module source.
export async function load(url, context, nextLoad) {
    if (url.endsWith('.js') && (url.startsWith(pathToFileURL(configDir).href) || url === baseEntry)) {
        return nextLoad(url, { ...context, format: 'module' });
    }
    return nextLoad(url, context);
}

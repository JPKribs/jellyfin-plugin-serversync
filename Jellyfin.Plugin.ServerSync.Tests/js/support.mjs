// support.mjs
// Shared setup for the config page tests. It installs Base's fake DOM, stubs the Jellyfin globals the pages
// read, serves the plugin's US English strings, and loads a page script from the plugin's Web folder through the loader hook.

import { register } from 'node:module';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';
import { existsSync, readFileSync } from 'node:fs';

var realSetTimeout = globalThis.setTimeout;
var here = dirname(fileURLToPath(import.meta.url));
var projectDir = join(here, '..', '..', 'Jellyfin.Plugin.ServerSync');
var configDir = join(projectDir, 'Web');
var baseRoot = join(here, '..', '..', '..', 'Shared', 'jellyfin-plugin-base');
var baseBundle = join(baseRoot, 'dist', 'jpkribs_shared.js');
var baseEntry = pathToFileURL(baseBundle).href;
var fakeDomPath = join(baseRoot, 'JPKribs.Jellyfin.Base.Tests', 'js', 'fake-dom.mjs');

if (!existsSync(fakeDomPath)) {
    throw new Error('Base is not checked out next to this repository. Expected ' + baseRoot + '.');
}

// The pages load the bundle Base ships, so a test runs the same code a plugin receives.
if (!existsSync(baseBundle)) {
    throw new Error('Base has no bundle. Run scripts/bundle.sh in ' + baseRoot + '.');
}

register(pathToFileURL(join(here, 'loader.mjs')).href, { parentURL: import.meta.url, data: { configDir: configDir, baseEntry: baseEntry } });

var fakeDom = await import(pathToFileURL(fakeDomPath).href);

// usEnglish
// Builds the strings the Translations route answers with in US English: the plugin's own, and the Jellyfin
// strings the project declares, read from the copy of Jellyfin's strings in Base.
//
// Output
// [object]: The strings by key.
function usEnglish() {
    var strings = {};
    var jellyfin = {
        web: JSON.parse(readFileSync(join(baseRoot, 'dist', 'jellyfin', 'web', 'en-us.json'), 'utf8')),
        server: JSON.parse(readFileSync(join(baseRoot, 'dist', 'jellyfin', 'server', 'en-us.json'), 'utf8'))
    };
    var project = readFileSync(join(projectDir, 'Jellyfin.Plugin.ServerSync.csproj'), 'utf8');
    for (var match of project.matchAll(/<JellyfinString Include="([^"]+)"( Source="server")?/g)) {
        strings[match[1]] = jellyfin[match[2] ? 'server' : 'web'][match[1]];
    }
    return Object.assign(strings, JSON.parse(readFileSync(join(projectDir, 'Localization', 'en-us.json'), 'utf8')));
}

export var strings = usEnglish();

// casingList
// Reads the plugin's casing list, the words its text keeps as written.
//
// Output
// [string[]]: The words, or an empty list when the plugin has none.
function casingList() {
    var file = join(projectDir, 'Localization', 'casing.json');
    return existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : [];
}

// The pages load their strings with fetch, which answers here as BaseTranslations.Serve does, in US English.
globalThis.fetch = function (url) {
    if (String(url).indexOf('Sync/Translations') < 0) return Promise.reject(new Error('No route for ' + url));
    var body = { language: 'en-us', strings: strings, casing: casingList() };
    return Promise.resolve({ ok: true, json: function () { return Promise.resolve(body); } });
};

// The fake element has no dataset or remove, which the page scripts use.
var elementProto = Object.getPrototypeOf(new fakeDom.FakeElement({}, 'div'));
if (!Object.getOwnPropertyDescriptor(elementProto, 'dataset')) {
    Object.defineProperty(elementProto, 'dataset', {
        get: function () {
            var el = this;
            var attr = function (key) { return 'data-' + key.replace(/[A-Z]/g, function (c) { return '-' + c.toLowerCase(); }); };
            return new Proxy({}, {
                get: function (target, key) { var v = el.getAttribute(attr(String(key))); return v === null ? undefined : v; },
                set: function (target, key, value) { el.setAttribute(attr(String(key)), value); return true; },
                has: function (target, key) { return el.hasAttribute(attr(String(key))); },
                deleteProperty: function (target, key) { el.removeAttribute(attr(String(key))); return true; }
            });
        }
    });
}
if (!elementProto.remove) {
    elementProto.remove = function () { if (this.parentNode) this.parentNode.removeChild(this); };
}

// installPage
// Installs a fresh fake DOM and the Jellyfin globals a page script reads.
//
// Inputs
// opts [object]: config is what ApiClient returns as the plugin configuration. storage seeds localStorage.
// calls is an array that collects each LibraryMenu.setTabs call.
//
// Output
// [object]: The fake DOM helper from Base, plus the stubbed ApiClient requests it recorded.
export function installPage(opts) {
    opts = opts || {};
    var dom = fakeDom.installFakeDom();
    dom.document.getElementById = function (id) { return dom.document.documentElement.querySelector('#' + id); };
    var requests = [];
    var store = Object.assign({}, opts.storage || {});
    globalThis.localStorage = {
        getItem: function (key) { return Object.prototype.hasOwnProperty.call(store, key) ? store[key] : null; },
        setItem: function (key, value) { store[key] = String(value); }
    };
    globalThis.ApiClient = {
        getUrl: function (path) { return path; },
        fetch: function (options) { requests.push(options); return Promise.resolve({}); },
        getPluginConfiguration: function () { return Promise.resolve(opts.config || {}); },
        updatePluginConfiguration: function () { return Promise.resolve(); },
        getPublicSystemInfo: function () { return Promise.resolve({ ServerName: 'Test Server' }); }
    };
    globalThis.LibraryMenu = { setTabs: function (key, index, getTabs) { (opts.calls || []).push({ key: key, index: index, tabs: getTabs() }); } };
    globalThis.Dashboard = { alert: function () { throw new Error('Dashboard.alert must not be used.'); } };
    return { dom: dom, requests: requests, store: store };
}

// loadScript
// Imports one of the plugin's Web scripts, so a test calls its exports.
//
// Inputs
// name [string]: The file name, such as serversync_shared.js.
//
// Output
// [Promise]: The module namespace.
export function loadScript(name) {
    return import(pathToFileURL(join(configDir, name)).href);
}

export var paths = { configDir: configDir, baseRoot: baseRoot };

// createSyncContext
// Builds the page context the Sync view modules are made from, over a view that holds the given elements.
//
// Inputs
// ids [string[]]: The element ids the view holds. syncStatus is always added.
// opts [object]: confirmDialog replaces the confirm dialog, createPaginatedTable replaces the table builder, and
// config seeds the plugin configuration.
//
// Output
// [Promise]: The page, the view, the shared bag, the table kit, and the context.
export async function createSyncContext(ids, opts) {
    opts = opts || {};
    var page = installPage({ config: opts.config });
    var view = page.dom.el('div', {}, ['syncStatus'].concat(ids || []).map(function (id) { return page.dom.el('div', { id: id }); }));
    page.dom.document.documentElement.appendChild(view);
    var sharedMod = await loadScript('serversync_shared.js');
    var shared = sharedMod.createServerSyncShared(view);
    await shared.translations;
    var responses = [];
    globalThis.ApiClient.fetch = function (options) {
        page.requests.push(options);
        var next = responses.shift();
        if (next instanceof Error) return Promise.reject(next);
        return Promise.resolve(next === undefined ? {} : next);
    };
    globalThis.ApiClient.getImageUrl = function (id) { return '/image/' + id; };
    globalThis.ApiClient.getUserImageUrl = function (id) { return '/user-image/' + id; };
    var tableMod = await loadScript('serversync_sync_table.js');
    var ctx = {
        view: view,
        shared: shared,
        createPaginatedTable: opts.createPaginatedTable || function () { return { getBulkActionsContainer: function () { return null; } }; },
        bindDialog: function () { return { isOpen: function () { return false; }, close: function () {}, open: function () {} }; },
        confirmDialog: opts.confirmDialog || function () { return Promise.resolve(true); },
        activePollIntervals: []
    };
    return { page: page, view: view, shared: shared, kit: tableMod.createSyncTableKit(ctx), ctx: ctx, responses: responses };
}

// flush
// Waits for the promise chains a call started to settle.
export async function flush() {
    for (var i = 0; i < 8; i++) await new Promise(function (resolve) { setImmediate(resolve); });
}

// settle
// Waits real time for the module imports a page started to land, then for the promise chains after them.
//
// Inputs
// ms [number]: How long to wait. Defaults to 150.
export async function settle(ms) {
    await new Promise(function (resolve) { realSetTimeout(resolve, ms || 150); });
    await flush();
}

// clickDialogButton
// Clicks the button with this text in the dialog that is open.
//
// Inputs
// dom [object]: The fake DOM helper from installPage.
// text [string]: The button's label.
//
// Output
// [boolean]: Whether a button with that label was found.
export function clickDialogButton(dom, text) {
    var dialog = dom.document.querySelector('.base-dialog');
    if (!dialog) return false;
    var button = Array.from(dialog.querySelectorAll('button')).find(function (b) { return b.textContent === text; });
    if (!button) return false;
    button.click();
    return true;
}

// dialogText
// Reads the title and the paragraphs of the dialog that is open.
//
// Inputs
// dom [object]: The fake DOM helper from installPage.
//
// Output
// [object]: The title, and the text of each paragraph, or null when no dialog is open.
export function dialogText(dom) {
    var dialog = dom.document.querySelector('.base-dialog');
    if (!dialog) return null;
    var title = dialog.querySelector('.base-modal-title');
    var body = dialog.querySelector('.base-modal-body');
    return { title: title ? title.textContent : '', body: body ? (body.innerHTML || body.children.map(function (c) { return c.textContent; }).join(' ')) : '' };
}

// statusText
// Reads the message a status element shows, without the icon Base draws beside it.
//
// Inputs
// el [object]: The status element.
//
// Output
// [string]: The message, or an empty string when it shows none.
export function statusText(el) {
    var message = el && el.querySelector ? el.querySelector('.base-status-message') : null;
    return message ? message.textContent : '';
}

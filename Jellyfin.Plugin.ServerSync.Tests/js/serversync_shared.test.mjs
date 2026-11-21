// serversync_shared.test.mjs
// Tests for serversync_shared.js: the config readers, the request body, the Storage tab flag, the save helper,
// the thumbnail markup, and the health banner.

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { installPage, loadScript, statusText } from './support.mjs';

var timers = [];
globalThis.setTimeout = function (fn) { timers.push(fn); return timers.length; };
globalThis.clearTimeout = function () {};

var page;
var view;
var shared;
var mod;

// build
// Creates a page view with the elements the helpers touch, and the shared bag over it.
async function build(opts) {
    opts = opts || {};
    page = installPage(opts);
    var FakeElement = page.dom.document.createElement('div').constructor;
    FakeElement.prototype.remove = function () { if (this.parentNode) this.parentNode.removeChild(this); };
    view = page.dom.el('div', {}, [
        page.dom.el('div', { class: 'content-primary' }),
        page.dom.el('span', { id: 'status' }),
        page.dom.el('span', { id: 'syncStatus' }),
        page.dom.el('input', { id: 'field' })
    ]);
    page.dom.document.documentElement.appendChild(view);
    mod = await loadScript('serversync_shared.js');
    shared = mod.createServerSyncShared(view);
    await shared.translations;
    return shared;
}

beforeEach(async function () {
    timers.length = 0;
    await build();
});

test('plural writes the one form for one and the many form for every other count', function () {
    assert.equal(shared.plural(1, 'ToastItemsIgnoredOne', 'ToastItemsIgnoredMany'), '1 item ignored.');
    assert.equal(shared.plural(0, 'ToastItemsIgnoredOne', 'ToastItemsIgnoredMany'), '0 items ignored.');
    assert.equal(shared.plural(3, 'ToastItemsIgnoredOne', 'ToastItemsIgnoredMany'), '3 items ignored.');
});

test('scanServers keeps enabled Pull and Sync servers that have a Server URL and API Key, in order', function () {
    var config = {
        Servers: [
            { Key: 'a', Url: 'http://a', ApiKey: 'k', Mode: 'Pull' },
            { Key: 'b', Url: 'http://b', ApiKey: 'k', Mode: 'Push' },
            { Key: 'c', Url: 'http://c', ApiKey: 'k', Mode: 'Sync' },
            { Key: 'd', Url: 'http://d', ApiKey: 'k', IsEnabled: false },
            { Key: 'e', Url: '', ApiKey: 'k' },
            { Key: 'f', Url: 'http://f', ApiKey: '' },
            { Key: 'g', Url: 'http://g', ApiKey: 'k' },
            { Key: 'h', Url: 'http://h', ApiKey: 'k', Mode: 0 },
            { Key: 'i', Url: 'http://i', ApiKey: 'k', Mode: 1 },
            { Key: 'j', Url: 'http://j', ApiKey: 'k', Mode: 2 },
            null
        ]
    };
    assert.deepEqual(shared.scanServers(config).map(function (s) { return s.Key; }), ['a', 'c', 'g', 'h', 'j']);
});

test('scanServers returns an empty list for a missing config', function () {
    assert.deepEqual(shared.scanServers(null), []);
    assert.deepEqual(shared.scanServers({}), []);
});

test('allLibraryMappings and allUserMappings gather only the servers that scan', function () {
    var config = {
        Servers: [
            { Key: 'a', Url: 'u', ApiKey: 'k', LibraryMappings: [{ Id: 1 }], UserMappings: [{ Id: 'u1' }] },
            { Key: 'b', Url: 'u', ApiKey: 'k', Mode: 'Push', LibraryMappings: [{ Id: 2 }], UserMappings: [{ Id: 'u2' }] },
            { Key: 'c', Url: 'u', ApiKey: 'k', Mode: 'Sync', LibraryMappings: [{ Id: 3 }] }
        ]
    };
    assert.deepEqual(shared.allLibraryMappings(config), [{ Id: 1 }, { Id: 3 }]);
    assert.deepEqual(shared.allUserMappings(config), [{ Id: 'u1' }]);
});

test('serverFor finds a server by key and falls back to the first scan server', function () {
    var config = { Servers: [
        { Key: 'push', Url: 'u', ApiKey: 'k', Mode: 'Push' },
        { Key: 'first', Url: 'u', ApiKey: 'k' },
        { Key: 'second', Url: 'u', ApiKey: 'k' }
    ] };
    assert.equal(shared.serverFor(config, 'second').Key, 'second');
    assert.equal(shared.serverFor(config, 'push').Key, 'push');
    assert.equal(shared.serverFor(config, 'gone').Key, 'first');
    assert.equal(shared.serverFor(config, '').Key, 'first');
    assert.equal(shared.serverFor({ Servers: [] }, 'x'), null);
});

test('serverDisplayName prefers the name, then the reported name, then the Server URL', function () {
    assert.equal(shared.serverDisplayName({ Name: 'Main', ServerName: 'x', Url: 'u' }), 'Main');
    assert.equal(shared.serverDisplayName({ ServerName: 'Reported', Url: 'u' }), 'Reported');
    assert.equal(shared.serverDisplayName({ Url: 'http://u' }), 'http://u');
    assert.equal(shared.serverDisplayName({}), 'Source');
    assert.equal(shared.serverDisplayName(null), 'Source');
});

test('serverNameFor names the server a row came from', function () {
    var config = { Servers: [{ Key: 'a', Name: 'Alpha', Url: 'u', ApiKey: 'k' }] };
    assert.equal(shared.serverNameFor(config, 'a'), 'Alpha');
    assert.equal(shared.serverNameFor({ Servers: [] }, 'a'), 'Source');
});

test('requestFor builds the request body and lets an override replace the API Key', function () {
    var server = { Url: 'http://a', ApiKey: 'stored', Key: 'k1', AllowPrivateNetwork: false, AuthenticatedUserId: 'u1' };
    assert.deepEqual(shared.requestFor(server), { ServerUrl: 'http://a', ApiKey: 'stored', ServerKey: 'k1', AllowPrivateNetwork: false, AuthenticatedUserId: 'u1' });
    assert.equal(shared.requestFor(server, 'typed').ApiKey, 'typed');
});

test('requestFor allows a private network address unless it is turned off, and sends no user when there is none', function () {
    var body = shared.requestFor({ Url: 'http://a', Key: 'k1' });
    assert.equal(body.AllowPrivateNetwork, true);
    assert.equal(body.ApiKey, '');
    assert.equal(body.AuthenticatedUserId, null);
});

test('rememberContentSync stores 1 while any of content, history, or metadata sync is on', function () {
    shared.rememberContentSync({ EnableContentSync: false, EnableHistorySync: false, EnableMetadataSync: true });
    assert.equal(page.store[mod.STORAGE_TAB_KEY], '1');
    shared.rememberContentSync({ EnableContentSync: false, EnableHistorySync: false, EnableMetadataSync: false });
    assert.equal(page.store[mod.STORAGE_TAB_KEY], '0');
});

test('rememberContentSync ignores a config that does not carry the content setting', function () {
    shared.rememberContentSync({ Other: true });
    shared.rememberContentSync(null);
    assert.equal(mod.STORAGE_TAB_KEY in page.store, false);
});

test('rememberContentSync runs onTabsChanged only when what shows changes', function () {
    var changes = 0;
    shared.onTabsChanged = function () { changes++; };
    shared.rememberContentSync({ EnableContentSync: false });
    assert.equal(changes, 1, 'a missing flag showed the tab, so turning it off changes the tabs');
    shared.rememberContentSync({ EnableContentSync: false });
    assert.equal(changes, 1, 'the same answer changes nothing');
    shared.rememberContentSync({ EnableContentSync: true });
    assert.equal(changes, 2, 'turning it back on shows the tab again');
});

test('rememberContentSync does not run onTabsChanged when the first answer matches the default of shown', function () {
    var changes = 0;
    shared.onTabsChanged = function () { changes++; };
    shared.rememberContentSync({ EnableContentSync: true });
    assert.equal(changes, 0);
});

test('rememberContentSync survives storage that throws', async function () {
    await build();
    globalThis.localStorage = { getItem: function () { throw new Error('blocked'); }, setItem: function () { throw new Error('blocked'); } };
    shared.rememberContentSync({ EnableContentSync: true });
});

test('getConfig remembers the content setting of the config it reads', async function () {
    await build({ config: { EnableContentSync: false, EnableHistorySync: false, EnableMetadataSync: false } });
    var config = await shared.getConfig();
    assert.equal(config.EnableContentSync, false);
    assert.equal(page.store[mod.STORAGE_TAB_KEY], '0');
});

test('saveConfig remembers the content setting of the config it saved', async function () {
    await shared.saveConfig({ EnableContentSync: true });
    assert.equal(page.store[mod.STORAGE_TAB_KEY], '1');
});

test('saveSection saves the changed config and resolves with it', async function () {
    await build({ config: { Servers: [] } });
    var seen = null;
    var saved = await shared.saveSection(function (config) { config.Changed = true; seen = config; });
    assert.equal(seen.Changed, true);
    assert.equal(saved.Changed, true);
});

test('saveSection rejects when the save does not land, so a Save Bar shows it failed', async function () {
    await build({ config: {} });
    globalThis.ApiClient.updatePluginConfiguration = function () { return Promise.reject(new Error('refused')); };
    await assert.rejects(shared.saveSection(function () {}));
});

test('saveSection rejects when the mutator throws, and never saves', async function () {
    await build({ config: {} });
    var posted = 0;
    globalThis.ApiClient.updatePluginConfiguration = function () { posted++; return Promise.resolve(); };
    await assert.rejects(shared.saveSection(function () { throw new Error('removed'); }), /removed/);
    assert.equal(posted, 0);
});

// fakePage
// A page handle that keeps the unsaved check createSaveBars gives it.
function fakePage() {
    var handle = { guard: null };
    handle.guardUnsaved = function (fn) { handle.guard = fn; };
    return handle;
}

test('createSaveBars ends a section with a full width Save Bar, and a changed field marks it unsaved', function () {
    var handle = fakePage();
    var bars = mod.createSaveBars(handle);
    var section = page.dom.el('div', {}, [page.dom.el('input', { id: 'sectionField' })]);
    view.appendChild(section);
    var bar = bars.add(section, { savedText: 'Saved.', failedText: 'Could not save.', onSave: function () { return Promise.resolve(); } });

    assert.equal(section.children[section.children.length - 1], bar.element);
    assert.ok(bar.element.classList.contains('base-save-bar'));
    assert.equal(handle.guard(), false);

    section.querySelector('#sectionField').dispatch('input');
    assert.equal(bar.getState(), 'pending');
    assert.equal(handle.guard(), true);

    bars.clean();
    assert.equal(bar.getState(), '');
    assert.equal(handle.guard(), false);
});

test('createSaveBars clears the unsaved mark only once a save lands', async function () {
    var handle = fakePage();
    var bars = mod.createSaveBars(handle);
    var section = page.dom.el('div');
    view.appendChild(section);
    var result = Promise.reject(new Error('refused'));
    result.catch(function () {});
    var bar = bars.add(section, { savedText: 'Saved.', failedText: 'Could not save.', onSave: function () { return result; } });

    bars.markDirty(bar);
    assert.equal(await bar.save(), false);
    assert.equal(bar.getState(), 'failed');
    assert.equal(handle.guard(), true);

    result = Promise.resolve();
    assert.equal(await bar.save(), true);
    assert.equal(bar.getState(), 'saved');
    assert.equal(handle.guard(), false);
});

test('syncStatus shows a result on the Sync tab status line, in the error color for a failure', function () {
    shared.syncStatus('Item deleted.', false);
    var el = view.querySelector('#syncStatus');
    assert.equal(statusText(el), 'Item deleted.');
    assert.ok(el.classList.contains('base-status-ok'));
    shared.syncStatus('Could not delete the item.', true);
    assert.ok(el.classList.contains('base-status-bad'));
    assert.equal(el.classList.contains('base-status-ok'), false);
});

test('pageHelpers returns helpers bound to the bag, so a page can call them by bare name', function () {
    var h = shared.pageHelpers();
    var { setValue, getValue, getIntValue, escapeHtml, requestFor } = h;
    setValue('field', '42');
    assert.equal(getValue('field'), '42');
    assert.equal(getIntValue('field', 0), 42);
    assert.equal(getIntValue('missing', 7), 7);
    assert.equal(escapeHtml('<b>'), '&lt;b&gt;');
    assert.equal(requestFor({ Url: 'u', Key: 'k' }).ServerKey, 'k');
});

test('proxyImage gives no loader when there is no id', function () {
    assert.equal(shared.proxyImage('', false, 'k'), null);
    assert.equal(shared.proxyImage(null, true, 'k'), null);
});

test('proxyImage asks the image proxy for the item on its server, and for a user when told', async function () {
    var asked = [];
    globalThis.ApiClient.getUrl = function (path, params) { asked.push({ path: path, params: params }); return 'proxy-url'; };
    globalThis.ApiClient.fetch = function () { return Promise.resolve({ ok: true, blob: function () { return Promise.resolve('picture'); } }); };

    var item = await shared.proxyImage('item-1', false, 'k')();
    await shared.proxyImage('user-1', true, '')();

    assert.equal(item, 'picture');
    assert.deepEqual(asked[0], { path: 'Sync/ImageProxy', params: { itemId: 'item-1', user: 'false', maxHeight: 80, serverKey: 'k' } });
    assert.deepEqual(asked[1], { path: 'Sync/ImageProxy', params: { itemId: 'user-1', user: 'true', maxHeight: 80 } });
});

test('proxyImage fails its loader when the proxy answers with an error, so the placeholder stays', async function () {
    globalThis.ApiClient.getUrl = function () { return 'proxy-url'; };
    globalThis.ApiClient.fetch = function () { return Promise.resolve({ ok: false }); };

    await assert.rejects(shared.proxyImage('item-1', false, 'k')());
});

test('scheduleProxyImage hides the image and shows its placeholder when the image cannot load', async function () {
    var img = page.dom.el('img', { id: 'pic' });
    var placeholder = page.dom.el('div', { class: 'hidden' });
    var holder = page.dom.el('div', {}, [img, placeholder]);
    Object.defineProperty(img, 'nextElementSibling', { get: function () { return placeholder; } });
    view.appendChild(holder);
    page.dom.document.getElementById = function (id) { return view.querySelector('#' + id); };
    globalThis.ApiClient.fetch = function () { return Promise.resolve({ ok: false }); };
    shared.scheduleProxyImage('pic', 'item', false, 80, 'k');
    timers.shift()();
    await new Promise(function (resolve) { setImmediate(resolve); });
    assert.ok(img.classList.contains('hidden'));
    assert.equal(placeholder.classList.contains('hidden'), false);
});

test('fetchLocalServerName reads the name once and falls back to Local', async function () {
    assert.equal(await shared.fetchLocalServerName(), 'Test Server');
    assert.equal(shared.localServerName, 'Test Server');
    globalThis.ApiClient.getPublicSystemInfo = function () { return Promise.reject(new Error('down')); };
    assert.equal(await shared.fetchLocalServerName(), 'Local');
});

test('showHealth adds a banner while the plugin is held and removes it once it is ready', async function () {
    globalThis.ApiClient.fetch = function () { return Promise.resolve({ Ready: false, Message: 'Install 12.4.13.0 first.' }); };
    await shared.showHealth();
    var banner = view.querySelector('.serversync-health-banner');
    assert.ok(banner);
    assert.equal(banner.textContent, 'Install 12.4.13.0 first.');
    assert.equal(banner.getAttribute('role'), 'alert');
    globalThis.ApiClient.fetch = function () { return Promise.resolve({ Ready: true }); };
    await shared.showHealth();
    assert.equal(view.querySelector('.serversync-health-banner'), null);
});

test('showHealth gives a default message when the plugin is held and says nothing', async function () {
    globalThis.ApiClient.fetch = function () { return Promise.resolve({ Ready: false }); };
    await shared.showHealth();
    assert.equal(view.querySelector('.serversync-health-banner').textContent, 'Server Sync cannot run on this database.');
});

test('the shared module re-exports Base builders and the kept secret sentinel', function () {
    assert.equal(typeof mod.createPaginatedTable, 'function');
    assert.equal(typeof mod.confirmDialog, 'function');
    assert.equal(typeof mod.bindDialog, 'function');
    assert.equal(typeof mod.generateGuid, 'function');
    assert.equal(shared.SECRET_KEPT, mod.SECRET_KEPT);
    assert.ok(mod.SECRET_KEPT.length > 0);
});

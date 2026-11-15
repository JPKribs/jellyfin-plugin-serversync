// serversync_storage.test.mjs
// Tests for serversync_storage.js: the tabs it sets, that a load failure shows on the page status and never in an
// alert, and that the page says so when no sync type that uses storage is on.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installPage, loadScript, settle } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};
globalThis.setInterval = function () { return 1; };

var IDS = ['storageStatus', 'storageDisabled', 'storageFiltersOnly', 'storageContent', 'storageLibrariesContainer', 'storageServerContainer',
    'storageNoServers', 'storageLibrariesSection', 'keptCopiesSection', 'selStorageServer'];

// openPage
// Loads the Storage controller over a view that holds the elements it uses, and shows the page.
async function openPage(opts) {
    opts = opts || {};
    var calls = [];
    var page = installPage({ calls: calls, config: opts.config === undefined ? {} : opts.config, storage: opts.storage });
    var view = page.dom.el('div', {}, IDS.map(function (id) { return page.dom.el(id === 'selStorageServer' ? 'select' : 'div', { id: id }); }));
    page.dom.document.documentElement.appendChild(view);
    if (opts.fail) globalThis.ApiClient.getPluginConfiguration = function () { return Promise.reject(new Error('refused')); };
    var mod = await loadScript('serversync_storage.js');
    mod.default(view);
    view.dispatch('viewshow');
    await settle();
    return { page: page, view: view, calls: calls };
}

test('the Storage tab always lists itself, even while the stored answer says it is hidden', async function () {
    var opened = await openPage({ storage: { 'serversync.storageTab': '0' } });
    assert.equal(opened.calls[0].index, 2);
    assert.deepEqual(opened.calls[0].tabs.map(function (t) { return t.name; }), ['Sync', 'Servers', 'Storage', 'Settings']);
});

test('a config that cannot be loaded shows an error on the page status and never opens an alert', async function () {
    var opened = await openPage({ fail: true });
    var status = opened.view.querySelector('#storageStatus');
    assert.equal(status.textContent, 'Could not load the storage settings. Reload the page.');
    assert.ok(status.classList.contains('jpk-status-bad'));
});

test('the page says nothing applies while content, history, and metadata sync are all off', async function () {
    var opened = await openPage({ config: { EnableContentSync: false, EnableHistorySync: false, EnableMetadataSync: false } });
    assert.equal(opened.view.querySelector('#storageDisabled').classList.contains('hidden'), false);
    assert.equal(opened.view.querySelector('#storageContent').classList.contains('hidden'), true);
});

test('the page says only the filter applies while content sync is off and another sync type is on', async function () {
    var opened = await openPage({ config: { EnableContentSync: false, EnableHistorySync: true, Servers: [] } });
    assert.equal(opened.view.querySelector('#storageDisabled').classList.contains('hidden'), true);
    assert.equal(opened.view.querySelector('#storageFiltersOnly').classList.contains('hidden'), false);
    assert.equal(opened.view.querySelector('#storageContent').classList.contains('hidden'), false);
    assert.ok(opened.view.querySelector('#storageLibrariesContainer').classList.contains('serversync-filters-only'));
});

test('the page shows the server picker only when more than one server has library mappings', async function () {
    var one = { Key: 'a', Name: 'A', Url: 'u', ApiKey: 'k', LibraryMappings: [] };
    var two = { Key: 'b', Name: 'B', Url: 'u', ApiKey: 'k', LibraryMappings: [] };
    var opened = await openPage({ config: { EnableContentSync: true, Servers: [{ ...one, LibraryMappings: [{ IsEnabled: false }] }] } });
    assert.equal(opened.view.querySelector('#storageServerContainer').classList.contains('hidden'), true);
    opened = await openPage({ config: { EnableContentSync: true, Servers: [{ ...one, LibraryMappings: [{ IsEnabled: false }] }, { ...two, LibraryMappings: [{ IsEnabled: false }] }] } });
    assert.equal(opened.view.querySelector('#storageServerContainer').classList.contains('hidden'), false);
});

test('a Push server maps no libraries, so the picker leaves it out', async function () {
    var opened = await openPage({ config: { EnableContentSync: true, Servers: [{ Key: 'p', Name: 'P', Url: 'u', ApiKey: 'k', Mode: 'Push', LibraryMappings: [{ IsEnabled: false }] }] } });
    assert.equal(opened.view.querySelector('#storageNoServers').classList.contains('hidden'), false);
});

// serversync_sync.test.mjs
// Tests for serversync_sync.js: the tab list the Sync tab sets, and that the page stops its work when it hides.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installPage, loadScript, flush } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};
globalThis.setInterval = function () { return 1; };
globalThis.clearInterval = function () {};

// openPage
// Loads the page controller over a view, shows it, and returns the setTabs calls it made.
async function openPage(storage) {
    var calls = [];
    var page = installPage({ calls: calls, storage: storage });
    var view = page.dom.el('div', { class: 'page' });
    page.dom.document.documentElement.appendChild(view);
    var mod = await loadScript('serversync_sync.js');
    mod.default(view);
    view.dispatch('viewshow');
    await flush();
    return { calls: calls, view: view, page: page };
}

test('the Sync tab sets the four tabs, with itself first, while the Storage tab shows', async function () {
    var opened = await openPage({});
    var first = opened.calls[0];
    assert.equal(first.key, 'serversync');
    assert.equal(first.index, 0);
    assert.deepEqual(first.tabs.map(function (t) { return t.name; }), ['Sync', 'Servers', 'Storage', 'Statistics', 'Settings']);
    assert.equal(first.tabs[0].href, 'configurationpage?name=serversync_sync');
});

test('the Sync tab leaves the Storage tab out once the stored answer says it is hidden', async function () {
    var opened = await openPage({ 'serversync.storageTab': '0' });
    assert.deepEqual(opened.calls[0].tabs.map(function (t) { return t.name; }), ['Sync', 'Servers', 'Statistics', 'Settings']);
});

test('the Sync tab shows the Storage tab when storage is blocked', async function () {
    var calls = [];
    var page = installPage({ calls: calls });
    globalThis.localStorage = { getItem: function () { throw new Error('blocked'); } };
    var view = page.dom.el('div');
    page.dom.document.documentElement.appendChild(view);
    var mod = await loadScript('serversync_sync.js');
    mod.default(view);
    view.dispatch('viewshow');
    assert.equal(calls[0].tabs.length, 5);
});

test('the Sync tab registers for viewshow and viewhide', async function () {
    var opened = await openPage({});
    assert.equal((opened.view.listeners.viewshow || []).length, 1);
    assert.equal((opened.view.listeners.viewhide || []).length, 1);
});

test('showing the tab again sets the tabs again', async function () {
    var opened = await openPage({});
    var before = opened.calls.length;
    opened.view.dispatch('viewshow');
    await flush();
    assert.equal(opened.calls.length, before + 1);
});

test('hiding the tab before the page modules load does not fail', async function () {
    var calls = [];
    var page = installPage({ calls: calls });
    var view = page.dom.el('div');
    page.dom.document.documentElement.appendChild(view);
    var mod = await loadScript('serversync_sync.js');
    mod.default(view);
    view.dispatch('viewhide');
    await flush();
});

// serversync_sync_table.test.mjs
// Tests for serversync_sync_table.js: the row and badge markup, the health figures, the value helpers, and the
// module every Sync view is built from.

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { installPage, loadScript, statusText } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};

var page;
var view;
var shared;
var kit;
var responses;

// setup
// Builds a view with the given element ids, the shared bag over it, and the kit.
async function setup(ids) {
    page = installPage();
    view = page.dom.el('div', {}, (ids || []).map(function (id) { return page.dom.el('div', { id: id }); }));
    page.dom.document.documentElement.appendChild(view);
    var sharedMod = await loadScript('serversync_shared.js');
    shared = sharedMod.createServerSyncShared(view);
    responses = [];
    globalThis.ApiClient.fetch = function (options) {
        page.requests.push(options);
        var next = responses.shift();
        if (next instanceof Error) return Promise.reject(next);
        return Promise.resolve(next === undefined ? {} : next);
    };
    var tableMod = await loadScript('serversync_sync_table.js');
    kit = tableMod.createSyncTableKit({
        view: view,
        shared: shared,
        createPaginatedTable: function () { return {}; },
        bindDialog: function () { return { isOpen: function () { return false; }, close: function () {} }; },
        activePollIntervals: []
    });
}

// flush
// Waits for the promise chains a call started to settle.
async function flush() {
    for (var i = 0; i < 6; i++) await new Promise(function (resolve) { setImmediate(resolve); });
}

beforeEach(async function () {
    await setup(['syncStatus', 'healthLastSync', 'healthCount', 'version']);
});

test('parseJsonSafe parses JSON and gives null for text that is not JSON', function () {
    assert.deepEqual(kit.parseJsonSafe('{"a":1}'), { a: 1 });
    assert.equal(kit.parseJsonSafe('plain text'), null);
    assert.equal(kit.parseJsonSafe(''), null);
    assert.equal(kit.parseJsonSafe(null), null);
    assert.equal(kit.parseJsonSafe(42), null);
});

test('formatImageDisplay shows a dash for no images and a count when the size is unknown', function () {
    assert.equal(kit.formatImageDisplay(0, 0), '-');
    assert.equal(kit.formatImageDisplay(0, 1), '1 image');
    assert.equal(kit.formatImageDisplay(null, 3), '3 images');
});

test('formatImageDisplay shows the size, with the count when there are several images', function () {
    assert.equal(kit.formatImageDisplay(2048, 1), '2 KB');
    assert.equal(kit.formatImageDisplay(2048, 3), '2 KB (3)');
});

test('renderChangeBadges gives one gray badge when nothing changed', function () {
    assert.equal(kit.renderChangeBadges([]), '<span class="base-badge gray">No Changes</span>');
});

test('renderChangeBadges gives an orange badge for each changed category', function () {
    assert.equal(kit.renderChangeBadges(['Metadata', 'Images']),
        '<span class="base-badge orange">Metadata</span> <span class="base-badge orange">Images</span>');
});

test('renderRowInfo escapes the name and the line, and sets the name as the tooltip', function () {
    var html = kit.renderRowInfo('A <b>film</b>', 'Movies & more', {}, 'Synced');
    assert.ok(html.startsWith('<div class="serversync-item-info">'));
    assert.match(html, /class="serversync-item-name" title="A &lt;b&gt;film&lt;\/b&gt;">A &lt;b&gt;film&lt;\/b&gt;<\/div>/);
    assert.match(html, /class="serversync-item-path">Movies &amp; more<\/div>/);
    assert.doesNotMatch(html, /serversync-item-error/);
});

test('renderRowInfo shows the error preview only for an errored row', function () {
    var errored = kit.renderRowInfo('Film', 'Movies', { ErrorMessage: 'Disk <full>' }, 'Errored');
    assert.match(errored, /class="serversync-item-error" title="Disk &lt;full&gt;">Disk &lt;full&gt;<\/div>/);
    var synced = kit.renderRowInfo('Film', 'Movies', { ErrorMessage: 'Disk full' }, 'Synced');
    assert.doesNotMatch(synced, /serversync-item-error/);
});

test('renderRowInfo leaves out the second line when it is null and the tooltip when the title is empty', function () {
    var html = kit.renderRowInfo('Person', null, {}, 'Synced', '');
    assert.doesNotMatch(html, /serversync-item-path/);
    assert.match(html, /<div class="serversync-item-name">Person<\/div>/);
});

test('setHealthLastSync shows the relative time, and Never when there is none', function () {
    kit.setHealthLastSync('healthLastSync', new Date().toISOString());
    var el = view.querySelector('#healthLastSync');
    assert.equal(el.textContent, 'Just now');
    assert.equal(el.className, 'serversync-health-value');
    kit.setHealthLastSync('healthLastSync', null);
    assert.equal(el.textContent, 'Never');
});

test('setHealthCount colors a positive count as success and a zero count as a warning', function () {
    kit.setHealthCount('healthCount', 4);
    var el = view.querySelector('#healthCount');
    assert.equal(el.textContent, '4');
    assert.equal(el.className, 'serversync-health-value serversync-success');
    kit.setHealthCount('healthCount', 0);
    assert.equal(el.className, 'serversync-health-value serversync-warning');
});

test('setHealthLastSync and setHealthCount ignore an element that is not on the page', function () {
    kit.setHealthLastSync('missing', null);
    kit.setHealthCount('missing', 1);
});

test('showObjectVersion says where and when an object was last edited', async function () {
    responses.push({ Timestamp: new Date().toISOString(), IsThisServer: true });
    kit.showObjectVersion('version', { kind: 'History', localItemId: 'i1', localUserId: '' });
    await flush();
    assert.equal(view.querySelector('#version').textContent, 'Just now here');
    assert.equal(page.requests[page.requests.length - 1].url, 'Sync/Hints/Version?kind=History&localItemId=i1');
});

test('showObjectVersion names the other server, and says Not recorded when nobody edited the object', async function () {
    responses.push({ Timestamp: new Date().toISOString(), IsThisServer: false, ServerName: 'Alpha' });
    kit.showObjectVersion('version', { kind: 'Metadata' });
    await flush();
    assert.equal(view.querySelector('#version').textContent, 'Just now on Alpha');
    responses.push({});
    kit.showObjectVersion('version', { kind: 'Metadata' });
    await flush();
    assert.equal(view.querySelector('#version').textContent, 'Not recorded');
    responses.push(new Error('down'));
    kit.showObjectVersion('version', { kind: 'Metadata' });
    await flush();
    assert.equal(view.querySelector('#version').textContent, 'Not recorded');
});

test('showObjectVersion drops a reply that a newer request has replaced', async function () {
    var release;
    var first = new Promise(function (resolve) { release = resolve; });
    var calls = 0;
    globalThis.ApiClient.fetch = function () {
        calls++;
        if (calls === 1) return first;
        return Promise.resolve({ Timestamp: new Date().toISOString(), IsThisServer: false, ServerName: 'Second' });
    };
    kit.showObjectVersion('version', { kind: 'History' });
    kit.showObjectVersion('version', { kind: 'History' });
    await flush();
    release({ Timestamp: new Date().toISOString(), IsThisServer: false, ServerName: 'First' });
    await flush();
    assert.equal(view.querySelector('#version').textContent, 'Just now on Second');
});

// historySpec
// A spec like the History view's, for the module tests.
function historySpec() {
    return {
        name: 'History',
        prefix: 'history',
        containerId: 'historyItemsTableContainer',
        listEndpoint: 'HistoryItems',
        statusEndpoint: 'HistoryStatus',
        itemsEndpoint: 'HistoryItems',
        retryEndpoint: 'HistoryItems/Queue',
        modalId: 'historyItemDetailModal',
        modalButtonPrefix: 'btnHistoryModal',
        tasks: { refresh: { key: 'RefreshKey' }, sync: { key: 'SyncKey' } },
        taskNoun: 'history',
        singularNoun: 'Item',
        nounOne: 'item',
        nounMany: 'items',
        retryOne: 'history item',
        retryMany: 'history items',
        failNoun: 'items',
        columns: function () { return []; },
        statusRequest: function (item, status) { return { endpoint: 'HistoryItems/' + (status === 'Ignored' ? 'Ignore' : 'Queue'), body: { Ids: [item.Id] } }; }
    };
}

// fakeTable
// A table that records what the module asks of it.
function fakeTable(selected) {
    var log = [];
    return {
        log: log,
        getSelectedIds: function () { return selected || []; },
        clearSelection: function () { log.push('clear'); },
        reload: function () { log.push('reload'); return Promise.resolve(); },
        refresh: function () { log.push('refresh'); }
    };
}

test('a module builds its element ids from the view name', async function () {
    await setup(['syncStatus', 'historySyncedCount', 'historyStatusGroupSynced', 'btnRetryHistoryErrors']);
    var module = kit.createSyncTableModule(historySpec());
    module._showStatusCounts({ Synced: 7, Queued: 0, Errored: 2, Ignored: 0 });
    assert.equal(view.querySelector('#historySyncedCount').textContent, '7');
    assert.equal(view.querySelector('#historyStatusGroupSynced').getAttribute('title'), 'Synced: 7');
    assert.equal(view.querySelector('#btnRetryHistoryErrors').classList.contains('hidden'), false);
    module._showStatusCounts({ Synced: 7, Errored: 0 });
    assert.equal(view.querySelector('#btnRetryHistoryErrors').classList.contains('hidden'), true);
});

test('a bulk action posts the selected ids and reports how many were changed', async function () {
    await setup(['syncStatus']);
    var module = kit.createSyncTableModule(historySpec());
    module.table = fakeTable(['a', 'b']);
    module.bulkIgnore();
    await flush();
    var post = page.requests.find(function (r) { return r.type === 'POST'; });
    assert.equal(post.url, 'Sync/HistoryItems/Ignore');
    assert.deepEqual(JSON.parse(post.data), { Ids: ['a', 'b'] });
    assert.equal(statusText(view.querySelector('#syncStatus')), '2 items ignored.');
    assert.ok(module.table.log.includes('clear'));
});

test('a bulk action with nothing selected does nothing', async function () {
    await setup(['syncStatus']);
    var module = kit.createSyncTableModule(historySpec());
    module.table = fakeTable([]);
    module.bulkQueue();
    await flush();
    assert.equal(page.requests.filter(function (r) { return r.type === 'POST'; }).length, 0);
});

test('a bulk action that fails reports it as an error', async function () {
    await setup(['syncStatus']);
    var module = kit.createSyncTableModule(historySpec());
    module.table = fakeTable(['a']);
    responses.push(new Error('refused'));
    module.bulkQueue();
    await flush();
    var el = view.querySelector('#syncStatus');
    assert.equal(statusText(el), 'Could not queue the items.');
    assert.ok(el.classList.contains('base-status-bad'));
});

test('retryErrors asks the server to requeue every errored row and says how many it queued', async function () {
    await setup(['syncStatus']);
    var module = kit.createSyncTableModule(historySpec());
    module.table = fakeTable([]);
    responses.push({ Updated: 2 });
    await module.retryErrors();
    var post = page.requests.find(function (r) { return r.type === 'POST'; });
    assert.equal(post.url, 'Sync/HistoryItems/Queue');
    assert.deepEqual(JSON.parse(post.data), { Ids: [], Status: 'Errored' });
    assert.equal(statusText(view.querySelector('#syncStatus')), '2 errored history items queued for retry.');
});

test('retryErrors says so when no row was errored, and when the request fails', async function () {
    await setup(['syncStatus']);
    var module = kit.createSyncTableModule(historySpec());
    module.table = fakeTable([]);
    responses.push({ Updated: 0 });
    await module.retryErrors();
    assert.equal(statusText(view.querySelector('#syncStatus')), 'No errored history items to retry.');
    responses.push(new Error('refused'));
    await module.retryErrors();
    assert.equal(statusText(view.querySelector('#syncStatus')), 'Could not retry the errored history items.');
});

test('retryErrors counts a single row with the singular noun', async function () {
    await setup(['syncStatus']);
    var module = kit.createSyncTableModule(historySpec());
    module.table = fakeTable([]);
    responses.push({ Updated: 1 });
    await module.retryErrors();
    assert.equal(statusText(view.querySelector('#syncStatus')), '1 errored history item queued for retry.');
});

test('setModalStatus posts the status change, closes the modal, and reports it', async function () {
    await setup(['syncStatus']);
    var module = kit.createSyncTableModule(historySpec());
    module.table = fakeTable([]);
    module._dialog = { isOpen: function () { return false; }, close: function () {} };
    module.currentModalItem = { Id: 9 };
    module.setModalStatus('Queued');
    await flush();
    var post = page.requests.find(function (r) { return r.type === 'POST'; });
    assert.equal(post.url, 'Sync/HistoryItems/Queue');
    assert.deepEqual(JSON.parse(post.data), { Ids: [9] });
    assert.equal(statusText(view.querySelector('#syncStatus')), 'Item status updated to Queued.');
    assert.equal(module.currentModalItem, null);
});

test('setModalStatus reports an error when the change is refused', async function () {
    await setup(['syncStatus']);
    var module = kit.createSyncTableModule(historySpec());
    module.table = fakeTable([]);
    module.currentModalItem = { Id: 9 };
    responses.push(new Error('refused'));
    module.modalIgnore();
    await flush();
    assert.equal(statusText(view.querySelector('#syncStatus')), 'Could not update the item status.');
});

test('a task that cannot start reports it and puts the button back', async function () {
    await setup(['syncStatus']);
    var button = page.dom.el('button', { id: 'btnRefreshHistoryItems' }, [page.dom.el('span')]);
    view.appendChild(button);
    var module = kit.createSyncTableModule(historySpec());
    responses.push(new Error('refused'));
    module.runTask('refresh');
    assert.equal(button.disabled, true);
    assert.equal(button.querySelector('span').textContent, 'Starting…');
    await flush();
    assert.equal(button.disabled, false);
    assert.equal(button.querySelector('span').textContent, 'Refresh');
    assert.equal(statusText(view.querySelector('#syncStatus')), 'Could not start the history refresh task.');
});

test('a spec method replaces the shared method of the same name', async function () {
    await setup([]);
    var spec = historySpec();
    spec.methods = { bulkIgnore: function () { return 'custom'; } };
    var module = kit.createSyncTableModule(spec);
    assert.equal(module.bulkIgnore(), 'custom');
});

test('openSyncView reads the server name and the config, then initializes and loads the module', async function () {
    await setup([]);
    var order = [];
    var module = {
        init: function (config) { order.push('init:' + JSON.stringify(config)); },
        loadAll: function () { order.push('loadAll'); return Promise.resolve(); }
    };
    globalThis.ApiClient.getPluginConfiguration = function () { return Promise.resolve({ Servers: [] }); };
    await kit.openSyncView(module);
    assert.deepEqual(order, ['init:{"Servers":[]}', 'loadAll']);
    assert.equal(shared.localServerName, 'Test Server');
});

test('openSyncView still initializes the module when the config cannot be read', async function () {
    await setup([]);
    var inits = [];
    var module = { init: function (config) { inits.push(config); }, loadAll: function () { return Promise.resolve(); } };
    globalThis.ApiClient.getPluginConfiguration = function () { return Promise.reject(new Error('down')); };
    await kit.openSyncView(module);
    assert.deepEqual(inits, [null]);
});

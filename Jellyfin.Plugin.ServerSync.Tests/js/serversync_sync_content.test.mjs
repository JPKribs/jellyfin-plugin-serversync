// serversync_sync_content.test.mjs
// Tests for serversync_sync_content.js: the status words and classes of a content row, which filters show for the
// approval settings, and the bulk and modal actions that ask before they delete.

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { createSyncContext, loadScript, flush } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};

var IDS = ['statusGroupPendingDownload', 'statusGroupPendingReplacement', 'statusGroupPendingDeletion', 'statusGroupDeleting', 'pendingStatusRow'];

var env;
var module;
var questions;
var answer;

// build
// Creates the Content view's module over a view with the elements its filters toggle.
async function build() {
    questions = [];
    answer = true;
    env = await createSyncContext(IDS, {
        confirmDialog: function (message, opts) { questions.push({ message: message, opts: opts }); return Promise.resolve(answer); }
    });
    var mod = await loadScript('serversync_sync_content.js');
    module = mod.createContentSyncTableModule(env.ctx, env.kit);
    module.table = {
        selected: [],
        items: [],
        getSelectedIds: function () { return this.selected; },
        getItems: function () { return this.items; },
        clearSelection: function () {},
        reload: function () { return Promise.resolve(); },
        refresh: function () {},
        filterOptions: {},
        setFilterOptionVisible: function (id, visible) { this.filterOptions[id] = visible; }
    };
}

beforeEach(build);

test('getDisplayStatus says Synced for a synced copy and Linked for a synced link', function () {
    assert.equal(module.getDisplayStatus({ Status: 'Synced', StorageMode: 'Copy' }), 'Synced');
    assert.equal(module.getDisplayStatus({ Status: 'Synced', StorageMode: 'Link' }), 'Linked');
    assert.equal(module.getDisplayStatus({ Status: 'Synced', StorageMode: 'Link', Cached: true }), 'Linked (Cached)');
    assert.equal(module.getDisplayStatus({ Status: 'Synced', StorageMode: 'Promoting' }), 'Linked (Copy Next)');
});

test('getDisplayStatus adds the storage change to a row that is not synced yet', function () {
    assert.equal(module.getDisplayStatus({ Status: 'Queued', StorageMode: 'Link' }), 'Queued (Link)');
    assert.equal(module.getDisplayStatus({ Status: 'Queued', StorageMode: 'Link', Cached: true }), 'Queued (Link, Cached)');
    assert.equal(module.getDisplayStatus({ Status: 'Queued', StorageMode: 'Promoting' }), 'Queued (Link to Copy)');
    assert.equal(module.getDisplayStatus({ Status: 'Errored', StorageMode: 'Unkeeping' }), 'Errored (Copy to Link)');
    assert.equal(module.getDisplayStatus({ Status: 'Ignored', StorageMode: 'Copy' }), 'Ignored');
});

test('getDisplayStatus names the kind of pending row', function () {
    assert.equal(module.getDisplayStatus({ Status: 'Pending', PendingType: 'Deletion', StorageMode: 'Copy' }), 'Pending Deletion');
    assert.equal(module.getDisplayStatus({ Status: 'Pending', PendingType: 'Download', StorageMode: 'Link' }), 'Pending Download (Link)');
});

test('getStatusClass gives the badge state a status maps to', function () {
    assert.equal(module.getStatusClass({ Status: 'Synced' }), 'Synced');
    assert.equal(module.getStatusClass({ Status: 'Pending' }), 'Pending');
    assert.equal(module.getStatusClass({ Status: 'Pending', PendingType: 'Replacement' }), 'Pending-Replacement');
});

test('updatePendingFilterVisibility shows the approval states only while their setting needs approval', function () {
    module.updatePendingFilterVisibility({ DownloadNewContentMode: 'RequireApproval', ReplaceExistingContentMode: 'Enabled', DeleteMissingContentMode: 'Disabled' });
    assert.deepEqual(module.table.filterOptions, { optPendingDownload: true, optPendingReplacement: false, optPendingDeletion: false, optDeleting: false });
    var hidden = function (id) { return env.view.querySelector('#' + id).classList.contains('hidden'); };
    assert.equal(hidden('statusGroupPendingDownload'), false);
    assert.equal(hidden('statusGroupPendingReplacement'), true);
    assert.equal(hidden('statusGroupPendingDeletion'), true);
    assert.equal(hidden('pendingStatusRow'), false);
});

test('updatePendingFilterVisibility shows the deletion states whenever deleting is on at all', function () {
    module.updatePendingFilterVisibility({ DeleteMissingContentMode: 'RequireApproval' });
    assert.equal(module.table.filterOptions.optPendingDeletion, true);
    assert.equal(module.table.filterOptions.optDeleting, true);
    assert.equal(env.view.querySelector('#pendingStatusRow').classList.contains('hidden'), false);
});

test('updatePendingFilterVisibility hides the whole pending row when nothing needs approval', function () {
    module.updatePendingFilterVisibility({});
    assert.equal(env.view.querySelector('#pendingStatusRow').classList.contains('hidden'), true);
});

test('the pending filters read Pending Replacement and Pending Deletion', async function () {
    var options = [];
    var fresh = await createSyncContext([], {
        createPaginatedTable: function (view, shared, opts) { options = opts.filters.options; return { getBulkActionsContainer: function () { return null; } }; }
    });
    var mod = await loadScript('serversync_sync_content.js');
    var content = mod.createContentSyncTableModule(fresh.ctx, fresh.kit);
    content.init({});
    var labels = options.map(function (o) { return o.label; });
    assert.ok(labels.includes('Pending Replacement'));
    assert.ok(labels.includes('Pending Deletion'));
    assert.equal(labels.includes('Pending Replace'), false);
});

test('the filter turns each choice into the query the server reads', async function () {
    var filters = null;
    var fresh = await createSyncContext([], {
        createPaginatedTable: function (view, shared, opts) { filters = opts.filters; return { getBulkActionsContainer: function () { return null; } }; }
    });
    var mod = await loadScript('serversync_sync_content.js');
    mod.createContentSyncTableModule(fresh.ctx, fresh.kit).init({});
    assert.deepEqual(filters.buildParams('Cached'), { cached: true });
    assert.deepEqual(filters.buildParams('Synced'), { status: 'Synced', storage: 'Copy' });
    assert.deepEqual(filters.buildParams('Linked'), { status: 'Synced', storage: 'Link' });
    assert.deepEqual(filters.buildParams('Pending:Replacement'), { status: 'Pending', pendingType: 'Replacement' });
    assert.deepEqual(filters.buildParams('Queued'), { status: 'Queued' });
});

test('bulkQueue leaves out rows that are pending deletion', async function () {
    module.table.selected = ['a', 'b'];
    module.table.items = [{ SourceItemId: 'a', Status: 'Pending', PendingType: 'Deletion' }, { SourceItemId: 'b', Status: 'Queued' }];
    module.bulkQueue();
    await flush();
    var post = env.page.requests.find(function (r) { return r.url === 'Sync/QueueItems'; });
    assert.deepEqual(JSON.parse(post.data), { SourceItemIds: ['b'] });
    assert.equal(env.view.querySelector('#syncStatus').textContent, '1 item queued.');
});

test('bulkQueue says so when every selected row is pending deletion', async function () {
    module.table.selected = ['a'];
    module.table.items = [{ SourceItemId: 'a', Status: 'Pending', PendingType: 'Deletion' }];
    module.bulkQueue();
    await flush();
    assert.equal(env.page.requests.filter(function (r) { return r.type === 'POST'; }).length, 0);
    assert.equal(env.view.querySelector('#syncStatus').textContent, 'No items to queue. Items pending deletion cannot be queued.');
});

test('bulkDelete asks a destructive question first and deletes only when it is confirmed', async function () {
    module.table.selected = ['a', 'b'];
    answer = false;
    module.bulkDelete();
    await flush();
    assert.equal(questions.length, 1);
    assert.equal(questions[0].message, 'Delete 2 items from this server? This cannot be undone.');
    assert.equal(questions[0].opts.title, 'Delete Items');
    assert.equal(questions[0].opts.confirmText, 'Delete');
    assert.equal(questions[0].opts.destructive, true);
    assert.equal(env.page.requests.filter(function (r) { return r.type === 'POST'; }).length, 0);
    answer = true;
    env.responses.push({ Deleted: 2 });
    module.bulkDelete();
    await flush();
    var post = env.page.requests.find(function (r) { return r.url === 'Sync/DeleteLocalItems'; });
    assert.deepEqual(JSON.parse(post.data), { SourceItemIds: ['a', 'b'] });
    assert.equal(env.view.querySelector('#syncStatus').textContent, 'Deleted 2 items.');
});

test('bulkDelete with nothing selected asks nothing', async function () {
    module.bulkDelete();
    await flush();
    assert.equal(questions.length, 0);
});

test('modalDelete names the file in the question and deletes only when it is confirmed', async function () {
    module._dialog = { isOpen: function () { return false; }, close: function () {} };
    module.currentModalItem = { SourceItemId: 'x', LocalPath: '/local/Film.mkv', SourcePath: '/source/Film.mkv' };
    answer = false;
    module.modalDelete();
    await flush();
    assert.equal(questions[0].message, 'Delete "Film.mkv" from this server? This cannot be undone.');
    assert.equal(questions[0].opts.title, 'Delete File');
    assert.equal(env.page.requests.filter(function (r) { return r.type === 'POST'; }).length, 0);
    answer = true;
    module.modalDelete();
    await flush();
    assert.equal(env.page.requests.filter(function (r) { return r.url === 'Sync/DeleteLocalItems'; }).length, 1);
    assert.equal(env.view.querySelector('#syncStatus').textContent, 'Item deleted.');
});

test('keepMessage says how many links were moved from the cache and how many will be copied', function () {
    assert.equal(module.keepMessage({ Promoted: 2, FromCache: 0 }), '2 links will be copied on the next sync.');
    assert.equal(module.keepMessage({ Promoted: 3, FromCache: 1 }), '1 cached link is being moved into the library now, and 2 will be downloaded.');
    assert.equal(module.keepMessage({ Promoted: 2, FromCache: 2, Skipped: 1 }), '2 cached links are being moved into the library now, 1 item was not a link.');
});

test('bulkMarkSynced says how many items were marked and how many files were missing', async function () {
    module.table.selected = ['a', 'b', 'c'];
    env.responses.push({ Synced: 2, NotFound: 1 });
    module.bulkMarkSynced();
    await flush();
    assert.equal(env.view.querySelector('#syncStatus').textContent, '2 items marked as synced, 1 local file not found.');
});

test('the controller initializes the table once and loads its rows', async function () {
    var mod = await loadScript('serversync_sync_content.js');
    var calls = [];
    var fake = {
        init: function () { calls.push('init'); },
        loadAll: function () { calls.push('loadAll'); },
        updatePendingFilterVisibility: function () { calls.push('visibility'); }
    };
    mod.createContentPageController(env.ctx, fake).init();
    await flush();
    assert.deepEqual(calls.slice(0, 1), ['init']);
    assert.ok(calls.includes('loadAll'));
    assert.ok(calls.includes('visibility'));
});

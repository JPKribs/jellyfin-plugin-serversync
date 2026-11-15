// serversync_sync_history.test.mjs
// Tests for serversync_sync_history.js: how a history value reads in the detail modal, which row is marked as
// changed, and how a row finds the user mapping it was synced through.

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { createSyncContext, loadScript } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};

var env;
var module;

beforeEach(async function () {
    env = await createSyncContext(['historyModalPlayed', 'historyModalRowPlayed']);
    var mod = await loadScript('serversync_sync_history.js');
    module = mod.createHistorySyncTableModule(env.ctx, env.kit);
});

test('formatPosition shows minutes and seconds, and hours when there are any', function () {
    assert.equal(module.formatPosition(0), '-');
    assert.equal(module.formatPosition(null), '-');
    assert.equal(module.formatPosition(65 * 10000000), '1:05');
    assert.equal(module.formatPosition(3725 * 10000000), '1:02:05');
});

test('setTableValue writes Yes and No for a flag, a dash for an unknown one, and plain numbers', function () {
    var el = env.view.querySelector('#historyModalPlayed');
    module.setTableValue('historyModalPlayed', true, 'bool');
    assert.equal(el.textContent, 'Yes');
    module.setTableValue('historyModalPlayed', false, 'favorite');
    assert.equal(el.textContent, 'No');
    module.setTableValue('historyModalPlayed', null, 'bool');
    assert.equal(el.textContent, '-');
    module.setTableValue('historyModalPlayed', 3, 'number');
    assert.equal(el.textContent, '3');
    module.setTableValue('historyModalPlayed', undefined, 'number');
    assert.equal(el.textContent, '-');
    module.setTableValue('historyModalPlayed', 90 * 10000000, 'position');
    assert.equal(el.textContent, '1:30');
});

test('setTableValue ignores an element that is not on the page', function () {
    module.setTableValue('missing', true, 'bool');
});

test('formatDate gives the relative time, and a dash when there is no date', function () {
    assert.equal(module.formatDate(''), '-');
    assert.equal(module.formatDate(new Date().toISOString()), 'Just now');
});

test('highlightChangedRow marks a row whose merged value differs from the local one', function () {
    var row = env.view.querySelector('#historyModalRowPlayed');
    module.highlightChangedRow('historyModalRowPlayed', true, false);
    assert.ok(row.classList.contains('serversync-modal-changed-row'));
    module.highlightChangedRow('historyModalRowPlayed', true, true);
    assert.equal(row.classList.contains('serversync-modal-changed-row'), false);
    module.highlightChangedRow('historyModalRowPlayed', undefined, null);
    assert.equal(row.classList.contains('serversync-modal-changed-row'), false);
});

test('findUserMapping matches the source user within the row\'s own server, ignoring dashes and case', function () {
    module.currentConfig = { Servers: [
        { Key: 'a', Url: 'u', ApiKey: 'k', UserMappings: [{ SourceUserId: 'AA-BB', LocalUserId: 'l1', SourceUserName: 'Ann' }] },
        { Key: 'b', Url: 'u', ApiKey: 'k', UserMappings: [{ SourceUserId: 'aabb', LocalUserId: 'l2', SourceUserName: 'Other' }] }
    ] };
    assert.equal(module.findUserMapping({ ServerKey: 'a', SourceUserId: 'aabb' }).SourceUserName, 'Ann');
    assert.equal(module.findUserMapping({ ServerKey: 'b', SourceUserId: 'AA-BB' }).SourceUserName, 'Other');
});

test('findUserMapping falls back to the local user only when the row has no source user', function () {
    module.currentConfig = { Servers: [{ Key: 'a', Url: 'u', ApiKey: 'k', UserMappings: [{ SourceUserId: 's1', LocalUserId: 'L-1', SourceUserName: 'Ann' }] }] };
    assert.equal(module.findUserMapping({ ServerKey: 'a', LocalUserId: 'l1' }).SourceUserName, 'Ann');
    assert.equal(module.findUserMapping({ ServerKey: 'a', SourceUserId: 'nobody', LocalUserId: 'l1' }), null);
    assert.equal(module.findUserMapping({ ServerKey: 'a' }), null);
});

test('findUserMapping gives a row with no server key the first scan server, and null without a config', function () {
    module.currentConfig = { Servers: [
        { Key: 'push', Url: 'u', ApiKey: 'k', Mode: 'Push', UserMappings: [{ SourceUserId: 's1', SourceUserName: 'Push' }] },
        { Key: 'pull', Url: 'u', ApiKey: 'k', UserMappings: [{ SourceUserId: 's1', SourceUserName: 'Pull' }] }
    ] };
    assert.equal(module.findUserMapping({ SourceUserId: 's1' }).SourceUserName, 'Pull');
    module.currentConfig = null;
    assert.equal(module.findUserMapping({ SourceUserId: 's1' }), null);
});

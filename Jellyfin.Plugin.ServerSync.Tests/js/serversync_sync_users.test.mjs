// serversync_sync_users.test.mjs
// Tests for serversync_sync_users.js: how a user setting value reads, and the change badges the detail modal
// shows for the categories that are on.

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { createSyncContext, loadScript } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};

var env;
var module;

beforeEach(async function () {
    env = await createSyncContext(['userSyncModalChangesSummary']);
    var mod = await loadScript('serversync_sync_users.js');
    module = mod.createUserSyncTableModule(env.ctx, env.kit);
    module.currentConfig = {};
});

test('_formatValue writes a dash for nothing, Yes and No for a flag, and a list as text', function () {
    assert.equal(module._formatValue(null), '-');
    assert.equal(module._formatValue(undefined), '-');
    assert.equal(module._formatValue(true), 'Yes');
    assert.equal(module._formatValue(false), 'No');
    assert.equal(module._formatValue([]), '-');
    assert.equal(module._formatValue(['a', 'b']), 'a, b');
    assert.equal(module._formatValue(7), '7');
});

test('buildChangesSummary gives a badge for each category that is on, orange when it changed', function () {
    module.buildChangesSummary({ ConfigurationItem: { HasChanges: true }, PolicyItem: { HasChanges: false }, ProfileImageItem: null });
    var html = env.view.querySelector('#userSyncModalChangesSummary').innerHTML;
    assert.match(html, /<span class="base-badge orange">Configuration: Changes<\/span>/);
    assert.match(html, /<span class="base-badge green">Policy: Synced<\/span>/);
    assert.match(html, /<span class="base-badge green">Image: Synced<\/span>/);
});

test('buildChangesSummary leaves out the categories that are turned off', function () {
    module.currentConfig = { UserSyncPolicy: false, UserSyncConfiguration: false };
    module.buildChangesSummary({ ProfileImageItem: { HasChanges: true } });
    assert.equal(env.view.querySelector('#userSyncModalChangesSummary').innerHTML, '<span class="base-badge orange">Image: Changes</span>');
});

test('_addPropertyRows adds a row for each setting and marks the ones that differ', function () {
    var rows = [];
    var tbody = { appendChild: function (row) { rows.push(row); } };
    module._addPropertyRows(tbody, { SourceValue: JSON.stringify({ A: 1, B: 2 }), LocalValue: JSON.stringify({ A: 1, B: 3 }), MergedValue: JSON.stringify({ A: 1, B: 2 }) });
    assert.equal(rows.length, 2);
    assert.equal(rows[0].className, '');
    assert.equal(rows[1].className, 'serversync-modal-changed-row');
});

test('_addPropertyRows shows a value that is not JSON as one row', function () {
    var rows = [];
    module._addPropertyRows({ appendChild: function (row) { rows.push(row); } }, { SourceValue: 'plain', LocalValue: 'other', MergedValue: 'plain', HasChanges: true, PropertyCategory: 'Thing' });
    assert.equal(rows.length, 1);
    assert.equal(rows[0].className, 'serversync-modal-changed-row');
});

test('_createDisabledRow escapes the category and the message', function () {
    var row = module._createDisabledRow('<b>Policy</b>', 'Not available');
    assert.equal(row.className, 'serversync-modal-disabled-row');
    assert.doesNotMatch(row.children[0].innerHTML, /<b>/);
});

test('hasSynced is false for nothing, the default date, and text that is no date, and true for a real date', async function () {
    var mod = await loadScript('serversync_sync_users.js');
    assert.equal(mod.hasSynced(null), false);
    assert.equal(mod.hasSynced(''), false);
    assert.equal(mod.hasSynced('0001-01-01T00:00:00'), false);
    assert.equal(mod.hasSynced('not a date'), false);
    assert.equal(mod.hasSynced('2026-11-14T20:00:00Z'), true);
});

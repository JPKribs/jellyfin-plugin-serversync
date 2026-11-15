// serversync_sync_people.test.mjs
// Tests for serversync_sync_people.js: how a person's field is written as plain text, and the rows the detail
// modal builds.

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { createSyncContext, loadScript } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};

var env;
var module;

beforeEach(async function () {
    env = await createSyncContext([]);
    var mod = await loadScript('serversync_sync_people.js');
    module = mod.createPeopleSyncTableModule(env.ctx, env.kit);
});

test('_formatFieldValue writes plain text and does not escape it, since the cells use textContent', function () {
    assert.equal(module._formatFieldValue('Tom & Jerry', {}), 'Tom & Jerry');
    assert.equal(module._formatFieldValue(null, {}), '-');
    assert.equal(module._formatFieldValue(true, {}), 'Yes');
    assert.equal(module._formatFieldValue(false, {}), 'No');
    assert.equal(module._formatFieldValue([], {}), '-');
    assert.equal(module._formatFieldValue(['a', 'b'], {}), 'a, b');
    assert.equal(module._formatFieldValue({ a: 1 }, {}), '{"a":1}');
});

test('_formatFieldValue cuts a long overview with an ellipsis and keeps a bad date as it came', function () {
    assert.equal(module._formatFieldValue('x'.repeat(250), { truncate: true }), 'x'.repeat(200) + '…');
    assert.equal(module._formatFieldValue('not a date', { isDate: true }), 'not a date');
    assert.equal(module._formatFieldValue('2020-05-04T12:00:00Z', { isDate: true }), new Date('2020-05-04T12:00:00Z').toLocaleDateString());
});

test('_addComparisonRow fills four cells and marks a changed row', function () {
    var rows = [];
    var tbody = { appendChild: function (row) { rows.push(row); } };
    module._addComparisonRow(tbody, 'Name', 'Ann', 'Anne', true);
    module._addComparisonRow(tbody, 'Born', '', null, false);
    assert.equal(rows[0].className, 'serversync-modal-changed-row');
    assert.deepEqual(rows[0].children.map(function (c) { return c.textContent; }), ['Name', 'Ann', 'Anne', 'Ann']);
    assert.deepEqual(rows[1].children.map(function (c) { return c.textContent; }), ['Born', '-', '-', '-']);
    assert.equal(rows[1].className, '');
});

test('_addChangeDetailRow adds a note row, and nothing when there is no reason', function () {
    var rows = [];
    var tbody = { appendChild: function (row) { rows.push(row); } };
    module._addChangeDetailRow(tbody, '');
    assert.equal(rows.length, 0);
    module._addChangeDetailRow(tbody, 'Overview differs');
    assert.equal(rows[0].children[0].textContent, 'Changed: Overview differs');
});

test('_addSectionHeader adds a header row that spans every column', function () {
    var rows = [];
    module._addSectionHeader({ appendChild: function (row) { rows.push(row); } }, 'Images');
    assert.equal(rows[0].className, 'serversync-modal-section-header');
    assert.equal(rows[0].children[0].colSpan, 4);
    assert.equal(rows[0].children[0].textContent, 'Images');
});

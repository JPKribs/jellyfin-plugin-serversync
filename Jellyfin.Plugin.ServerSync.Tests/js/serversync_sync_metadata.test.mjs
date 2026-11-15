// serversync_sync_metadata.test.mjs
// Tests for serversync_sync_metadata.js: how a metadata value is written and compared, the property rows of the
// detail modal, and the change badges it shows for the categories that are on.

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { createSyncContext, loadScript } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};

var env;
var module;

beforeEach(async function () {
    env = await createSyncContext(['metadataSyncModalChangesSummary', 'metadataSyncModalTableBody']);
    var mod = await loadScript('serversync_sync_metadata.js');
    module = mod.createMetadataSyncTableModule(env.ctx, env.kit);
    module.currentConfig = {};
});

test('isEmpty is true for null, undefined, an empty string, and an empty list', function () {
    [null, undefined, '', []].forEach(function (v) { assert.equal(module.isEmpty(v), true); });
    [0, false, 'a', ['a']].forEach(function (v) { assert.equal(module.isEmpty(v), false); });
});

test('formatMetadataValue escapes text, writes Yes and No for a flag, and a dash for an empty value', function () {
    assert.equal(module.formatMetadataValue('<b>x</b>', {}), '&lt;b&gt;x&lt;/b&gt;');
    assert.equal(module.formatMetadataValue(true, { isBoolean: true }), 'Yes');
    assert.equal(module.formatMetadataValue(false, { isBoolean: true }), 'No');
    assert.equal(module.formatMetadataValue(null, { isBoolean: true }), '-');
    assert.equal(module.formatMetadataValue('', {}), '-');
});

test('formatMetadataValue joins a list, formats a date, and cuts a long overview with an ellipsis', function () {
    assert.equal(module.formatMetadataValue(['a', '<b>'], { isArray: true }), 'a, &lt;b&gt;');
    assert.equal(module.formatMetadataValue('2020-05-04T10:00:00Z', { isDate: true }), '2020-05-04');
    var long = 'x'.repeat(150);
    assert.equal(module.formatMetadataValue(long, { truncate: true }), 'x'.repeat(100) + '…');
    assert.equal(module.formatMetadataValue('short', { truncate: true }), 'short');
});

test('normalizeForComparison treats lists as sets and dates as days, so a reordered list is not a change', function () {
    assert.equal(module.normalizeForComparison(['b', 'a'], { isArray: true }), module.normalizeForComparison(['a', 'b'], { isArray: true }));
    assert.equal(module.normalizeForComparison('2020-05-04T01:00:00Z', { isDate: true }), module.normalizeForComparison('2020-05-04T23:00:00Z', { isDate: true }));
    assert.equal(module.normalizeForComparison(true, { isBoolean: true }), 'true');
    assert.equal(module.normalizeForComparison(undefined, { isBoolean: true }), '');
    assert.equal(module.normalizeForComparison(null, {}), '');
    assert.equal(module.normalizeForComparison(5, {}), '5');
});

test('buildChangeDetailRow escapes the reason, and gives nothing when there is none', function () {
    assert.equal(module.buildChangeDetailRow(''), '');
    assert.match(module.buildChangeDetailRow('Title <x>'), /Changed: Title &lt;x&gt;/);
});

test('buildArrayComparisonRow marks the count and the items when they differ', function () {
    var html = module.buildArrayComparisonRow('Genres', ['Drama'], ['Drama', 'Comedy']);
    var rows = html.split('</tr>').filter(Boolean);
    assert.equal(rows.length, 2);
    assert.match(rows[0], /serversync-modal-changed-row/);
    assert.match(rows[0], /Count/);
    assert.match(rows[1], /serversync-modal-changed-row/);
});

test('buildArrayComparisonRow marks nothing for the same items in another order', function () {
    var html = module.buildArrayComparisonRow('Tags', ['b', 'a'], ['a', 'b']);
    assert.doesNotMatch(html, /serversync-modal-changed-row/);
});

test('buildArrayComparisonRow escapes the items', function () {
    var html = module.buildArrayComparisonRow('Tags', ['<i>'], []);
    assert.doesNotMatch(html, /<i>/);
    assert.match(html, /&lt;i&gt;/);
});

test('buildPeopleComparisonRow compares the names of the people on each side', function () {
    var html = module.buildPeopleComparisonRow([{ Name: 'Ann' }, { Name: 'Bo' }], [{ Name: 'Ann' }]);
    assert.match(html, /serversync-modal-changed-row/);
    assert.match(html, /Ann, Bo/);
    assert.doesNotMatch(module.buildPeopleComparisonRow([{ Name: 'Ann' }], [{ Name: 'Ann' }]), /serversync-modal-changed-row/);
});

test('buildImagesRows lists every image type from either side and says when there are none', function () {
    var html = module.buildImagesRows({ Primary: [{ Size: 2048 }] }, { Primary: [{ Size: 2048 }], Backdrop: [{ Size: 1024 }] });
    assert.match(html, /Backdrop/);
    assert.match(html, /Primary/);
    assert.match(html, /serversync-modal-changed-row/);
    assert.match(module.buildImagesRows(null, null), /No images/);
});

test('buildImagesRows marks no row when both sides hold the same images', function () {
    var html = module.buildImagesRows({ Primary: [{ Size: 2048 }] }, { Primary: [{ Size: 2048 }] });
    assert.doesNotMatch(html, /serversync-modal-changed-row/);
});

test('buildCoreMetadataRows marks a changed field, escapes values, and lists provider ids', function () {
    var html = module.buildCoreMetadataRows({ Name: 'A <b>', ProviderIds: { Imdb: 'tt1' } }, { Name: 'A', ProviderIds: { Imdb: 'tt2' } });
    assert.match(html, /A &lt;b&gt;/);
    assert.match(html, /serversync-modal-changed-row/);
    assert.match(html, />Imdb</);
    assert.match(module.buildCoreMetadataRows({}, {}), /Provider IDs/);
});

test('buildChangesSummary gives one badge for each category that is on, orange when it changed', function () {
    module.currentConfig = { MetadataSyncPeople: true };
    module.buildChangesSummary({ HasMetadataChanges: true, HasImagesChanges: false, HasPeopleChanges: false, HasStudiosChanges: true,
        SourceMetadataValue: JSON.stringify({ Genres: ['A'], Tags: ['t'] }), LocalMetadataValue: JSON.stringify({ Genres: ['B'], Tags: ['t'] }) });
    var html = env.view.querySelector('#metadataSyncModalChangesSummary').innerHTML;
    assert.match(html, /<span class="jpk-badge orange">Metadata: Changes<\/span>/);
    assert.match(html, /<span class="jpk-badge orange">Genres: Changes<\/span>/);
    assert.match(html, /<span class="jpk-badge green">Tags: Synced<\/span>/);
    assert.match(html, /<span class="jpk-badge green">Images: Synced<\/span>/);
    assert.match(html, /<span class="jpk-badge green">People: Synced<\/span>/);
    assert.match(html, /<span class="jpk-badge orange">Studios: Changes<\/span>/);
});

test('buildChangesSummary leaves out the categories that are turned off', function () {
    module.currentConfig = { MetadataSyncMetadata: false, MetadataSyncGenres: false, MetadataSyncTags: false, MetadataSyncStudios: false, MetadataSyncImages: false };
    module.buildChangesSummary({});
    assert.equal(env.view.querySelector('#metadataSyncModalChangesSummary').innerHTML, '');
});

test('buildPropertyTable says no category is on when every one is off', function () {
    module.currentConfig = { MetadataSyncMetadata: false, MetadataSyncGenres: false, MetadataSyncTags: false, MetadataSyncStudios: false, MetadataSyncImages: false };
    module.buildPropertyTable({});
    assert.match(env.view.querySelector('#metadataSyncModalTableBody').innerHTML, /No sync categories are on\./);
});

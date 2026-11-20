// serversync_sync_queue.test.mjs
// Tests for serversync_sync_queue.js: the labels and times a change row shows, the rows built from the hint
// queues, the list markup, the filters, and the actions on failed and discarded changes.

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { createSyncContext, loadScript, flush, statusText } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};
globalThis.setInterval = function () { return 1; };
globalThis.clearInterval = function () {};

var QUEUE_IDS = ['queuePendingCount', 'queueSentCount', 'queueFailedCount', 'queueFailedActions', 'queueInboundCount',
    'queueGatheringCount', 'queuePeerRow', 'queuePeers', 'queueNoPeers', 'queueUnmatched', 'queueFilters', 'queueList', 'queueEmpty'];

var env;
var queue;
var confirmAnswer;
var realNow = Date.now;

// build
// Creates a queue module over a view that holds every element the queue draws into.
async function build() {
    confirmAnswer = true;
    env = await createSyncContext(QUEUE_IDS, { confirmDialog: function () { return Promise.resolve(confirmAnswer); } });
    env.view.querySelector('#queueUnmatched').appendChild(env.page.dom.el('span', { id: 'queueUnmatchedText' }));
    var mod = await loadScript('serversync_sync_queue.js');
    queue = mod.createQueueModule(env.ctx);
}

beforeEach(async function () {
    Date.now = realNow;
    await build();
});

test('_what names the kind of change in words, by name or by number', function () {
    assert.equal(queue._what('History'), 'watch history');
    assert.equal(queue._what(3), 'new file');
    assert.equal(queue._what('Playlists'), 'playlist');
    assert.equal(queue._what('Strange'), 'Strange');
});

test('_kindLabel and _kindLabelTitle name a kind for the list and for a badge', function () {
    assert.equal(queue._kindLabel('People'), 'people');
    assert.equal(queue._kindLabel('Unknown'), 'unknown');
    assert.equal(queue._kindLabelTitle(2), 'Person');
    assert.equal(queue._kindLabelTitle('History'), 'Watch history');
    assert.equal(queue._kindLabelTitle('Other'), 'Other');
});

test('_kindColor gives each kind one badge color and gray to an unknown one', function () {
    assert.equal(queue._kindColor('History'), 'purple');
    assert.equal(queue._kindColor(1), 'blue');
    assert.equal(queue._kindColor('Playlists'), 'yellow');
    assert.equal(queue._kindColor('Other'), 'gray');
});

test('_stateName turns an outbound state number into its name', function () {
    assert.equal(queue._stateName(0), 'Pending');
    assert.equal(queue._stateName(1), 'Sent');
    assert.equal(queue._stateName(2), 'Failed');
    assert.equal(queue._stateName('Sent'), 'Sent');
});

test('_fileName gives the name of a path without its folders or extension', function () {
    assert.equal(queue._fileName('/media/Movies/Film (2020)/Film (2020).mkv'), 'Film (2020)');
    assert.equal(queue._fileName('C:\\Media\\Show\\Episode.mp4'), 'Episode');
    assert.equal(queue._fileName(''), '');
    assert.equal(queue._fileName(null), '');
});

test('_isWide is true for the kinds that carry a landscape image', function () {
    ['Episode', 'Video', 'Trailer', 'Recording', 'MusicVideo'].forEach(function (type) { assert.equal(queue._isWide(type), true, type); });
    assert.equal(queue._isWide('Movie'), false);
    assert.equal(queue._isWide(undefined), false);
});

test('_ago counts seconds, minutes, hours, and days back from now', function () {
    Date.now = function () { return Date.parse('2026-01-10T12:00:00Z'); };
    assert.equal(queue._ago('2026-01-10T11:59:30Z'), '30s ago');
    assert.equal(queue._ago('2026-01-10T11:50:00Z'), '10m ago');
    assert.equal(queue._ago('2026-01-10T09:00:00Z'), '3h ago');
    assert.equal(queue._ago('2026-01-08T12:00:00Z'), '2d ago');
    assert.equal(queue._ago(''), '');
});

test('_ago never goes below zero for a time in the future', function () {
    Date.now = function () { return Date.parse('2026-01-10T12:00:00Z'); };
    assert.equal(queue._ago('2026-01-10T12:00:30Z'), '0s ago');
});

test('_time says how far away a time within a day is, and gives a date beyond that', function () {
    Date.now = function () { return Date.parse('2026-01-10T12:00:00Z'); };
    assert.equal(queue._time('2026-01-10T12:00:20Z'), 'in under a minute');
    assert.equal(queue._time('2026-01-10T12:30:00Z'), 'in 30m');
    assert.equal(queue._time('2026-01-10T16:00:00Z'), 'in 4h');
    assert.equal(queue._time(''), '');
    assert.equal(queue._time('2026-01-20T12:00:00Z'), new Date('2026-01-20T12:00:00Z').toLocaleString());
});

test('_countdown says when a gathering change is sent', function () {
    Date.now = function () { return Date.parse('2026-01-10T12:00:00Z'); };
    assert.equal(queue._countdown('2026-01-10T12:00:45Z'), 'sends in 45s');
    assert.equal(queue._countdown('2026-01-10T12:05:00Z'), 'sends in 5m');
    assert.equal(queue._countdown('2026-01-10T11:59:00Z'), 'sending now');
    assert.equal(queue._countdown(''), '');
});

test('_rows gathers every lane into one list, newest activity first', function () {
    queue._config = { Servers: [{ ServerId: 'srv-1', Key: 'k1', Name: 'Alpha' }] };
    queue._data = {
        Gathering: [{ Kind: 'Metadata', Title: 'Gathering title', EditedAt: '2026-01-10T10:00:00Z', DueAt: '2026-01-10T10:01:00Z' }],
        Outbound: [{ Id: 5, State: 1, Kind: 'History', PeerKey: 'k1', PeerName: 'Alpha', ItemPath: '/m/Film.mkv', SentAt: '2026-01-10T11:00:00Z', CreatedAt: '2026-01-10T09:00:00Z' }],
        Inbound: [{ Id: 7, Kind: 'Content', OriginServerId: 'srv-1', ItemPath: '/m/Other.mkv', ReceivedAt: '2026-01-10T12:00:00Z' }]
    };
    var rows = queue._rows();
    assert.deepEqual(rows.map(function (r) { return r.state; }), ['Received', 'Sent', 'Gathering']);
    assert.equal(rows[0].peerKey, 'k1');
    assert.equal(rows[0].peerName, 'Alpha');
    assert.equal(rows[0].title, 'Other');
    assert.equal(rows[1].title, 'Film');
    assert.equal(rows[1].dir, 'out');
    assert.equal(rows[2].what, 'metadata');
});

test('_rows names an inbound change by its origin id when no configured server holds it', function () {
    queue._config = { Servers: [] };
    queue._data = { Inbound: [{ Id: 1, Kind: 'Users', OriginServerId: 'unknown-id', UserName: 'Pat' }] };
    var rows = queue._rows();
    assert.equal(rows[0].peerKey, null);
    assert.equal(rows[0].peerName, 'unknown-id');
    assert.equal(rows[0].title, 'Pat');
});

test('renderList draws a row for each change and hides the empty message', function () {
    queue._data = { Outbound: [{ Id: 5, State: 0, Kind: 'Metadata', PeerKey: 'k1', PeerName: 'Alpha', Title: 'The Film', CreatedAt: '2026-01-10T09:00:00Z', Attempts: 0 }] };
    queue._config = { Servers: [] };
    queue.renderList();
    var html = env.view.querySelector('#queueList').innerHTML;
    assert.match(html, /class="serversync-queue-row"/);
    assert.match(html, /serversync-queue-chip serversync-pending/);
    assert.match(html, /The Film/);
    assert.equal(env.view.querySelector('#queueEmpty').classList.contains('hidden'), true);
    assert.equal(env.view.querySelector('#queueList').classList.contains('hidden'), false);
});

test('renderList shows the empty message when there is nothing to list', function () {
    queue._data = {};
    queue.renderList();
    assert.equal(env.view.querySelector('#queueEmpty').classList.contains('hidden'), false);
    assert.equal(env.view.querySelector('#queueList').classList.contains('hidden'), true);
});

test('a row escapes the title, the server name, and the error, and writes no inline style or handler', function () {
    queue._config = { Servers: [] };
    queue._data = { Outbound: [{ Id: 5, State: 2, Kind: 'History', PeerKey: 'k1', PeerName: '<b>Peer</b>', Title: '<script>x</script>', LastError: '"><img>', ItemId: 'abc', UserName: '<i>', UserId: 'u1' }] };
    queue.renderList();
    var html = env.view.querySelector('#queueList').innerHTML;
    assert.doesNotMatch(html, /<script>/);
    assert.doesNotMatch(html, /<b>Peer/);
    assert.match(html, /&lt;script&gt;x&lt;\/script&gt;/);
    assert.match(html, /&lt;b&gt;Peer&lt;\/b&gt;/);
    assert.doesNotMatch(html, /style=/);
    assert.doesNotMatch(html, /onerror=/);
});

test('a row has a discard button with a name and a times sign, and none of the old glyph', function () {
    queue._config = { Servers: [] };
    queue._data = { Outbound: [{ Id: 12, State: 0, Kind: 'Metadata', PeerKey: 'k1', PeerName: 'Alpha', Title: 'T', Attempts: 0 }] };
    queue.renderList();
    var html = env.view.querySelector('#queueList').innerHTML;
    assert.match(html, /class="base-row-btn serversync-queue-discard" data-lane="Outbound" data-discard="12" title="Discard" aria-label="Discard"><span aria-hidden="true">&times;<\/span>/);
    assert.doesNotMatch(html, /✕/);
});

test('a received row discards through the inbound lane', function () {
    queue._config = { Servers: [] };
    queue._data = { Inbound: [{ Id: 3, Kind: 'Content', OriginServerId: 'x', ItemPath: '/a/b.mkv' }] };
    queue.renderList();
    assert.match(env.view.querySelector('#queueList').innerHTML, /data-lane="Inbound" data-discard="3"/);
});

test('a gathering row shows its countdown chip and no discard button', function () {
    Date.now = function () { return Date.parse('2026-01-10T12:00:00Z'); };
    queue._config = { Servers: [] };
    queue._data = { Gathering: [{ Kind: 'History', Title: 'T', EditedAt: '2026-01-10T11:59:00Z', DueAt: '2026-01-10T12:01:00Z' }] };
    queue.renderList();
    var html = env.view.querySelector('#queueList').innerHTML;
    assert.match(html, /serversync-queue-chip serversync-gathering/);
    assert.match(html, /sends in 60s/);
    assert.doesNotMatch(html, /data-discard/);
});

test('a person and a wide item take their own thumbnail shapes', function () {
    queue._config = { Servers: [] };
    queue._data = { Outbound: [
        { Id: 1, State: 0, Kind: 'People', PeerKey: 'k', PeerName: 'A', Title: 'Person' },
        { Id: 2, State: 0, Kind: 'Metadata', PeerKey: 'k', PeerName: 'A', Title: 'Episode', ItemType: 'Episode', ItemId: 'e1' }
    ] };
    queue.renderList();
    var html = env.view.querySelector('#queueList').innerHTML;
    assert.match(html, /serversync-queue-thumb-holder serversync-round/);
    assert.match(html, /serversync-queue-thumb serversync-wide/);
});

test('renderList leaves out the states the cards have turned off', function () {
    queue._config = { Servers: [] };
    queue._data = { Outbound: [
        { Id: 1, State: 0, Kind: 'Metadata', PeerKey: 'k', PeerName: 'A', Title: 'Waiting', Attempts: 0 },
        { Id: 2, State: 2, Kind: 'Metadata', PeerKey: 'k', PeerName: 'A', Title: 'Broken' }
    ] };
    queue._states.Failed = false;
    queue.renderList();
    var html = env.view.querySelector('#queueList').innerHTML;
    assert.match(html, /Waiting/);
    assert.doesNotMatch(html, /Broken/);
});

test('renderList narrows to one server and to one direction', function () {
    queue._config = { Servers: [{ ServerId: 'srv', Key: 'kin', Name: 'In' }] };
    queue._data = {
        Outbound: [{ Id: 1, State: 0, Kind: 'Metadata', PeerKey: 'kout', PeerName: 'Out', Title: 'OutRow', Attempts: 0 }],
        Inbound: [{ Id: 2, Kind: 'Content', OriginServerId: 'srv', ItemPath: '/in/InRow.mkv' }]
    };
    queue._dir = 'in';
    queue.renderList();
    var html = env.view.querySelector('#queueList').innerHTML;
    assert.match(html, /InRow/);
    assert.doesNotMatch(html, /OutRow/);
    queue._dir = 'all';
    queue._peer = 'kout';
    queue.renderList();
    html = env.view.querySelector('#queueList').innerHTML;
    assert.match(html, /OutRow/);
    assert.doesNotMatch(html, /InRow/);
});

test('renderList stops at the row limit and says how many it left out', function () {
    queue._config = { Servers: [] };
    queue._maxRows = 2;
    queue._data = { Outbound: [1, 2, 3, 4, 5].map(function (n) { return { Id: n, State: 0, Kind: 'Metadata', PeerKey: 'k', PeerName: 'A', Title: 'Row' + n, Attempts: 0 }; }) };
    queue.renderList();
    assert.match(env.view.querySelector('#queueList').innerHTML, /Showing the latest 2 of 5\. Filter by state or server to see others\./);
});

test('render fills the counts and shows the unmatched warning', function () {
    queue._config = { Servers: [] };
    queue._data = {
        OutboundCounts: { Pending: 3, Sent: 2, Failed: 1 },
        InboundCount: 4,
        Gathering: [{ Kind: 'History', Title: 'T' }],
        Peers: [],
        Unmatched: 2,
        LastUnmatched: 'Film.mkv',
        Outbound: [],
        Inbound: []
    };
    queue.render();
    var text = function (id) { return env.view.querySelector('#' + id).textContent; };
    assert.equal(text('queuePendingCount'), '3');
    assert.equal(text('queueSentCount'), '2');
    assert.equal(text('queueFailedCount'), '1');
    assert.equal(text('queueInboundCount'), '4');
    assert.equal(text('queueGatheringCount'), '1');
    assert.equal(env.view.querySelector('#queueFailedActions').classList.contains('hidden'), false);
    assert.equal(env.view.querySelector('#queueUnmatched').classList.contains('hidden'), false);
    assert.match(env.view.querySelector('#queueUnmatchedText').textContent, /^2 local changes matched no library or user mapping/);
    assert.equal(env.view.querySelector('#queueNoPeers').classList.contains('hidden'), false);
    assert.equal(env.view.querySelector('#queuePeers').classList.contains('hidden'), true);
});

test('render adds a filter button for each server and a server card for each', function () {
    queue._config = { Servers: [] };
    queue._data = { Peers: [{ Key: 'k1', Name: 'Alpha', Sends: ['History'] }, { Key: 'k2', Name: 'Beta', Sends: [] }], Outbound: [], Inbound: [] };
    queue.render();
    var buttons = Array.from(env.view.querySelector('#queueFilters').querySelectorAll('[data-peer]'));
    assert.deepEqual(buttons.map(function (b) { return b.textContent; }), ['Alpha', 'Beta']);
    assert.ok(buttons[0].classList.contains('serversync-filter-pill'));
    var cards = env.view.querySelector('#queuePeerRow').innerHTML;
    assert.match(cards, /Sends watch history/);
    assert.match(cards, /Sends nothing: every module is off there/);
    assert.equal(env.view.querySelector('#queueNoPeers').classList.contains('hidden'), true);
});

test('a server that is paused shows when it resumes, in the paused color', function () {
    queue._config = { Servers: [] };
    queue._data = { Peers: [{ Key: 'k1', Name: 'Alpha', PausedUntil: new Date(Date.now() + 600000).toISOString(), Sends: null }], Outbound: [], Inbound: [] };
    queue.render();
    var cards = env.view.querySelector('#queuePeerRow').innerHTML;
    assert.match(cards, /serversync-health-value serversync-paused/);
    assert.match(cards, /Paused until/);
    assert.match(cards, /Sends everything until the server says what it applies/);
});

test('discarding a change deletes it from its lane and reports it', async function () {
    var button = env.page.dom.el('button', { 'data-discard': '5', 'data-lane': 'Outbound' });
    queue._onDiscard({ target: button });
    await flush();
    var del = env.page.requests.find(function (r) { return r.type === 'DELETE'; });
    assert.equal(del.url, 'Sync/Hints/Outbound/5');
    assert.equal(statusText(env.view.querySelector('#syncStatus')), 'Hint discarded.');
});

test('a discard that fails reports it and lets the button be used again', async function () {
    var button = env.page.dom.el('button', { 'data-discard': '5', 'data-lane': 'Inbound' });
    env.responses.push(new Error('refused'));
    queue._onDiscard({ target: button });
    await flush();
    assert.equal(statusText(env.view.querySelector('#syncStatus')), 'Could not discard the hint.');
    assert.equal(button.disabled, false);
});

test('a click that is not on a discard button does nothing', async function () {
    queue._onDiscard({ target: env.page.dom.el('span') });
    await flush();
    assert.equal(env.page.requests.filter(function (r) { return r.type === 'DELETE'; }).length, 0);
});

test('an avatar that fails to load is hidden in place, and a thumbnail swaps to its placeholder', function () {
    var avatar = env.page.dom.el('img', { class: 'serversync-queue-avatar' });
    queue._onImageError({ target: avatar });
    assert.ok(avatar.classList.contains('serversync-broken'));
    var placeholder = env.page.dom.el('div', { class: 'hidden' });
    var thumb = env.page.dom.el('img', { class: 'serversync-queue-thumb' });
    Object.defineProperty(thumb, 'nextElementSibling', { get: function () { return placeholder; } });
    queue._onImageError({ target: thumb });
    assert.ok(thumb.classList.contains('hidden'));
    assert.equal(placeholder.classList.contains('hidden'), false);
});

test('failedAction posts to its route and says how many changes it handled', async function () {
    var buttons = ['btnQueueRetryFailed', 'btnQueueDismissFailed'].map(function (id) {
        var b = env.page.dom.el('button', { id: id });
        env.view.appendChild(b);
        return b;
    });
    env.responses.push({ Retried: 2 });
    env.responses.push({});
    queue.failedAction('Hints/Failed/Retry', 'POST', 'Retried', 'ToastFailedRetriedOne', 'ToastFailedRetriedMany');
    assert.equal(buttons[0].disabled, true);
    await flush();
    assert.equal(env.page.requests.find(function (r) { return r.url === 'Sync/Hints/Failed/Retry'; }).type, 'POST');
    assert.equal(statusText(env.view.querySelector('#syncStatus')), '2 failed changes tried again.');
    assert.equal(buttons[0].disabled, false);
});

test('failedAction says what could not be done and points to the server log', async function () {
    ['btnQueueRetryFailed', 'btnQueueDismissFailed'].forEach(function (id) { env.view.appendChild(env.page.dom.el('button', { id: id })); });
    env.responses.push(new Error('refused'));
    queue.failedAction('Hints/Failed', 'DELETE', 'Removed', 'one', 'many');
    await flush();
    assert.equal(statusText(env.view.querySelector('#syncStatus')), 'Could not finish that. See the server log.');
});

test('runNow delivers what was due and reports it', async function () {
    var button = env.page.dom.el('button', { id: 'btnQueueRun' });
    env.view.appendChild(button);
    queue.runNow();
    assert.equal(button.disabled, true);
    await flush();
    assert.equal(statusText(env.view.querySelector('#syncStatus')), 'Delivered and applied what was due.');
    assert.equal(button.disabled, false);
});

test('the Dismiss Failed button asks first and sends nothing when the question is declined', async function () {
    ['btnQueueRefresh', 'btnQueueRun', 'btnQueueRetryFailed', 'btnQueueDismissFailed', 'btnQueueUnmatchedDismiss'].forEach(function (id) { env.view.appendChild(env.page.dom.el('button', { id: id })); });
    confirmAnswer = false;
    queue.init();
    await flush();
    env.view.querySelector('#btnQueueDismissFailed').click();
    await flush();
    assert.equal(env.page.requests.filter(function (r) { return r.type === 'DELETE'; }).length, 0);
    confirmAnswer = true;
    env.view.querySelector('#btnQueueDismissFailed').click();
    await flush();
    assert.equal(env.page.requests.filter(function (r) { return r.url === 'Sync/Hints/Failed' && r.type === 'DELETE'; }).length, 1);
});

test('startAutoRefresh and stopAutoRefresh start and stop both timers', function () {
    var started = 0;
    var cleared = 0;
    globalThis.setInterval = function () { started++; return started; };
    globalThis.clearInterval = function () { cleared++; };
    queue.startAutoRefresh();
    assert.equal(started, 2);
    queue.stopAutoRefresh();
    assert.equal(queue._timer, null);
    assert.equal(queue._tick, null);
    assert.ok(cleared >= 2);
    queue.stopAutoRefresh();
});

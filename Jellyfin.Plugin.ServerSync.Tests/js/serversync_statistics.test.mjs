// serversync_statistics.test.mjs
// Tests for serversync_statistics.js: the tab it sets, one section for each sync type that is on, the link
// streams chart under Content only, what shows when nothing is on or the read fails, and the chart data builders.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installPage, loadScript, settle, strings } from './support.mjs';

// The strings handle the chart builders name their series with, in US English.
var t = { tr: function (key) { return strings[key] || key; } };

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};
globalThis.setInterval = function () { return 1; };

var IDS = ['statisticsStatus', 'statisticsIntro', 'statisticsDisabled', 'statisticsSections'];

// openPage
// Loads the Statistics controller over a view that holds the elements it uses, answers the Statistics route, and shows the page.
async function openPage(opts) {
    opts = opts || {};
    var calls = [];
    var page = installPage({ calls: calls, config: {}, storage: opts.storage });
    var view = page.dom.el('div', {}, IDS.map(function (id) { return page.dom.el('div', { id: id }); }));
    page.dom.document.documentElement.appendChild(view);
    globalThis.ApiClient.fetch = function (options) {
        page.requests.push(options);
        if (String(options.url).indexOf('Statistics') < 0) return Promise.resolve({});
        return opts.fail ? Promise.reject(new Error('refused')) : Promise.resolve(opts.statistics || { Types: [] });
    };
    var mod = await loadScript('serversync_statistics.js');
    mod.default(view);
    view.dispatch('viewshow');
    await settle();
    return { page: page, view: view, calls: calls, mod: mod };
}

function run(added, updated, removed, at) {
    return { FinishedAt: at || '2026-04-01T08:00:00Z', Added: added, Updated: updated, Removed: removed };
}

function titles(view) {
    return Array.from(view.querySelectorAll('.base-collapsible-title')).map(function (el) { return el.textContent; });
}

test('the Statistics tab sits after Storage and before Settings', async function () {
    var opened = await openPage();
    assert.deepEqual(opened.calls[0].tabs.map(function (t) { return t.name; }), ['Sync', 'Servers', 'Storage', 'Statistics', 'Settings']);
    assert.equal(opened.calls[0].index, 3);
});

test('each sync type the route answers with gets its own section, in order, with the Users heading for User', async function () {
    var opened = await openPage({ statistics: { Types: [{ Key: 'Content', Runs: [run(1, 2, 3)] }, { Key: 'People', Runs: [] }, { Key: 'User', Runs: [] }] } });
    assert.deepEqual(titles(opened.view), ['Content', 'People', 'Users']);
    assert.equal(opened.view.querySelectorAll('.base-bar-chart').length, 3);
    assert.equal(opened.view.querySelector('#statisticsDisabled').classList.contains('hidden'), true);
    assert.equal(opened.view.querySelector('#statisticsIntro').classList.contains('hidden'), false);
});

test('a sync type with no scan yet says so in place of its chart', async function () {
    var opened = await openPage({ statistics: { Types: [{ Key: 'History', Runs: [] }] } });
    assert.match(opened.view.querySelector('#statisticsSections').textContent, /No scan has finished yet\./);
});

test('the link streams chart shows under Content when content sync is on, and only when the route lists link streams', async function () {
    var days = { Days: ['2026-04-01', '2026-04-02'], Servers: [{ Key: 'den', Name: 'Den', Streams: [2, 0] }, { Key: 'attic', Name: 'Attic', Streams: [0, 1] }] };
    var withLinks = await openPage({ statistics: { Types: [{ Key: 'Content', Runs: [run(1, 0, 0)] }, { Key: 'History', Runs: [] }], LinkStreams: days } });
    assert.equal(withLinks.view.querySelectorAll('.base-line-chart').length, 1);
    assert.deepEqual(Array.from(withLinks.view.querySelectorAll('.base-subsection-title')).map(function (el) { return el.textContent; }), ['Link streams']);
    assert.match(withLinks.view.querySelector('.base-line-chart').textContent, /Den/);
    assert.match(withLinks.view.querySelector('.base-line-chart').textContent, /Attic/);
    var without = await openPage({ statistics: { Types: [{ Key: 'Content', Runs: [run(1, 0, 0)] }], LinkStreams: null } });
    assert.equal(without.view.querySelectorAll('.base-line-chart').length, 0);
});

test('a server with content sync off shows the link streams in a section of their own', async function () {
    var days = { Days: ['2026-04-01'], Servers: [{ Key: 'den', Name: 'Den', Streams: [3] }] };
    var other = await openPage({ statistics: { Types: [{ Key: 'History', Runs: [] }], LinkStreams: days } });
    assert.deepEqual(titles(other.view), ['History', 'Link streams']);
    assert.equal(other.view.querySelectorAll('.base-line-chart').length, 1);
    assert.equal(other.view.querySelectorAll('.base-subsection-title').length, 0);
    var alone = await openPage({ statistics: { Types: [], LinkStreams: days } });
    assert.deepEqual(titles(alone.view), ['Link streams']);
    assert.equal(alone.view.querySelector('#statisticsDisabled').classList.contains('hidden'), true);
    assert.equal(alone.view.querySelector('#statisticsIntro').classList.contains('hidden'), true);
    assert.equal(other.view.querySelector('#statisticsIntro').classList.contains('hidden'), false);
});

test('the page says every sync type is off when the route lists none', async function () {
    var opened = await openPage({ statistics: { Types: [] } });
    assert.equal(opened.view.querySelector('#statisticsDisabled').classList.contains('hidden'), false);
    assert.equal(opened.view.querySelector('#statisticsIntro').classList.contains('hidden'), true);
    assert.equal(opened.view.querySelectorAll('.base-collapsible-section').length, 0);
});

test('a read that fails says so on the page status and draws nothing', async function () {
    var opened = await openPage({ fail: true });
    assert.equal(opened.view.querySelector('#statisticsStatus').textContent, 'The statistics could not be loaded.');
    assert.equal(opened.view.querySelector('#statisticsIntro').classList.contains('hidden'), true);
    assert.equal(opened.view.querySelectorAll('.base-collapsible-section').length, 0);
});

test('showing the page again redraws it once, and hiding it clears the charts', async function () {
    var opened = await openPage({ statistics: { Types: [{ Key: 'Content', Runs: [run(1, 0, 0)] }] } });
    opened.view.dispatch('viewshow');
    await settle();
    assert.equal(opened.view.querySelectorAll('.base-bar-chart').length, 1);
    opened.view.dispatch('viewhide');
    assert.equal(opened.view.querySelectorAll('.base-bar-chart').length, 0);
});

test('scanChartData makes one label for each scan and the New, Updated, and Removed series', async function () {
    var mod = (await openPage()).mod;
    var data = mod.scanChartData([run(1, 2, 3), run(4, 0, 0)], t);
    assert.equal(data.labels.length, 2);
    assert.deepEqual(data.series.map(function (s) { return [s.name, s.values]; }), [['New', [1, 4]], ['Updated', [2, 0]], ['Removed', [3, 0]]]);
    assert.deepEqual(mod.scanChartData(null, t).labels, []);
});

test('streamChartData makes one line for each server, with a point for every day', async function () {
    var mod = (await openPage()).mod;
    var data = mod.streamChartData({ Days: ['2026-04-01', '2026-04-02'], Servers: [{ Key: 'den', Name: 'Den', Streams: [5] }, { Key: 'attic', Streams: [0, 3] }] }, t);
    assert.equal(data.labels.length, 2);
    assert.deepEqual(data.series.map(function (s) { return [s.name, s.values]; }), [['Den', [5, 0]], ['attic', [0, 3]]]);
    assert.deepEqual(mod.streamChartData(null, t), { labels: [], series: [] });
});

test('runLabel writes the time for a scan today and the day for an older one', async function () {
    var mod = (await openPage()).mod;
    var now = new Date(2026, 3, 2, 12, 0, 0);
    var today = mod.runLabel(new Date(2026, 3, 2, 9, 5, 0).toISOString(), now);
    var older = mod.runLabel(new Date(2026, 3, 1, 9, 5, 0).toISOString(), now);
    assert.match(today, /9.05|09.05/);
    assert.doesNotMatch(older, /:/);
    assert.match(older, /1/);
    assert.equal(mod.runLabel('not a time', now), '');
});

test('dayLabel reads the day without shifting it by a time zone, and leaves an unreadable day as it is', async function () {
    var mod = (await openPage()).mod;
    assert.equal(mod.dayLabel('2026-04-01'), new Date(2026, 3, 1).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }));
    assert.equal(mod.dayLabel('soon'), 'soon');
    assert.equal(mod.dayLabel(null), '');
});

test('countAxisMax keeps a small chart on whole numbers and lets a large one choose', async function () {
    var mod = (await openPage()).mod;
    assert.equal(mod.countAxisMax([{ values: [0, 1, 2] }]), 4);
    assert.equal(mod.countAxisMax([]), 4);
    assert.equal(mod.countAxisMax([{ values: [3] }, { values: [12] }]), undefined);
});

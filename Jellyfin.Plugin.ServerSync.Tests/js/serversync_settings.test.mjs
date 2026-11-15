// serversync_settings.test.mjs
// Tests for serversync_settings.js: the tab it sets, that a load failure and every result show on a status line
// and never in an alert, that a reset and a cache delete ask first, and that a saved section posts its fields.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installPage, loadScript, settle, clickDialogButton, dialogText } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};
globalThis.setInterval = function () { return 1; };

var IDS = ['settingsStatus', 'resetStatus', 'saveBandwidthStatus', 'streamSpeedRows', 'linkTakeRows', 'linkCacheStatus', 'bandwidthTable'];
var BUTTONS = ['btnResetEntireDatabase', 'btnResetContentTable', 'btnClearLinkCache', 'btnSaveBandwidth'];

// openPage
// Loads the Settings controller over a view that holds the elements it uses, and shows the page.
async function openPage(opts) {
    opts = opts || {};
    var calls = [];
    var page = installPage({ calls: calls, config: opts.config === undefined ? { Servers: [] } : opts.config, storage: opts.storage });
    var children = IDS.map(function (id) { return page.dom.el('div', { id: id }); })
        .concat(BUTTONS.map(function (id) { return page.dom.el('button', { id: id }); }))
        .concat([page.dom.el('input', { id: 'txtMaxDownloadSpeed' })]);
    var view = page.dom.el('div', {}, children);
    page.dom.document.documentElement.appendChild(view);
    if (opts.fail) globalThis.ApiClient.getPluginConfiguration = function () { return Promise.reject(new Error('refused')); };
    var mod = await loadScript('serversync_settings.js');
    mod.default(view);
    view.dispatch('viewshow');
    await settle();
    return { page: page, view: view, calls: calls };
}

// posts
// Lists the POST and DELETE requests the page made, apart from the config save.
function posts(opened) {
    return opened.page.requests.filter(function (r) { return r.type === 'POST' || r.type === 'DELETE'; });
}

test('the Settings tab is the last tab, after Storage when that shows', async function () {
    var opened = await openPage();
    assert.equal(opened.calls[0].index, 3);
    assert.equal(opened.calls[0].tabs[3].name, 'Settings');
});

test('the Settings tab moves up one place when the Storage tab is hidden', async function () {
    var opened = await openPage({ storage: { 'serversync.storageTab': '0' } });
    assert.equal(opened.calls[0].index, 2);
    assert.deepEqual(opened.calls[0].tabs.map(function (t) { return t.name; }), ['Sync', 'Servers', 'Settings']);
});

test('a config that cannot be loaded shows an error on the page status and never opens an alert', async function () {
    var opened = await openPage({ fail: true });
    var status = opened.view.querySelector('#settingsStatus');
    assert.equal(status.textContent, 'Could not load the settings. Reload the page.');
    assert.ok(status.classList.contains('jpk-status-bad'));
});

test('resetting the whole database asks first and sends nothing when the question is cancelled', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnResetEntireDatabase').click();
    await settle(20);
    var dialog = dialogText(opened.page.dom);
    assert.equal(dialog.title, 'Reset Database');
    assert.match(dialog.body, /Reset the entire sync database\?/);
    assert.match(dialog.body, /It cannot be undone\./);
    assert.equal(clickDialogButton(opened.page.dom, 'Cancel'), true);
    await settle(20);
    assert.equal(posts(opened).length, 0);
    assert.equal(opened.view.querySelector('#resetStatus').textContent, '');
});

test('resetting the whole database posts the reset and reports it on the reset status', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnResetEntireDatabase').click();
    await settle(20);
    assert.equal(clickDialogButton(opened.page.dom, 'Reset'), true);
    await settle(20);
    assert.equal(posts(opened)[0].url, 'Sync/Reset/All');
    var status = opened.view.querySelector('#resetStatus');
    assert.equal(status.textContent, 'Database reset.');
    assert.ok(status.classList.contains('jpk-status-ok'));
});

test('resetting one table names it in the question and in the result', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnResetContentTable').click();
    await settle(20);
    assert.equal(dialogText(opened.page.dom).title, 'Reset Table');
    assert.match(dialogText(opened.page.dom).body, /Reset the content sync table\?/);
    clickDialogButton(opened.page.dom, 'Reset');
    await settle(20);
    assert.equal(posts(opened)[0].url, 'Sync/Reset/Content');
    assert.equal(opened.view.querySelector('#resetStatus').textContent, 'Content sync table reset.');
});

test('a reset that fails shows an error on the reset status', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnResetEntireDatabase').click();
    await settle(20);
    globalThis.ApiClient.fetch = function () { return Promise.reject(new Error('refused')); };
    clickDialogButton(opened.page.dom, 'Reset');
    await settle(20);
    var status = opened.view.querySelector('#resetStatus');
    assert.equal(status.textContent, 'Could not reset the database.');
    assert.ok(status.classList.contains('jpk-status-bad'));
});

test('deleting the cached files asks a destructive question and posts only when it is confirmed', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnClearLinkCache').click();
    await settle(20);
    var dialog = dialogText(opened.page.dom);
    assert.equal(dialog.title, 'Delete Cached Files');
    assert.match(dialog.body, /Delete every cached file\? Links keep playing from their sources\./);
    clickDialogButton(opened.page.dom, 'Cancel');
    await settle(20);
    assert.equal(posts(opened).length, 0);
    opened.view.querySelector('#btnClearLinkCache').click();
    await settle(20);
    clickDialogButton(opened.page.dom, 'Delete');
    await settle(20);
    assert.equal(posts(opened)[0].url, 'Sync/LinkCache/Clear');
});

test('the bandwidth rows list each server that may link, with its name and speeds escaped', async function () {
    var opened = await openPage({ config: { Servers: [
        { Key: 'k"1', Name: '<b>Alpha</b>', Url: 'u', ApiKey: 'k', Mode: 'Push', AllowLinking: true, LinkStreamSpeedMbps: 25, LinkScheduledStreamSpeedMbps: 10 },
        { Key: 'k2', Name: 'Pull only', Url: 'u', ApiKey: 'k', Mode: 'Pull', AllowLinking: true },
        { Key: 'k3', Name: 'No linking', Url: 'u', ApiKey: 'k', Mode: 'Sync', AllowLinking: false }
    ] } });
    var html = opened.view.querySelector('#streamSpeedRows').innerHTML;
    assert.match(html, /Streams to &lt;b&gt;Alpha&lt;\/b&gt;/);
    assert.doesNotMatch(html, /<b>Alpha/);
    assert.match(html, /data-server-key="k&quot;1"/);
    assert.match(html, /value="25"/);
    assert.match(html, /value="10"/);
    assert.doesNotMatch(html, /Pull only/);
    assert.doesNotMatch(html, /No linking/);
});

test('saving the bandwidth posts the changed fields and reports it beside the Save button', async function () {
    var opened = await openPage({ config: { Servers: [], Untouched: 'kept' } });
    var saved = null;
    globalThis.ApiClient.updatePluginConfiguration = function (id, config) { saved = config; return Promise.resolve(); };
    opened.view.querySelector('#txtMaxDownloadSpeed').value = '12';
    opened.view.querySelector('#btnSaveBandwidth').click();
    await settle(20);
    assert.equal(saved.MaxDownloadSpeed, 12);
    assert.equal(saved.DownloadSpeedUnit, 'Mbps');
    assert.equal(saved.Untouched, 'kept');
    var status = opened.view.querySelector('#saveBandwidthStatus');
    assert.equal(status.textContent, 'Bandwidth saved.');
    assert.ok(status.classList.contains('jpk-status-ok'));
});

test('a bandwidth save that fails says so and does not claim success', async function () {
    var opened = await openPage();
    globalThis.ApiClient.updatePluginConfiguration = function () { return Promise.reject(new Error('refused')); };
    opened.view.querySelector('#btnSaveBandwidth').click();
    await settle(20);
    var status = opened.view.querySelector('#saveBandwidthStatus');
    assert.equal(status.textContent, 'Could not save the bandwidth.');
    assert.ok(status.classList.contains('jpk-status-bad'));
});

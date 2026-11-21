// serversync_settings.test.mjs
// Tests for serversync_settings.js: the tab it sets, that a load failure and every result show on a status line
// and never in an alert, that a reset and a cache delete ask first, and that a saved section posts its fields.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { installPage, loadScript, settle, clickDialogButton, dialogText, statusText } from './support.mjs';

globalThis.setTimeout = function () { return 1; };
globalThis.clearTimeout = function () {};
globalThis.setInterval = function () { return 1; };

var IDS = ['settingsStatus', 'resetStatus', 'bandwidthConfigContent', 'streamSpeedRows', 'linkTakeRows', 'linkCacheStatus', 'bandwidthTable', 'watchedFilterUsersList', 'contentSyncSettingsContent', 'callerGrants', 'callerAccessContent'];
var BUTTONS = ['btnResetEntireDatabase', 'btnResetContentTable', 'btnClearLinkCache'];

// openPage
// Loads the Settings controller over a view that holds the elements it uses, and shows the page.
async function openPage(opts) {
    opts = opts || {};
    var calls = [];
    var page = installPage({ calls: calls, config: opts.config === undefined ? { Servers: [] } : opts.config, storage: opts.storage });
    var children = IDS.concat(opts.ids || []).map(function (id) { return page.dom.el('div', { id: id }); })
        .concat(BUTTONS.concat(opts.buttons || []).map(function (id) { return page.dom.el('button', { id: id }); }))
        .concat((opts.checkboxes || []).map(function (id) { return page.dom.el('input', { id: id, type: 'checkbox' }); }))
        .concat([page.dom.el('input', { id: 'txtMaxDownloadSpeed' })]);
    var view = page.dom.el('div', {}, children);
    page.dom.document.documentElement.appendChild(view);
    if (opts.answer) globalThis.ApiClient.fetch = function (options) { page.requests.push(options); return opts.answer(options); };
    if (opts.fail) globalThis.ApiClient.getPluginConfiguration = function () { return Promise.reject(new Error('refused')); };
    var mod = await loadScript('serversync_settings.js');
    mod.default(view);
    view.dispatch('viewshow');
    await settle();
    return { page: page, view: view, calls: calls };
}

// saveButton
// Finds the Save button of the Save Bar that ends a section.
function saveButton(view, contentId) {
    return view.querySelector('#' + contentId + ' .base-save-bar .base-save-button');
}

// barStatus
// Finds the status line of the Save Bar that ends a section.
function barStatus(view, contentId) {
    return view.querySelector('#' + contentId + ' .base-save-bar .base-save-status');
}

// posts
// Lists the POST and DELETE requests the page made, apart from the config save.
function posts(opened) {
    return opened.page.requests.filter(function (r) { return r.type === 'POST' || r.type === 'DELETE'; });
}

test('the Settings tab is the last tab, after Storage when that shows', async function () {
    var opened = await openPage();
    assert.equal(opened.calls[0].index, 4);
    assert.equal(opened.calls[0].tabs[4].name, 'Settings');
});

test('the Settings tab moves up one place when the Storage tab is hidden', async function () {
    var opened = await openPage({ storage: { 'serversync.storageTab': '0' } });
    assert.equal(opened.calls[0].index, 3);
    assert.deepEqual(opened.calls[0].tabs.map(function (t) { return t.name; }), ['Sync', 'Servers', 'Statistics', 'Settings']);
});

test('a config that cannot be loaded shows an error on the page status and never opens an alert', async function () {
    var opened = await openPage({ fail: true });
    var status = opened.view.querySelector('#settingsStatus');
    assert.equal(statusText(status), 'Could not load the settings. Reload the page.');
    assert.ok(status.classList.contains('base-status-bad'));
});

test('resetting the whole database asks first and sends nothing when the question is cancelled', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnResetEntireDatabase').click();
    await settle(20);
    var dialog = dialogText(opened.page.dom);
    assert.equal(dialog.title, 'Reset database');
    assert.match(dialog.body, /Reset the entire sync database\?/);
    assert.match(dialog.body, /It cannot be undone\./);
    assert.equal(clickDialogButton(opened.page.dom, 'Cancel'), true);
    await settle(20);
    assert.equal(posts(opened).length, 0);
    assert.equal(statusText(opened.view.querySelector('#resetStatus')), '');
});

test('resetting the whole database posts the reset and reports it on the reset status', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnResetEntireDatabase').click();
    await settle(20);
    assert.equal(clickDialogButton(opened.page.dom, 'Reset'), true);
    await settle(20);
    assert.equal(posts(opened)[0].url, 'Sync/Reset/All');
    var status = opened.view.querySelector('#resetStatus');
    assert.equal(statusText(status), 'Database reset.');
    assert.ok(status.classList.contains('base-status-ok'));
});

test('resetting one table names it in the question and in the result', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnResetContentTable').click();
    await settle(20);
    assert.equal(dialogText(opened.page.dom).title, 'Reset table');
    assert.match(dialogText(opened.page.dom).body, /Reset the content sync table\?/);
    clickDialogButton(opened.page.dom, 'Reset');
    await settle(20);
    assert.equal(posts(opened)[0].url, 'Sync/Reset/Content');
    assert.equal(statusText(opened.view.querySelector('#resetStatus')), 'Content sync table reset.');
});

test('a reset that fails shows an error on the reset status', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnResetEntireDatabase').click();
    await settle(20);
    globalThis.ApiClient.fetch = function () { return Promise.reject(new Error('refused')); };
    clickDialogButton(opened.page.dom, 'Reset');
    await settle(20);
    var status = opened.view.querySelector('#resetStatus');
    assert.equal(statusText(status), 'Could not reset the database.');
    assert.ok(status.classList.contains('base-status-bad'));
});

test('deleting the cached files asks a destructive question and posts only when it is confirmed', async function () {
    var opened = await openPage();
    opened.view.querySelector('#btnClearLinkCache').click();
    await settle(20);
    var dialog = dialogText(opened.page.dom);
    assert.equal(dialog.title, 'Delete cached files');
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

test('saving the bandwidth posts the changed fields and reports it on the Save Bar', async function () {
    var opened = await openPage({ config: { Servers: [], Untouched: 'kept' } });
    var saved = null;
    globalThis.ApiClient.updatePluginConfiguration = function (id, config) { saved = config; return Promise.resolve(); };
    opened.view.querySelector('#txtMaxDownloadSpeed').value = '12';
    saveButton(opened.view, 'bandwidthConfigContent').click();
    await settle(20);
    assert.equal(saved.MaxDownloadSpeed, 12);
    assert.equal(saved.DownloadSpeedUnit, 'Mbps');
    assert.equal(saved.Untouched, 'kept');
    var status = barStatus(opened.view, 'bandwidthConfigContent');
    assert.equal(statusText(status), 'Bandwidth saved.');
    assert.ok(status.classList.contains('base-status-ok'));
});

test('the provider switches show on when never saved, and each section saves its own switch', async function () {
    var opened = await openPage({
        config: { Servers: [] },
        checkboxes: ['chkMetadataSyncProvider', 'chkPeopleSyncProvider'],
        ids: ['metadataSyncSettingsContent', 'peopleSyncSettingsContent']
    });
    var saved = null;
    globalThis.ApiClient.updatePluginConfiguration = function (id, config) { saved = config; return Promise.resolve(); };
    var metadata = opened.view.querySelector('#chkMetadataSyncProvider');
    var people = opened.view.querySelector('#chkPeopleSyncProvider');
    assert.equal(metadata.checked, true);
    assert.equal(people.checked, true);

    metadata.checked = false;
    saveButton(opened.view, 'metadataSyncSettingsContent').click();
    await settle(20);
    assert.equal(saved.MetadataSyncProvider, false);

    people.checked = false;
    saveButton(opened.view, 'peopleSyncSettingsContent').click();
    await settle(20);
    assert.equal(saved.PeopleSyncProvider, false);
});

test('the media segment provider switch shows off when never saved, and the Metadata save keeps what it shows', async function () {
    var opened = await openPage({
        config: { Servers: [] },
        checkboxes: ['chkMetadataSyncSegmentProvider'],
        ids: ['metadataSyncSettingsContent']
    });
    var saved = null;
    globalThis.ApiClient.updatePluginConfiguration = function (id, config) { saved = config; return Promise.resolve(); };
    var segments = opened.view.querySelector('#chkMetadataSyncSegmentProvider');
    assert.equal(segments.checked, false);

    saveButton(opened.view, 'metadataSyncSettingsContent').click();
    await settle(20);
    assert.equal(saved.MetadataSyncSegmentProvider, false);

    segments.checked = true;
    saveButton(opened.view, 'metadataSyncSettingsContent').click();
    await settle(20);
    assert.equal(saved.MetadataSyncSegmentProvider, true);
});

test('a bandwidth save that fails says so and does not claim success', async function () {
    var opened = await openPage();
    // Jellyfin's client rejects with the response, which carries no message of its own.
    globalThis.ApiClient.updatePluginConfiguration = function () { return Promise.reject({ status: 500 }); };
    saveButton(opened.view, 'bandwidthConfigContent').click();
    await settle(20);
    var status = barStatus(opened.view, 'bandwidthConfigContent');
    assert.equal(statusText(status), 'Could not save the bandwidth.');
    assert.ok(status.classList.contains('base-status-bad'));
});

// usersAnswer
// Answers the user list of each scan server with two users.
function usersAnswer(options) {
    if (String(options.url).indexOf('GetSourceUsers') < 0) return Promise.resolve({});
    return Promise.resolve([{ Id: 'u1', Name: '<i>Ann</i>' }, { Id: 'u2', Name: 'Ben' }]);
}

var SCAN = { Key: 'a', Name: 'source-a', Url: 'http://source-a:8096', ApiKey: 'k', Mode: 'Pull', IsEnabled: true };

test('Skip items watched by lists each scan server user in a Checkbox List, with the saved users picked', async function () {
    var opened = await openPage({ config: { Servers: [SCAN], WatchedFilterUserIds: ['u2'] }, answer: usersAnswer });
    await settle(20);
    var list = opened.view.querySelector('#watchedFilterUsersList');
    assert.equal(list.querySelectorAll('.base-check-row').length, 2);
    assert.match(list.textContent, /<i>Ann<\/i>/);
    assert.match(list.textContent, /Ben/);
    assert.match(list.textContent, /source-a/);
    assert.equal(list.querySelector('i'), null);
});

test('saving Content keeps the picked users, keeps a saved user whose server did not answer, and turns skipping on', async function () {
    var opened = await openPage({ config: { Servers: [SCAN], WatchedFilterUserIds: ['u2', 'gone'] }, answer: usersAnswer });
    await settle(20);
    var saved = null;
    globalThis.ApiClient.updatePluginConfiguration = function (id, config) { saved = config; return Promise.resolve(); };
    saveButton(opened.view, 'contentSyncSettingsContent').click();
    await settle(20);
    assert.deepEqual(saved.WatchedFilterUserIds, ['u2', 'gone']);
    assert.equal(saved.SkipWatchedByAllUsers, true);
});

// grantUsersAnswer
// Answers the list of this server's users with one user.
function grantUsersAnswer(options) {
    if (String(options.url) !== 'Users') return Promise.resolve({});
    return Promise.resolve([{ Id: 'AAAA-1111', Name: 'viewer' }]);
}

test('the whitelist is a Repeater that saves each row with a user, its name, and its locations split at commas', async function () {
    var grants = [
        { UserId: 'apikey', UserName: 'API Keys', Locations: ['10.0.0.0/8', 'source-a'] },
        { UserId: 'AAAA-1111', UserName: 'viewer', Locations: [] },
        { UserId: 'gone', UserName: 'Left', Locations: [] }
    ];
    var opened = await openPage({ config: { Servers: [], PeerCallerGrants: grants }, answer: grantUsersAnswer });
    await settle(20);
    assert.ok(opened.view.querySelector('#callerGrants').children.length > 0);
    var saved = null;
    globalThis.ApiClient.updatePluginConfiguration = function (id, config) { saved = config; return Promise.resolve(); };
    saveButton(opened.view, 'callerAccessContent').click();
    await settle(20);
    assert.deepEqual(saved.PeerCallerGrants, [
        { UserId: 'apikey', UserName: 'API Keys', Locations: ['10.0.0.0/8', 'source-a'] },
        { UserId: 'aaaa1111', UserName: 'viewer', Locations: [] },
        { UserId: 'gone', UserName: 'Left', Locations: [] }
    ]);
    assert.equal(statusText(barStatus(opened.view, 'callerAccessContent')), 'Whitelist saved.');
});

test('the two media segment switches show off when never saved, and the Metadata save keeps what they show', async function () {
    var opened = await openPage({
        config: { Servers: [] },
        checkboxes: ['chkMetadataSyncReplaceSegments', 'chkMetadataSyncRemoveSegments'],
        ids: ['metadataSyncSettingsContent']
    });
    var saved = null;
    globalThis.ApiClient.updatePluginConfiguration = function (id, config) { saved = config; return Promise.resolve(); };
    var replace = opened.view.querySelector('#chkMetadataSyncReplaceSegments');
    var remove = opened.view.querySelector('#chkMetadataSyncRemoveSegments');
    assert.equal(replace.checked, false);
    assert.equal(remove.checked, false);

    replace.checked = true;
    saveButton(opened.view, 'metadataSyncSettingsContent').click();
    await settle(20);
    assert.equal(saved.MetadataSyncReplaceSegments, true);
    assert.equal(saved.MetadataSyncRemoveSegments, false);
});

// serversync_servers.js
// The Servers tab: the map of servers, each server's steps, and its library and user mappings.

import * as serverSyncModule from './configurationpage?name=serversync_shared.js';

// urlWasCleared
// Says whether a saved entry's Server URL was emptied in the editor.
//
// Inputs
// storedUrl [string]: The Server URL saved for the entry, or null for an entry never saved.
// editedUrl [string]: The Server URL in the editor.
//
// Output
// [boolean]: True when a saved address would be replaced by nothing.
export function urlWasCleared(storedUrl, editedUrl) {
    return !!(storedUrl || '').trim() && !(editedUrl || '').trim();
}

// serversync_servers
// Builds the Servers tab. It draws the server list and the pool map, edits one server at a time, tests its
// connection and sign in, and edits its library and user mappings.
//
// Inputs
// view [HTMLElement]: The page the dashboard passes to the controller.
export default function (view) {
    'use strict';

    var ServerSyncShared = null;
    var createSortableCardList = null;
    var createChoiceGroup = null;
    var confirmDialog = null;

    // Helpers called by bare name, taken from the shared bag.
    var escapeHtml, apiRequest, setVisible, bindClick, getEl, setChecked, getChecked, setValue, getValue, requestFor;

    // Base runs bindPage once and loadConfig each time the page shows, and sets the tabs.
    var made = serverSyncModule.createServerSyncPage(view, { page: 'serversync_servers', bind: function () { bindPage(); }, load: function () { loadConfig(); } });
    ServerSyncShared = made.shared;
    createSortableCardList = serverSyncModule.createSortableCardList;
    createChoiceGroup = serverSyncModule.createChoiceGroup;
    confirmDialog = serverSyncModule.confirmDialog;
    ({ escapeHtml, apiRequest, setVisible, bindClick, getEl, setChecked, getChecked, setValue, getValue, requestFor } = ServerSyncShared.pageHelpers());


    var currentConfig = null;
    var sourceLibraries = [];
    var localLibraries = [];
    var sourceUsers = [];
    var localUsers = [];

    // saveSection
    // Saves one section and reports the result on a status line. Resolves true once the save landed and false
    // when it did not, so a caller never acts on a change the server never kept. The config is replaced only
    // from a save that landed.
    //
    // Inputs
    // mutator [function]: Changes the fresh config in place.
    // statusId [string]: The id of the status element that reports the result.
    // successMessage [string]: What the status says when the save landed.
    // failureMessage [string]: What the status says when it did not.
    //
    // Output
    // [Promise]: Resolves true when the save landed.
    function saveSection(mutator, statusId, successMessage, failureMessage) {
        return ServerSyncShared.saveSection(mutator, statusId, successMessage, failureMessage).then(function (saved) {
            if (saved) currentConfig = saved;
            return !!saved;
        });
    }

    // The configured servers live in config.Servers in rank order. The page
    // keeps that array in memory, edits one entry at a time through a single
    // editor, and posts the whole array on save. Keys round-trip so a renamed
    // or reordered entry keeps its stored API key and its sync rows.

    var servers = [];
    var selectedIndex = -1;

    function newServerKey() {
        var guid = (ServerSyncShared.generateGuid ? ServerSyncShared.generateGuid() : '') || '';
        guid = String(guid).replace(/-/g, '');
        if (guid.length === 32) return guid;
        var out = '';
        for (var i = 0; i < 32; i++) out += Math.floor(Math.random() * 16).toString(16);
        return out;
    }

    function selectedServer() {
        return selectedIndex >= 0 && selectedIndex < servers.length ? servers[selectedIndex] : null;
    }

    // The entry with this key in the current list. A save replaces every entry with a fresh copy, so an
    // answer that arrives after one looks its server up again by key. Writing into the object it started
    // with would change a copy the page no longer shows or saves.
    function serverByKey(key) {
        return servers.find(function (s) { return s.Key === key; }) || null;
    }

    // Whether the server an answer belongs to is still the one open in the editor. A slow answer for a
    // server the operator has since moved away from must not be written into another server's editor,
    // where the next save would store it under that server.
    function stillSelected(key) {
        var open = selectedServer();
        return !!open && open.Key === key;
    }

    // The value a mapping select stands for: what is picked, or the saved id when the list it chooses
    // from could not be loaded, so a failed fetch never saves an empty id over a working mapping.
    function pickedValue(select) {
        return (select && (select.value || select.dataset.savedValue)) || '';
    }

    function pickedName(select) {
        if (!select) return '';
        if (select.value && select.options[select.selectedIndex]) return select.options[select.selectedIndex].textContent;
        return select.dataset.savedName || '';
    }

    // Last known reachability per entry key, for the dot on each card.
    var serverHealth = {};

    // Whether the mapping rows of the open server have been rendered from fetched libraries and users.
    // Until then the rows' selects are empty, and reading them back would save blank mappings.
    var mappingsLoaded = false;

    // The shared card list owns selection, folding, arrows, and drag. This page renders a card's text
    // and reads the editor back before the open card changes.
    var cards = null;

    function serverTitle(server) {
        return server.Name || server.ServerName || server.Url || ServerSyncShared.t.tr('ServerNewName');
    }

    // modeName
    // Names a server's mode in the viewer's language.
    //
    // Inputs
    // mode [string]: Pull, Push, or Sync.
    //
    // Output
    // [string]: The mode's name.
    function modeName(mode) {
        if (mode === 'Push') return ServerSyncShared.t.tr('ModePush');
        if (mode === 'Sync') return ServerSyncShared.t.tr('TabSync');
        return ServerSyncShared.t.tr('ModePull');
    }

    function modeBadgeClass(mode) {
        return mode === 'Sync' ? 'purple' : mode === 'Push' ? 'blue' : 'green';
    }

    function cardSpec(server) {
        var health = serverHealth[server.Key] || (server.Url && server.ApiKey ? 'unknown' : 'none');
        var badges = [{ label: modeName(server.Mode || 'Pull'), cls: modeBadgeClass(server.Mode || 'Pull') }];
        if (server.IsEnabled === false) badges.push({ label: ServerSyncShared.t.tr('Disabled'), cls: 'gray' });
        var dotTitles = {
            ok: ServerSyncShared.t.tr('HealthReachable'),
            bad: ServerSyncShared.t.tr('HealthNotReachable'),
            checking: ServerSyncShared.t.tr('StatusChecking'),
            unknown: ServerSyncShared.t.tr('HealthNotChecked'),
            none: ServerSyncShared.t.tr('HealthNotConfigured')
        };
        return {
            title: serverTitle(server),
            subtitle: server.Url || ServerSyncShared.t.tr('ServerNoUrl'),
            badges: badges,
            dot: health,
            dotTitle: dotTitles[health],
            disabled: server.IsEnabled === false
        };
    }

    function ensureCards() {
        if (cards) return cards;
        cards = createSortableCardList(getEl('serverList'), {
            items: servers,
            editor: getEl('serverEditor'),
            render: cardSpec,
            escapeHtml: escapeHtml,
            // The open card stays open. Another card's head switches to it.
            collapsible: false,
            onBeforeLeave: function () { collectServerEditor(); },
            onSelect: function (index) {
                selectedIndex = index;
                loadServerEditor();
            },
            onMove: function () {
                selectedIndex = cards.getSelected();
                saveServers(ServerSyncShared.t.tr('ToastServerOrderSaved'), ServerSyncShared.t.tr('ErrorServerOrderSave'), null, 'serverListStatus');
            }
        });
        return cards;
    }

    function renderServerList() {
        ensureCards().setItems(servers);
        setVisible('serverEmpty', servers.length === 0);
        renderPoolMap();
    }

    var POOL_DOT_CLASSES = { ok: 'serversync-pool-ok', noPlugin: 'serversync-pool-no-plugin', down: 'serversync-pool-down', disabled: 'serversync-pool-disabled', pending: 'serversync-pool-pending' };

    function modeOf(server) {
        var mode = server.Mode;
        return mode === 1 ? 'Push' : mode === 2 ? 'Sync' : (typeof mode === 'string' ? mode : 'Pull');
    }

    // A server's color: green runs Server Sync and answers, orange answers without it, red does not
    // answer, gray is disabled. One still being checked is drawn dim until the answer comes.
    function poolState(server) {
        if (server.IsEnabled === false) return 'disabled';
        // A server with no address or key cannot be reached, so it is shown as unreachable.
        if (!server.Url || !server.ApiKey) return 'down';
        var health = serverHealth[server.Key];
        var plugin = peerPlugin[server.Key];
        if (health === 'bad' || plugin === 'unreachable') return 'down';
        if (plugin === 'no') return 'noPlugin';
        if (plugin === 'yes') return 'ok';
        return 'pending';
    }

    // Asks each server once whether it runs Server Sync, so the map can color it.
    function checkPoolPlugins() {
        servers.forEach(function (server) {
            if (!server.Url || !server.ApiKey || server.IsEnabled === false || peerPlugin[server.Key] !== undefined) return;
            var key = server.Key;
            var request = requestFor(server);
            request.Mode = modeOf(server);
            peerPlugin[key] = 'checking';
            apiRequest('Hints/CheckPeer', 'POST', request).then(function (result) {
                peerPlugin[key] = result && result.Reachable ? (result.HasPlugin ? 'yes' : 'no') : 'unreachable';
            }).catch(function () {
                peerPlugin[key] = 'unreachable';
            }).then(renderPoolMap);
        });
    }

    // This server as a card in the middle, and every other server as its own pill in a lane that is
    // always in the same place: the servers this one pulls from on the left, the servers it pushes to
    // on the right, and the servers it syncs with below. A lane's pills join one trunk line, so
    // however many servers a lane holds, one line meets the card. The line's color is the color of
    // that relationship's badge on the cards, and its arrow points the way things travel.
    var POOL_LANES = [
        { key: 'Pull', side: 'left', lineClass: 'serversync-pool-pull' },
        { key: 'Push', side: 'right', lineClass: 'serversync-pool-push' },
        { key: 'Sync', side: 'below', lineClass: 'serversync-pool-sync' }
    ];
    var POOL_PILL_H = 26;
    var POOL_PILL_GAP = 8;
    var POOL_REACH = 46;      // from the card to a lane's trunk
    var POOL_STUB = 22;       // from the trunk to a pill
    var POOL_SIDE_ROWS = 8;   // pills in one column of a side lane
    var POOL_BELOW_COLS = 4;  // pills in one row of the lane below

    // This server's own name, for the middle of the map, read once.
    var localServerName = '';
    var localNameAsked = false;

    function askLocalServerName() {
        if (localNameAsked) return;
        localNameAsked = true;
        Promise.resolve(ApiClient.getSystemInfo()).then(function (info) {
            localServerName = (info && info.ServerName) || '';
            renderPoolMap();
        }).catch(function () { /* the map keeps its fallback */ });
    }

    // The name in the middle of the map. A name too long for the circle is shown as the word for Server, and
    // the full name stays in the circle's tooltip.
    var SELF_NAME_CHARS = 12;

    function renderPoolMap() {
        askLocalServerName();
        var host = getEl('poolMap');
        if (!host) return;
        if (!servers.length) {
            host.innerHTML = '';
            setVisible('poolMap', false);
            return;
        }
        setVisible('poolMap', true);

        var members = {};
        servers.forEach(function (server) {
            var mode = modeOf(server);
            (members[mode] = members[mode] || []).push(server);
        });
        var lanes = POOL_LANES.filter(function (lane) { return members[lane.key] && members[lane.key].length; });

        var hereName = (localServerName || ServerSyncShared.t.tr('PoolThisServer')).trim();
        if (hereName.length > SELF_NAME_CHARS) hereName = ServerSyncShared.t.tr('TabServer');
        var cardW = Math.max(92, hereName.length * 7.4 + 32);
        var cardH = 46;
        var parts = ['<defs><marker id="poolArrowHead" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M2 1L8 5L2 9" fill="none" stroke="context-stroke" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></marker></defs>'];
        var bounds = [[-cardW / 2, -cardH / 2], [cardW / 2, cardH / 2]];
        var labels = [];
        var pills = [];
        var fixed = function (n) { return n.toFixed(1); };
        var line = function (d, lineClass, start, end) {
            parts.push('<path class="serversync-pool-line ' + lineClass + '" d="' + d + '"' +
                (start ? ' marker-start="url(#poolArrowHead)"' : '') + (end ? ' marker-end="url(#poolArrowHead)"' : '') + ' />');
        };
        var pill = function (server, x, y, w) {
            var name = serverTitle(server);
            if (name.length > 18) name = name.substring(0, 17) + '\u2026';
            var title = ServerSyncShared.t.format('ListSeparator', serverTitle(server), modeName(modeOf(server)));
            pills.push('<g><title>' + escapeHtml(title) + '</title>' +
                '<rect x="' + fixed(x) + '" y="' + fixed(y) + '" width="' + fixed(w) + '" height="' + POOL_PILL_H + '" rx="6" class="serversync-pool-pill" />' +
                '<circle cx="' + fixed(x + 14) + '" cy="' + fixed(y + POOL_PILL_H / 2) + '" r="4" class="serversync-pool-dot ' + POOL_DOT_CLASSES[poolState(server)] + '" />' +
                '<text class="serversync-pool-name" x="' + fixed(x + 25) + '" y="' + fixed(y + POOL_PILL_H / 2) + '" dominant-baseline="central">' + escapeHtml(name) + '</text></g>');
            labels.push(title);
            bounds.push([x, y], [x + w, y + POOL_PILL_H]);
        };

        // The side lanes are drawn first, so the lane below can start under the taller of them.
        var sideBottom = cardH / 2;
        // Where an overflow column or row starts: centered in the gaps of the first, and one place over
        // when that would put one of its lines on top of the trunk through the middle.
        var overflowStart = function (firstOfFull, pitch, full, count) {
            var skip = Math.max(0, Math.floor((full - 1 - count) / 2));
            var start = firstOfFull + pitch / 2 + skip * pitch;
            for (var n = 0; n < count; n++) {
                if (Math.abs(start + n * pitch) < 1) return start + (skip > 0 ? -pitch : pitch);
            }
            return start;
        };
        lanes.slice().sort(function (a, b) { return (a.side === 'below') - (b.side === 'below'); }).forEach(function (lane) {
            var list = members[lane.key];
            var longest = Math.max.apply(null, list.map(function (s) { return Math.min(serverTitle(s).length, 18); }));
            var w = longest * 6.9 + 38;
            var pitch = POOL_PILL_H + POOL_PILL_GAP;

            if (lane.side === 'below') {
                // Rows of pills under the card. A row's pills hang from one level line, and a second
                // row sits half a pill across so its lines drop through the gaps of the row above.
                // Never more than two rows, since a third could not drop its lines through the first.
                var step = w + 12;
                var busY = sideBottom + POOL_REACH * 0.6;
                var perRow = Math.max(POOL_BELOW_COLS, Math.ceil(list.length / 2));
                var rows = [];
                for (var r = 0; r * perRow < list.length; r++) rows.push(list.slice(r * perRow, (r + 1) * perRow));
                var widest = Math.min(list.length, perRow);
                var xs = [];
                rows.forEach(function (row, r) {
                    var top = busY + POOL_STUB + r * pitch;
                    // The second row is centered under the first, in its gaps.
                    var first = r % 2 ? overflowStart(-((widest - 1) * step) / 2, step, widest, row.length) : -((widest - 1) * step) / 2;
                    row.forEach(function (server, c) {
                        var cx = first + c * step;
                        xs.push(cx);
                        pill(server, cx - w / 2, top, w);
                        line('M' + fixed(cx) + ' ' + fixed(busY) + ' V' + fixed(top - 3), lane.lineClass, false, true);
                    });
                });
                line('M' + fixed(Math.min.apply(null, xs.concat([0]))) + ' ' + fixed(busY) + ' H' + fixed(Math.max.apply(null, xs.concat([0]))), lane.lineClass, false, false);
                line('M0 ' + fixed(cardH / 2 + 3) + ' V' + fixed(busY), lane.lineClass, true, false);
                return;
            }

            // Columns of pills beside the card. A column's pills join one upright line, and a further
            // column sits half a pill down so its lines run through the gaps of the column before it.
            var dir = lane.side === 'left' ? -1 : 1;
            var busX = dir * (cardW / 2 + POOL_REACH);
            // Never more than two columns, since a third could not run its lines through the first.
            var perColumn = Math.max(POOL_SIDE_ROWS, Math.ceil(list.length / 2));
            var columns = [];
            for (var k = 0; k * perColumn < list.length; k++) columns.push(list.slice(k * perColumn, (k + 1) * perColumn));
            var tallest = Math.min(list.length, perColumn);
            var ys = [];
            columns.forEach(function (column, c) {
                var near = busX + dir * (POOL_STUB + c * (w + 14));
                // The second column is centered beside the first, in its gaps.
                var firstY = c % 2 ? overflowStart(-((tallest - 1) * pitch) / 2, pitch, tallest, column.length) : -((tallest - 1) * pitch) / 2;
                column.forEach(function (server, r) {
                    var cy = firstY + r * pitch;
                    ys.push(cy);
                    pill(server, dir < 0 ? near - w : near, cy - POOL_PILL_H / 2, w);
                    // Pull runs from the pill to the trunk. Push runs from the trunk to the pill, where its arrow is.
                    if (dir < 0) line('M' + fixed(near + 3) + ' ' + fixed(cy) + ' H' + fixed(busX), lane.lineClass, false, false);
                    else line('M' + fixed(busX) + ' ' + fixed(cy) + ' H' + fixed(near - 3), lane.lineClass, false, true);
                });
            });
            line('M' + fixed(busX) + ' ' + fixed(Math.min.apply(null, ys.concat([0]))) + ' V' + fixed(Math.max.apply(null, ys.concat([0]))), lane.lineClass, false, false);
            sideBottom = Math.max(sideBottom, Math.max.apply(null, ys) + POOL_PILL_H / 2);
            if (dir < 0) line('M' + fixed(busX) + ' 0 H' + fixed(-cardW / 2 - 3), lane.lineClass, false, true);
            else line('M' + fixed(cardW / 2 + 3) + ' 0 H' + fixed(busX), lane.lineClass, false, false);
        });

        parts = parts.concat(pills);
        parts.push('<rect x="' + fixed(-cardW / 2) + '" y="' + fixed(-cardH / 2) + '" width="' + fixed(cardW) + '" height="' + cardH + '" rx="8" class="serversync-pool-card"><title>' + escapeHtml(localServerName ? ServerSyncShared.t.format('PoolSelfTitle', localServerName) : ServerSyncShared.t.tr('PoolThisServer')) + '</title></rect>');
        parts.push('<text class="serversync-pool-self" x="0" y="0" dominant-baseline="central">' + escapeHtml(hereName) + '</text>');

        var minX = Math.min.apply(null, bounds.map(function (b) { return b[0]; })) - 6;
        var minY = Math.min.apply(null, bounds.map(function (b) { return b[1]; })) - 6;
        var maxX = Math.max.apply(null, bounds.map(function (b) { return b[0]; })) + 6;
        // The card stays in the middle of the picture, whichever lanes are in use.
        var half = Math.max(-minX, maxX);
        minX = -half;
        maxX = half;
        var maxY = Math.max.apply(null, bounds.map(function (b) { return b[1]; })) + 6;
        host.setAttribute('aria-label', ServerSyncShared.t.format('PoolMapLabel', labels.join(', ')));
        host.innerHTML = '<svg viewBox="' + minX.toFixed(0) + ' ' + minY.toFixed(0) + ' ' + (maxX - minX).toFixed(0) + ' ' + (maxY - minY).toFixed(0) + '" width="' + Math.round(maxX - minX) + '" xmlns="http://www.w3.org/2000/svg">' + parts.join('') + '</svg>';
    }

    // Updates each card's dot in place. A full render rebuilds the cards and moves the open editor,
    // which takes the cursor out of whatever field the operator is typing in.
    function updateHealthDots() {
        renderPoolMap();
        view.querySelectorAll('#serverList .base-card-item').forEach(function (card) {
            var server = servers[parseInt(card.getAttribute('data-index'), 10)];
            var dot = card.querySelector('.base-card-item-title .base-dot');
            if (!server || !dot) return;
            var spec = cardSpec(server);
            var color = { ok: 'green', bad: 'red', checking: 'orange', unknown: 'gray' }[spec.dot];
            if (!color) return;
            dot.className = 'base-dot ' + color;
            dot.title = spec.dotTitle || '';
            dot.setAttribute('aria-label', spec.dotTitle || '');
        });
    }

    // Tests every configured entry in the background so the dots mean something. Entries with the
    // kept sentinel resolve to their stored key on the server.
    function refreshServerHealth() {
        servers.forEach(function (server) {
            if (!server.Url || !server.ApiKey) return;
            serverHealth[server.Key] = 'checking';
            apiRequest('TestConnection', 'POST', requestFor(server)).then(function (response) {
                serverHealth[server.Key] = response && response.Success ? 'ok' : 'bad';
                if (response && response.Success) {
                    if (response.IsAdministrator === true) server.AccessLevel = 'Administrator';
                    else if (response.IsAdministrator === false) server.AccessLevel = 'User';
                    if (response.UserId) {
                        server.AuthenticatedUserId = response.UserId;
                        server.AuthenticatedUser = response.UserName || server.AuthenticatedUser || '';
                    }
                    if (response.CanDownload === true || response.CanDownload === false) server.CanDownload = response.CanDownload;
                    server.SyncAccess = response.SyncAccess || '';
                    server.SyncAccessMessage = response.SyncAccessMessage || '';
                    if (stillSelected(server.Key)) reflectAccess(server);
                }
            }).catch(function () {
                serverHealth[server.Key] = 'bad';
            }).then(function () {
                updateHealthDots();
            });
        });
        renderServerList();
    }

    // Either a pasted API key or a sign in that yields a token. The two never show at once.

    var authGroup = null;
    var modeGroup = null;

    function getAuthMethod() {
        return (authGroup && authGroup.get()) || 'key';
    }

    function setAuthMethod(method) {
        if (authGroup) authGroup.set(method);
        reflectAuthMethod();
    }

    function reflectAuthMethod() {
        var method = getAuthMethod();
        setVisible('authKeyPanel', method === 'key');
        setVisible('authUserPanel', method === 'user');
        reflectAddressChange();
    }

    function normalizeId(id) {
        return (id || '').replace(/-/g, '').toLowerCase();
    }

    function isStandardUser(server) {
        return !!server && server.AccessLevel === 'User';
    }

    function maySyncUser(server, sourceUserId) {
        if (!isStandardUser(server)) return true;
        return !!sourceUserId && normalizeId(sourceUserId) === normalizeId(server.AuthenticatedUserId);
    }

    // accessRows
    // Lists what the key can do there, one row for each module, from the access the last test or sign in found.
    //
    // Inputs
    // server [object]: The open server.
    //
    // Output
    // [array[]]: Each row as the key of its title, its state, and its text.
    function accessRows(server) {
        var t = ServerSyncShared.t;
        var admin = server.AccessLevel === 'Administrator';
        var who = server.AuthenticatedUser || t.tr('AccessThisUser');
        var downloads = server.CanDownload === false
            ? ['no', t.format('AccessDownloadsRefused', server.AuthenticatedUser || t.tr('AccessThisAccount'))]
            : ['ok', t.tr('AccessAllowed')];
        if (admin) {
            return [
                ['AccessContentListing', 'ok', t.tr('AccessContentAll')],
                ['AccessFileDownloads', downloads[0], downloads[1]],
                ['AccessMetadata', 'ok', t.tr('PermissionEveryItem')],
                ['AccessWatchHistory', 'ok', t.tr('AccessMappedUsers')],
                ['AccessUserSettings', 'ok', t.tr('AccessMappedUsers')],
                ['Playlists', 'ok', t.tr('AccessPlaylistsAll')],
                ['AccessTwoWayHistory', 'ok', t.tr('AccessTwoWayAll')],
                ['AccessPushAndSync', 'ok', t.tr('AccessPushAll')]
            ];
        }
        return [
            ['AccessContentListing', 'limited', t.format('AccessContentLimited', who)],
            ['AccessFileDownloads', downloads[0], downloads[1]],
            ['AccessMetadata', 'limited', t.format('AccessMetadataLimited', who)],
            ['AccessWatchHistory', 'limited', t.format('AccessHistoryLimited', who)],
            ['AccessUserSettings', 'limited', t.format('AccessSettingsLimited', who)],
            ['Playlists', 'limited', t.format('AccessPlaylistsLimited', who)],
            ['AccessTwoWayHistory', 'limited', t.format('AccessTwoWayLimited', who)],
            ['AccessPushAndSync', 'limited', t.format('AccessPushLimited', who)]
        ];
    }

    // The key of each access state's badge.
    var ACCESS_LABELS = { ok: 'LabelAvailable', limited: 'BadgeLimited', no: 'BadgeUnavailable', none: 'LabelNotInstalled' };
    var ACCESS_BADGES = { ok: 'green', limited: 'orange', no: 'red', none: 'gray' };

    function syncAccessRow(server) {
        if (!server.SyncAccess) return null;
        var state = server.SyncAccess === 'Allowed' ? 'ok' : server.SyncAccess === 'NotInstalled' ? 'none' : 'no';
        var detail = server.SyncAccessMessage || '';
        if (state === 'no') detail = ServerSyncShared.t.format('AccessSyncRoutesFix', detail);
        return ['AccessSyncRoutes', state, detail];
    }

    // What the key can do there, learned by the last test or sign in.
    function reflectAccess(server) {
        var level = (server && server.AccessLevel) || '';
        var known = level === 'Administrator' || level === 'User';
        setVisible('accessUnknown', !!server && !!server.Url && !known);
        setVisible('accessSummary', known);
        if (known) {
            var headline = getEl('accessHeadline');
            if (headline) {
                headline.className = 'serversync-mb-md ' + (level === 'Administrator' ? 'base-success' : 'base-warn');
                headline.textContent = level !== 'Administrator'
                    ? ServerSyncShared.t.format('AccessStandardAs', server.AuthenticatedUser || ServerSyncShared.t.tr('AccessUnknownUser'))
                    : server.AuthenticatedUser ? ServerSyncShared.t.format('AccessAdminAs', server.AuthenticatedUser) : ServerSyncShared.t.tr('AccessAdmin');
            }
            var rows = getEl('accessRows');
            if (rows) {
                var list = accessRows(server);
                var endpoints = syncAccessRow(server);
                var reach = !endpoints ? ['limited', ServerSyncShared.t.tr('AccessCheckingRoutes')] : endpoints[1] !== 'ok' ? ['no', ServerSyncShared.t.tr('AccessNeedsRoutes')] : null;
                if (reach) {
                    list = list.map(function (row) {
                        return row[0] === 'AccessTwoWayHistory' || row[0] === 'AccessPushAndSync' ? [row[0], reach[0], reach[1]] : row;
                    });
                }
                if (endpoints) list.unshift(endpoints);
                rows.innerHTML = list.map(function (row) {
                    return '<div class="serversync-access-row">' +
                        '<div class="serversync-access-text">' +
                            '<div class="serversync-access-title">' + escapeHtml(ServerSyncShared.t.tr(row[0])) + '</div>' +
                            '<div class="serversync-access-description">' + escapeHtml(row[2]) + '</div>' +
                        '</div>' +
                        '<span class="base-badge ' + ACCESS_BADGES[row[1]] + '">' + escapeHtml(ServerSyncShared.t.tr(ACCESS_LABELS[row[1]])) + '</span>' +
                        '</div>';
                }).join('');
            }
        }
        reflectModeLimits(server);
        reflectUserLimits(server);
    }

    function reflectModeLimits(server) {
        setVisible('modeLimited', isStandardUser(server) && getMode() !== 'Pull');
    }

    function reflectUserLimits(server) {
        var limited = isStandardUser(server);
        var note = getEl('usersLimited');
        if (note) {
            note.textContent = limited
                ? ServerSyncShared.t.format('WarnStandardUserKey', server.AuthenticatedUser || ServerSyncShared.t.tr('AccessThatUser'))
                : '';
        }
        setVisible('usersLimited', limited);
        updateUserSelects();
    }

    function reflectSignedIn(server) {
        var signedIn = !!(server && server.AuthenticatedUser);
        setVisible('authSignedIn', signedIn);
        var note = getEl('authSignedIn');
        // The user's name is set in bold, so the sentence is escaped first and the name put in after.
        if (note) note.innerHTML = signedIn ? escapeHtml(ServerSyncShared.t.tr('MessageSignedInAs')).replace('{0}', '<strong>' + escapeHtml(server.AuthenticatedUser) + '</strong>') : '';
    }

    // A token the page never saw round trips as the sentinel. Anything stored comes back encrypted.
    function SecretLooksStored(value) {
        return typeof value === 'string' && value.indexOf('enc:') === 0;
    }

    // Whether two server addresses name the same server, ignoring case and a trailing slash. Mirrors
    // the server's own rule, which decides whether a saved key or token may go to an address.
    function sameServerUrl(a, b) {
        var left = (a || '').trim();
        var right = (b || '').trim();
        try {
            var ua = new URL(left);
            var ub = new URL(right);
            return ua.protocol === ub.protocol
                && ua.hostname.toLowerCase() === ub.hostname.toLowerCase()
                && ua.port === ub.port
                && ua.pathname.replace(/\/+$/, '').toLowerCase() === ub.pathname.replace(/\/+$/, '').toLowerCase();
        } catch (e) {
            return left.replace(/\/+$/, '').toLowerCase() === right.replace(/\/+$/, '').toLowerCase();
        }
    }

    // The address saved for an entry, or null for an entry that was never saved.
    function storedUrlFor(key) {
        var saved = currentConfig && currentConfig.Servers
            ? currentConfig.Servers.find(function (s) { return s.Key === key; })
            : null;
        return saved ? (saved.Url || '') : null;
    }

    // Whether the open entry holds a saved key or token that the edited address would lose. The server
    // only keeps a saved secret for the address it was saved with, so a new address needs a new key
    // or a new sign in. A token from a sign in made on this page is new and belongs to the new address.
    function savedSecretLeftBehind(server) {
        if (!server || !(server.ApiKey === ServerSyncShared.SECRET_KEPT || SecretLooksStored(server.ApiKey))) return false;
        var stored = storedUrlFor(server.Key);
        // An emptied address is refused on save, so it never costs the entry its key.
        if (urlWasCleared(stored, getValue('txtSourceServerUrl', ''))) return false;
        return stored !== null && !sameServerUrl(getValue('txtSourceServerUrl', ''), stored);
    }

    // Clears the kept placeholder and says what to do while the address differs from the saved one,
    // and puts the placeholder back when the address returns to it.
    function reflectAddressChange() {
        var server = selectedServer();
        // A new address may be another server, so whether it runs Server Sync is asked again.
        if (server) delete peerPlugin[server.Key];
        var moved = savedSecretLeftBehind(server);
        var keyField = getEl('txtSourceServerApiKey');
        if (keyField) {
            if (moved && keyField.value === ServerSyncShared.SECRET_KEPT) keyField.value = '';
            if (!moved && !keyField.value && server && server.ApiKey) keyField.value = ServerSyncShared.SECRET_KEPT;
        }
        var note = getEl('savedSecretNote');
        if (note) {
            note.textContent = !moved ? '' : getAuthMethod() === 'user'
                ? ServerSyncShared.t.format('WarnSignInUrlChanged', storedUrlFor(server.Key))
                : ServerSyncShared.t.format('WarnApiKeyUrlChanged', storedUrlFor(server.Key));
        }
        setVisible('savedSecretNote', moved);
    }

    function getMode() {
        return (modeGroup && modeGroup.get()) || 'Pull';
    }

    function setMode(mode) {
        if (modeGroup) modeGroup.set(mode);
        reflectMode();
    }

    function reflectMode() {
        setVisible('peerCheckArea', true);
        reflectStream();
        reflectModeLimits(selectedServer());
        ServerSyncShared.setStatus('peerCheckStatus', '', false);
        setVisible('peerCheckResult', false);
    }

    // The answer to a link check: a verdict in the color of its severity, then one line for each
    // thing learned, with a badge. A check that ended early has only a sentence, shown beside the button.
    function showPeerCheck(result) {
        var severity = (result && result.Severity) || 'error';
        var rows = (result && result.Details) || [];
        if (!rows.length) {
            setVisible('peerCheckResult', false);
            ServerSyncShared.setStatus('peerCheckStatus', (result && result.Message) || ServerSyncShared.t.tr('ErrorNoAnswer'), severity !== 'ok', { timeout: 0 });
            return;
        }
        ServerSyncShared.setStatus('peerCheckStatus', '', false);
        var headline = getEl('peerCheckHeadline');
        if (headline) {
            headline.className = 'serversync-mb-md ' + (severity === 'ok' ? 'base-success' : severity === 'warn' ? 'base-warn' : 'base-error');
            headline.textContent = result.Headline || result.Message;
        }
        var grid = getEl('peerCheckRows');
        if (grid) {
            grid.innerHTML = rows.map(function (row) {
                return '<div class="serversync-access-row">' +
                    '<div class="serversync-access-text">' +
                        '<div class="serversync-access-title">' + escapeHtml(row.Title) + '</div>' +
                        '<div class="serversync-access-description">' + escapeHtml(row.Text) + '</div>' +
                    '</div>' +
                    '<span class="base-badge ' + (ACCESS_BADGES[row.State] || 'gray') + '">' + escapeHtml(row.Badge) + '</span>' +
                    '</div>';
            }).join('');
        }
        setVisible('peerCheckResult', true);
    }

    // Asks the other server whether it runs Server Sync, accepts hints, and lists this server, so a
    // link that would only fail in the queue is caught here.
    function checkPeer() {
        var server = collectServerEditor();
        if (!server) return;
        if (!server.Url || !server.ApiKey) {
            ServerSyncShared.setStatus('peerCheckStatus', ServerSyncShared.t.tr('ErrorEnterUrlAndKey'), true);
            return;
        }
        ServerSyncShared.setStatus('peerCheckStatus', ServerSyncShared.t.tr('StatusCheckingWork'), false, { timeout: 0 });
        setVisible('peerCheckResult', false);
        var key = server.Key;
        var request = requestFor(server);
        request.Mode = getMode();
        ServerSyncShared.apiRequest('Hints/CheckPeer', 'POST', request).then(function (result) {
            peerPlugin[key] = result && result.Reachable ? (result.HasPlugin ? 'yes' : 'no') : 'unreachable';
            if (stillSelected(key)) reflectLinkingPlugin();
            if (result && result.Reachable) {
                serverHealth[key] = 'ok';
                var current = serverByKey(key);
                if (current && result.ServerName) current.ServerName = result.ServerName;
                if (current && result.ServerId) current.ServerId = result.ServerId;
            }
            // The status line belongs to whichever server is open, so an answer for one the operator has
            // left only updates that server's dot.
            if (!stillSelected(key)) {
                updateHealthDots();
                return;
            }
            showPeerCheck(result);
            if (result && result.Reachable) renderServerList();
        }).catch(function () {
            if (!stillSelected(key)) return;
            ServerSyncShared.setStatus('peerCheckStatus', ServerSyncShared.t.tr('ErrorCheckLink'), true);
        });
    }

    // Fills the editor from the selected entry. The stored key is never shown. The sentinel
    // round-trips and the server keeps the existing secret.
    function loadServerEditor() {
        var server = selectedServer();
        if (!server) return;

        setValue('txtServerName', server.Name || '');
        setMode(server.Mode || 'Pull');
        setChecked('chkServerEnabled', server.IsEnabled !== false);
        setValue('txtSourceServerUrl', server.Url || '');
        setValue('txtSourceServerExternalUrl', server.ExternalUrl || '');
        setChecked('chkAllowPrivateNetwork', server.AllowPrivateNetwork !== false);
        setChecked('chkAllowLinking', server.AllowLinking === true);
        setValue('txtLinkMaxStreams', String(server.LinkMaxStreams || 0));
        setChecked('chkLinkCountOwnSessions', server.LinkCountOwnSessions === true);
        setChecked('chkLinkHonorBitrateLimit', server.LinkHonorBitrateLimit === true);
        fillFallbackUsers(server.LinkFallbackUserId || '');
        reflectLinking();
        clampUserStreams();
        setValue('txtSourceServerApiKey', server.ApiKey ? ServerSyncShared.SECRET_KEPT : '');
        setValue('txtAuthUsername', server.AuthenticatedUser || '');
        setValue('txtAuthPassword', '');
        setAuthMethod(server.AuthenticatedUser ? 'user' : 'key');
        reflectSignedIn(server);
        reflectAccess(server);
        reflectAddressChange();

        var nameEl = getEl('txtSourceServerName');
        var idEl = getEl('txtSourceServerId');
        if (nameEl) nameEl.textContent = server.ServerName || ServerSyncShared.t.tr('Unknown');
        if (idEl) idEl.textContent = server.ServerId || ServerSyncShared.t.tr('Unknown');
        setVisible('serverInfoContainer', !!(server.ServerName || server.ServerId));

        var authUserEl = getEl('txtAuthenticatedUser');
        if (authUserEl) authUserEl.textContent = server.AuthenticatedUser || '';
        setVisible('authenticatedUserRow', !!server.AuthenticatedUser);

        ServerSyncShared.setStatus('connectionStatus', '', false);
        ServerSyncShared.setStatus('tokenGeneratorStatus', '', false);

        sourceLibraries = [];
        sourceUsers = [];
        var configured = !!(server.Url && server.ApiKey);
        setMappingSectionsVisible(configured);
        if (configured) {
            loadMappings(server.Key);
        } else {
            renderLibraryMappings([]);
            renderUserMappings([]);
            setMappingsLoaded(true);
        }
    }

    // Marks whether the open server's mapping rows are ready, and enables the add buttons only then. A
    // row added before the lists arrive would be wiped when the fetched mappings are rendered.
    function setMappingsLoaded(loaded) {
        mappingsLoaded = loaded;
        ['btnAddMapping', 'btnAddUserMapping'].forEach(function (id) {
            var btn = getEl(id);
            if (btn) btn.disabled = !loaded;
        });
    }

    // Fetches the libraries and users of the server with this key, then renders its mapping rows from
    // them. The rows on screen are replaced by a loading note at once, so rows left over from the server
    // shown before, or from before a reconnect, can never be edited and then silently dropped.
    function loadMappings(key) {
        setMappingsLoaded(false);
        clearMappingContainer('libraryMappingsContainer', ServerSyncShared.t.tr('StatusLoadingLibraryMappings'));
        clearMappingContainer('userMappingsContainer', ServerSyncShared.t.tr('StatusLoadingUserMappings'));
        var server = serverByKey(key);
        if (!server) return Promise.resolve();
        return Promise.all([fetchSourceLibraries(server), fetchSourceUsers(server)]).then(function () {
            if (!stillSelected(key)) return;
            var current = serverByKey(key);
            renderLibraryMappings((current && current.LibraryMappings) || []);
            renderUserMappings((current && current.UserMappings) || []);
            setMappingsLoaded(true);
        });
    }

    // Empties a mapping container and shows a note in place of its rows when one is given.
    function clearMappingContainer(containerId, note) {
        var container = getEl(containerId);
        if (!container) return;
        container.innerHTML = '';
        if (note) {
            var noteEl = document.createElement('div');
            noteEl.className = 'fieldDescription';
            noteEl.textContent = note;
            container.appendChild(noteEl);
        }
    }

    function setMappingSectionsVisible(visible) {
        setVisible('librariesSection', visible);
        setVisible('usersSection', visible);
        setVisible('librariesLocked', !visible);
        setVisible('usersLocked', !visible);
    }

    // Reads the editor back into the selected entry.
    function collectServerEditor() {
        var server = selectedServer();
        if (!server) return null;
        server.Name = getValue('txtServerName', '').trim();
        server.Mode = getMode();
        server.IsEnabled = getChecked('chkServerEnabled');
        // Read before the address is taken from the editor. The saved secret stays with the saved
        // address, so the entry carries no key until one is entered or signed in again.
        var secretLeftBehind = savedSecretLeftBehind(server);
        server.Url = getValue('txtSourceServerUrl', '').trim();
        server.ExternalUrl = getValue('txtSourceServerExternalUrl', '').trim();
        server.AllowPrivateNetwork = getChecked('chkAllowPrivateNetwork');
        server.AllowLinking = getChecked('chkAllowLinking');
        clampUserStreams();
        server.LinkMaxStreams = Math.max(0, parseInt(getValue('txtLinkMaxStreams', '0'), 10) || 0);
        server.LinkCountOwnSessions = getChecked('chkLinkCountOwnSessions');
        server.LinkHonorBitrateLimit = getChecked('chkLinkHonorBitrateLimit');
        server.LinkFallbackUserId = getValue('selLinkFallbackUser', '');
        if (getAuthMethod() === 'key') {
            var apiKey = getValue('txtSourceServerApiKey', '').trim();
            // Text typed after the kept placeholder is a new key, never the placeholder plus a suffix.
            if (apiKey.length > ServerSyncShared.SECRET_KEPT.length && apiKey.indexOf(ServerSyncShared.SECRET_KEPT) === 0) {
                apiKey = apiKey.substring(ServerSyncShared.SECRET_KEPT.length);
            }
            // An empty field on an entry that has a stored key means "keep it", the same as the sentinel,
            // unless the address changed, which the stored key may not follow.
            server.ApiKey = apiKey || (server.ApiKey && !secretLeftBehind ? ServerSyncShared.SECRET_KEPT : '');
            // A pasted key belongs to no signed in user.
            if (apiKey && apiKey !== ServerSyncShared.SECRET_KEPT) {
                server.AuthenticatedUser = '';
                server.AuthenticatedUserId = '';
                server.AccessLevel = '';
                server.CanDownload = null;
                server.SyncAccess = '';
                server.SyncAccessMessage = '';
            }
        } else if (!server.ApiKey) {
            server.ApiKey = '';
        } else if (secretLeftBehind) {
            // The token was issued by the saved address. A new address needs a new sign in.
            server.ApiKey = '';
            server.AuthenticatedUser = '';
            server.AuthenticatedUserId = '';
            server.AccessLevel = '';
            server.CanDownload = null;
            server.SyncAccess = '';
            server.SyncAccessMessage = '';
        } else {
            // Signed in: the token lives in the entry already, and the field is not in play.
            server.ApiKey = server.ApiKey === ServerSyncShared.SECRET_KEPT || SecretLooksStored(server.ApiKey) ? ServerSyncShared.SECRET_KEPT : server.ApiKey;
        }
        // Mapping rows are read back only once they were rendered from fetched data, otherwise a save
        // made while they load, such as a reorder, would replace real mappings with blank ones.
        if (mappingsLoaded && !getEl('librariesSection').classList.contains('hidden')) {
            server.LibraryMappings = collectLibraryMappings();
        }
        if (mappingsLoaded && !getEl('usersSection').classList.contains('hidden')) {
            server.UserMappings = collectUserMappings();
        }
        return server;
    }

    function selectServer(index) {
        ensureCards().setItems(servers);
        setVisible('serverEmpty', servers.length === 0);
        if (servers.length === 0 || index < 0) {
            selectedIndex = -1;
            cards.setSelected(-1);
            return;
        }
        // setSelected raises onSelect, which sets selectedIndex and loads the editor.
        cards.setSelected(Math.max(0, Math.min(index, servers.length - 1)));
    }

    function setStepOpen(target, open) {
        var header = view.querySelector('.base-collapsible-header[data-target="' + target + '"]');
        var content = getEl(target);
        if (!header || !content) return;
        header.classList.toggle('collapsed', !open);
        content.classList.toggle('collapsed', !open);
        header.setAttribute('aria-expanded', String(open));
    }

    // The steps start closed. A brand new server has nothing to do but connect, so that step opens.
    function addServer() {
        collectServerEditor();
        setStepOpen('serverStepConnection', true);
        setStepOpen('serverStepAuthentication', true);
        servers.push({
            Key: newServerKey(),
            Name: '',
            Url: '',
            ExternalUrl: '',
            AllowPrivateNetwork: true,
            ApiKey: '',
            AuthenticatedUser: '',
            AuthenticatedUserId: '',
            ServerName: '',
            ServerId: '',
            Mode: 'Pull',
            IsEnabled: true,
            LibraryMappings: [],
            UserMappings: []
        });
        selectServer(servers.length - 1);
    }

    function deleteServer() {
        var server = selectedServer();
        if (!server) return;
        var label = server.Name || server.ServerName || server.Url || ServerSyncShared.t.tr('ServerThisServer');
        var question = '<p>' + escapeHtml(ServerSyncShared.t.format('ConfirmRemoveServer', label)) + '</p>' +
            '<p>' + escapeHtml(ServerSyncShared.t.tr('ConfirmRemoveServerDetail')) + '</p>';
        confirmDialog(question, {
            html: true,
            title: ServerSyncShared.t.tr('ButtonRemoveServer'),
            confirmText: ServerSyncShared.t.tr('ButtonRemove'),
            destructive: true
        }).then(function (confirmed) {
            // A save landing while the dialog is open replaces the entries with fresh copies, so the
            // entry removed is found again by its key.
            var removedIndex = servers.findIndex(function (s) { return s === server || (server.Key && s.Key === server.Key); });
            if (!confirmed || removedIndex < 0) {
                return;
            }
            var removedKey = servers[removedIndex].Key;
            var removedId = servers[removedIndex].ServerId || '';

            // The list on the page changes only once the save lands. Removing it first would leave the page
            // showing a list the server never kept, and the rows would be forgotten for a server still in use.
            var remaining = servers.filter(function (s, i) { return i !== removedIndex; });
            saveServers(ServerSyncShared.t.tr('ToastServerRemoved'), ServerSyncShared.t.tr('ErrorServerRemove'), remaining, 'serverListStatus').then(function (saved) {
                if (!saved) {
                    return;
                }

                selectServer(servers.length > 0 ? Math.min(removedIndex, servers.length - 1) : -1);
                // The rows it tracked and the hints it sent have nothing to pull from now. Failing to clear
                // them is not fatal. The tables simply keep inert rows until a reset.
                if (removedKey) {
                    apiRequest('Servers/' + encodeURIComponent(removedKey) + '/Rows' + (removedId ? '?serverId=' + encodeURIComponent(removedId) : ''), 'DELETE').catch(function () {});
                }
            });
        });
    }

    // saveServers
    // Posts the in memory list. The server resolves kept sentinels against the stored entry with the same key,
    // so keys are never retyped. A list other than the page's own, such as the list without a server being
    // removed, can be saved, and becomes the page's list only on success.
    //
    // Inputs
    // successMessage [string]: What the status says when the save landed.
    // failureMessage [string]: What the status says when it did not.
    // list [object[]]: The list to save, instead of the page's own.
    // statusId [string]: The status element that reports the result. Defaults to the editor's.
    //
    // Output
    // [Promise]: Resolves true when the save landed.
    function saveServers(successMessage, failureMessage, list, statusId) {
        var snapshot = JSON.parse(JSON.stringify(list || servers));
        // A save made for another reason never empties a saved address.
        snapshot.forEach(function (entry) {
            var stored = storedUrlFor(entry.Key);
            if (urlWasCleared(stored, entry.Url)) entry.Url = stored;
        });
        return saveSection(function (config) {
            keepStoredStorageFields(config.Servers, snapshot);
            config.Servers = snapshot;
        }, statusId || 'saveServerStatus', successMessage || ServerSyncShared.t.tr('ToastServersSaved'), failureMessage || ServerSyncShared.t.tr('ErrorServersSave')).then(function (saved) {
            // The saved list becomes the page's list. Its entries are fresh copies, which is why answers
            // that arrive later look their server up again with serverByKey.
            if (saved && currentConfig && currentConfig.Servers) {
                servers = currentConfig.Servers;
                renderServerList();
            }
            return saved;
        });
    }

    // Push and Sync, and a library kept as links, only work when the other server runs a Server Sync
    // this one can talk to. A plain Jellyfin server can only be pulled from, as copies. So a server
    // that answers and does not run it, or runs one too far from this version, is not saved that way. One that cannot be reached is saved, since it cannot be asked.
    function saveServerConfig() {
        var server = collectServerEditor();
        if (!server) return;
        ServerSyncShared.setStatus('saveServerStatus', '', false);
        if (urlWasCleared(storedUrlFor(server.Key), server.Url)) {
            ServerSyncShared.setStatus('saveServerStatus', ServerSyncShared.t.tr('ErrorEnterUrlNotSaved'), true, { timeout: 0 });
            return;
        }
        var links = (server.LibraryMappings || []).some(function (m) { return m.IsEnabled !== false && (m.StorageMode === 'Link' || m.StorageMode === 1); });
        if ((modeOf(server) === 'Pull' && !links) || !server.Url || !server.ApiKey) {
            saveServers(ServerSyncShared.t.tr('ToastServerSaved'), ServerSyncShared.t.tr('ErrorServerSave'));
            return;
        }

        var request = requestFor(server);
        request.Mode = modeOf(server);
        apiRequest('Hints/CheckPeer', 'POST', request).then(function (result) {
            if (result && result.Reachable && !result.HasPlugin) {
                peerPlugin[server.Key] = 'no';
                ServerSyncShared.setStatus('saveServerStatus', ServerSyncShared.t.format('ErrorPeerNoPlugin', serverTitle(server)), true, { timeout: 0 });
                return;
            }
            // Its Server Sync and this one cannot talk until one is updated, which leaves it where a
            // server without Server Sync is: nothing sent to it would be taken, and no link would play.
            if (result && result.Reachable && result.Incompatible) {
                ServerSyncShared.setStatus('saveServerStatus', ServerSyncShared.t.format('ErrorPeerIncompatible', result.Message || ServerSyncShared.t.format('ErrorPeerIncompatibleReason', serverTitle(server))), true, { timeout: 0 });
                return;
            }

            saveServers(result && result.Reachable ? ServerSyncShared.t.tr('ToastServerSaved') : ServerSyncShared.t.tr('ToastServerSavedUnreachable'), ServerSyncShared.t.tr('ErrorServerSave'));
        }).catch(function () {
            saveServers(ServerSyncShared.t.tr('ToastServerSaved'), ServerSyncShared.t.tr('ErrorServerSave'));
        });
    }

    function testConnection() {
        var server = collectServerEditor();
        if (!server) return;

        if (!server.Url || !server.ApiKey) {
            ServerSyncShared.setStatus('connectionStatus', ServerSyncShared.t.tr('ErrorEnterUrlAndKey'), true);
            return;
        }

        ServerSyncShared.setStatus('connectionStatus', ServerSyncShared.t.tr('StatusTesting'), false, { timeout: 0 });

        var key = server.Key;
        apiRequest('TestConnection', 'POST', requestFor(server)).then(function(response) {
            if (!stillSelected(key)) {
                serverHealth[key] = response && response.Success ? 'ok' : 'bad';
                updateHealthDots();
                return;
            }

            if (response && response.Success) {
                // The editor is read back into the entry that holds this key now. A save while the test
                // ran replaced the entry it started with, and the operator may have edited fields since.
                var current = collectServerEditor();
                ServerSyncShared.setStatus('connectionStatus', ServerSyncShared.t.format('ToastConnectedTo', response.ServerName || ServerSyncShared.t.tr('ServerTheServer')), false);
                current.ServerName = response.ServerName || '';
                current.ServerId = response.ServerId || '';
                if (response.IsAdministrator === true) current.AccessLevel = 'Administrator';
                else if (response.IsAdministrator === false) current.AccessLevel = 'User';
                if (response.UserId) {
                    current.AuthenticatedUserId = response.UserId;
                    current.AuthenticatedUser = response.UserName || current.AuthenticatedUser || '';
                }
                current.CanDownload = response.CanDownload === true || response.CanDownload === false ? response.CanDownload : null;
                current.SyncAccess = response.SyncAccess || '';
                current.SyncAccessMessage = response.SyncAccessMessage || '';
                reflectAccess(current);
                var nameEl = getEl('txtSourceServerName');
                var idEl = getEl('txtSourceServerId');
                if (nameEl) nameEl.textContent = current.ServerName || ServerSyncShared.t.tr('Unknown');
                if (idEl) idEl.textContent = current.ServerId || ServerSyncShared.t.tr('Unknown');
                setVisible('serverInfoContainer', true);
                serverHealth[key] = 'ok';
                renderServerList();

                setMappingSectionsVisible(true);
                loadMappings(key);
            } else {
                ServerSyncShared.setStatus('connectionStatus', (response && response.Message) || ServerSyncShared.t.tr('ErrorConnect'), true, { timeout: 10000 });
                serverHealth[key] = 'bad';
                renderServerList();
            }
        }).catch(function() {
            serverHealth[key] = 'bad';
            if (!stillSelected(key)) {
                updateHealthDots();
                return;
            }
            ServerSyncShared.setStatus('connectionStatus', ServerSyncShared.t.tr('ErrorConnect'), true);
            renderServerList();
        });
    }

    function generateToken() {
        var server = collectServerEditor();
        var usernameEl = getEl('txtAuthUsername');
        var passwordEl = getEl('txtAuthPassword');
        if (!server) return;

        var username = usernameEl ? usernameEl.value : '';
        var password = passwordEl ? passwordEl.value : '';

        if (!server.Url) {
            ServerSyncShared.setStatus('tokenGeneratorStatus', ServerSyncShared.t.tr('ErrorEnterUrl'), true);
            return;
        }

        if (!username || !password) {
            ServerSyncShared.setStatus('tokenGeneratorStatus', ServerSyncShared.t.tr('ErrorEnterCredentials'), true);
            return;
        }

        ServerSyncShared.setStatus('tokenGeneratorStatus', ServerSyncShared.t.tr('StatusAuthenticating'), false, { timeout: 0 });

        var key = server.Key;
        apiRequest('Authenticate', 'POST', {
            ServerUrl: server.Url,
            Username: username,
            Password: password,
            AllowPrivateNetwork: server.AllowPrivateNetwork !== false
        }).then(function(response) {
            if (!response || !response.Success) {
                // The status line belongs to whichever server is open, so a failure for one the operator
                // has left is not written over the other server's editor.
                if (stillSelected(key)) ServerSyncShared.setStatus('tokenGeneratorStatus', (response && response.Message) || ServerSyncShared.t.tr('ErrorSignIn'), true, { timeout: 10000 });
                return;
            }

            var selected = stillSelected(key);
            // A save while the sign in ran replaced every entry with a fresh copy, so the token goes into
            // the entry that holds this key now, which is the one the next save sends. When the server is
            // still open, the editor is read back first so edits made meanwhile are saved with the token.
            var current = selected ? collectServerEditor() : serverByKey(key);
            if (!current) {
                ServerSyncShared.setStatus('serverListStatus', ServerSyncShared.t.tr('ErrorSignedInServerRemoved'), true, { timeout: 10000 });
                return;
            }

            current.ApiKey = response.AccessToken;
            current.AuthenticatedUser = response.Username || username;
            current.AuthenticatedUserId = response.UserId || '';
            current.ServerName = response.ServerName || current.ServerName || '';
            current.ServerId = response.ServerId || current.ServerId || '';
            if (response.IsAdministrator === true) current.AccessLevel = 'Administrator';
            else if (response.IsAdministrator === false) current.AccessLevel = 'User';
            current.CanDownload = response.CanDownload === true || response.CanDownload === false ? response.CanDownload : null;
            current.SyncAccess = response.SyncAccess || '';
            current.SyncAccessMessage = response.SyncAccessMessage || '';

            if (!selected) {
                // The operator moved to another server meanwhile. The key still belongs to this one, so it
                // is stored on it and saved, without touching the editor that now shows the other.
                saveServers(ServerSyncShared.t.tr('ToastTokenSaved'), ServerSyncShared.t.tr('ErrorTokenSave'), null, 'serverListStatus');
                return;
            }

            // Clear the password field for security
            if (passwordEl) passwordEl.value = '';

            reflectAccess(current);
            setValue('txtSourceServerApiKey', response.AccessToken);

            var authUserEl = getEl('txtAuthenticatedUser');
            if (authUserEl) authUserEl.textContent = current.AuthenticatedUser;
            setVisible('authenticatedUserRow', true);
            reflectSignedIn(current);

            var nameEl = getEl('txtSourceServerName');
            var idEl = getEl('txtSourceServerId');
            if (nameEl) nameEl.textContent = current.ServerName || ServerSyncShared.t.tr('Unknown');
            if (idEl) idEl.textContent = current.ServerId || ServerSyncShared.t.tr('Unknown');
            setVisible('serverInfoContainer', true);

            // The success line shows only once the save landed, since a token the server never stored
            // would be lost on the next page load.
            saveServers(ServerSyncShared.t.tr('ToastTokenSaved'), ServerSyncShared.t.tr('ErrorTokenSave'), null, 'tokenGeneratorStatus').then(function(ok) {
                if (!stillSelected(key) || !ok) return;
                serverHealth[key] = 'ok';
                renderServerList();
                setMappingSectionsVisible(true);
                loadMappings(key);
            });
        }).catch(function(error) {
            console.error('Token generation error:', error);
            if (stillSelected(key)) ServerSyncShared.setStatus('tokenGeneratorStatus', ServerSyncShared.t.tr('ErrorSignIn'), true);
        });
    }

    // Selects an option by value, matching ids with or without dashes and in any case, since a stored
    // id may have been written either way and the server answers with dashed ids.
    function selectLoose(select, value) {
        if (!select || !value) return;
        select.value = value;
        if (select.value === value) return;
        var wanted = String(value).replace(/-/g, '').toLowerCase();
        for (var i = 0; i < select.options.length; i++) {
            if (String(select.options[i].value).replace(/-/g, '').toLowerCase() === wanted) {
                select.selectedIndex = i;
                return;
            }
        }
    }

    function fetchSourceLibraries(server) {
        return apiRequest('GetSourceLibraries', 'POST', requestFor(server)).then(function(libraries) {
            if (!stillSelected(server.Key)) return;
            sourceLibraries = libraries || [];
            updateLibrarySelects();
        }).catch(function() {
            if (stillSelected(server.Key)) sourceLibraries = [];
        });
    }

    function fetchLocalLibraries() {
        return ApiClient.fetch({
            url: ApiClient.getUrl('Library/VirtualFolders'),
            type: 'GET',
            dataType: 'json'
        }).then(function(folders) {
            localLibraries = (folders || []).map(function(folder) {
                return { Id: folder.ItemId, Name: folder.Name, Locations: folder.Locations || [] };
            });
        }).catch(function() {
            localLibraries = [];
        });
    }

    function updateLibrarySelects() {
        view.querySelectorAll('.serversync-source-library-select').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">' + escapeHtml(ServerSyncShared.t.tr('OptionSelectSourceLibrary')) + '</option>';
            sourceLibraries.forEach(function(lib) {
                var option = document.createElement('option');
                option.value = lib.Id;
                option.textContent = lib.Name;
                option.dataset.locations = JSON.stringify(lib.Locations || []);
                select.appendChild(option);
            });
            if (savedValue) selectLoose(select, savedValue);
        });
        view.querySelectorAll('.serversync-local-library-select').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">' + escapeHtml(ServerSyncShared.t.tr('OptionSelectLocalLibrary')) + '</option>';
            localLibraries.forEach(function(lib) {
                var option = document.createElement('option');
                option.value = lib.Id;
                option.textContent = lib.Name;
                option.dataset.locations = JSON.stringify(lib.Locations || []);
                select.appendChild(option);
            });
            if (savedValue) selectLoose(select, savedValue);
        });
    }

    function renderLibraryMappings(mappings) {
        clearMappingContainer('libraryMappingsContainer');
        (mappings || []).forEach(function(mapping) {
            addLibraryMappingRow(mapping);
        });
    }

    // The fields of a library mapping edited on the Storage tab. This page never shows them, and a save
    // here carries them through from the stored mapping so it never drops them.
    var STORAGE_FIELDS = ['StorageMode', 'ExcludeFromLinkCache', 'PermanentItems', 'FilterMode', 'FilteredItems', 'NegotiateHistory'];

    function sameId(a, b) {
        return !!a && !!b && String(a).replace(/-/g, '').toLowerCase() === String(b).replace(/-/g, '').toLowerCase();
    }

    // The stored mapping a mapping on screen stands for: the same source and local library, else the
    // same source library alone.
    function storedMappingFor(storedMappings, mapping) {
        var list = storedMappings || [];
        return list.find(function (m) { return sameId(m.SourceLibraryId, mapping.SourceLibraryId) && sameId(m.LocalLibraryId, mapping.LocalLibraryId); })
            || list.find(function (m) { return sameId(m.SourceLibraryId, mapping.SourceLibraryId); })
            || null;
    }

    function copyStorageFields(from, to) {
        STORAGE_FIELDS.forEach(function (field) {
            if (from[field] !== undefined) to[field] = from[field];
        });
    }

    // Puts the Storage tab's fields of the stored config onto each mapping about to be saved, so a
    // change made on the Storage tab since this page loaded survives a save here.
    function keepStoredStorageFields(storedServers, list) {
        (list || []).forEach(function (server) {
            var stored = (storedServers || []).find(function (s) { return s.Key === server.Key; });
            if (!stored) return;
            // A server's stream speeds are set under Settings > Bandwidth, so the stored ones are kept.
            if (stored.LinkStreamSpeedMbps !== undefined) server.LinkStreamSpeedMbps = stored.LinkStreamSpeedMbps;
            if (stored.LinkScheduledStreamSpeedMbps !== undefined) server.LinkScheduledStreamSpeedMbps = stored.LinkScheduledStreamSpeedMbps;
            (server.LibraryMappings || []).forEach(function (mapping) {
                var match = storedMappingFor(stored.LibraryMappings, mapping);
                if (match) copyStorageFields(match, mapping);
            });
        });
    }

    function addLibraryMappingRow(mapping) {
        mapping = mapping || { IsEnabled: true };
        var container = view.querySelector('#libraryMappingsContainer');
        if (!container) return;

        var div = document.createElement('div');
        div.className = 'serversync-mapping serversync-library-mapping';
        div.innerHTML =
            '<div class="serversync-mapping-header">' +
                '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="serversync-mapping-enabled" ' + (mapping.IsEnabled ? 'checked' : '') + ' /><span class="checkboxLabel">' + escapeHtml(ServerSyncShared.t.tr('OptionEnabled')) + '</span></label>' +
                '<button is="emby-button" type="button" class="serversync-remove-mapping raised base-button destructive small"><span>' + escapeHtml(ServerSyncShared.t.tr('ButtonRemove')) + '</span></button>' +
            '</div>' +
            '<div class="serversync-mapping-grid">' +
                '<div class="serversync-mapping-column">' +
                    '<div class="selectContainer"><label>' + escapeHtml(ServerSyncShared.t.tr('LabelSourceLibrary')) + '</label><select is="emby-select" class="serversync-source-library-select"></select></div>' +
                    '<div class="inputContainer"><label>' + escapeHtml(ServerSyncShared.t.tr('LabelSourceRootPath')) + '</label><input is="emby-input" type="text" class="serversync-source-root-path" value="' + escapeHtml(mapping.SourceRootPath || '') + '" /></div>' +
                '</div>' +
                '<div class="serversync-mapping-column">' +
                    '<div class="selectContainer"><label>' + escapeHtml(ServerSyncShared.t.tr('LabelLocalLibrary')) + '</label><select is="emby-select" class="serversync-local-library-select"></select></div>' +
                    '<div class="inputContainer"><label>' + escapeHtml(ServerSyncShared.t.tr('LabelLocalRootPath')) + '</label><input is="emby-input" type="text" class="serversync-local-root-path" value="' + escapeHtml(mapping.LocalRootPath || '') + '" /></div>' +
                '</div>' +
            '</div>';

        container.appendChild(div);

        // The mapping as loaded. Its Storage tab fields go back out with the save.
        div._storedMapping = mapping;

        var sourceSelect = div.querySelector('.serversync-source-library-select');
        if (mapping.SourceLibraryId) sourceSelect.dataset.savedValue = mapping.SourceLibraryId;
        if (mapping.SourceLibraryName) sourceSelect.dataset.savedName = mapping.SourceLibraryName;
        sourceSelect.innerHTML = '<option value="">' + escapeHtml(ServerSyncShared.t.tr('OptionSelectSourceLibrary')) + '</option>';
        sourceLibraries.forEach(function(lib) {
            var option = document.createElement('option');
            option.value = lib.Id;
            option.textContent = lib.Name;
            option.dataset.locations = JSON.stringify(lib.Locations || []);
            sourceSelect.appendChild(option);
        });
        if (mapping.SourceLibraryId) selectLoose(sourceSelect, mapping.SourceLibraryId);
        sourceSelect.addEventListener('change', function() {
            var option = this.options[this.selectedIndex];
            if (option && option.dataset.locations) {
                var locations = JSON.parse(option.dataset.locations);
                if (locations.length > 0) div.querySelector('.serversync-source-root-path').value = locations[0];
            }
        });

        var localSelect = div.querySelector('.serversync-local-library-select');
        if (mapping.LocalLibraryId) localSelect.dataset.savedValue = mapping.LocalLibraryId;
        if (mapping.LocalLibraryName) localSelect.dataset.savedName = mapping.LocalLibraryName;
        localSelect.innerHTML = '<option value="">' + escapeHtml(ServerSyncShared.t.tr('OptionSelectLocalLibrary')) + '</option>';
        localLibraries.forEach(function(lib) {
            var option = document.createElement('option');
            option.value = lib.Id;
            option.textContent = lib.Name;
            option.dataset.locations = JSON.stringify(lib.Locations || []);
            localSelect.appendChild(option);
        });
        if (mapping.LocalLibraryId) selectLoose(localSelect, mapping.LocalLibraryId);
        localSelect.addEventListener('change', function() {
            var option = this.options[this.selectedIndex];
            if (option && option.dataset.locations) {
                var locations = JSON.parse(option.dataset.locations);
                if (locations.length > 0) div.querySelector('.serversync-local-root-path').value = locations[0];
            }
        });

        div.querySelector('.serversync-remove-mapping').addEventListener('click', function() {
            div.remove();
        });
    }

    function collectLibraryMappings() {
        var mappings = [];
        view.querySelectorAll('.serversync-library-mapping').forEach(function(row) {
            var sourceSelect = row.querySelector('.serversync-source-library-select');
            var localSelect = row.querySelector('.serversync-local-library-select');
            var stored = row._storedMapping || {};

            var mapping = {
                IsEnabled: row.querySelector('.serversync-mapping-enabled').checked,
                SourceLibraryId: pickedValue(sourceSelect),
                SourceLibraryName: pickedName(sourceSelect),
                SourceRootPath: row.querySelector('.serversync-source-root-path').value,
                LocalLibraryId: pickedValue(localSelect),
                LocalLibraryName: pickedName(localSelect),
                LocalRootPath: row.querySelector('.serversync-local-root-path').value,
                StorageMode: 'Copy',
                ExcludeFromLinkCache: false,
                PermanentItems: null,
                FilterMode: 'AllowAll',
                FilteredItems: []
            };
            // The Storage tab settings stay with the row. Picks name items of one source library, so a
            // row moved to another source library keeps its storage, cache, and filter mode but starts
            // with no picks.
            copyStorageFields(stored, mapping);
            if (stored.SourceLibraryId && !sameId(stored.SourceLibraryId, mapping.SourceLibraryId)) {
                mapping.PermanentItems = null;
                mapping.FilteredItems = [];
            }
            mappings.push(mapping);
        });
        return mappings;
    }

    function fetchSourceUsers(server) {
        return apiRequest('GetSourceUsers', 'POST', requestFor(server)).then(function(users) {
            if (!stillSelected(server.Key)) return;
            sourceUsers = users || [];
            updateUserSelects();
        }).catch(function() {
            if (stillSelected(server.Key)) sourceUsers = [];
        });
    }

    function fetchLocalUsers() {
        return ApiClient.fetch({
            url: ApiClient.getUrl('Users'),
            type: 'GET',
            dataType: 'json'
        }).then(function(users) {
            localUsers = (users || []).map(function(user) {
                return { Id: user.Id, Name: user.Name };
            });
        }).catch(function() {
            localUsers = [];
        });
    }

    // The limits only mean something while linking is allowed.
    function linkingAllowed() {
        return getMode() !== 'Pull' && getChecked('chkAllowLinking');
    }

    // A user never gets more streams than the whole server, so each per user field is held to the
    // server's total while one is set.
    function clampUserStreams() {
        var total = Math.max(0, parseInt(getValue('txtLinkMaxStreams', '0'), 10) || 0);
        // Counting a user's own playback only changes anything while some per user limit applies.
        var anyUserLimit = total > 0
            || Array.prototype.some.call(view.querySelectorAll('.serversync-user-link-max-streams'), function (f) { return (parseInt(f.value, 10) || 0) > 0; });
        setVisible('countOwnSessionsContainer', anyUserLimit);
        var fields = Array.prototype.slice.call(view.querySelectorAll('.serversync-user-link-max-streams'));
        fields.forEach(function(field) {
            if (!field) return;
            field.max = total > 0 ? String(total) : '1000';
            var value = parseInt(field.value, 10);
            if (total > 0 && value > total) field.value = String(total);
        });
    }

    // Whether each server runs Server Sync, as last checked: 'yes', 'no', 'unreachable', or 'checking'.
    // Linking needs it there, so the Linking step warns when it is missing.
    var peerPlugin = {};

    function reflectLinkingPlugin() {
        var server = selectedServer();
        var note = getEl('linkingNeedsPlugin');
        if (!server || !note) return;
        var on = linkingAllowed();
        var state = peerPlugin[server.Key];
        if (on && state === undefined && server.Url && server.ApiKey) {
            var key = server.Key;
            var request = requestFor(server);
            request.Mode = getMode();
            peerPlugin[key] = 'checking';
            apiRequest('Hints/CheckPeer', 'POST', request).then(function (result) {
                peerPlugin[key] = result && result.Reachable ? (result.HasPlugin ? 'yes' : 'no') : 'unreachable';
            }).catch(function () {
                peerPlugin[key] = 'unreachable';
            }).then(function () {
                if (stillSelected(key)) reflectLinkingPlugin();
            });
        }
        note.textContent = state === 'no'
            ? ServerSyncShared.t.tr('WarnLinkingNoPlugin')
            : state === 'unreachable' ? ServerSyncShared.t.tr('WarnLinkingUnreachable') : '';
        setVisible('linkingNeedsPlugin', on && (state === 'no' || state === 'unreachable'));
    }

    function reflectLinking() {
        reflectLinkingPlugin();
        setVisible('linkLimits', linkingAllowed());
        view.querySelectorAll('.serversync-user-link-caps').forEach(function(caps) {
            caps.classList.toggle('hidden', !linkingAllowed());
        });
    }

    // Linking is for Push and Sync servers. A Pull server only gives this one things to read, so it
    // may never link here, and the step is hidden. Base numbers the steps that show.
    function reflectStream() {
        var mode = getMode();
        setVisible('serverStepStreamSection', mode !== 'Pull');
        reflectLinking();
    }

    // Refilling keeps the choice on screen unless a server's own value is given, which always wins,
    // so opening a server with no fallback never shows the last server's.
    function fillFallbackUsers(value) {
        var select = getEl('selLinkFallbackUser');
        if (!select) return;
        var savedValue = value !== undefined ? value : select.value;
        select.innerHTML = '<option value="">' + escapeHtml(ServerSyncShared.t.tr('OptionRefuseStream')) + '</option>';
        localUsers.forEach(function(user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            select.appendChild(option);
        });
        select.value = '';
        if (savedValue) selectLoose(select, savedValue);
    }

    function updateUserSelects() {
        fillFallbackUsers();
        view.querySelectorAll('.serversync-source-user-select').forEach(function(select) {
            var savedValue = select.value || select.dataset.savedValue;
            fillSourceUserOptions(select);
            if (savedValue) selectLoose(select, savedValue);
            reflectUserRowLimit(select);
        });
        view.querySelectorAll('.serversync-local-user-select').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">' + escapeHtml(ServerSyncShared.t.tr('OptionSelectLocalUser')) + '</option>';
            localUsers.forEach(function(user) {
                var option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                select.appendChild(option);
            });
            if (savedValue) selectLoose(select, savedValue);
        });
    }

    function fillSourceUserOptions(select) {
        var server = selectedServer();
        select.innerHTML = '<option value="">' + escapeHtml(ServerSyncShared.t.tr('OptionSelectSourceUser')) + '</option>';
        sourceUsers.forEach(function(user) {
            var option = document.createElement('option');
            option.value = user.Id;
            var allowed = maySyncUser(server, user.Id);
            option.textContent = allowed ? user.Name : ServerSyncShared.t.format('OptionUserNeedsAdmin', user.Name);
            option.disabled = !allowed;
            select.appendChild(option);
        });
    }

    function reflectUserRowLimit(select) {
        var row = select.closest('.serversync-user-mapping');
        if (!row) return;
        var server = selectedServer();
        var value = pickedValue(select);
        var blocked = !!value && !maySyncUser(server, value);
        var warning = row.querySelector('.serversync-user-mapping-limit');
        if (!warning) {
            warning = document.createElement('div');
            warning.className = 'serversync-user-mapping-limit base-warn hidden';
            warning.textContent = ServerSyncShared.t.tr('WarnUserSkipped');
            row.appendChild(warning);
        }
        warning.classList.toggle('hidden', !blocked);
    }

    function renderUserMappings(mappings) {
        clearMappingContainer('userMappingsContainer');
        (mappings || []).forEach(function(mapping) {
            addUserMappingRow(mapping);
        });
    }

    function addUserMappingRow(mapping) {
        mapping = mapping || { IsEnabled: true };
        var container = view.querySelector('#userMappingsContainer');
        if (!container) return;

        var div = document.createElement('div');
        div.className = 'serversync-mapping serversync-user-mapping';
        div.innerHTML =
            '<div class="serversync-mapping-header">' +
                '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="serversync-user-mapping-enabled" ' + (mapping.IsEnabled !== false ? 'checked' : '') + ' /><span class="checkboxLabel">' + escapeHtml(ServerSyncShared.t.tr('OptionEnabled')) + '</span></label>' +
                '<button is="emby-button" type="button" class="serversync-remove-user-mapping raised base-button destructive small"><span>' + escapeHtml(ServerSyncShared.t.tr('ButtonRemove')) + '</span></button>' +
            '</div>' +
            '<div class="serversync-mapping-grid">' +
                '<div class="serversync-mapping-column"><div class="selectContainer"><label>' + escapeHtml(ServerSyncShared.t.tr('LabelSourceUser')) + '</label><select is="emby-select" class="serversync-source-user-select"></select></div></div>' +
                '<div class="serversync-mapping-column"><div class="selectContainer"><label>' + escapeHtml(ServerSyncShared.t.tr('LabelLocalUser')) + '</label><select is="emby-select" class="serversync-local-user-select"></select></div></div>' +
            '</div>' +
            '<div class="serversync-mapping-grid serversync-user-link-caps' + (getChecked('chkAllowLinking') ? '' : ' hidden') + '">' +
                '<div class="serversync-mapping-column"><div class="inputContainer"><label>' + escapeHtml(ServerSyncShared.t.tr('LabelLinkStreamsAtOnce')) + '</label><input is="emby-input" type="number" min="0" max="1000" class="serversync-user-link-max-streams" placeholder="' + escapeHtml(ServerSyncShared.t.tr('Default')) + '" value="' + (mapping.LinkMaxStreams == null ? '' : escapeHtml(String(mapping.LinkMaxStreams))) + '" />' +
                    '<div class="fieldDescription">' + escapeHtml(ServerSyncShared.t.tr('HelpUserLinkMaxStreams')) + '</div></div></div>' +
                '<div class="serversync-mapping-column"><div class="inputContainer"><label>' + escapeHtml(ServerSyncShared.t.tr('LabelLinkSpeed')) + '</label><input is="emby-input" type="number" min="0" max="100000" class="serversync-user-link-speed" placeholder="' + escapeHtml(ServerSyncShared.t.tr('Default')) + '" value="' + (mapping.LinkStreamSpeedMbps == null ? '' : escapeHtml(String(mapping.LinkStreamSpeedMbps))) + '" />' +
                    '<div class="fieldDescription">' + escapeHtml(ServerSyncShared.t.tr('HelpUserLinkSpeed')) + '</div></div></div>' +
            '</div>';

        container.appendChild(div);

        var sourceSelect = div.querySelector('.serversync-source-user-select');
        if (mapping.SourceUserId) sourceSelect.dataset.savedValue = mapping.SourceUserId;
        if (mapping.SourceUserName) sourceSelect.dataset.savedName = mapping.SourceUserName;
        fillSourceUserOptions(sourceSelect);
        if (mapping.SourceUserId) selectLoose(sourceSelect, mapping.SourceUserId);
        reflectUserRowLimit(sourceSelect);
        sourceSelect.addEventListener('change', function() { reflectUserRowLimit(sourceSelect); });

        var localSelect = div.querySelector('.serversync-local-user-select');
        if (mapping.LocalUserId) localSelect.dataset.savedValue = mapping.LocalUserId;
        if (mapping.LocalUserName) localSelect.dataset.savedName = mapping.LocalUserName;
        localSelect.innerHTML = '<option value="">' + escapeHtml(ServerSyncShared.t.tr('OptionSelectLocalUser')) + '</option>';
        localUsers.forEach(function(user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            localSelect.appendChild(option);
        });
        if (mapping.LocalUserId) selectLoose(localSelect, mapping.LocalUserId);

        div.querySelector('.serversync-remove-user-mapping').addEventListener('click', function() { div.remove(); });
    }

    // An empty box means "use the server's own value", which is stored as null.
    function optionalNumber(input) {
        if (!input || input.value === '') return null;
        var value = parseInt(input.value, 10);
        return isNaN(value) ? null : Math.max(0, value);
    }

    function collectUserMappings() {
        var mappings = [];
        view.querySelectorAll('.serversync-user-mapping').forEach(function(row) {
            var sourceSelect = row.querySelector('.serversync-source-user-select');
            var localSelect = row.querySelector('.serversync-local-user-select');
            mappings.push({
                IsEnabled: row.querySelector('.serversync-user-mapping-enabled').checked,
                SourceUserId: pickedValue(sourceSelect),
                SourceUserName: pickedName(sourceSelect),
                LocalUserId: pickedValue(localSelect),
                LocalUserName: pickedName(localSelect),
                LinkMaxStreams: optionalNumber(row.querySelector('.serversync-user-link-max-streams')),
                LinkStreamSpeedMbps: optionalNumber(row.querySelector('.serversync-user-link-speed'))
            });
        });
        return mappings;
    }

    function loadConfig() {
        ServerSyncShared.getConfig().then(function(config) {
            currentConfig = config;
            servers = config.Servers || [];
            Promise.all([fetchLocalLibraries(), fetchLocalUsers()]).then(function() {
                selectServer(servers.length > 0 ? 0 : -1);
                refreshServerHealth();
                checkPoolPlugins();
            });
        }).catch(function() {
            ServerSyncShared.setStatus('serverListStatus', ServerSyncShared.t.tr('ErrorServersLoad'), true, { timeout: 0 });
        });
    }

    // bindPage
    // Wires the page's buttons and controls. Base calls it once, the first time the page shows.
    function bindPage() {
        ServerSyncShared.initCollapsibles();

        bindClick('btnTestConnection', testConnection);
        bindClick('btnSaveServer', saveServerConfig);
        bindClick('btnGenerateToken', generateToken);
        bindClick('btnAddServer', addServer);
        bindClick('btnDeleteServer', deleteServer);
        bindClick('btnCheckPeer', checkPeer);

        // The stored key shows as a placeholder. Focusing the field clears it, so typing enters a
        // new key rather than adding to the placeholder. Leaving it empty keeps the stored key.
        var keyField = getEl('txtSourceServerApiKey');
        if (keyField) {
            keyField.addEventListener('focus', function () {
                if (keyField.value === ServerSyncShared.SECRET_KEPT) keyField.value = '';
            });
            keyField.addEventListener('blur', function () {
                var open = selectedServer();
                if (!keyField.value && open && open.ApiKey && !savedSecretLeftBehind(open)) keyField.value = ServerSyncShared.SECRET_KEPT;
            });
        }
        var urlField = getEl('txtSourceServerUrl');
        if (urlField) urlField.addEventListener('input', reflectAddressChange);
        var linkingField = getEl('chkAllowLinking');
        if (linkingField) linkingField.addEventListener('change', reflectLinking);
        var totalField = getEl('txtLinkMaxStreams');
        if (totalField) totalField.addEventListener('input', clampUserStreams);
        view.addEventListener('change', function(e) {
            if (e.target && ((e.target.classList && e.target.classList.contains('serversync-user-link-max-streams')))) clampUserStreams();
        });
        modeGroup = createChoiceGroup(getEl('modeChoices'), { onChange: reflectMode });
        authGroup = createChoiceGroup(getEl('authChoices'), { onChange: reflectAuthMethod });

        // The buttons are disabled while the open server's lists load. The check here covers a
        // click that lands before the disabled state does.
        bindClick('btnAddMapping', function() { if (mappingsLoaded) addLibraryMappingRow(); });
        bindClick('btnAddUserMapping', function() { if (mappingsLoaded) addUserMappingRow(); });
    }
}

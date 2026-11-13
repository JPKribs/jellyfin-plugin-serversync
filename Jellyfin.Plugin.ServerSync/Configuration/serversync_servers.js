// ============================================
// SERVERS - PAGE CONTROLLER
// ============================================

export default function (view) {
    'use strict';

    // ============================================
    // TAB NAVIGATION (local copy for synchronous access)
    // ============================================

    // The Storage tab shows only while content sync is on. The last known answer is kept per browser
    // and refreshed whenever the shared module loads or saves the config (see serversync_shared.js).
    // An unknown answer shows the tab.
    function storageTabShown() {
        try {
            return window.localStorage.getItem('serversync.storageTab') !== '0';
        } catch (e) {
            return true;
        }
    }

    function getTabs() {
        var tabs = [
            { href: 'configurationpage?name=serversync_sync', name: 'Sync' },
            { href: 'configurationpage?name=serversync_servers', name: 'Servers' }
        ];
        if (storageTabShown()) tabs.push({ href: 'configurationpage?name=serversync_storage', name: 'Storage' });
        tabs.push({ href: 'configurationpage?name=serversync_settings', name: 'Settings' });
        return tabs;
    }

    // ============================================
    // SHARED MODULE IMPORT (deferred)
    // ============================================

    var ServerSyncShared = null;
    var createSortableCardList = null;
    var createChoiceGroup = null;
    var confirmDialog = null;

    // Helpers called by bare name, taken from the shared module once it loads. Every call happens after
    // the load, from a viewshow handler that waits for it.
    var escapeHtml, apiRequest, setVisible, bindClick, getEl, setChecked, getChecked, setValue, getValue, requestFor;

    // Relative specifier so a server hosted under a base URL still resolves it.
    var _sharedPromise = import('./configurationpage?name=serversync_shared.js').then(function(shared) {
        ServerSyncShared = shared.createServerSyncShared(view);
        // A config load that turns content sync on or off adds or drops the Storage tab. A page the
        // dashboard keeps hidden leaves the tabs to the page on screen.
        ServerSyncShared.onTabsChanged = function () {
            if (!view.classList.contains('hide')) LibraryMenu.setTabs('serversync', 1, getTabs);
        };
        createSortableCardList = shared.createSortableCardList;
        createChoiceGroup = shared.createChoiceGroup;
        confirmDialog = shared.confirmDialog;
        ({ escapeHtml, apiRequest, setVisible, bindClick, getEl, setChecked, getChecked, setValue, getValue, requestFor } = ServerSyncShared.pageHelpers());
    });

    // ============================================
    // CONSTANTS & STATE
    // ============================================
    var _initialized = false;

    var currentConfig = null;
    var sourceLibraries = [];
    var localLibraries = [];
    var sourceUsers = [];
    var localUsers = [];

    // Resolves true once the save landed and false when it did not, so a caller never acts on a change
    // the server never kept. The config is replaced only from a save that landed.
    function saveSection(mutator, successMessage, failureMessage) {
        return ServerSyncShared.saveSection(mutator, successMessage, failureMessage).then(function (saved) {
            if (saved) currentConfig = saved;
            return !!saved;
        });
    }

    // ============================================
    // SERVER MODULE
    // ============================================
    // The configured servers live in config.Servers in priority order. The page
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
        return server.Name || server.ServerName || server.Url || 'New server';
    }

    function modeBadgeClass(mode) {
        return mode === 'Sync' ? 'purple' : mode === 'Push' ? 'blue' : mode === 'Stream' ? 'orange' : 'green';
    }

    function cardSpec(server) {
        var health = serverHealth[server.Key] || (server.Url && server.ApiKey ? 'unknown' : 'none');
        var badges = [{ label: server.Mode || 'Pull', cls: modeBadgeClass(server.Mode || 'Pull') }];
        if (server.IsEnabled === false) badges.push({ label: 'Disabled', cls: 'gray' });
        return {
            title: serverTitle(server),
            subtitle: server.Url || 'No URL yet',
            badges: badges,
            dot: health,
            dotTitle: { ok: 'Reachable', bad: 'Not reachable', checking: 'Checking', unknown: 'Not checked yet', none: 'Not configured yet' }[health],
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
                saveServers('Server order saved', 'Failed to save server order');
            }
        });
        return cards;
    }

    function renderServerList() {
        ensureCards().setItems(servers);
        setVisible('serverEmpty', servers.length === 0);
        renderPoolMap();
    }

    // ============================================
    // POOL MAP
    // ============================================

    var POOL_COLORS = { ok: '#5cb85c', noPlugin: '#f0ad4e', down: '#d9534f', disabled: '#999999', pending: '#555555' };

    function modeOf(server) {
        var mode = server.Mode;
        return mode === 1 ? 'Push' : mode === 2 ? 'Sync' : mode === 3 ? 'Stream' : (typeof mode === 'string' ? mode : 'Pull');
    }

    // A server's colour: green runs Server Sync and answers, orange answers without it, red does not
    // answer, grey is disabled. One still being checked is drawn dim until the answer comes.
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

    // Asks each server once whether it runs Server Sync, so the map can colour it.
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

    function hasLinkLibraries(server) {
        return (server.LibraryMappings || []).some(function (m) { return m.IsEnabled !== false && (m.StorageMode === 'Link' || m.StorageMode === 1); });
    }

    // This server in the middle, and one box per relationship around it, each listing its servers with
    // their status. The arrows say the relationship, so the boxes carry no label, and every arrow
    // points the way things travel: Pull points here, Push points out, Sync both ways. A dashed arrow
    // is video streamed: Stream points out, since those servers stream this one's files, and a dashed
    // arrow pointing here marks the servers whose files this one streams.
    var POOL_GROUPS = [
        { key: 'Sync', toward: 'both' },
        { key: 'Push', toward: 'there' },
        { key: 'Links', toward: 'here', dashed: true },
        { key: 'Stream', toward: 'there', dashed: true },
        { key: 'Pull', toward: 'here' }
    ];

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
            if (mode !== 'Stream' && hasLinkLibraries(server)) (members.Links = members.Links || []).push(server);
        });
        var groups = POOL_GROUPS.filter(function (g) { return members[g.key] && members[g.key].length; });

        var rowH = 19;
        var maxRows = 8;
        var hereName = localServerName || 'This Server';
        if (hereName.length > 16) hereName = hereName.substring(0, 15) + '\u2026';
        var hereR = Math.max(30, hereName.length * 3.6 + 12);
        var ring = (groups.length <= 2 ? 150 : 175) + hereR - 30;
        var parts = ['<defs><marker id="poolArrowHead" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse"><path d="M0,1 L9,5 L0,9 z" fill="#9aa4ab"/></marker></defs>'];
        var bounds = [[-hereR, -hereR], [hereR, hereR]];
        var labels = [];
        var boxes = [];

        groups.forEach(function (group, gi) {
            var list = members[group.key];
            var columns = Math.ceil(list.length / maxRows);
            var rows = Math.min(list.length, maxRows);
            var longest = Math.max.apply(null, list.map(function (s) { return Math.min(serverTitle(s).length, 18); }));
            var colW = longest * 6.9 + 30;
            var w = columns * colW + 8;
            var h = rows * rowH + 14;
            // Two boxes sit left and right; more spread round the circle from the top.
            var angle = groups.length === 1 ? 0 : groups.length === 2 ? (gi === 0 ? Math.PI : 0) : -Math.PI / 2 + 2 * Math.PI * gi / groups.length;
            var ux = Math.cos(angle);
            var uy = Math.sin(angle);
            var cx = ux * (ring + w / 2 * Math.abs(ux) * 0.6);
            var cy = uy * (ring * 0.72 + h / 2 * Math.abs(uy) * 0.6);
            var x0 = cx - w / 2;
            var y0 = cy - h / 2;

            // The line meets the box's edge where the spoke enters it.
            var dx = cx;
            var dy = cy;
            var len = Math.sqrt(dx * dx + dy * dy) || 1;
            var vx = dx / len;
            var vy = dy / len;
            var tBox = Math.min(Math.abs(vx) > 1e-6 ? (w / 2) / Math.abs(vx) : Infinity, Math.abs(vy) > 1e-6 ? (h / 2) / Math.abs(vy) : Infinity);
            var sx = vx * (hereR + 3);
            var sy = vy * (hereR + 3);
            var ex = cx - vx * (tBox + 3);
            var ey = cy - vy * (tBox + 3);
            var here = group.toward === 'here' || group.toward === 'both';
            var there = group.toward === 'there' || group.toward === 'both';
            parts.push('<line x1="' + sx.toFixed(1) + '" y1="' + sy.toFixed(1) + '" x2="' + ex.toFixed(1) + '" y2="' + ey.toFixed(1) + '" stroke="#9aa4ab" stroke-width="1.8"' +
                (group.dashed ? ' stroke-dasharray="5 4"' : '') + (here ? ' marker-start="url(#poolArrowHead)"' : '') + (there ? ' marker-end="url(#poolArrowHead)"' : '') + ' />');

            var box = '<rect x="' + x0.toFixed(1) + '" y="' + y0.toFixed(1) + '" width="' + w.toFixed(1) + '" height="' + h.toFixed(1) + '" rx="8" fill="#1c1c1c" stroke="#444" stroke-width="1" />';
            list.forEach(function (server, si) {
                var col = Math.floor(si / maxRows);
                var row = si % maxRows;
                var px = x0 + 4 + col * colW + 12;
                var py = y0 + 7 + row * rowH + rowH / 2;
                var state = poolState(server);
                var name = serverTitle(server);
                if (name.length > 18) name = name.substring(0, 17) + '\u2026';
                box += '<g><title>' + escapeHtml(serverTitle(server) + ', ' + modeOf(server)) + '</title>' +
                    '<circle cx="' + px.toFixed(1) + '" cy="' + py.toFixed(1) + '" r="4" fill="' + POOL_COLORS[state] + '" />' +
                    '<text class="ssPoolName" x="' + (px + 10).toFixed(1) + '" y="' + py.toFixed(1) + '" dominant-baseline="middle">' + escapeHtml(name) + '</text></g>';
                if (group.key !== 'Links') labels.push(serverTitle(server) + ' ' + modeOf(server));
            });
            boxes.push(box);
            bounds.push([x0, y0], [x0 + w, y0 + h]);
        });

        parts = parts.concat(boxes);
        parts.push('<circle cx="0" cy="0" r="' + hereR + '" fill="#52b5db" fill-opacity="0.15" stroke="#52b5db" stroke-width="1.6"><title>' + escapeHtml((localServerName || 'This server') + ', this server') + '</title></circle>');
        parts.push('<text class="ssPoolSelf" x="0" y="4">' + escapeHtml(hereName) + '</text>');

        var minX = Math.min.apply(null, bounds.map(function (b) { return b[0]; })) - 6;
        var minY = Math.min.apply(null, bounds.map(function (b) { return b[1]; })) - 6;
        var maxX = Math.max.apply(null, bounds.map(function (b) { return b[0]; })) + 6;
        var maxY = Math.max.apply(null, bounds.map(function (b) { return b[1]; })) + 6;
        var legendDot = function (state, text) { return '<span><i style="background:' + POOL_COLORS[state] + '"></i>' + text + '</span>'; };
        host.setAttribute('aria-label', 'Servers this one is set up with: ' + labels.join(', '));
        host.innerHTML = '<svg viewBox="' + minX.toFixed(0) + ' ' + minY.toFixed(0) + ' ' + (maxX - minX).toFixed(0) + ' ' + (maxY - minY).toFixed(0) + '" style="max-width:' + Math.round(maxX - minX) + 'px" xmlns="http://www.w3.org/2000/svg">' + parts.join('') + '</svg>' +
            '<div class="ssPoolLegend">' +
                legendDot('ok', 'Runs Server Sync') + legendDot('noPlugin', 'No Server Sync') + legendDot('down', 'Unreachable') + legendDot('disabled', 'Disabled') +
            '</div>';
    }

    // Updates each card's dot in place. A full render rebuilds the cards and moves the open editor,
    // which takes the cursor out of whatever field the operator is typing in.
    function updateHealthDots() {
        renderPoolMap();
        view.querySelectorAll('#serverList .jpk-card-item').forEach(function (card) {
            var server = servers[parseInt(card.getAttribute('data-index'), 10)];
            var dot = card.querySelector('.jpk-card-item-dot');
            if (!server || !dot) return;
            var spec = cardSpec(server);
            dot.className = 'jpk-card-item-dot ' + spec.dot;
            dot.title = spec.dotTitle || '';
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

    // --- Authentication method ---
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

    function accessRows(server) {
        var admin = server.AccessLevel === 'Administrator';
        var who = server.AuthenticatedUser || 'this user';
        var downloads = server.CanDownload === false
            ? ['no', (server.AuthenticatedUser || 'This account') + ' may not download media there. Turn on Allow media downloading for that user, or use an API key.']
            : ['ok', 'Allowed'];
        if (admin) {
            return [
                ['Content listing', 'ok', 'Every library, with its folder paths'],
                ['File downloads', downloads[0], downloads[1]],
                ['Metadata, people, and collections', 'ok', 'Every item'],
                ['Watch history', 'ok', 'Every mapped user'],
                ['User settings', 'ok', 'Every mapped user'],
                ['Playlists', 'ok', 'Every playlist of a mapped user'],
                ['Two way history', 'ok', 'Merged history is written back there'],
                ['Push and Sync', 'ok', 'Once that server whitelists this one']
            ];
        }
        return [
            ['Content listing', 'limited', 'Only the libraries ' + who + ' can see. Type each source root path by hand, since folder paths are only shown to administrators'],
            ['File downloads', downloads[0], downloads[1]],
            ['Metadata, people, and collections', 'limited', 'Only items in the libraries ' + who + ' can see'],
            ['Watch history', 'limited', 'Only the history of ' + who],
            ['User settings', 'limited', 'Only the settings of ' + who],
            ['Playlists', 'limited', 'Only the playlists ' + who + ' can see, owned by that user here'],
            ['Two way history', 'limited', 'Only for ' + who + ', once that server whitelists ' + who],
            ['Push and Sync', 'limited', 'Once that server whitelists ' + who]
        ];
    }

    var ACCESS_LABELS = { ok: 'Available', limited: 'Limited', no: 'Unavailable', none: 'Not Installed' };
    var ACCESS_BADGES = { ok: 'green', limited: 'orange', no: 'red', none: 'gray' };

    function syncAccessRow(server) {
        if (!server.SyncAccess) return null;
        var state = server.SyncAccess === 'Allowed' ? 'ok' : server.SyncAccess === 'NotInstalled' ? 'none' : 'no';
        var detail = server.SyncAccessMessage || '';
        if (state === 'no') detail += '. On that server, add this one to its Servers tab or add this key\'s user to its Whitelist.';
        return ['Sync endpoints there', state, detail];
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
                headline.className = 'mb-md ' + (level === 'Administrator' ? 'jpk-success' : 'jpk-warn');
                headline.textContent = level === 'Administrator'
                    ? 'Administrator access' + (server.AuthenticatedUser ? ' as ' + server.AuthenticatedUser : '') + '. Every module works.'
                    : 'Standard user access as ' + (server.AuthenticatedUser || 'an unknown user') + '. Some features are off.';
            }
            var rows = getEl('accessRows');
            if (rows) {
                var list = accessRows(server);
                var endpoints = syncAccessRow(server);
                var reach = !endpoints ? ['limited', 'Checking the sync endpoints there'] : endpoints[1] !== 'ok' ? ['no', 'Needs the sync endpoints there'] : null;
                if (reach) {
                    list = list.map(function (row) {
                        return row[0] === 'Two way history' || row[0] === 'Push and Sync' ? [row[0], reach[0], reach[1]] : row;
                    });
                }
                if (endpoints) list.unshift(endpoints);
                rows.innerHTML = list.map(function (row) {
                    return '<div class="ssAccessRow">' +
                        '<div class="ssAccessText">' +
                            '<div class="ssAccessTitle">' + escapeHtml(row[0]) + '</div>' +
                            '<div class="ssAccessDescription">' + escapeHtml(row[2]) + '</div>' +
                        '</div>' +
                        '<span class="jpk-badge ' + ACCESS_BADGES[row[1]] + '">' + ACCESS_LABELS[row[1]] + '</span>' +
                        '</div>';
                }).join('');
            }
        }
        reflectModeLimits(server);
        reflectUserLimits(server);
    }

    function reflectModeLimits(server) {
        setVisible('modeLimited', isStandardUser(server) && getMode() !== 'Pull' && getMode() !== 'Stream');
    }

    function reflectUserLimits(server) {
        var limited = isStandardUser(server);
        var note = getEl('usersLimited');
        if (note) {
            note.textContent = limited
                ? 'Standard user key. Only ' + (server.AuthenticatedUser || 'that user') + ' can be mapped.'
                : '';
        }
        setVisible('usersLimited', limited);
        updateUserSelects();
    }

    function reflectSignedIn(server) {
        var signedIn = !!(server && server.AuthenticatedUser);
        setVisible('authSignedIn', signedIn);
        var userEl = getEl('authSignedInUser');
        if (userEl) userEl.textContent = signedIn ? server.AuthenticatedUser : '';
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
                ? 'The saved sign in only works with ' + storedUrlFor(server.Key) + '. Sign in again for the new address.'
                : 'The saved API key is only sent to ' + storedUrlFor(server.Key) + '. Enter the key again for the new address.';
        }
        setVisible('savedSecretNote', moved);
    }

    // --- Mode ---

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
        var statusEl = getEl('peerCheckStatus');
        if (statusEl) statusEl.textContent = '';
    }

    // Asks the other server whether it runs Server Sync, accepts hints, and lists this server, so a
    // link that would only fail in the queue is caught here.
    function checkPeer() {
        var server = collectServerEditor();
        var statusEl = getEl('peerCheckStatus');
        if (!server) return;
        if (!server.Url || !server.ApiKey) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Enter the server URL and API key first</span>';
            return;
        }
        if (statusEl) statusEl.textContent = 'Checking...';
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
            if (statusEl) {
                var severity = (result && result.Severity) || 'error';
                statusEl.innerHTML = '<span class="' + (severity === 'ok' ? 'text-success' : severity === 'warn' ? 'text-warn' : 'text-error') + '">' + escapeHtml((result && result.Message) || 'No answer') + '</span>';
            }
            if (result && result.Reachable) renderServerList();
        }).catch(function () {
            if (!stillSelected(key)) return;
            if (statusEl) statusEl.innerHTML = '<span class="text-error">The check failed. See the server log</span>';
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
        if (nameEl) nameEl.textContent = server.ServerName || 'Unknown';
        if (idEl) idEl.textContent = server.ServerId || 'Unknown';
        setVisible('serverInfoContainer', !!(server.ServerName || server.ServerId));

        var authUserEl = getEl('txtAuthenticatedUser');
        if (authUserEl) authUserEl.textContent = server.AuthenticatedUser || '';
        setVisible('authenticatedUserRow', !!server.AuthenticatedUser);

        var statusEl = getEl('connectionStatus');
        if (statusEl) statusEl.textContent = '';
        var tokenStatusEl = getEl('tokenGeneratorStatus');
        if (tokenStatusEl) tokenStatusEl.textContent = '';

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
        clearMappingContainer('libraryMappingsContainer', 'Loading library mappings...');
        clearMappingContainer('userMappingsContainer', 'Loading user mappings...');
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
        var header = view.querySelector('.jpk-collapsible-header[data-target="' + target + '"]');
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
        var label = server.Name || server.ServerName || server.Url || 'this server';
        var question = '<p>' + escapeHtml('Remove ' + label + '?') + '</p>' +
            '<p>Its mappings and the sync rows tracked from it are removed with it. Files already downloaded stay where they are.</p>';
        confirmDialog(question, { html: true, destructive: true }).then(function (confirmed) {
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
            saveServers('Server removed', 'Failed to remove server', remaining).then(function (saved) {
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

    // Posts the in memory list. The server resolves kept sentinels against the
    // stored entry with the same key, so keys are never retyped.
    // Resolves true when the save landed. A list other than the page's own, such as the list without a
    // server being removed, can be saved, and becomes the page's list only on success.
    function saveServers(successMessage, failureMessage, list) {
        var snapshot = JSON.parse(JSON.stringify(list || servers));
        return saveSection(function (config) {
            keepStoredStorageFields(config.Servers, snapshot);
            config.Servers = snapshot;
        }, successMessage || 'Servers saved', failureMessage || 'Failed to save servers').then(function (saved) {
            // The saved list becomes the page's list. Its entries are fresh copies, which is why answers
            // that arrive later look their server up again with serverByKey.
            if (saved && currentConfig && currentConfig.Servers) {
                servers = currentConfig.Servers;
                renderServerList();
            }
            return saved;
        });
    }

    function saveServerConfig() {
        if (!collectServerEditor()) return;
        saveServers('Server saved', 'Failed to save server');
    }

    function testConnection() {
        var server = collectServerEditor();
        var statusEl = getEl('connectionStatus');
        if (!server) return;

        if (!server.Url || !server.ApiKey) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Please enter URL and API key</span>';
            return;
        }

        if (statusEl) statusEl.textContent = 'Testing...';

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
                if (statusEl) statusEl.innerHTML = '<span class="text-success">Connected to ' + escapeHtml(response.ServerName) + '</span>';
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
                if (nameEl) nameEl.textContent = current.ServerName || 'Unknown';
                if (idEl) idEl.textContent = current.ServerId || 'Unknown';
                setVisible('serverInfoContainer', true);
                serverHealth[key] = 'ok';
                renderServerList();

                setMappingSectionsVisible(true);
                loadMappings(key);
            } else {
                if (statusEl) statusEl.innerHTML = '<span class="text-error">' + escapeHtml((response && response.Message) || 'Connection failed') + '</span>';
                serverHealth[key] = 'bad';
                renderServerList();
            }
        }).catch(function() {
            serverHealth[key] = 'bad';
            if (!stillSelected(key)) {
                updateHealthDots();
                return;
            }
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Connection failed</span>';
            renderServerList();
        });
    }

    // --- Token Generation ---

    function generateToken() {
        var server = collectServerEditor();
        var usernameEl = getEl('txtAuthUsername');
        var passwordEl = getEl('txtAuthPassword');
        var statusEl = getEl('tokenGeneratorStatus');
        if (!server) return;

        var username = usernameEl ? usernameEl.value : '';
        var password = passwordEl ? passwordEl.value : '';

        if (!server.Url) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Please enter a Server URL first</span>';
            return;
        }

        if (!username || !password) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Username and password are required</span>';
            return;
        }

        if (statusEl) statusEl.textContent = 'Authenticating...';

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
                if (stillSelected(key) && statusEl) statusEl.innerHTML = '<span class="text-error">' + escapeHtml((response && response.Message) || 'Authentication failed') + '</span>';
                return;
            }

            var selected = stillSelected(key);
            // A save while the sign in ran replaced every entry with a fresh copy, so the token goes into
            // the entry that holds this key now, which is the one the next save sends. When the server is
            // still open, the editor is read back first so edits made meanwhile are saved with the token.
            var current = selected ? collectServerEditor() : serverByKey(key);
            if (!current) {
                Dashboard.alert('Signed in, but the server was removed meanwhile, so the token was not kept');
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
                saveServers('Token generated and saved', 'Token generated, but saving failed');
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
            if (nameEl) nameEl.textContent = current.ServerName || 'Unknown';
            if (idEl) idEl.textContent = current.ServerId || 'Unknown';
            setVisible('serverInfoContainer', true);

            // The success line shows only once the save landed, since a token the server never stored
            // would be lost on the next page load.
            saveServers('Token generated and saved', 'Token generated, but saving failed').then(function(ok) {
                if (!stillSelected(key)) return;
                if (!ok) {
                    if (statusEl) statusEl.innerHTML = '<span class="text-error">Token generated, but saving failed</span>';
                    return;
                }
                if (statusEl) statusEl.innerHTML = '<span class="text-success">Token generated and saved!</span>';
                serverHealth[key] = 'ok';
                renderServerList();
                setMappingSectionsVisible(true);
                loadMappings(key);
            });
        }).catch(function(error) {
            console.error('Token generation error:', error);
            if (stillSelected(key) && statusEl) statusEl.innerHTML = '<span class="text-error">Authentication failed</span>';
        });
    }

    // ============================================
    // LIBRARY MAPPINGS MODULE
    // ============================================

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
        view.querySelectorAll('.sourceLibrarySelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select source library...</option>';
            sourceLibraries.forEach(function(lib) {
                var option = document.createElement('option');
                option.value = lib.Id;
                option.textContent = lib.Name;
                option.dataset.locations = JSON.stringify(lib.Locations || []);
                select.appendChild(option);
            });
            if (savedValue) selectLoose(select, savedValue);
        });
        view.querySelectorAll('.localLibrarySelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select local library...</option>';
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
        div.className = 'mapping libraryMapping';
        div.innerHTML =
            '<div class="mappingHeader">' +
                '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="mappingEnabled" ' + (mapping.IsEnabled ? 'checked' : '') + ' /><span class="checkboxLabel">Enabled</span></label>' +
                '<button is="emby-button" type="button" class="btnRemoveMapping raised jpk-button-destructive jpk-button-small"><span>Remove</span></button>' +
            '</div>' +
            '<div class="mappingGrid">' +
                '<div class="mappingColumn">' +
                    '<div class="inputContainer"><label class="inputLabel">Source Library</label><select is="emby-select" class="sourceLibrarySelect"></select></div>' +
                    '<div class="inputContainer"><label class="inputLabel">Source Root Path</label><input is="emby-input" type="text" class="sourceRootPath" value="' + escapeHtml(mapping.SourceRootPath || '') + '" /></div>' +
                '</div>' +
                '<div class="mappingColumn">' +
                    '<div class="inputContainer"><label class="inputLabel">Local Library</label><select is="emby-select" class="localLibrarySelect"></select></div>' +
                    '<div class="inputContainer"><label class="inputLabel">Local Root Path</label><input is="emby-input" type="text" class="localRootPath" value="' + escapeHtml(mapping.LocalRootPath || '') + '" /></div>' +
                '</div>' +
            '</div>';

        container.appendChild(div);

        // The mapping as loaded. Its Storage tab fields go back out with the save.
        div._storedMapping = mapping;

        var sourceSelect = div.querySelector('.sourceLibrarySelect');
        if (mapping.SourceLibraryId) sourceSelect.dataset.savedValue = mapping.SourceLibraryId;
        if (mapping.SourceLibraryName) sourceSelect.dataset.savedName = mapping.SourceLibraryName;
        sourceSelect.innerHTML = '<option value="">Select source library...</option>';
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
                if (locations.length > 0) div.querySelector('.sourceRootPath').value = locations[0];
            }
        });

        var localSelect = div.querySelector('.localLibrarySelect');
        if (mapping.LocalLibraryId) localSelect.dataset.savedValue = mapping.LocalLibraryId;
        if (mapping.LocalLibraryName) localSelect.dataset.savedName = mapping.LocalLibraryName;
        localSelect.innerHTML = '<option value="">Select local library...</option>';
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
                if (locations.length > 0) div.querySelector('.localRootPath').value = locations[0];
            }
        });

        div.querySelector('.btnRemoveMapping').addEventListener('click', function() {
            div.remove();
        });
    }

    function collectLibraryMappings() {
        var mappings = [];
        view.querySelectorAll('.libraryMapping').forEach(function(row) {
            var sourceSelect = row.querySelector('.sourceLibrarySelect');
            var localSelect = row.querySelector('.localLibrarySelect');
            var stored = row._storedMapping || {};

            var mapping = {
                IsEnabled: row.querySelector('.mappingEnabled').checked,
                SourceLibraryId: pickedValue(sourceSelect),
                SourceLibraryName: pickedName(sourceSelect),
                SourceRootPath: row.querySelector('.sourceRootPath').value,
                LocalLibraryId: pickedValue(localSelect),
                LocalLibraryName: pickedName(localSelect),
                LocalRootPath: row.querySelector('.localRootPath').value,
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

    // ============================================
    // USER MAPPINGS MODULE
    // ============================================

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
            || Array.prototype.some.call(view.querySelectorAll('.userLinkMaxStreams'), function (f) { return (parseInt(f.value, 10) || 0) > 0; });
        setVisible('countOwnSessionsContainer', anyUserLimit);
        var fields = Array.prototype.slice.call(view.querySelectorAll('.userLinkMaxStreams'));
        fields.forEach(function(field) {
            if (!field) return;
            field.max = total > 0 ? String(total) : '1000';
            var value = parseInt(field.value, 10);
            if (total > 0 && value > total) field.value = String(total);
        });
    }

    // Whether each server runs Server Sync, as last checked: 'yes', 'no', 'unreachable', or 'checking'.
    // Linking needs it there, so the Stream step warns when it is missing.
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
            ? 'That server does not run Server Sync, so it cannot link to files here until it is installed there.'
            : state === 'unreachable' ? 'That server could not be reached to check that it runs Server Sync, which linking needs.' : '';
        setVisible('linkingNeedsPlugin', on && (state === 'no' || state === 'unreachable'));
    }

    function reflectLinking() {
        reflectLinkingPlugin();
        setVisible('linkLimits', linkingAllowed());
        view.querySelectorAll('.userLinkCaps').forEach(function(caps) {
            caps.classList.toggle('hidden', !linkingAllowed());
        });
    }

    // A Stream server maps no libraries here, so the Libraries step is hidden and the steps after it
    // are numbered on from the ones still shown.
    // Linking is for Stream, Push, and Sync servers. A Pull server only gives this one things to read,
    // so it may never link here, and the step is hidden. A Stream server may be there only to download,
    // so it links only with Allow Linking on, like the others.
    function reflectStream() {
        var mode = getMode();
        var stream = mode === 'Stream';
        setVisible('serverStepLibrariesSection', !stream);
        setVisible('serverStepStreamSection', mode !== 'Pull');
        var number = 0;
        view.querySelectorAll('#serverEditor .jpk-collapsible-section').forEach(function(section) {
            var label = section.querySelector('.jpk-step-number');
            if (label && !section.classList.contains('hidden')) label.textContent = String(++number);
        });
        reflectLinking();
    }

    // Refilling keeps the choice on screen unless a server's own value is given, which always wins,
    // so opening a server with no fallback never shows the last server's.
    function fillFallbackUsers(value) {
        var select = getEl('selLinkFallbackUser');
        if (!select) return;
        var savedValue = value !== undefined ? value : select.value;
        select.innerHTML = '<option value="">Refuse Stream</option>';
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
        view.querySelectorAll('.sourceUserSelect').forEach(function(select) {
            var savedValue = select.value || select.dataset.savedValue;
            fillSourceUserOptions(select);
            if (savedValue) selectLoose(select, savedValue);
            reflectUserRowLimit(select);
        });
        view.querySelectorAll('.localUserSelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select local user...</option>';
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
        select.innerHTML = '<option value="">Select source user...</option>';
        sourceUsers.forEach(function(user) {
            var option = document.createElement('option');
            option.value = user.Id;
            var allowed = maySyncUser(server, user.Id);
            option.textContent = allowed ? user.Name : user.Name + ' (needs administrator)';
            option.disabled = !allowed;
            select.appendChild(option);
        });
    }

    function reflectUserRowLimit(select) {
        var row = select.closest('.userMapping');
        if (!row) return;
        var server = selectedServer();
        var value = pickedValue(select);
        var blocked = !!value && !maySyncUser(server, value);
        var warning = row.querySelector('.userMappingLimit');
        if (!warning) {
            warning = document.createElement('div');
            warning.className = 'userMappingLimit jpk-warn hidden';
            warning.textContent = 'Skipped. A standard user key only syncs its own user.';
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
        div.className = 'mapping userMapping';
        div.innerHTML =
            '<div class="mappingHeader">' +
                '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="userMappingEnabled" ' + (mapping.IsEnabled !== false ? 'checked' : '') + ' /><span class="checkboxLabel">Enabled</span></label>' +
                '<button is="emby-button" type="button" class="btnRemoveUserMapping raised jpk-button-destructive jpk-button-small"><span>Remove</span></button>' +
            '</div>' +
            '<div class="mappingGrid">' +
                '<div class="mappingColumn"><div class="inputContainer"><label class="inputLabel">Source User</label><select is="emby-select" class="sourceUserSelect"></select></div></div>' +
                '<div class="mappingColumn"><div class="inputContainer"><label class="inputLabel">Local User</label><select is="emby-select" class="localUserSelect"></select></div></div>' +
            '</div>' +
            '<div class="mappingGrid userLinkCaps' + (getChecked('chkAllowLinking') ? '' : ' hidden') + '">' +
                '<div class="mappingColumn"><div class="inputContainer"><label class="inputLabel">Link Streams At Once</label><input is="emby-input" type="number" min="0" max="1000" class="userLinkMaxStreams" placeholder="Server\'s limit" value="' + (mapping.LinkMaxStreams == null ? '' : mapping.LinkMaxStreams) + '" />' +
                    '<div class="fieldDescription">Most streams the local user may hold at once for that server\'s links. Empty or 0 for up to the server\'s limit.</div></div></div>' +
                '<div class="mappingColumn"><div class="inputContainer"><label class="inputLabel">Link Speed (Mbps)</label><input is="emby-input" type="number" min="0" max="100000" class="userLinkSpeed" placeholder="Server\'s speed" value="' + (mapping.LinkStreamSpeedMbps == null ? '' : mapping.LinkStreamSpeedMbps) + '" />' +
                    '<div class="fieldDescription">Most each stream and cache download for this user is sent at, never above the server\'s speed. Empty or 0 uses the server\'s.</div></div></div>' +
            '</div>';

        container.appendChild(div);

        var sourceSelect = div.querySelector('.sourceUserSelect');
        if (mapping.SourceUserId) sourceSelect.dataset.savedValue = mapping.SourceUserId;
        if (mapping.SourceUserName) sourceSelect.dataset.savedName = mapping.SourceUserName;
        fillSourceUserOptions(sourceSelect);
        if (mapping.SourceUserId) selectLoose(sourceSelect, mapping.SourceUserId);
        reflectUserRowLimit(sourceSelect);
        sourceSelect.addEventListener('change', function() { reflectUserRowLimit(sourceSelect); });

        var localSelect = div.querySelector('.localUserSelect');
        if (mapping.LocalUserId) localSelect.dataset.savedValue = mapping.LocalUserId;
        if (mapping.LocalUserName) localSelect.dataset.savedName = mapping.LocalUserName;
        localSelect.innerHTML = '<option value="">Select local user...</option>';
        localUsers.forEach(function(user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            localSelect.appendChild(option);
        });
        if (mapping.LocalUserId) selectLoose(localSelect, mapping.LocalUserId);

        div.querySelector('.btnRemoveUserMapping').addEventListener('click', function() { div.remove(); });
    }

    // An empty box means "use the server's own value", which is stored as null.
    function optionalNumber(input) {
        if (!input || input.value === '') return null;
        var value = parseInt(input.value, 10);
        return isNaN(value) ? null : Math.max(0, value);
    }

    function collectUserMappings() {
        var mappings = [];
        view.querySelectorAll('.userMapping').forEach(function(row) {
            var sourceSelect = row.querySelector('.sourceUserSelect');
            var localSelect = row.querySelector('.localUserSelect');
            mappings.push({
                IsEnabled: row.querySelector('.userMappingEnabled').checked,
                SourceUserId: pickedValue(sourceSelect),
                SourceUserName: pickedName(sourceSelect),
                LocalUserId: pickedValue(localSelect),
                LocalUserName: pickedName(localSelect),
                LinkMaxStreams: optionalNumber(row.querySelector('.userLinkMaxStreams')),
                LinkStreamSpeedMbps: optionalNumber(row.querySelector('.userLinkSpeed'))
            });
        });
        return mappings;
    }

    // ============================================
    // PAGE INITIALIZATION
    // ============================================

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
            Dashboard.alert('Failed to load plugin configuration');
        });
    }

    // ============================================
    // EVENT LISTENERS
    // ============================================

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('serversync', 1, getTabs);

        _sharedPromise.then(function() {
            if (!_initialized) {
                _initialized = true;

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
                    if (e.target && ((e.target.classList && e.target.classList.contains('userLinkMaxStreams')))) clampUserStreams();
                });
                modeGroup = createChoiceGroup(getEl('modeChoices'), { onChange: reflectMode });
                authGroup = createChoiceGroup(getEl('authChoices'), { onChange: reflectAuthMethod });

                // The buttons are disabled while the open server's lists load. The check here covers a
                // click that lands before the disabled state does.
                bindClick('btnAddMapping', function() { if (mappingsLoaded) addLibraryMappingRow(); });
                bindClick('btnAddUserMapping', function() { if (mappingsLoaded) addUserMappingRow(); });
            }

            loadConfig();
        });
    });
}

// ============================================
// SERVERS - PAGE CONTROLLER
// ============================================

export default function (view) {
    'use strict';

    // ============================================
    // TAB NAVIGATION (local copy for synchronous access)
    // ============================================

    function getTabs() {
        return [
            { href: 'configurationpage?name=serversync_sync', name: 'Sync' },
            { href: 'configurationpage?name=serversync_servers', name: 'Servers' },
            { href: 'configurationpage?name=serversync_settings', name: 'Settings' }
        ];
    }

    // ============================================
    // SHARED MODULE IMPORT (deferred)
    // ============================================

    var ServerSyncShared = null;
    var createPaginatedTable = null;
    var _filterTableSeq = 0;
    var _filterThumbSeq = 0;
    var createSortableCardList = null;
    var createChoiceGroup = null;

    // Helpers called by bare name, taken from the shared module once it loads. Every call happens after
    // the load, from a viewshow handler that waits for it.
    var escapeHtml, apiRequest, setVisible, bindClick, getEl, setChecked, getChecked, setValue, getValue, requestFor;

    // Relative specifier so a server hosted under a base URL still resolves it.
    var _sharedPromise = import('./configurationpage?name=serversync_shared.js').then(function(shared) {
        ServerSyncShared = shared.createServerSyncShared(view);
        createPaginatedTable = shared.createPaginatedTable;
        createSortableCardList = shared.createSortableCardList;
        createChoiceGroup = shared.createChoiceGroup;
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
        return mode === 'Sync' ? 'purple' : mode === 'Push' ? 'blue' : 'green';
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
    }

    // Updates each card's dot in place. A full render rebuilds the cards and moves the open editor,
    // which takes the cursor out of whatever field the operator is typing in.
    function updateHealthDots() {
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
    }

    // What the key can do there, learned by the last test or sign in.
    function reflectAccess(server) {
        var level = (server && server.AccessLevel) || '';
        setVisible('accessAdmin', level === 'Administrator');
        setVisible('accessUser', level === 'User');
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
            if (statusEl) statusEl.innerHTML = '<span class="text-error">The check failed; see the server log</span>';
        });
    }

    // Fills the editor from the selected entry. The stored key is never shown.
    // the sentinel round-trips and the server keeps the existing secret.
    function loadServerEditor() {
        var server = selectedServer();
        if (!server) return;

        setValue('txtServerName', server.Name || '');
        setMode(server.Mode || 'Pull');
        setChecked('chkServerEnabled', server.IsEnabled !== false);
        setValue('txtSourceServerUrl', server.Url || '');
        setValue('txtSourceServerExternalUrl', server.ExternalUrl || '');
        setChecked('chkAllowPrivateNetwork', server.AllowPrivateNetwork !== false);
        setValue('txtSourceServerApiKey', server.ApiKey ? ServerSyncShared.SECRET_KEPT : '');
        setValue('txtAuthUsername', server.AuthenticatedUser || '');
        setValue('txtAuthPassword', '');
        setAuthMethod(server.AuthenticatedUser ? 'user' : 'key');
        reflectSignedIn(server);
        reflectAccess(server);

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

    // Empties a mapping container, stopping any filter table observers its rows hold, and shows a note
    // in their place when one is given.
    function clearMappingContainer(containerId, note) {
        var container = getEl(containerId);
        if (!container) return;
        container.querySelectorAll('.libraryMapping').forEach(function (row) {
            if (row._disconnectFilterTable) row._disconnectFilterTable();
        });
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
        server.Url = getValue('txtSourceServerUrl', '').trim();
        server.ExternalUrl = getValue('txtSourceServerExternalUrl', '').trim();
        server.AllowPrivateNetwork = getChecked('chkAllowPrivateNetwork');
        if (getAuthMethod() === 'key') {
            var apiKey = getValue('txtSourceServerApiKey', '').trim();
            // Text typed after the kept placeholder is a new key, never the placeholder plus a suffix.
            if (apiKey.length > ServerSyncShared.SECRET_KEPT.length && apiKey.indexOf(ServerSyncShared.SECRET_KEPT) === 0) {
                apiKey = apiKey.substring(ServerSyncShared.SECRET_KEPT.length);
            }
            // An empty field on an entry that has a stored key means "keep it", the same as the sentinel.
            server.ApiKey = apiKey || (server.ApiKey ? ServerSyncShared.SECRET_KEPT : '');
            // A pasted key belongs to no signed in user.
            if (apiKey && apiKey !== ServerSyncShared.SECRET_KEPT) {
                server.AuthenticatedUser = '';
                server.AuthenticatedUserId = '';
            }
        } else if (!server.ApiKey) {
            server.ApiKey = '';
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
        if (!confirm('Remove ' + label + '?\n\nIts mappings and the sync rows tracked from it are removed with it. Files already downloaded stay where they are.')) {
            return;
        }
        var removedKey = server.Key;
        var removedId = server.ServerId || '';
        var removedIndex = selectedIndex;

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
    }

    // Posts the in memory list. The server resolves kept sentinels against the
    // stored entry with the same key, so keys are never retyped.
    // Resolves true when the save landed. A list other than the page's own, such as the list without a
    // server being removed, can be saved, and becomes the page's list only on success.
    function saveServers(successMessage, failureMessage, list) {
        var snapshot = JSON.parse(JSON.stringify(list || servers));
        return saveSection(function (config) {
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
        var container = view.querySelector('#libraryMappingsContainer');
        if (!container) return;
        clearMappingContainer('libraryMappingsContainer');
        (mappings || []).forEach(function(mapping) {
            addLibraryMappingRow(mapping);
        });
    }

    function addLibraryMappingRow(mapping) {
        mapping = mapping || { IsEnabled: true };
        var container = view.querySelector('#libraryMappingsContainer');
        if (!container) return;

        var div = document.createElement('div');
        div.className = 'mapping libraryMapping';
        var filterTableId = 'filterTable_' + (++_filterTableSeq);
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
            '</div>' +
            '<div class="filterSection">' +
                '<h3 class="jpk-subsection-title">Library Filter</h3>' +
                '<div class="filterHeader">' +
                    '<label>Filter Mode</label>' +
                    '<select is="emby-select" class="filterModeSelect">' +
                        '<option value="AllowAll">Allow All</option>' +
                        '<option value="Whitelist">Whitelist</option>' +
                        '<option value="Blacklist">Blacklist</option>' +
                    '</select>' +
                '</div>' +
                '<div class="filterBrowserContainer" style="display:none;">' +
                    '<div class="filterBrowseToggle">' +
                        '<button is="emby-button" type="button" class="raised button-submit filterBrowseItems" data-browse="items">Files</button>' +
                        '<button is="emby-button" type="button" class="raised filterBrowseCollections" data-browse="collections">Collections</button>' +
                        '<button is="emby-button" type="button" class="raised filterBrowsePlaylists" data-browse="playlists">Playlists</button>' +
                    '</div>' +
                    '<div class="filterTableContainer" id="' + filterTableId + '"></div>' +
                '</div>' +
            '</div>';

        container.appendChild(div);

        // --- Filter Mode + Item Picker (base paginated table) ---
        var filterModeSelect = div.querySelector('.filterModeSelect');
        var filterBrowserContainer = div.querySelector('.filterBrowserContainer');

        var selectedFilterItems = {};
        var filterCurrentLibraryId = mapping.SourceLibraryId || '';
        var filterTable = null;
        var filterBrowseMode = 'items';

        (mapping.FilteredItems || []).forEach(function(fi) {
            if (fi.ItemId) {
                selectedFilterItems[fi.ItemId] = { ItemId: fi.ItemId, Name: fi.Name || '', Year: fi.Year, Path: fi.Path || '', Type: fi.Type || null };
            }
        });

        // Renders one source item (thumb + info + selection checkmark) into a base table cell.
        function renderFilterItem(item) {
            var sel = !!selectedFilterItems[item.Id];
            var thumbHtml;
            if (ServerSyncShared && item.Id) {
                var thumbId = 'ss-filter-thumb-' + (++_filterThumbSeq);
                ServerSyncShared.scheduleProxyImage(thumbId, item.Id, false, 120, selectedServer() ? selectedServer().Key : '');
                thumbHtml = '<img id="' + thumbId + '" class="filterItemThumb" />' +
                    '<div class="filterItemThumbPlaceholder" style="display:none"><span class="material-icons">movie</span></div>';
            } else {
                thumbHtml = '<div class="filterItemThumbPlaceholder"><span class="material-icons">movie</span></div>';
            }
            var metaParts = [];
            if (item.Year) metaParts.push(item.Year);
            if (item.Type) metaParts.push(item.Type);
            var overviewHtml = '';
            if (item.Overview) {
                var snippet = item.Overview.substring(0, 120);
                if (item.Overview.length > 120) snippet += '...';
                overviewHtml = '<div class="filterItemOverview">' + escapeHtml(snippet) + '</div>';
            }
            return '<div class="filterItem' + (sel ? ' selected' : '') + '">' +
                thumbHtml +
                '<div class="filterItemInfo">' +
                    '<div class="filterItemName">' + escapeHtml(item.Name || '') + '</div>' +
                    '<div class="filterItemMeta">' + escapeHtml(metaParts.join(' \u2022 ')) + '</div>' +
                    overviewHtml +
                '</div>' +
                '<div class="filterItemCheck"><span class="material-icons">' + (sel ? 'check_box' : 'check_box_outline_blank') + '</span></div>' +
            '</div>';
        }

        // Toggles whitelist/blacklist membership. Selection lives in selectedFilterItems so it
        // persists across searches and pagination (the custom render reads it on every load).
        function onFilterItemClick(item) {
            if (selectedFilterItems[item.Id]) {
                delete selectedFilterItems[item.Id];
            } else {
                selectedFilterItems[item.Id] = { ItemId: item.Id, Name: item.Name || '', Year: item.Year, Path: item.Path || '', Type: item.Type || null };
            }
            var sel = !!selectedFilterItems[item.Id];
            var rowEl = filterBrowserContainer.querySelector('.jpk-table-row[data-id="' + item.Id + '"]');
            if (rowEl) {
                var wrap = rowEl.querySelector('.filterItem');
                if (wrap) wrap.classList.toggle('selected', sel);
                var icon = rowEl.querySelector('.filterItemCheck .material-icons');
                if (icon) icon.textContent = sel ? 'check_box' : 'check_box_outline_blank';
            }
        }

        function buildFilterTable() {
            if (!createPaginatedTable || !filterCurrentLibraryId) return;
            if (!filterTable) {
                filterTable = createPaginatedTable(view, ServerSyncShared, {
                    containerId: filterTableId,
                    endpoint: 'SourceLibraryItems',
                    pagination: { pageSize: 50, loadMore: true },
                    search: { enabled: true, placeholder: 'Search items...' },
                    selection: { enabled: false, idKey: 'Id' },
                    emptyState: { message: 'No items found' },
                    filters: { buildParams: function() { var current = selectedServer(); return { libraryId: filterCurrentLibraryId, serverKey: current ? current.Key : '', collections: filterBrowseMode === 'collections', playlists: filterBrowseMode === 'playlists' }; } },
                    columns: [{ key: 'Id', type: 'custom', render: renderFilterItem }],
                    actions: { onRowClick: onFilterItemClick }
                });
            }
            // Seed a (non-empty) filter value so the table sends libraryId via buildParams, then load.
            filterTable.setFilterValue(filterCurrentLibraryId);
            filterTable.reload();
        }

        function updateFilterVisibility() {
            var show = (filterModeSelect.value !== 'AllowAll');
            filterBrowserContainer.style.display = show ? '' : 'none';
            if (show && filterCurrentLibraryId) {
                buildFilterTable();
            }
        }

        filterModeSelect.value = mapping.FilterMode || 'AllowAll';
        filterModeSelect.addEventListener('change', updateFilterVisibility);

        // Files/ Collections / Playlists browse toggle. Collections
        // and playlists are sync selectors: whitelisting one syncs its
        // members (membership re-resolved every refresh), blacklisting one
        // excludes them. button-submit is Jellyfin's accent (primary) button
        // style. Unselected sides stay plain raised (secondary) buttons.
        var browseButtons = div.querySelectorAll('.filterBrowseToggle > button');
        function setBrowseMode(mode) {
            filterBrowseMode = mode;
            browseButtons.forEach(function (btn) {
                btn.classList.toggle('button-submit', btn.dataset.browse === mode);
            });
            if (filterTable) {
                filterTable.reload();
            }
        }
        browseButtons.forEach(function (btn) {
            btn.addEventListener('click', function () { setBrowseMode(btn.dataset.browse); });
        });

        updateFilterVisibility();

        // Stored on the div so collectLibraryMappings can read them back.
        div._filterModeSelect = filterModeSelect;
        div._selectedFilterItems = selectedFilterItems;
        div._disconnectFilterTable = function() {
            if (filterTable && filterTable.disconnectObserver) filterTable.disconnectObserver();
        };

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
            filterCurrentLibraryId = this.value;
            selectedFilterItems = {};
            div._selectedFilterItems = selectedFilterItems;
            if (filterModeSelect.value !== 'AllowAll' && filterCurrentLibraryId) {
                buildFilterTable();
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
            if (div._disconnectFilterTable) div._disconnectFilterTable();
            div.remove();
        });
    }

    function collectLibraryMappings() {
        var mappings = [];
        view.querySelectorAll('.libraryMapping').forEach(function(row) {
            var sourceSelect = row.querySelector('.sourceLibrarySelect');
            var localSelect = row.querySelector('.localLibrarySelect');

            var filterMode = row._filterModeSelect ? row._filterModeSelect.value : 'AllowAll';
            var filteredItems = [];
            var selectedItems = row._selectedFilterItems || {};
            Object.keys(selectedItems).forEach(function(id) {
                var fi = selectedItems[id];
                filteredItems.push({
                    ItemId: fi.ItemId,
                    Name: fi.Name || '',
                    Year: fi.Year || null,
                    Path: fi.Path || '',
                    Type: fi.Type || null
                });
            });

            mappings.push({
                IsEnabled: row.querySelector('.mappingEnabled').checked,
                SourceLibraryId: pickedValue(sourceSelect),
                SourceLibraryName: pickedName(sourceSelect),
                SourceRootPath: row.querySelector('.sourceRootPath').value,
                LocalLibraryId: pickedValue(localSelect),
                LocalLibraryName: pickedName(localSelect),
                LocalRootPath: row.querySelector('.localRootPath').value,
                FilterMode: filterMode,
                FilteredItems: filteredItems
            });
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

    function updateUserSelects() {
        view.querySelectorAll('.sourceUserSelect').forEach(function(select) {
            var savedValue = select.dataset.savedValue || select.value;
            select.innerHTML = '<option value="">Select source user...</option>';
            sourceUsers.forEach(function(user) {
                var option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                select.appendChild(option);
            });
            if (savedValue) selectLoose(select, savedValue);
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

    function renderUserMappings(mappings) {
        var container = view.querySelector('#userMappingsContainer');
        if (!container) return;
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
            '</div>';

        container.appendChild(div);

        var sourceSelect = div.querySelector('.sourceUserSelect');
        if (mapping.SourceUserId) sourceSelect.dataset.savedValue = mapping.SourceUserId;
        if (mapping.SourceUserName) sourceSelect.dataset.savedName = mapping.SourceUserName;
        sourceSelect.innerHTML = '<option value="">Select source user...</option>';
        sourceUsers.forEach(function(user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            sourceSelect.appendChild(option);
        });
        if (mapping.SourceUserId) selectLoose(sourceSelect, mapping.SourceUserId);

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
                LocalUserName: pickedName(localSelect)
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
                        if (!keyField.value && open && open.ApiKey) keyField.value = ServerSyncShared.SECRET_KEPT;
                    });
                }
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

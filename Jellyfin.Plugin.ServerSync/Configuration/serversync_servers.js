// ============================================
// SETTINGS - PAGE CONTROLLER
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
    // Relative specifier so a server hosted under a base URL still resolves it.
    var createSortableCardList = null;
    var createChoiceGroup = null;
    var _sharedPromise = import('./configurationpage?name=serversync_shared.js').then(function(shared) {
        ServerSyncShared = shared.createServerSyncShared(view);
        createPaginatedTable = shared.createPaginatedTable;
        createSortableCardList = shared.createSortableCardList;
        createChoiceGroup = shared.createChoiceGroup;
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

    // ============================================
    // UTILITY ALIASES (delegate to shared module)
    // ============================================

    function escapeHtml(str) {
        return ServerSyncShared.escapeHtml(str);
    }

    function apiRequest(endpoint, method, data) {
        return ServerSyncShared.apiRequest(endpoint, method, data);
    }

    function setVisible(elementId, visible) {
        ServerSyncShared.setVisible(elementId, visible);
    }

    function bindClick(id, handler) {
        return ServerSyncShared.bindClick(id, handler);
    }

    function getEl(id) {
        return view.querySelector('#' + id);
    }

    function setChecked(id, value) {
        ServerSyncShared.setChecked(id, value);
    }

    function getChecked(id) {
        return ServerSyncShared.getChecked(id);
    }

    function setValue(id, value) {
        var el = getEl(id);
        if (el) el.value = value;
    }

    function getValue(id, fallback) {
        var el = getEl(id);
        return el ? el.value : (fallback || '');
    }

    function getIntValue(id, fallback) {
        var v = parseInt(getValue(id, ''), 10);
        return isNaN(v) ? fallback : v;
    }

    // Section saves re-fetch the config and apply only this section's
    // fields: a page-load snapshot posted whole would clobber values other
    // writers changed since — task-written timestamps/failure records and
    // the other dashboard tab's sections.
    function saveSection(mutator, successMessage, failureMessage) {
        return ServerSyncShared.getConfig().then(function (config) {
            mutator(config);
            currentConfig = config;
            return ServerSyncShared.saveConfig(config);
        }).then(function () {
            Dashboard.alert(successMessage);
        }).catch(function () {
            Dashboard.alert(failureMessage);
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
            // The open card stays open; another card's head switches to it.
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

    // The old name is still used by the connection and token flows.
    var renderServerSelector = renderServerList;

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
                renderServerList();
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

    // A token the page never saw round trips as the sentinel; anything stored comes back encrypted.
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
        var mode = getMode();
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
        var request = requestFor(server);
        request.Mode = getMode();
        ServerSyncShared.apiRequest('Hints/CheckPeer', 'POST', request).then(function (result) {
            if (!statusEl) return;
            var severity = (result && result.Severity) || 'error';
            statusEl.innerHTML = '<span class="' + (severity === 'ok' ? 'text-success' : severity === 'warn' ? 'text-warn' : 'text-error') + '">' + escapeHtml((result && result.Message) || 'No answer') + '</span>';
            if (result && result.Reachable) {
                serverHealth[server.Key] = 'ok';
                if (result.ServerName) server.ServerName = result.ServerName;
                if (result.ServerId) server.ServerId = result.ServerId;
                renderServerList();
            }
        }).catch(function () {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">The check failed; see the server log</span>';
        });
    }

    // Fills the editor from the selected entry. The stored key is never shown;
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
        mappingsLoaded = false;
        var configured = !!(server.Url && server.ApiKey);
        setMappingSectionsVisible(configured);
        if (configured) {
            Promise.all([fetchSourceLibraries(server), fetchSourceUsers(server)]).then(function () {
                if (selectedServer() !== server) return;
                renderLibraryMappings(server.LibraryMappings || []);
                renderUserMappings(server.UserMappings || []);
                mappingsLoaded = true;
            });
        } else {
            renderLibraryMappings([]);
            renderUserMappings([]);
            mappingsLoaded = true;
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
            var apiKey = getValue('txtSourceServerApiKey', '');
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
        servers.splice(selectedIndex, 1);
        saveServers('Server removed', 'Failed to remove server').then(function () {
            selectServer(servers.length > 0 ? Math.min(selectedIndex, servers.length - 1) : -1);
            // The rows it tracked have nothing to pull from now. Failing to clear them is not fatal; the
            // tables simply keep inert rows until a reset.
            if (removedKey) {
                apiRequest('Servers/' + encodeURIComponent(removedKey) + '/Rows', 'DELETE').catch(function () {});
            }
        });
    }

    // Posts the in memory list. The server resolves kept sentinels against the
    // stored entry with the same key, so keys are never retyped.
    function saveServers(successMessage, failureMessage) {
        var snapshot = JSON.parse(JSON.stringify(servers));
        return saveSection(function (config) {
            config.Servers = snapshot;
        }, successMessage || 'Servers saved', failureMessage || 'Failed to save servers').then(function () {
            if (currentConfig && currentConfig.Servers) {
                servers = currentConfig.Servers;
                renderServerSelector();
            }
        });
    }

    function saveServerConfig() {
        if (!collectServerEditor()) return;
        saveServers('Server saved', 'Failed to save server');
    }

    function requestFor(server, apiKeyOverride) {
        return {
            ServerUrl: server.Url,
            ApiKey: apiKeyOverride || (server.ApiKey ? server.ApiKey : ''),
            ServerKey: server.Key,
            AllowPrivateNetwork: server.AllowPrivateNetwork !== false,
            AuthenticatedUserId: server.AuthenticatedUserId || null
        };
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

        apiRequest('TestConnection', 'POST', requestFor(server)).then(function(response) {
            if (response && response.Success) {
                if (statusEl) statusEl.innerHTML = '<span class="text-success">Connected to ' + escapeHtml(response.ServerName) + '</span>';
                server.ServerName = response.ServerName || '';
                server.ServerId = response.ServerId || '';
                if (response.IsAdministrator === true) server.AccessLevel = 'Administrator';
                else if (response.IsAdministrator === false) server.AccessLevel = 'User';
                reflectAccess(server);
                var nameEl = getEl('txtSourceServerName');
                var idEl = getEl('txtSourceServerId');
                if (nameEl) nameEl.textContent = server.ServerName || 'Unknown';
                if (idEl) idEl.textContent = server.ServerId || 'Unknown';
                setVisible('serverInfoContainer', true);
                serverHealth[server.Key] = 'ok';
                renderServerSelector();

                setMappingSectionsVisible(true);
                mappingsLoaded = false;
                Promise.all([fetchSourceLibraries(server), fetchSourceUsers(server)]).then(function () {
                    renderLibraryMappings(server.LibraryMappings || []);
                    renderUserMappings(server.UserMappings || []);
                    mappingsLoaded = true;
                });
            } else {
                if (statusEl) statusEl.innerHTML = '<span class="text-error">' + escapeHtml((response && response.Message) || 'Connection failed') + '</span>';
                serverHealth[server.Key] = 'bad';
                renderServerSelector();
            }
        }).catch(function() {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Connection failed</span>';
            serverHealth[server.Key] = 'bad';
            renderServerSelector();
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

        apiRequest('Authenticate', 'POST', {
            ServerUrl: server.Url,
            Username: username,
            Password: password,
            AllowPrivateNetwork: server.AllowPrivateNetwork !== false
        }).then(function(response) {
            if (response && response.Success) {
                // Clear the password field for security
                if (passwordEl) passwordEl.value = '';

                server.ApiKey = response.AccessToken;
                server.AuthenticatedUser = response.Username || username;
                server.AuthenticatedUserId = response.UserId || '';
                if (response.IsAdministrator === true) server.AccessLevel = 'Administrator';
                else if (response.IsAdministrator === false) server.AccessLevel = 'User';
                reflectAccess(server);
                server.ServerName = response.ServerName || '';
                server.ServerId = response.ServerId || '';
                setValue('txtSourceServerApiKey', response.AccessToken);

                var authUserEl = getEl('txtAuthenticatedUser');
                if (authUserEl) authUserEl.textContent = server.AuthenticatedUser;
                setVisible('authenticatedUserRow', true);
                reflectSignedIn(server);

                var nameEl = getEl('txtSourceServerName');
                var idEl = getEl('txtSourceServerId');
                if (nameEl) nameEl.textContent = server.ServerName || 'Unknown';
                if (idEl) idEl.textContent = server.ServerId || 'Unknown';
                setVisible('serverInfoContainer', true);

                saveServers('Token generated and saved', 'Token generated, but saving failed').then(function() {
                    if (statusEl) statusEl.innerHTML = '<span class="text-success">Token generated and saved!</span>';
                    var saved = selectedServer() || server;
                    serverHealth[saved.Key] = 'ok';
                    renderServerSelector();
                    setMappingSectionsVisible(true);
                    mappingsLoaded = false;
                    Promise.all([fetchSourceLibraries(saved), fetchSourceUsers(saved)]).then(function () {
                        renderLibraryMappings(saved.LibraryMappings || []);
                        renderUserMappings(saved.UserMappings || []);
                        mappingsLoaded = true;
                    });
                });
            } else {
                if (statusEl) statusEl.innerHTML = '<span class="text-error">' + escapeHtml((response && response.Message) || 'Authentication failed') + '</span>';
            }
        }).catch(function(error) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Authentication failed</span>';
            console.error('Token generation error:', error);
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
            sourceLibraries = libraries || [];
            updateLibrarySelects();
        }).catch(function() {
            sourceLibraries = [];
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
        container.innerHTML = '';
        (mappings || []).forEach(function(mapping, index) {
            addLibraryMappingRow(mapping, index);
        });
    }

    function addLibraryMappingRow(mapping, index) {
        mapping = mapping || { IsEnabled: true };
        var container = view.querySelector('#libraryMappingsContainer');
        if (!container) return;
        if (index === undefined) index = container.children.length;

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
        // style; unselected sides stay plain raised (secondary) buttons.
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
                SourceLibraryId: sourceSelect.value,
                SourceLibraryName: sourceSelect.options[sourceSelect.selectedIndex] ? sourceSelect.options[sourceSelect.selectedIndex].textContent : '',
                SourceRootPath: row.querySelector('.sourceRootPath').value,
                LocalLibraryId: localSelect.value,
                LocalLibraryName: localSelect.options[localSelect.selectedIndex] ? localSelect.options[localSelect.selectedIndex].textContent : '',
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
            sourceUsers = users || [];
            updateUserSelects();
        }).catch(function() {
            sourceUsers = [];
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
        container.innerHTML = '';
        (mappings || []).forEach(function(mapping, index) {
            addUserMappingRow(mapping, index);
        });
    }

    function addUserMappingRow(mapping, index) {
        mapping = mapping || { IsEnabled: true };
        var container = view.querySelector('#userMappingsContainer');
        if (!container) return;
        if (index === undefined) index = container.children.length;

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
                SourceUserId: sourceSelect.value,
                SourceUserName: sourceSelect.options[sourceSelect.selectedIndex] ? sourceSelect.options[sourceSelect.selectedIndex].textContent : '',
                LocalUserId: localSelect.value,
                LocalUserName: localSelect.options[localSelect.selectedIndex] ? localSelect.options[localSelect.selectedIndex].textContent : ''
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
                modeGroup = createChoiceGroup(getEl('modeChoices'), { onChange: reflectMode });
                authGroup = createChoiceGroup(getEl('authChoices'), { onChange: reflectAuthMethod });

                bindClick('btnAddMapping', function() { addLibraryMappingRow(); });
                bindClick('btnAddUserMapping', function() { addUserMappingRow(); });
            }

            loadConfig();
        });
    });
}

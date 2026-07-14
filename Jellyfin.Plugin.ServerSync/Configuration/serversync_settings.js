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
            { href: 'configurationpage?name=serversync_settings', name: 'Settings' }
        ];
    }

    // ============================================
    // SHARED MODULE IMPORT (deferred)
    // ============================================

    var ServerSyncShared = null;
    var createPaginatedTable = null;
    var _filterTableSeq = 0;
    var _sharedPromise = import('/web/configurationpage?name=serversync_shared.js').then(function(shared) {
        ServerSyncShared = shared.createServerSyncShared(view);
        createPaginatedTable = shared.createPaginatedTable;
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

    // --- Server Configuration ---

    function loadServerConfig(config) {
        var urlEl = view.querySelector('#txtSourceServerUrl');
        var apiKeyEl = view.querySelector('#txtSourceServerApiKey');
        var externalUrlEl = view.querySelector('#txtSourceServerExternalUrl');
        if (urlEl) urlEl.value = config.SourceServerUrl || '';
        // Never show the stored (encrypted) key; the sentinel round-trips
        // untouched and the server keeps the existing secret on save.
        if (apiKeyEl) apiKeyEl.value = config.SourceServerApiKey ? ServerSyncShared.SECRET_KEPT : '';
        if (externalUrlEl) externalUrlEl.value = config.SourceServerExternalUrl || '';

        if (config.SourceServerName || config.SourceServerId) {
            var nameEl = view.querySelector('#txtSourceServerName');
            var idEl = view.querySelector('#txtSourceServerId');
            if (nameEl) nameEl.textContent = config.SourceServerName || 'Unknown';
            if (idEl) idEl.textContent = config.SourceServerId || 'Unknown';
            setVisible('serverInfoContainer', true);
        }

        if (config.SourceServerAuthenticatedUser) {
            var authUserEl = view.querySelector('#txtAuthenticatedUser');
            if (authUserEl) authUserEl.textContent = config.SourceServerAuthenticatedUser;
            setVisible('authenticatedUserRow', true);

            // Pre-fill the username field for convenience
            var usernameEl = view.querySelector('#txtAuthUsername');
            if (usernameEl) usernameEl.value = config.SourceServerAuthenticatedUser;
        } else {
            setVisible('authenticatedUserRow', false);
        }
    }

    function testConnection() {
        var urlEl = view.querySelector('#txtSourceServerUrl');
        var apiKeyEl = view.querySelector('#txtSourceServerApiKey');
        var statusEl = view.querySelector('#connectionStatus');
        var url = urlEl ? urlEl.value : '';
        var apiKey = apiKeyEl ? apiKeyEl.value : '';

        if (!url || !apiKey) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Please enter URL and API key</span>';
            return;
        }

        if (statusEl) statusEl.textContent = 'Testing...';

        apiRequest('TestConnection', 'POST', { ServerUrl: url, ApiKey: apiKey }).then(function(response) {
            if (response && response.Success) {
                if (statusEl) statusEl.innerHTML = '<span class="text-success">Connected to ' + escapeHtml(response.ServerName) + '</span>';
                var nameEl = view.querySelector('#txtSourceServerName');
                var idEl = view.querySelector('#txtSourceServerId');
                if (nameEl) nameEl.textContent = response.ServerName || 'Unknown';
                if (idEl) idEl.textContent = response.ServerId || 'Unknown';
                setVisible('serverInfoContainer', true);

                if (currentConfig) {
                    currentConfig.SourceServerName = response.ServerName;
                    currentConfig.SourceServerId = response.ServerId;
                }

                fetchSourceLibraries(url, apiKey);
                fetchSourceUsers(url, apiKey);
                showMappingSections();
            } else {
                if (statusEl) statusEl.innerHTML = '<span class="text-error">' + escapeHtml((response && response.Message) || 'Connection failed') + '</span>';
            }
        }).catch(function() {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Connection failed</span>';
        });
    }

    function saveServerConfig() {
        saveSection(function (config) {
            var urlEl = view.querySelector('#txtSourceServerUrl');
            var apiKeyEl = view.querySelector('#txtSourceServerApiKey');
            var externalUrlEl = view.querySelector('#txtSourceServerExternalUrl');
            config.SourceServerUrl = urlEl ? urlEl.value : '';
            config.SourceServerApiKey = apiKeyEl ? apiKeyEl.value : '';
            config.SourceServerExternalUrl = externalUrlEl ? externalUrlEl.value : '';

            // Test Connection stores the discovered server name/id only in
            // currentConfig; carry them into the freshly fetched config or
            // they are lost on save.
            if (currentConfig && currentConfig.SourceServerName) {
                config.SourceServerName = currentConfig.SourceServerName;
            }
            if (currentConfig && currentConfig.SourceServerId) {
                config.SourceServerId = currentConfig.SourceServerId;
            }
        }, 'Server settings saved', 'Failed to save server settings');
    }

    // --- Token Generation ---

    function generateToken() {
        var urlEl = view.querySelector('#txtSourceServerUrl');
        var usernameEl = view.querySelector('#txtAuthUsername');
        var passwordEl = view.querySelector('#txtAuthPassword');
        var statusEl = view.querySelector('#tokenGeneratorStatus');

        var serverUrl = urlEl ? urlEl.value : '';
        var username = usernameEl ? usernameEl.value : '';
        var password = passwordEl ? passwordEl.value : '';

        if (!serverUrl) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Please enter a Server URL first</span>';
            return;
        }

        if (!username || !password) {
            if (statusEl) statusEl.innerHTML = '<span class="text-error">Username and password are required</span>';
            return;
        }

        if (statusEl) statusEl.textContent = 'Authenticating...';

        apiRequest('Authenticate', 'POST', {
            ServerUrl: serverUrl,
            Username: username,
            Password: password
        }).then(function(response) {
            if (response && response.Success) {
                // Clear the password field for security
                if (passwordEl) passwordEl.value = '';

                var apiKeyEl = view.querySelector('#txtSourceServerApiKey');
                if (apiKeyEl) apiKeyEl.value = response.AccessToken;

                var authUserEl = view.querySelector('#txtAuthenticatedUser');
                if (authUserEl) authUserEl.textContent = response.Username || username;
                setVisible('authenticatedUserRow', true);

                var nameEl = view.querySelector('#txtSourceServerName');
                var idEl = view.querySelector('#txtSourceServerId');
                if (nameEl) nameEl.textContent = response.ServerName || 'Unknown';
                if (idEl) idEl.textContent = response.ServerId || 'Unknown';
                setVisible('serverInfoContainer', true);

                if (currentConfig) {
                    currentConfig.SourceServerApiKey = response.AccessToken;
                    currentConfig.SourceServerAuthenticatedUser = response.Username || username;
                    currentConfig.SourceServerAuthenticatedUserId = response.UserId || '';
                    currentConfig.SourceServerName = response.ServerName;
                    currentConfig.SourceServerId = response.ServerId;
                }

                ServerSyncShared.getConfig().then(function (config) {
                    config.SourceServerUrl = serverUrl;
                    config.SourceServerApiKey = response.AccessToken;
                    config.SourceServerAuthenticatedUser = response.Username || username;
                    config.SourceServerAuthenticatedUserId = response.UserId || '';
                    config.SourceServerName = response.ServerName;
                    config.SourceServerId = response.ServerId;
                    currentConfig = config;
                    return ServerSyncShared.saveConfig(config);
                }).then(function() {
                    if (statusEl) statusEl.innerHTML = '<span class="text-success">Token generated and saved!</span>';

                    // Fetch source data now that we have valid credentials
                    fetchSourceLibraries(serverUrl, response.AccessToken);
                    fetchSourceUsers(serverUrl, response.AccessToken);
                    showMappingSections();
                }).catch(function() {
                    if (statusEl) statusEl.innerHTML = '<span class="text-success">Token generated!</span> <span class="text-error">(Save failed)</span>';
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

    function fetchSourceLibraries(serverUrl, apiKey) {
        return apiRequest('GetSourceLibraries', 'POST', { ServerUrl: serverUrl, ApiKey: apiKey }).then(function(libraries) {
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
            if (savedValue) select.value = savedValue;
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
            if (savedValue) select.value = savedValue;
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
        mapping = mapping || {};
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
                var thumbId = 'ss-filter-thumb-' + escapeHtml(String(item.Id));
                ServerSyncShared.scheduleProxyImage(thumbId, item.Id, false, 120);
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
                    filters: { buildParams: function() { return { libraryId: filterCurrentLibraryId, collections: filterBrowseMode === 'collections', playlists: filterBrowseMode === 'playlists' }; } },
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
        if (mapping.SourceLibraryId) sourceSelect.value = mapping.SourceLibraryId;
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
        if (mapping.LocalLibraryId) localSelect.value = mapping.LocalLibraryId;
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

    function saveLibraries() {
        saveSection(function (config) {
            config.LibraryMappings = collectLibraryMappings();
        }, 'Library mappings saved', 'Failed to save library mappings');
    }

    // ============================================
    // USER MAPPINGS MODULE
    // ============================================

    function fetchSourceUsers(serverUrl, apiKey) {
        return apiRequest('GetSourceUsers', 'POST', { ServerUrl: serverUrl, ApiKey: apiKey }).then(function(users) {
            sourceUsers = users || [];
            updateUserSelects();
            renderWatchedFilterUsers(getCurrentWatchedFilterUserIds());
        }).catch(function() {
            sourceUsers = [];
            renderWatchedFilterUsers(getCurrentWatchedFilterUserIds());
        });
    }

    function getCurrentWatchedFilterUserIds() {
        var rendered = collectWatchedFilterUsers();
        if (rendered.length > 0) {
            return rendered;
        }

        return (currentConfig && currentConfig.WatchedFilterUserIds) || [];
    }

    function renderWatchedFilterUsers(selectedIds) {
        var container = view.querySelector('#watchedFilterUsersList');
        if (!container) return;

        var selectedSet = {};
        (selectedIds || []).forEach(function(id) { selectedSet[id] = true; });

        container.innerHTML = '';

        if (!sourceUsers || sourceUsers.length === 0) {
            container.innerHTML = '<div class="filterBrowserStatus">No source users available. Connect to the source server to load users.</div>';
            return;
        }

        var list = document.createElement('div');
        list.className = 'filterItemsList';

        sourceUsers.forEach(function(user) {
            var isSelected = !!selectedSet[user.Id];

            var itemEl = document.createElement('div');
            itemEl.className = 'filterItem watchedFilterUserItem' + (isSelected ? ' selected' : '');
            itemEl.dataset.userId = user.Id;

            var thumbHtml;
            if (ServerSyncShared && user.Id) {
                var userThumbId = 'ss-user-thumb-' + escapeHtml(String(user.Id));
                ServerSyncShared.scheduleProxyImage(userThumbId, user.Id, true, 120);
                thumbHtml = '<img id="' + userThumbId + '" class="filterItemThumb filterItemThumbSquare" />' +
                    '<div class="filterItemThumbPlaceholder filterItemThumbSquare" style="display:none"><span class="material-icons">person</span></div>';
            } else {
                thumbHtml = '<div class="filterItemThumbPlaceholder filterItemThumbSquare"><span class="material-icons">person</span></div>';
            }

            itemEl.innerHTML = thumbHtml +
                '<div class="filterItemInfo">' +
                    '<div class="filterItemName">' + escapeHtml(user.Name || '') + '</div>' +
                '</div>' +
                '<div class="filterItemCheck"><span class="material-icons">' + (isSelected ? 'check_box' : 'check_box_outline_blank') + '</span></div>';

            itemEl.addEventListener('click', function() {
                toggleWatchedFilterUser(itemEl);
            });

            list.appendChild(itemEl);
        });

        container.appendChild(list);
    }

    function toggleWatchedFilterUser(itemEl) {
        var icon = itemEl.querySelector('.filterItemCheck .material-icons');
        if (itemEl.classList.contains('selected')) {
            itemEl.classList.remove('selected');
            if (icon) icon.textContent = 'check_box_outline_blank';
        } else {
            itemEl.classList.add('selected');
            if (icon) icon.textContent = 'check_box';
        }
    }

    function collectWatchedFilterUsers() {
        var ids = [];
        view.querySelectorAll('.watchedFilterUserItem.selected').forEach(function(row) {
            if (row.dataset.userId) {
                ids.push(row.dataset.userId);
            }
        });
        return ids;
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
            if (savedValue) select.value = savedValue;
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
            if (savedValue) select.value = savedValue;
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
        if (mapping.SourceUserId) sourceSelect.value = mapping.SourceUserId;

        var localSelect = div.querySelector('.localUserSelect');
        if (mapping.LocalUserId) localSelect.dataset.savedValue = mapping.LocalUserId;
        localSelect.innerHTML = '<option value="">Select local user...</option>';
        localUsers.forEach(function(user) {
            var option = document.createElement('option');
            option.value = user.Id;
            option.textContent = user.Name;
            localSelect.appendChild(option);
        });
        if (mapping.LocalUserId) localSelect.value = mapping.LocalUserId;

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

    function saveUsers() {
        saveSection(function (config) {
            config.UserMappings = collectUserMappings();
        }, 'User mappings saved', 'Failed to save user mappings');
    }

    // ============================================
    // SYNC SETTINGS MODULE
    // ============================================

    // --- Content Settings ---

    function loadContentSettings(config) {
        setChecked('chkEnableContentSync', config.EnableContentSync || false);
        setChecked('chkDetectUpdatedFiles', config.DetectUpdatedFiles !== false);
        setChecked('chkMirrorSyncedCollections', config.MirrorSyncedCollections !== false);
        setChecked('chkIncludeCompanionFiles', config.IncludeCompanionFiles || false);
        setChecked('chkSkipWatchedByAllUsers', config.SkipWatchedByAllUsers || false);
        renderWatchedFilterUsers(config.WatchedFilterUserIds || []);
        setValue('selDownloadNewContentMode', config.DownloadNewContentMode || 'Enabled');
        setValue('selReplaceExistingContentMode', config.ReplaceExistingContentMode || 'Enabled');
        setValue('selDeleteMissingContentMode', config.DeleteMissingContentMode || 'Disabled');
        setChecked('chkEnableRecyclingBin', config.EnableRecyclingBin || false);
        setValue('txtRecyclingBinPath', config.RecyclingBinPath || '');
        setValue('txtRecyclingBinRetentionDays', config.RecyclingBinRetentionDays || 7);
        setChecked('chkRemoveEmptyFolders', config.RemoveEmptyFoldersOnDelete || false);
        setValue('txtMaxConcurrentDownloads', config.MaxConcurrentDownloads || 2);
        setValue('txtMaxRetryCount', config.MaxRetryCount || 3);
        setValue('txtSizeMatchToleranceBytes', config.SizeMatchToleranceBytes || 0);
        setValue('txtTempDownloadPath', config.TempDownloadPath || '');
        setValue('txtMaxDownloadSpeed', config.MaxDownloadSpeed || 0);
        setValue('selDownloadSpeedUnit', config.DownloadSpeedUnit || 'MB');
        setValue('txtMinFreeDiskSpace', config.MinimumFreeDiskSpaceGb == null ? 10 : config.MinimumFreeDiskSpaceGb);
        setChecked('chkEnableBandwidthScheduling', config.EnableBandwidthScheduling || false);
        setValue('txtScheduledStartHour', config.ScheduledStartHour || 0);
        setValue('txtScheduledEndHour', config.ScheduledEndHour == null ? 6 : config.ScheduledEndHour);
        setValue('txtScheduledDownloadSpeed', config.ScheduledDownloadSpeed || 0);
        setValue('selScheduledDownloadSpeedUnit', config.ScheduledDownloadSpeedUnit || 'MB');

        ServerSyncShared.bindReveal('chkSkipWatchedByAllUsers', 'watchedFilterUsersSettings');
        ServerSyncShared.bindReveal('chkEnableRecyclingBin', 'recyclingBinSettings');
        ServerSyncShared.bindReveal('chkEnableBandwidthScheduling', 'bandwidthScheduleContainer');
    }

    function saveContentSettings() {
        saveSection(function (config) {
            config.EnableContentSync = getChecked('chkEnableContentSync');
            config.DetectUpdatedFiles = getChecked('chkDetectUpdatedFiles');
            config.MirrorSyncedCollections = getChecked('chkMirrorSyncedCollections');
            config.IncludeCompanionFiles = getChecked('chkIncludeCompanionFiles');
            config.SkipWatchedByAllUsers = getChecked('chkSkipWatchedByAllUsers');
            config.WatchedFilterUserIds = collectWatchedFilterUsers();
            config.DownloadNewContentMode = getValue('selDownloadNewContentMode', 'Enabled');
            config.ReplaceExistingContentMode = getValue('selReplaceExistingContentMode', 'Enabled');
            config.DeleteMissingContentMode = getValue('selDeleteMissingContentMode', 'Disabled');
            config.EnableRecyclingBin = getChecked('chkEnableRecyclingBin');
            config.RecyclingBinPath = getValue('txtRecyclingBinPath');
            config.RecyclingBinRetentionDays = getIntValue('txtRecyclingBinRetentionDays', 7);
            config.RemoveEmptyFoldersOnDelete = getChecked('chkRemoveEmptyFolders');
            config.MaxConcurrentDownloads = getIntValue('txtMaxConcurrentDownloads', 2);
            config.MaxRetryCount = getIntValue('txtMaxRetryCount', 3);
            config.SizeMatchToleranceBytes = getIntValue('txtSizeMatchToleranceBytes', 0);
            config.TempDownloadPath = getValue('txtTempDownloadPath') || null;
            config.MaxDownloadSpeed = getIntValue('txtMaxDownloadSpeed', 0);
            config.DownloadSpeedUnit = getValue('selDownloadSpeedUnit', 'MB');
            config.MinimumFreeDiskSpaceGb = getIntValue('txtMinFreeDiskSpace', 10);
            config.EnableBandwidthScheduling = getChecked('chkEnableBandwidthScheduling');
            config.ScheduledStartHour = getIntValue('txtScheduledStartHour', 0);
            config.ScheduledEndHour = getIntValue('txtScheduledEndHour', 6);
            config.ScheduledDownloadSpeed = getIntValue('txtScheduledDownloadSpeed', 0);
            config.ScheduledDownloadSpeedUnit = getValue('selScheduledDownloadSpeedUnit', 'MB');

        }, 'Content settings saved', 'Failed to save content settings');
    }

    // --- History Settings ---

    function loadHistorySettings(config) {
        setChecked('chkEnableHistorySync', config.EnableHistorySync || false);
        setChecked('chkHistorySyncPlayedStatus', config.HistorySyncPlayedStatus !== false);
        setChecked('chkHistorySyncPlaybackPosition', config.HistorySyncPlaybackPosition !== false);
        setChecked('chkHistorySyncPlayCount', config.HistorySyncPlayCount !== false);
        setChecked('chkHistorySyncLastPlayedDate', config.HistorySyncLastPlayedDate !== false);
        setChecked('chkHistorySyncFavorites', config.HistorySyncFavorites !== false);
    }

    function saveHistorySettings() {
        saveSection(function (config) {
            config.EnableHistorySync = getChecked('chkEnableHistorySync');
            config.HistorySyncPlayedStatus = getChecked('chkHistorySyncPlayedStatus');
            config.HistorySyncPlaybackPosition = getChecked('chkHistorySyncPlaybackPosition');
            config.HistorySyncPlayCount = getChecked('chkHistorySyncPlayCount');
            config.HistorySyncLastPlayedDate = getChecked('chkHistorySyncLastPlayedDate');
            config.HistorySyncFavorites = getChecked('chkHistorySyncFavorites');

        }, 'History settings saved', 'Failed to save history settings');
    }

    // --- Metadata Settings ---

    function loadMetadataSettings(config) {
        setChecked('chkEnableMetadataSync', config.EnableMetadataSync || false);
        setChecked('chkMetadataSyncMetadata', config.MetadataSyncMetadata !== false);
        setChecked('chkMetadataSyncGenres', config.MetadataSyncGenres !== false);
        setChecked('chkMetadataSyncTags', config.MetadataSyncTags !== false);
        setChecked('chkMetadataSyncStudios', config.MetadataSyncStudios !== false);
        setChecked('chkMetadataSyncPeople', config.MetadataSyncPeople === true);
        setChecked('chkMetadataSyncImages', config.MetadataSyncImages !== false);
        setChecked('chkMetadataSyncFolderItems', config.MetadataSyncFolderItems === true);
    }

    function saveMetadataSettings() {
        saveSection(function (config) {
            config.EnableMetadataSync = getChecked('chkEnableMetadataSync');
            config.MetadataSyncMetadata = getChecked('chkMetadataSyncMetadata');
            config.MetadataSyncGenres = getChecked('chkMetadataSyncGenres');
            config.MetadataSyncTags = getChecked('chkMetadataSyncTags');
            config.MetadataSyncStudios = getChecked('chkMetadataSyncStudios');
            config.MetadataSyncPeople = getChecked('chkMetadataSyncPeople');
            config.MetadataSyncImages = getChecked('chkMetadataSyncImages');
            config.MetadataSyncFolderItems = getChecked('chkMetadataSyncFolderItems');

        }, 'Metadata settings saved', 'Failed to save metadata settings');
    }

    // --- People Sync Settings ---

    function loadPeopleSettings(config) {
        setChecked('chkEnablePeopleSync', config.EnablePeopleSync === true);
        setChecked('chkPeopleSyncImages', config.PeopleSyncImages !== false);
    }

    function savePeopleSettings() {
        saveSection(function (config) {
            config.EnablePeopleSync = getChecked('chkEnablePeopleSync');
            config.PeopleSyncImages = getChecked('chkPeopleSyncImages');

        }, 'People settings saved', 'Failed to save people settings');
    }

    // --- User Sync Settings ---

    function loadUserSyncSettings(config) {
        setChecked('chkEnableUserSync', config.EnableUserSync || false);
        setChecked('chkUserSyncPolicy', config.UserSyncPolicy !== false);
        setChecked('chkUserSyncConfiguration', config.UserSyncConfiguration !== false);
        setChecked('chkUserSyncProfileImage', config.UserSyncProfileImage !== false);
    }

    function saveUserSyncSettings() {
        saveSection(function (config) {
            config.EnableUserSync = getChecked('chkEnableUserSync');
            config.UserSyncPolicy = getChecked('chkUserSyncPolicy');
            config.UserSyncConfiguration = getChecked('chkUserSyncConfiguration');
            config.UserSyncProfileImage = getChecked('chkUserSyncProfileImage');

        }, 'User sync settings saved', 'Failed to save user sync settings');
    }

    // --- Processing Settings ---

    function loadProcessingSettings(config) {
        setValue('txtRefreshParallelism', config.RefreshParallelism || 8);
        setChecked('chkDeepImageVerification', config.DeepImageVerification === true);
    }

    function saveProcessingSettings() {
        saveSection(function (config) {
            config.RefreshParallelism = Math.min(16, Math.max(1, getIntValue('txtRefreshParallelism', 8)));
            config.DeepImageVerification = getChecked('chkDeepImageVerification');

        }, 'Processing settings saved', 'Failed to save processing settings');
    }

    // ============================================
    // PAGE INITIALIZATION
    // ============================================

    function showMappingSections() {
        setVisible('librariesSection', true);
        setVisible('usersSection', true);
    }

    function loadConfig() {
        ServerSyncShared.getConfig().then(function(config) {
            currentConfig = config;

            loadServerConfig(config);
            loadContentSettings(config);
            loadHistorySettings(config);
            loadMetadataSettings(config);
            loadPeopleSettings(config);
            loadUserSyncSettings(config);
            loadProcessingSettings(config);

            if (config.SourceServerUrl && config.SourceServerApiKey) {
                showMappingSections();
            }

            var promises = [fetchLocalLibraries(), fetchLocalUsers()];
            if (config.SourceServerUrl && config.SourceServerApiKey) {
                promises.push(fetchSourceLibraries(config.SourceServerUrl, config.SourceServerApiKey));
                promises.push(fetchSourceUsers(config.SourceServerUrl, config.SourceServerApiKey));
            }

            Promise.all(promises).then(function() {
                renderLibraryMappings(config.LibraryMappings || []);
                renderUserMappings(config.UserMappings || []);
            });
        }).catch(function() {
            Dashboard.alert('Failed to load plugin configuration');
        });
    }

    // ============================================
    // TROUBLESHOOTING: DATABASE RESET
    // ============================================

    function resetTable(endpoint, tableName) {
        if (!confirm('Are you sure you want to reset the ' + tableName + ' table?\n\nThis will delete all ' + tableName + ' tracking data and you will need to re-sync. This cannot be undone.')) {
            return;
        }

        ServerSyncShared.apiRequest(endpoint, 'POST').then(function() {
            ServerSyncShared.showAlert('The ' + tableName + ' table has been reset.');
        }).catch(function(err) {
            console.error(endpoint + ' error:', err);
            ServerSyncShared.showAlert('Failed to reset ' + tableName + ' table.');
        });
    }

    function resetEntireDatabase() {
        if (!confirm('Are you sure you want to reset the ENTIRE sync database?\n\nThis will delete ALL tracking data across all sync types (Content, History, Metadata, Users). You will need to re-sync everything from scratch. This cannot be undone.')) {
            return;
        }

        ServerSyncShared.apiRequest('ResetSyncDatabase', 'POST').then(function() {
            ServerSyncShared.showAlert('The entire sync database has been reset.');
        }).catch(function(err) {
            console.error('ResetSyncDatabase error:', err);
            ServerSyncShared.showAlert('Failed to reset sync database.');
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

                bindClick('btnAddMapping', function() { addLibraryMappingRow(); });
                bindClick('btnSaveLibraries', saveLibraries);

                bindClick('btnAddUserMapping', function() { addUserMappingRow(); });
                bindClick('btnSaveUsers', saveUsers);
                bindClick('btnSaveProcessing', saveProcessingSettings);

                bindClick('btnSaveContentSettings', saveContentSettings);
                bindClick('btnSaveHistorySettings', saveHistorySettings);
                bindClick('btnSaveMetadataSettings', saveMetadataSettings);
                bindClick('btnSavePeopleSettings', savePeopleSettings);
                bindClick('btnSaveUserSyncSettings', saveUserSyncSettings);

                bindClick('btnResetContentTable', function() { resetTable('ResetContentSyncDatabase', 'content sync'); });
                bindClick('btnResetHistoryTable', function() { resetTable('ResetHistorySyncDatabase', 'history sync'); });
                bindClick('btnResetMetadataTable', function() { resetTable('ResetMetadataSyncDatabase', 'metadata sync'); });
                bindClick('btnResetUserTable', function() { resetTable('ResetUserSyncDatabase', 'user sync'); });
                bindClick('btnResetPeopleTable', function() { resetTable('ResetPeopleSyncDatabase', 'people sync'); });
                bindClick('btnResetEntireDatabase', resetEntireDatabase);
            }

            loadConfig();
        });
    });
}

// serversync_storage.js
// How each library mapping keeps its files: Copy or Link, the link cache, Always Keep, and the
// Library Filter, plus the permanent copies of the Link libraries. The mappings themselves are made on
// the Servers tab. A save here changes only these fields of the matching stored mapping.

// serversync_storage
// Builds the Storage tab. It draws a card for each enabled library mapping of the picked server, saves
// their storage settings, and lists the permanent copies of its Link libraries.
//
// Inputs
// view [HTMLElement]: The page the dashboard passes to the controller.
export default function (view) {
    'use strict';

    // getTabs
    // Lists the dashboard tabs in order. This page always lists itself, so opening it while content sync is
    // off still marks a tab.
    //
    // Output
    // [object[]]: Each tab's href and name.
    function getTabs() {
        return [
            { href: 'configurationpage?name=serversync_sync', name: 'Sync' },
            { href: 'configurationpage?name=serversync_servers', name: 'Servers' },
            { href: 'configurationpage?name=serversync_storage', name: 'Storage' },
            { href: 'configurationpage?name=serversync_settings', name: 'Settings' }
        ];
    }

    var ServerSyncShared = null;
    var createPaginatedTable = null;

    // Helpers called by bare name, taken from the shared module once it loads. Every call happens after
    // the load, from a viewshow handler that waits for it.
    var escapeHtml, apiRequest, setVisible, bindClick, getEl;

    // Relative specifier so a server hosted under a base URL still resolves it.
    var _sharedPromise = import('./configurationpage?name=serversync_shared.js').then(function (shared) {
        ServerSyncShared = shared.createServerSyncShared(view);
        createPaginatedTable = shared.createPaginatedTable;
        ({ escapeHtml, apiRequest, setVisible, bindClick, getEl } = ServerSyncShared.pageHelpers());
    });

    var _initialized = false;
    var contentOn = true;
    var currentConfig = null;
    var selectedKey = '';
    var _tableSeq = 0;
    var _thumbSeq = 0;

    // One entry per library card on screen: where its mapping sits and how to read the card back.
    var cards = [];

    function sameId(a, b) {
        return !!a && !!b && String(a).replace(/-/g, '').toLowerCase() === String(b).replace(/-/g, '').toLowerCase();
    }

    function isPushServer(server) {
        return server.Mode === 'Push' || server.Mode === 1;
    }

    // The servers whose libraries are mapped here. A Push server sends files and maps no libraries.
    function storageServers(config) {
        return ((config && config.Servers) || []).filter(function (s) {
            return s && !isPushServer(s) && (s.LibraryMappings || []).length > 0;
        });
    }

    function selectedServer() {
        return storageServers(currentConfig).find(function (s) { return s.Key === selectedKey; }) || null;
    }

    function isLink(mapping) {
        return mapping.StorageMode === 'Link' || mapping.StorageMode === 1;
    }

    // The icon for a library, by the kind of library it is here, the way Jellyfin's own menus show it.
    var LIBRARY_ICONS = {
        movies: 'movie',
        tvshows: 'tv',
        music: 'library_music',
        musicvideos: 'music_video',
        books: 'menu_book',
        photos: 'photo_library',
        homevideos: 'video_camera_back',
        boxsets: 'collections',
        playlists: 'queue_music'
    };
    var _libraryTypes = null;

    // This server's libraries by id, read once per page load. A failed read leaves the default icon.
    function libraryTypes() {
        if (!_libraryTypes) {
            _libraryTypes = Promise.resolve(ApiClient.getVirtualFolders()).then(function (folders) {
                var types = {};
                (folders || []).forEach(function (f) {
                    if (f && f.ItemId) types[String(f.ItemId).replace(/-/g, '').toLowerCase()] = (f.CollectionType || '').toLowerCase();
                });
                return types;
            }).catch(function () { return {}; });
        }
        return _libraryTypes;
    }

    function setLibraryIcon(el, localLibraryId) {
        if (!el || !localLibraryId) return;
        libraryTypes().then(function (types) {
            var icon = LIBRARY_ICONS[types[String(localLibraryId).replace(/-/g, '').toLowerCase()]];
            if (icon) el.textContent = icon;
        });
    }

    function renderServerPicker() {
        var list = storageServers(currentConfig);
        var select = getEl('selStorageServer');
        if (!list.some(function (s) { return s.Key === selectedKey; })) {
            selectedKey = list.length ? list[0].Key : '';
        }
        if (select) {
            select.innerHTML = '';
            list.forEach(function (server) {
                var option = document.createElement('option');
                option.value = server.Key;
                option.textContent = ServerSyncShared.serverDisplayName(server) + (server.IsEnabled === false ? ' (disabled)' : '');
                select.appendChild(option);
            });
            select.value = selectedKey;
        }
        // One server needs no picker.
        setVisible('storageServerContainer', list.length > 1);
        setVisible('storageNoServers', list.length === 0);
        setVisible('storageLibrariesSection', list.length > 0);
    }

    function clearCards() {
        cards.forEach(function (card) { card.disconnect(); });
        cards = [];
        var container = getEl('storageLibrariesContainer');
        if (container) container.innerHTML = '';
    }

    function renderLibraries() {
        clearCards();
        var server = selectedServer();
        var count = 0;
        if (server) {
            (server.LibraryMappings || []).forEach(function (mapping, index) {
                if (!mapping.IsEnabled) return;
                cards.push(addLibraryCard(server.Key, mapping, index));
                count++;
            });
        }
        setVisible('storageNoLibraries', !!server && count === 0);
        setVisible('btnSaveStorage', count > 0);
    }

    function pickedItem(item) {
        return { ItemId: item.Id, Name: item.Name || '', Year: item.Year, Path: item.Path || '', Type: item.Type || null };
    }

    function storedPicks(list) {
        var picks = {};
        (list || []).forEach(function (pi) {
            if (pi.ItemId) picks[pi.ItemId] = { ItemId: pi.ItemId, Name: pi.Name || '', Year: pi.Year, Path: pi.Path || '', Type: pi.Type || null };
        });
        return picks;
    }

    function picksToList(picks) {
        return Object.keys(picks).map(function (id) {
            var p = picks[id];
            return { ItemId: p.ItemId, Name: p.Name || '', Year: p.Year || null, Path: p.Path || '', Type: p.Type || null };
        });
    }

    function addLibraryCard(serverKey, mapping, index) {
        // Only a Sync server writes history back, so only its libraries offer negotiation, and only
        // while history sync is on.
        var owner = storageServers(currentConfig).find(function (s) { return s.Key === serverKey; });
        var negotiable = !!(currentConfig && currentConfig.EnableHistorySync) && !!owner && (owner.Mode === 'Sync' || owner.Mode === 2);
        var container = getEl('storageLibrariesContainer');
        var div = document.createElement('div');
        div.className = 'serversync-library-card';
        var filterTableId = 'storageFilterTable_' + (++_tableSeq);
        var permanentTableId = 'storagePermanentTable_' + _tableSeq;
        var sourceName = mapping.SourceLibraryName || mapping.SourceLibraryId || 'Source library';
        var localName = mapping.LocalLibraryName || mapping.LocalLibraryId || 'Local library';
        div.innerHTML =
            '<div class="serversync-library-card-header">' +
                '<span class="serversync-library-card-icon material-icons" aria-hidden="true">video_library</span>' +
                '<div class="serversync-library-card-route" title="' + escapeHtml(sourceName + ' to ' + localName) + '">' +
                    '<div class="serversync-library-card-end">' +
                        '<span class="serversync-library-card-label">From</span>' +
                        '<span class="serversync-library-card-name">' + escapeHtml(sourceName) + '</span>' +
                    '</div>' +
                    '<span class="serversync-library-card-arrow material-icons" aria-hidden="true">arrow_forward</span>' +
                    '<div class="serversync-library-card-end">' +
                        '<span class="serversync-library-card-label">To</span>' +
                        '<span class="serversync-library-card-name">' + escapeHtml(localName) + '</span>' +
                    '</div>' +
                '</div>' +
                '<span class="base-badge gray serversync-storage-mode-badge"></span>' +
            '</div>' +
            '<div class="selectContainer serversync-storage-mode-block">' +
                '<label>Storage</label>' +
                '<select is="emby-select" class="serversync-storage-mode-select">' +
                    '<option value="Copy">Copy</option>' +
                    '<option value="Link">Link</option>' +
                '</select>' +
                '<div class="fieldDescription">Copy downloads each file and follows that server, so a file it removes is handled by Removed files here. Link writes a .strm that streams from any server that holds the file and allows linking, and goes only when none holds it.</div>' +
            '</div>' +
            '<div class="serversync-link-cache-section hidden">' +
                '<h3 class="base-subsection-title">Link Cache</h3>' +
                '<div class="checkboxContainer checkboxContainer-withDescription">' +
                    '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="serversync-cache-links" ' + (mapping.ExcludeFromLinkCache ? '' : 'checked') + ' /><span class="checkboxLabel">Cache This Library\'s Links</span></label>' +
                    '<div class="fieldDescription checkboxFieldDescription">Cache played links and upcoming episodes. The cache is set up under Settings &gt; Content.</div>' +
                '</div>' +
            '</div>' +
            '<div class="serversync-permanent-section hidden">' +
                '<h3 class="base-subsection-title">Always Keep</h3>' +
                '<div class="fieldDescription serversync-mb-md">Picked shows and films, every version included, are full copies here instead of links. Removing a pick turns them back into links on the next sync.</div>' +
                '<div class="serversync-filter-table-container" id="' + permanentTableId + '"></div>' +
            '</div>' +
            '<div class="serversync-filter-section">' +
                '<h3 class="base-subsection-title">Library Filter</h3>' +
                '<div class="selectContainer">' +
                    '<label>Filter Mode</label>' +
                    '<select is="emby-select" class="serversync-filter-mode-select">' +
                        '<option value="AllowAll">Allow All</option>' +
                        '<option value="Whitelist">Whitelist</option>' +
                        '<option value="Blacklist">Blacklist</option>' +
                    '</select>' +
                    '<div class="fieldDescription">Allow All syncs everything. Whitelist syncs only what is picked. Blacklist syncs everything but what is picked.</div>' +
                '</div>' +
                '<div class="serversync-filter-browser-container hidden">' +
                    '<div class="serversync-filter-pills serversync-filter-browse-toggle">' +
                        '<button type="button" class="serversync-filter-pill serversync-on" data-browse="items">Files</button>' +
                        '<button type="button" class="serversync-filter-pill" data-browse="collections">Collections</button>' +
                        '<button type="button" class="serversync-filter-pill" data-browse="playlists">Playlists</button>' +
                    '</div>' +
                    '<div class="serversync-filter-table-container" id="' + filterTableId + '"></div>' +
                '</div>' +
            '</div>' +
            (negotiable
                ? '<div class="serversync-history-section">' +
                    '<h3 class="base-subsection-title">History</h3>' +
                    '<div class="checkboxContainer checkboxContainer-withDescription">' +
                        '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="serversync-negotiate-history" ' + (mapping.NegotiateHistory ? 'checked' : '') + ' /><span class="checkboxLabel">Negotiate History</span></label>' +
                        '<div class="fieldDescription checkboxFieldDescription">Write merged watch history back to that server too.</div>' +
                    '</div>' +
                  '</div>'
                : '');
        container.appendChild(div);

        var libraryId = mapping.SourceLibraryId || '';
        var storageModeSelect = div.querySelector('.serversync-storage-mode-select');
        setLibraryIcon(div.querySelector('.serversync-library-card-icon'), mapping.LocalLibraryId);
        var filterModeSelect = div.querySelector('.serversync-filter-mode-select');
        var cacheLinks = div.querySelector('.serversync-cache-links');
        var negotiateBox = div.querySelector('.serversync-negotiate-history');
        var linkCacheSection = div.querySelector('.serversync-link-cache-section');
        var permanentSection = div.querySelector('.serversync-permanent-section');
        var filterBrowserContainer = div.querySelector('.serversync-filter-browser-container');
        var selectedFilterItems = storedPicks(mapping.FilteredItems);
        var selectedPermanentItems = storedPicks(mapping.PermanentItems);
        var filterTable = null;
        var permanentTable = null;
        var filterBrowseMode = 'items';

        storageModeSelect.value = isLink(mapping) ? 'Link' : 'Copy';
        filterModeSelect.value = mapping.FilterMode || 'AllowAll';

        // Renders one source item (thumb + info + selection checkmark) into a base table cell.
        function renderPickerItem(item, selected) {
            var sel = !!selected[item.Id];
            var thumbHtml;
            if (item.Id) {
                var thumbId = 'ss-storage-thumb-' + (++_thumbSeq);
                ServerSyncShared.scheduleProxyImage(thumbId, item.Id, false, 120, serverKey);
                thumbHtml = '<img id="' + thumbId + '" class="serversync-filter-item-thumb" />' +
                    '<div class="serversync-filter-item-thumb-placeholder hidden"><span class="material-icons">movie</span></div>';
            } else {
                thumbHtml = '<div class="serversync-filter-item-thumb-placeholder"><span class="material-icons">movie</span></div>';
            }
            var metaParts = [];
            if (item.Year) metaParts.push(item.Year);
            if (item.Type) metaParts.push(item.Type);
            var overviewHtml = '';
            if (item.Overview) {
                var snippet = item.Overview.substring(0, 120);
                if (item.Overview.length > 120) snippet += '…';
                overviewHtml = '<div class="serversync-filter-item-overview">' + escapeHtml(snippet) + '</div>';
            }
            return '<div class="serversync-filter-item' + (sel ? ' serversync-selected' : '') + '">' +
                thumbHtml +
                '<div class="serversync-filter-item-info">' +
                    '<div class="serversync-filter-item-name">' + escapeHtml(item.Name || '') + '</div>' +
                    '<div class="serversync-filter-item-meta">' + escapeHtml(metaParts.join(' • ')) + '</div>' +
                    overviewHtml +
                '</div>' +
                '<div class="serversync-filter-item-check"><span class="material-icons">' + (sel ? 'check_box' : 'check_box_outline_blank') + '</span></div>' +
            '</div>';
        }

        // Selection lives in the picks object, so it persists across searches and pagination.
        function onPickerItemClick(item, selected, pickerContainer) {
            if (selected[item.Id]) {
                delete selected[item.Id];
            } else {
                selected[item.Id] = pickedItem(item);
            }
            var sel = !!selected[item.Id];
            pickerContainer.querySelectorAll('.base-table-row').forEach(function (rowEl) {
                if (rowEl.getAttribute('data-id') !== String(item.Id)) return;
                var wrap = rowEl.querySelector('.serversync-filter-item');
                if (wrap) wrap.classList.toggle('serversync-selected', sel);
                var icon = rowEl.querySelector('.serversync-filter-item-check .material-icons');
                if (icon) icon.textContent = sel ? 'check_box' : 'check_box_outline_blank';
            });
        }

        function pickerTable(containerId, selected, section, browse) {
            var table = createPaginatedTable(view, ServerSyncShared, {
                containerId: containerId,
                endpoint: 'SourceLibraryItems',
                pagination: { pageSize: 50, loadMore: true },
                search: { enabled: true, placeholder: 'Search items' },
                selection: { enabled: false, idKey: 'Id' },
                emptyState: { message: 'No items found.' },
                filters: { buildParams: function () { var mode = browse(); return { libraryId: libraryId, serverKey: serverKey, collections: mode === 'collections', playlists: mode === 'playlists' }; } },
                columns: [{ key: 'Id', type: 'custom', render: function (item) { return renderPickerItem(item, selected); } }],
                actions: { onRowClick: function (item) { onPickerItemClick(item, selected, section); } }
            });
            // A non-empty filter value makes the table send the buildParams values.
            table.setFilterValue(libraryId);
            return table;
        }

        function updateFilterVisibility() {
            var show = filterModeSelect.value !== 'AllowAll';
            filterBrowserContainer.classList.toggle('hidden', !show);
            if (show && libraryId && createPaginatedTable) {
                if (!filterTable) filterTable = pickerTable(filterTableId, selectedFilterItems, filterBrowserContainer, function () { return filterBrowseMode; });
                filterTable.reload();
            }
        }

        // The cache and Always Keep only apply to links, so they show for a Link library.
        function updateLinkVisibility() {
            var linked = storageModeSelect.value === 'Link';
            var badge = div.querySelector('.serversync-storage-mode-badge');
            if (badge) {
                badge.textContent = linked ? 'Link' : 'Copy';
                badge.classList.toggle('blue', linked);
                badge.classList.toggle('gray', !linked);
            }
            linkCacheSection.classList.toggle('hidden', !linked);
            permanentSection.classList.toggle('hidden', !linked);
            if (linked && libraryId && createPaginatedTable) {
                if (!permanentTable) permanentTable = pickerTable(permanentTableId, selectedPermanentItems, permanentSection, function () { return 'items'; });
                permanentTable.reload();
            }
        }

        // Files / Collections / Playlists. Whitelisting a collection or playlist syncs its members, and
        // blacklisting one excludes them. The pill that is on marks the side in use.
        var browseButtons = div.querySelectorAll('.serversync-filter-browse-toggle > button');
        browseButtons.forEach(function (btn) {
            btn.addEventListener('click', function () {
                filterBrowseMode = btn.dataset.browse;
                browseButtons.forEach(function (b) { b.classList.toggle('serversync-on', b.dataset.browse === filterBrowseMode); });
                if (filterTable) filterTable.reload();
            });
        });

        storageModeSelect.addEventListener('change', updateLinkVisibility);
        filterModeSelect.addEventListener('change', updateFilterVisibility);
        updateLinkVisibility();
        updateFilterVisibility();

        return {
            index: index,
            sourceLibraryId: mapping.SourceLibraryId,
            localLibraryId: mapping.LocalLibraryId,
            read: function () {
                var permanent = picksToList(selectedPermanentItems);
                return {
                    StorageMode: storageModeSelect.value === 'Link' ? 'Link' : 'Copy',
                    ExcludeFromLinkCache: !cacheLinks.checked,
                    PermanentItems: permanent.length ? permanent : null,
                    FilterMode: filterModeSelect.value || 'AllowAll',
                    FilteredItems: picksToList(selectedFilterItems),
                    NegotiateHistory: negotiateBox ? negotiateBox.checked : !!mapping.NegotiateHistory
                };
            },
            disconnect: function () {
                if (filterTable && filterTable.disconnectObserver) filterTable.disconnectObserver();
                if (permanentTable && permanentTable.disconnectObserver) permanentTable.disconnectObserver();
            }
        };
    }

    // The stored mapping a card was drawn from: the same position when it still holds the same pair of
    // libraries, else the first mapping with that pair. A mapping removed meanwhile has none.
    function storedMappingFor(server, card) {
        var list = server.LibraryMappings || [];
        var matches = function (m) { return m && sameId(m.SourceLibraryId, card.sourceLibraryId) && sameId(m.LocalLibraryId, card.localLibraryId); };
        if (matches(list[card.index])) return list[card.index];
        return list.find(matches) || null;
    }

    function saveStorage() {
        var key = selectedKey;
        var edits = cards.map(function (card) { return { card: card, values: card.read() }; });
        if (!key || edits.length === 0) return;
        var missing = 0;
        ServerSyncShared.saveSection(function (config) {
            var server = ((config && config.Servers) || []).find(function (s) { return s.Key === key; });
            if (!server) throw new Error('The server was removed');
            edits.forEach(function (edit) {
                var target = storedMappingFor(server, edit.card);
                if (!target) {
                    missing++;
                    return;
                }
                Object.keys(edit.values).forEach(function (field) { target[field] = edit.values[field]; });
            });
        }, 'saveStorageStatus', 'Storage saved.', 'Could not save the storage.').then(function (saved) {
            if (!saved) return;
            currentConfig = saved;
            if (missing > 0) {
                ServerSyncShared.setStatus('saveStorageStatus', ServerSyncShared.plural(missing, 'library mapping', 'library mappings') + ' changed on the Servers tab meanwhile and ' + (missing === 1 ? 'was' : 'were') + ' not saved. The cards were reloaded.', true, { timeout: 10000 });
                renderServerPicker();
                renderLibraries();
            }
            reloadKeptCopies();
        });
    }

    var keptTable = null;
    var keptRows = {};
    var KEPT_BADGES = {
        Pick: { label: 'Always Keep', cls: 'blue', title: 'Copied because of an Always Keep pick.' },
        Hand: { label: 'Kept by Hand', cls: 'purple', title: 'Downloaded by hand with Download.' },
        Own: { label: 'Already Here', cls: 'green', title: 'On this server before it was matched to that server\'s file. It stays when that server removes its file.' },
        Unknown: { label: 'Earlier Copy', cls: 'gray', title: 'A copy from before Server Sync recorded why it is a copy.' }
    };

    function ensureKeptTable() {
        if (keptTable || !createPaginatedTable) return keptTable;
        keptTable = createPaginatedTable(view, ServerSyncShared, {
            containerId: 'keptCopiesTableContainer',
            endpoint: 'KeptCopies',
            pagination: { pageSize: 50 },
            search: { enabled: true, placeholder: 'Search permanent copies' },
            selection: {
                enabled: true,
                idKey: 'Id',
                onSelectionChange: function (ids) {
                    var btn = getEl('btnUnkeepCopies');
                    if (btn) btn.disabled = ids.length === 0;
                }
            },
            filters: { buildParams: function () { return { serverKey: selectedKey }; } },
            emptyState: { message: 'No permanent copies in this server\'s Link libraries.' },
            columns: [
                {
                    key: 'name',
                    label: 'Item',
                    type: 'custom',
                    className: 'base-table-cell-with-thumb',
                    image: function (item) { return ServerSyncShared.proxyImage(item.SourceItemId, false, item.ServerKey || selectedKey); },
                    render: function (item) {
                        keptRows[String(item.Id)] = item;
                        var path = item.LocalPath || item.SourcePath || '';
                        return '<div class="serversync-item-info">' +
                                '<div class="serversync-item-name" title="' + escapeHtml(path) + '">' + escapeHtml(ServerSyncShared.getFileName(path)) + '</div>' +
                                '<div class="serversync-item-path">' + escapeHtml(path) + '</div>' +
                            '</div>';
                    }
                },
                {
                    key: 'details',
                    label: 'Details',
                    type: 'custom',
                    className: 'serversync-details-cell',
                    render: function (item) {
                        var badge = KEPT_BADGES[item.Kept] || KEPT_BADGES.Unknown;
                        var size = item.SourceSizeFormatted || '';
                        return '<span class="base-badge ' + badge.cls + '" title="' + escapeHtml(badge.title) + '">' + escapeHtml(badge.label) + '</span>' +
                            (size ? ' <span class="base-badge purple">' + escapeHtml(size) + '</span>' : '');
                    }
                },
                { key: 'Status', label: 'Status', type: 'status' }
            ]
        });
        var bulk = keptTable.getBulkActionsContainer();
        if (bulk) {
            bulk.innerHTML = '<button is="emby-button" type="button" id="btnUnkeepCopies" class="raised base-button primary small serversync-kept-bulk-button" disabled>' +
                '<span class="material-icons" aria-hidden="true">link</span><span>Turn Back Into Links</span></button>';
            bulk.querySelector('#btnUnkeepCopies').addEventListener('click', unkeepSelected);
        }
        return keptTable;
    }

    function isNotFound(err) {
        return !!err && (err.status === 404 || (err.response && err.response.status === 404));
    }

    // Asks for one row first. A server without the kept copies endpoint answers 404, and the section
    // stays hidden. Kept copies only exist in Link libraries, so a server without one hides it too.
    function reloadKeptCopies() {
        var server = selectedServer();
        var hasLink = !!server && (server.LibraryMappings || []).some(function (m) { return m.IsEnabled && isLink(m); });
        if (!server || !hasLink || !contentOn) {
            setVisible('keptCopiesSection', false);
            return;
        }
        var key = server.Key;
        apiRequest('KeptCopies?serverKey=' + encodeURIComponent(key) + '&skip=0&take=1', 'GET').then(function () {
            if (key !== selectedKey) return;
            setVisible('keptCopiesSection', true);
            var table = ensureKeptTable();
            if (!table) return;
            keptRows = {};
            // Leaving the page disconnected the scroll observer, so showing the table again connects it.
            table.reconnectObserver();
            table.clearSelection();
            table.setFilterValue(key);
            table.reload();
        }, function (err) {
            if (key !== selectedKey) return;
            if (isNotFound(err)) {
                setVisible('keptCopiesSection', false);
                return;
            }
            setVisible('keptCopiesSection', true);
            var table = ensureKeptTable();
            if (table) {
                table.reconnectObserver();
                table.setFilterValue(key);
                table.reload();
            }
        });
    }

    function unkeepSelected() {
        if (!keptTable) return;
        var selected = keptTable.getSelectedIds();
        var ids = selected.map(function (id) { return parseInt(id, 10); }).filter(function (id) { return !isNaN(id); });
        if (ids.length === 0) return;
        var btn = getEl('btnUnkeepCopies');
        if (btn) btn.disabled = true;
        apiRequest('KeptCopies/Unkeep', 'POST', { Ids: ids }).then(function (result) {
            var queued = (result && result.Queued) || 0;
            var skipped = (result && result.Skipped) || [];
            var lines = [queued === 1 ? '1 copy will turn back into a link.' : queued + ' copies will turn back into links.'];
            if (skipped.length) {
                var picked = 0;
                var reasons = {};
                skipped.forEach(function (s) {
                    var row = keptRows[String(s.Id)];
                    if (row && row.Kept === 'Pick') picked++;
                    var reason = s.Reason || 'Skipped';
                    reasons[reason] = (reasons[reason] || 0) + 1;
                });
                lines.push(skipped.length + ' skipped. ' + Object.keys(reasons).map(function (reason) {
                    return reason + ' (' + reasons[reason] + ')';
                }).join(', ') + '.');
                if (picked > 0) {
                    lines.push('Copies covered by an Always Keep pick stay copies. Remove the pick under Always Keep on this tab instead.');
                }
            }
            ServerSyncShared.setStatus('keptCopiesStatus', lines.join(' '), false, { timeout: 15000 });
        }, function (err) {
            console.error('Unkeep failed:', err);
            ServerSyncShared.setStatus('keptCopiesStatus', 'Could not turn the copies back into links.', true, { timeout: 10000 });
        }).then(function () {
            keptTable.clearSelection();
            keptRows = {};
            keptTable.reload();
        });
    }

    function loadConfig() {
        ServerSyncShared.getConfig().then(function (config) {
            currentConfig = config;
            // The library filter applies to content, history, and metadata sync. How files are kept,
            // the cache, Always Keep, and permanent copies are content sync's alone.
            var on = !!(config.EnableContentSync || config.EnableHistorySync || config.EnableMetadataSync);
            contentOn = !!config.EnableContentSync;
            setVisible('storageDisabled', !on);
            setVisible('storageFiltersOnly', on && !contentOn);
            setVisible('storageContent', on);
            var libraries = getEl('storageLibrariesContainer');
            if (libraries) libraries.classList.toggle('serversync-filters-only', !contentOn);
            if (!on) {
                clearCards();
                return;
            }
            renderServerPicker();
            renderLibraries();
            reloadKeptCopies();
        }).catch(function () {
            ServerSyncShared.setStatus('storageStatus', 'Could not load the storage settings. Reload the page.', true, { timeout: 0 });
        });
    }

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('serversync', 2, getTabs);

        _sharedPromise.then(function () {
            if (!_initialized) {
                _initialized = true;

                ServerSyncShared.initCollapsibles();
                bindClick('btnSaveStorage', saveStorage);

                var select = getEl('selStorageServer');
                if (select) {
                    select.addEventListener('change', function () {
                        selectedKey = select.value;
                        renderLibraries();
                        reloadKeptCopies();
                    });
                }
            }

            loadConfig();
        });
    });

    view.addEventListener('viewhide', function () {
        cards.forEach(function (card) { card.disconnect(); });
        if (keptTable && keptTable.disconnectObserver) keptTable.disconnectObserver();
    });
}

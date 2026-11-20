// serversync_storage.js
// How each library mapping keeps its files: Copy or Link, the link cache, Always keep, and the
// library filter, plus the permanent copies of the Link libraries. The mappings themselves are made on
// the Servers tab. A save here changes only these fields of the matching stored mapping.

import * as serverSyncModule from './configurationpage?name=serversync_shared.js';

// serversync_storage
// Builds the Storage tab. It draws a card for each enabled library mapping of the picked server, saves
// their storage settings, and lists the permanent copies of its Link libraries.
//
// Inputs
// view [HTMLElement]: The page the dashboard passes to the controller.
export default function (view) {
    'use strict';

    var ServerSyncShared = null;
    var createPaginatedTable = null;

    // Helpers called by bare name, taken from the shared bag.
    var escapeHtml, apiRequest, setVisible, bindClick, getEl;

    // tr
    // Reads one of the plugin's strings in the viewer's language.
    //
    // Inputs
    // key [string]: The string's key.
    //
    // Output
    // [string]: The text.
    function tr(key) {
        return ServerSyncShared.t.tr(key);
    }

    // format
    // Reads one of the plugin's strings and fills its {0}, {1} placeholders.
    //
    // Inputs
    // key [string]: The string's key.
    // ...args [any]: The values for the placeholders.
    //
    // Output
    // [string]: The text.
    function format() {
        return ServerSyncShared.t.format.apply(null, arguments);
    }

    // Base runs bindPage once and loadConfig each time the page shows, and sets the tabs.
    var made = serverSyncModule.createServerSyncPage(view, { page: 'serversync_storage', alwaysStorage: true, bind: function () { bindPage(); }, load: function () { loadConfig(); }, onHide: function () { hidePage(); } });
    ServerSyncShared = made.shared;
    createPaginatedTable = serverSyncModule.createPaginatedTable;
    ({ escapeHtml, apiRequest, setVisible, bindClick, getEl } = ServerSyncShared.pageHelpers());

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
                option.textContent = server.IsEnabled === false ? format('OptionServerDisabled', ServerSyncShared.serverDisplayName(server)) : ServerSyncShared.serverDisplayName(server);
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
        var sourceName = mapping.SourceLibraryName || mapping.SourceLibraryId || tr('LabelSourceLibrary');
        var localName = mapping.LocalLibraryName || mapping.LocalLibraryId || tr('LabelLocalLibrary');
        div.innerHTML =
            '<div class="serversync-library-card-header">' +
                '<span class="serversync-library-card-icon material-icons" aria-hidden="true">video_library</span>' +
                '<div class="serversync-library-card-route" title="' + escapeHtml(format('LibraryRoute', sourceName, localName)) + '">' +
                    '<div class="serversync-library-card-end">' +
                        '<span class="serversync-library-card-label">' + escapeHtml(tr('LabelFrom')) + '</span>' +
                        '<span class="serversync-library-card-name">' + escapeHtml(sourceName) + '</span>' +
                    '</div>' +
                    '<span class="serversync-library-card-arrow material-icons" aria-hidden="true">arrow_forward</span>' +
                    '<div class="serversync-library-card-end">' +
                        '<span class="serversync-library-card-label">' + escapeHtml(tr('LabelTo')) + '</span>' +
                        '<span class="serversync-library-card-name">' + escapeHtml(localName) + '</span>' +
                    '</div>' +
                '</div>' +
                '<span class="base-badge gray serversync-storage-mode-badge"></span>' +
            '</div>' +
            '<div class="selectContainer serversync-storage-mode-block">' +
                '<label>' + escapeHtml(tr('TabStorage')) + '</label>' +
                '<select is="emby-select" class="serversync-storage-mode-select">' +
                    '<option value="Copy">' + escapeHtml(tr('Copy')) + '</option>' +
                    '<option value="Link">' + escapeHtml(tr('StorageModeLink')) + '</option>' +
                '</select>' +
                '<div class="fieldDescription">' + escapeHtml(tr('HelpStorageMode')) + '</div>' +
            '</div>' +
            '<div class="serversync-link-cache-section hidden">' +
                '<h3 class="base-subsection-title">' + escapeHtml(tr('HeaderLinkCache')) + '</h3>' +
                '<div class="checkboxContainer checkboxContainer-withDescription">' +
                    '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="serversync-cache-links" ' + (mapping.ExcludeFromLinkCache ? '' : 'checked') + ' /><span class="checkboxLabel">' + escapeHtml(tr('LabelCacheLibraryLinks')) + '</span></label>' +
                    '<div class="fieldDescription checkboxFieldDescription">' + escapeHtml(tr('HelpCacheLibraryLinks')) + '</div>' +
                '</div>' +
            '</div>' +
            '<div class="serversync-permanent-section hidden">' +
                '<h3 class="base-subsection-title">' + escapeHtml(tr('HeaderAlwaysKeep')) + '</h3>' +
                '<div class="fieldDescription serversync-mb-md">' + escapeHtml(tr('HelpAlwaysKeep')) + '</div>' +
                '<div class="serversync-filter-table-container" id="' + permanentTableId + '"></div>' +
            '</div>' +
            '<div class="serversync-filter-section">' +
                '<h3 class="base-subsection-title">' + escapeHtml(tr('HeaderLibraryFilter')) + '</h3>' +
                '<div class="selectContainer">' +
                    '<label>' + escapeHtml(tr('LabelFilterMode')) + '</label>' +
                    '<select is="emby-select" class="serversync-filter-mode-select">' +
                        '<option value="AllowAll">' + escapeHtml(tr('AllowEmbeddedSubtitlesAllowAllOption')) + '</option>' +
                        '<option value="Whitelist">' + escapeHtml(tr('Whitelist')) + '</option>' +
                        '<option value="Blacklist">' + escapeHtml(tr('Blacklist')) + '</option>' +
                    '</select>' +
                    '<div class="fieldDescription">' + escapeHtml(tr('HelpFilterMode')) + '</div>' +
                '</div>' +
                '<div class="serversync-filter-browser-container hidden">' +
                    '<div class="serversync-filter-pills serversync-filter-browse-toggle">' +
                        '<button type="button" class="serversync-filter-pill serversync-on" data-browse="items">' + escapeHtml(tr('HeaderFiles')) + '</button>' +
                        '<button type="button" class="serversync-filter-pill" data-browse="collections">' + escapeHtml(tr('Collections')) + '</button>' +
                        '<button type="button" class="serversync-filter-pill" data-browse="playlists">' + escapeHtml(tr('Playlists')) + '</button>' +
                    '</div>' +
                    '<div class="serversync-filter-table-container" id="' + filterTableId + '"></div>' +
                '</div>' +
            '</div>' +
            (negotiable
                ? '<div class="serversync-history-section">' +
                    '<h3 class="base-subsection-title">' + escapeHtml(tr('SyncTypeHistory')) + '</h3>' +
                    '<div class="checkboxContainer checkboxContainer-withDescription">' +
                        '<label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" class="serversync-negotiate-history" ' + (mapping.NegotiateHistory ? 'checked' : '') + ' /><span class="checkboxLabel">' + escapeHtml(tr('LabelNegotiateHistory')) + '</span></label>' +
                        '<div class="fieldDescription checkboxFieldDescription">' + escapeHtml(tr('HelpNegotiateHistory')) + '</div>' +
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
                rowEl.classList.toggle('selected', sel);
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
                search: { enabled: true },
                selection: { enabled: false, idKey: 'Id' },
                emptyState: { message: tr('EmptyItemsFound') },
                filters: { buildParams: function () { var mode = browse(); return { libraryId: libraryId, serverKey: serverKey, collections: mode === 'collections', playlists: mode === 'playlists' }; } },
                columns: [{ key: 'Id', type: 'custom', render: function (item) { return renderPickerItem(item, selected); } }],
                rowClass: function (item) { return selected[item.Id] ? 'selected' : ''; },
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

        // The cache and Always keep only apply to links, so they show for a Link library.
        function updateLinkVisibility() {
            var linked = storageModeSelect.value === 'Link';
            var badge = div.querySelector('.serversync-storage-mode-badge');
            if (badge) {
                badge.textContent = linked ? tr('StorageModeLink') : tr('Copy');
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
        }, 'saveStorageStatus', tr('ToastStorageSaved'), tr('ErrorSaveStorage')).then(function (saved) {
            if (!saved) return;
            currentConfig = saved;
            if (missing > 0) {
                ServerSyncShared.setStatus('saveStorageStatus', ServerSyncShared.plural(missing, 'ErrorMappingsChangedOne', 'ErrorMappingsChangedMany'), true, { timeout: 10000 });
                renderServerPicker();
                renderLibraries();
            }
            reloadKeptCopies();
        });
    }

    var keptTable = null;
    var keptRows = {};
    // The badge of each reason a copy is kept, with the keys of its label and its title.
    var KEPT_BADGES = {
        Pick: { label: 'HeaderAlwaysKeep', cls: 'blue', title: 'HelpKeptPick' },
        Hand: { label: 'BadgeKeptByHand', cls: 'purple', title: 'HelpKeptHand' },
        Own: { label: 'BadgeAlreadyHere', cls: 'green', title: 'HelpKeptOwn' },
        Unknown: { label: 'BadgeEarlierCopy', cls: 'gray', title: 'HelpKeptUnknown' }
    };

    function ensureKeptTable() {
        if (keptTable || !createPaginatedTable) return keptTable;
        keptTable = createPaginatedTable(view, ServerSyncShared, {
            containerId: 'keptCopiesTableContainer',
            endpoint: 'KeptCopies',
            pagination: { pageSize: 50 },
            search: { enabled: true, placeholder: tr('PlaceholderSearchPermanentCopies') },
            selection: {
                enabled: true,
                idKey: 'Id',
                onSelectionChange: function (ids) {
                    var btn = getEl('btnUnkeepCopies');
                    if (btn) btn.disabled = ids.length === 0;
                }
            },
            filters: { buildParams: function () { return { serverKey: selectedKey }; } },
            emptyState: { message: tr('EmptyPermanentCopies') },
            columns: [
                {
                    key: 'name',
                    label: tr('ColumnItem'),
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
                    label: tr('ColumnDetails'),
                    type: 'custom',
                    className: 'serversync-details-cell',
                    render: function (item) {
                        var badge = KEPT_BADGES[item.Kept] || KEPT_BADGES.Unknown;
                        var size = item.SourceSizeFormatted || '';
                        return '<span class="base-badge ' + badge.cls + '" title="' + escapeHtml(tr(badge.title)) + '">' + escapeHtml(tr(badge.label)) + '</span>' +
                            (size ? ' <span class="base-badge purple">' + escapeHtml(size) + '</span>' : '');
                    }
                },
                { key: 'Status', label: tr('HeaderStatus'), type: 'status' }
            ]
        });
        var bulk = keptTable.getBulkActionsContainer();
        if (bulk) {
            bulk.innerHTML = '<button is="emby-button" type="button" id="btnUnkeepCopies" class="raised base-button primary small serversync-kept-bulk-button" disabled>' +
                '<span class="material-icons" aria-hidden="true">link</span><span>' + escapeHtml(tr('ButtonTurnBackIntoLinks')) + '</span></button>';
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
            var lines = [ServerSyncShared.plural(queued, 'ToastCopiesUnkeptOne', 'ToastCopiesUnkeptMany')];
            if (skipped.length) {
                var picked = 0;
                var reasons = {};
                skipped.forEach(function (s) {
                    var row = keptRows[String(s.Id)];
                    if (row && row.Kept === 'Pick') picked++;
                    var reason = s.Reason || tr('StatusSkipped');
                    reasons[reason] = (reasons[reason] || 0) + 1;
                });
                lines.push(format('ToastUnkeepSkipped', skipped.length, Object.keys(reasons).map(function (reason) {
                    return format('ReasonCount', reason, reasons[reason]);
                }).join(', ')));
                if (picked > 0) {
                    lines.push(tr('HelpUnkeepPicked'));
                }
            }
            ServerSyncShared.setStatus('keptCopiesStatus', lines.join(' '), false, { timeout: 15000 });
        }, function (err) {
            console.error('Unkeep failed:', err);
            ServerSyncShared.setStatus('keptCopiesStatus', tr('ErrorUnkeepCopies'), true, { timeout: 10000 });
        }).then(function () {
            keptTable.clearSelection();
            keptRows = {};
            keptTable.reload();
        });
    }

    // The Pie Chart of synced files, or null before the first draw.
    var filesChart = null;

    // countSynced
    // Counts the picked server's synced Content rows that match a query.
    //
    // Inputs
    // query [string]: The Items filters, such as storage=Link.
    //
    // Output
    // [Promise]: Resolves to the count.
    function countSynced(query) {
        return apiRequest('Items?status=Synced&skip=0&take=1&serverKey=' + encodeURIComponent(selectedKey) + '&' + query, 'GET').then(function (page) {
            return (page && page.TotalCount) || 0;
        });
    }

    // loadFilesChart
    // Draws how many of the picked server's synced files are copies, links, and cached links. A failed count
    // hides the chart, and an answer for a server that is no longer picked is dropped.
    function loadFilesChart() {
        var key = selectedKey;
        if (!contentOn || !key) {
            setVisible('storageFilesSection', false);
            return;
        }
        Promise.all([countSynced('storage=Copy'), countSynced('storage=Link'), countSynced('storage=Link&cached=true')]).then(function (counts) {
            if (key !== selectedKey) return;
            var cached = Math.min(counts[2], counts[1]);
            var slices = [
                { label: tr('ChartCopies'), value: counts[0] },
                { label: tr('ChartLinks'), value: counts[1] - cached },
                { label: tr('ChartCachedLinks'), value: cached }
            ];
            var holder = getEl('storageFilesChart');
            if (!holder) return;
            if (filesChart) filesChart.destroy();
            holder.innerHTML = '';
            filesChart = serverSyncModule.createPieChart({
                slices: slices,
                label: tr('ChartFilesLabel'),
                donut: true,
                centerText: String(counts[0] + counts[1]),
                centerLabel: tr('HeaderFiles'),
                valueName: tr('HeaderFiles'),
                emptyText: tr('ChartFilesEmpty')
            });
            holder.appendChild(filesChart.element);
            setVisible('storageFilesSection', true);
        }).catch(function () {
            if (key === selectedKey) setVisible('storageFilesSection', false);
        });
    }

    // The Usage Meter of the link cache, or null before the first draw.
    var usageMeter = null;

    // loadUsage
    // Draws how much of the link cache's room is used. The section shows only while the cache is on.
    function loadUsage() {
        if (!contentOn) {
            setVisible('storageUsageSection', false);
            return;
        }
        apiRequest('LinkCache', 'GET').then(function (cache) {
            var holder = getEl('storageUsageMeter');
            var inUse = !!cache && !!cache.Enabled && cache.LimitBytes > 0;
            setVisible('storageUsageSection', inUse && !!holder);
            if (!inUse || !holder) return;
            var value = { used: cache.UsedBytes || 0, total: cache.LimitBytes };
            if (usageMeter) {
                usageMeter.setValue(value);
                return;
            }
            usageMeter = serverSyncModule.createUsageMeter({ label: tr('HeaderLinkCache'), bytes: true, used: value.used, total: value.total });
            holder.appendChild(usageMeter.element);
        }).catch(function () {
            setVisible('storageUsageSection', false);
        });
    }

    function loadConfig() {
        ServerSyncShared.getConfig().then(function (config) {
            currentConfig = config;
            // The library filter applies to content, history, and metadata sync. How files are kept,
            // the cache, Always keep, and permanent copies are content sync's alone.
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
            loadUsage();
            renderServerPicker();
            loadFilesChart();
            renderLibraries();
            reloadKeptCopies();
        }).catch(function () {
            ServerSyncShared.setStatus('storageStatus', tr('ErrorLoadStorage'), true, { timeout: 0 });
        });
    }

    // bindPage
    // Wires the page's buttons and controls. Base calls it once, the first time the page shows.
    function bindPage() {
        ServerSyncShared.initCollapsibles();
        bindClick('btnSaveStorage', saveStorage);

        var select = getEl('selStorageServer');
        if (select) {
            select.addEventListener('change', function () {
                selectedKey = select.value;
                loadFilesChart();
                renderLibraries();
                reloadKeptCopies();
            });
        }
    }

    // hidePage
    // Stops the observers of the cards and tables when the page hides.
    function hidePage() {
        cards.forEach(function (card) { card.disconnect(); });
        if (keptTable && keptTable.disconnectObserver) keptTable.disconnectObserver();
    }
}

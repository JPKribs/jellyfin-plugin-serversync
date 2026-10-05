// ============================================
// SERVER SYNC PLUGIN - UNIFIED SYNC PAGE CONTROLLER
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
    // EVENT LISTENERS (registered FIRST to avoid missing viewshow)
    // ============================================

    var _pageReady = false;

    function onViewShow() {
        LibraryMenu.setTabs('serversync', 0, getTabs);
        if (_pageReady) {
            SyncViewManager.init();
        }
    }

    // Active task-progress pollers, stopped on viewhide. pollTaskProgress returns a { cancel } handle.
    var _activePollIntervals = [];

    view.addEventListener('viewshow', onViewShow);
    view.addEventListener('viewhide', function () {
        // Stop any running pollTaskProgress handles
        _activePollIntervals.forEach(function(handle) {
            if (handle && typeof handle.cancel === 'function') handle.cancel();
        });
        _activePollIntervals.length = 0;

        // Disconnect IntersectionObservers on all PaginatedTable instances
        var modules = [SyncTableModule, HistorySyncTableModule, MetadataSyncTableModule, UserSyncTableModule, PeopleSyncTableModule];
        QueueModule.stopAutoRefresh();
        modules.forEach(function(mod) {
            if (mod.table && mod.table.disconnectObserver) {
                mod.table.disconnectObserver();
            }
        });
    });

    // ============================================
    // SHARED MODULE IMPORT (deferred)
    // ============================================

    var ServerSyncShared = null;
    var createPaginatedTable = null;

    // Relative specifier so a server hosted under a base URL still resolves it.
    var _sharedPromise = import('./configurationpage?name=serversync_shared.js').then(function(shared) {
        ServerSyncShared = shared.createServerSyncShared(view);
        createPaginatedTable = shared.createPaginatedTable;
    });

    // ============================================
    // SYNC VIEW MANAGER
    // ============================================

    var SyncViewManager = {
        currentView: null,     // Currently active view name ('content', 'history', etc.)
        initialized: {},       // Tracks which views have been lazy-initialized
        _listenerBound: false, // Prevents duplicate dropdown change listeners

        init: function() {
            var self = this;
            if (!this._listenerBound) {
                this._listenerBound = true;
                var dropdown = view.querySelector('#syncTypeDropdown');
                if (dropdown) {
                    dropdown.addEventListener('change', function() {
                        self.switchView(dropdown.value);
                    });
                }
            }

            // The config is fetched again on every show and handed to the dropdown, the queue, and every
            // table module, so a change saved on the Settings page shows here without a reload.
            _sharedPromise.then(function() {
                // Coming back to the page re-selects the view it showed, which does not switch views, so the
                // queue's refresh is restarted here rather than only on a switch.
                if (self.currentView === 'queue') {
                    QueueModule._signature = null;
                    QueueModule.startAutoRefresh();
                    QueueModule.load(true);
                }

                // Leaving the page disconnects every table's infinite scroll observer, and re-selecting the
                // same view does not switch views either, so the open table's observer is reconnected here.
                // Without this, scrolling stops loading more rows after a visit to another page.
                var currentModule = self._getTableModule(self.currentView);
                if (currentModule && currentModule.table && currentModule.table.reconnectObserver) {
                    currentModule.table.reconnectObserver();
                }

                ServerSyncShared.getConfig().then(function(config) {
                    QueueModule._config = config;
                    self._applyConfig(config);
                    self._applyEnabledTypes(config);
                }).catch(function() {
                    // Without a config the enabled types are unknown, so the dropdown keeps the options it
                    // already shows and the current view, or the first one still offered, is selected.
                    self._selectFirstEnabled();
                });
            });
        },

        // Hands a freshly fetched config to every table module that keeps one. Each module first loaded
        // its config when its view opened, and without this it would keep that copy until a page reload,
        // so a server, mapping, or category changed on another page would never reach its rows or modal.
        _applyConfig: function(config) {
            [SyncTableModule, HistorySyncTableModule, MetadataSyncTableModule, UserSyncTableModule, PeopleSyncTableModule].forEach(function(mod) {
                mod.currentConfig = config;
            });
            // The pending filters follow the approval modes, which are set on the Settings page.
            if (SyncTableModule.table) {
                SyncTableModule.updatePendingFilterVisibility(config);
            }
        },

        _applyEnabledTypes: function(config) {
            var dropdown = view.querySelector('#syncTypeDropdown');
            if (!dropdown) return;

            // The Queue has no config key and is always offered, so with every module off it is the
            // view that opens: a server that only receives hints still has its queue to watch.
            var options = dropdown.querySelectorAll('option');
            for (var i = 0; i < options.length; i++) {
                var opt = options[i];
                var configKey = opt.getAttribute('data-config-key');
                var isEnabled = !configKey || (config && config[configKey]);
                opt.style.display = isEnabled ? '' : 'none';
                opt.disabled = !isEnabled;
            }

            this._selectFirstEnabled();
        },

        // Preserves the current view if it's still enabled.
        _selectFirstEnabled: function() {
            var dropdown = view.querySelector('#syncTypeDropdown');
            if (!dropdown) return;

            if (this.currentView) {
                var currentOpt = dropdown.querySelector('option[value="' + this.currentView + '"]');
                if (currentOpt && !currentOpt.disabled) {
                    // Current view is still valid, just ensure dropdown matches
                    dropdown.value = this.currentView;
                    // Re-trigger switchView in case it wasn't initialized yet
                    // (reset currentView to force re-entry)
                    var cv = this.currentView;
                    if (!this.initialized[cv]) {
                        this.currentView = null;
                        this.switchView(cv);
                    }
                    return;
                }
            }

            // Current view is disabled or not set, find first enabled option
            var firstEnabled = null;
            for (var i = 0; i < dropdown.options.length; i++) {
                if (!dropdown.options[i].disabled) {
                    firstEnabled = dropdown.options[i].value;
                    break;
                }
            }

            if (firstEnabled) {
                dropdown.value = firstEnabled;
                this.currentView = null; // Force switchView to re-enter
                this.switchView(firstEnabled);
            }
        },

        _getTableModule: function(name) {
            switch (name) {
                case 'content': return SyncTableModule;
                case 'history': return HistorySyncTableModule;
                case 'metadata': return MetadataSyncTableModule;
                case 'users': return UserSyncTableModule;
                case 'people': return PeopleSyncTableModule;
                default: return null;
            }
        },

        switchView: function(viewName) {
            if (this.currentView === viewName) return;

            // Disconnect IntersectionObserver on the outgoing tab's table
            // (prevents stale triggers while the container is hidden)
            var outgoingModule = this._getTableModule(this.currentView);
            if (outgoingModule && outgoingModule.table && outgoingModule.table.disconnectObserver) {
                outgoingModule.table.disconnectObserver();
            }

            var views = view.querySelectorAll('.syncView');
            for (var i = 0; i < views.length; i++) {
                views[i].classList.add('hidden');
            }

            var targetView = view.querySelector('#syncView-' + viewName);
            if (targetView) {
                targetView.classList.remove('hidden');
            }

            var dropdown = view.querySelector('#syncTypeDropdown');
            var displayName = viewName.charAt(0).toUpperCase() + viewName.slice(1);
            if (dropdown) {
                var selected = dropdown.options[dropdown.selectedIndex];
                if (selected) {
                    displayName = selected.text;
                }
            }
            var titleEl = view.querySelector('#syncPageTitle');
            if (titleEl) {
                titleEl.textContent = viewName === 'queue' ? 'Change Queue' : displayName + ' Sync';
            }

            // The queue view refreshes itself while it is showing.
            if (viewName === 'queue') {
                QueueModule.startAutoRefresh();
            } else {
                QueueModule.stopAutoRefresh();
            }

            var descEl = view.querySelector('#syncTypeDescription');
            if (descEl) {
                var descriptions = {
                    content: 'Content sync downloads media files from the source server and mirrors them here. It matches libraries by your mappings, queues missing or updated files, includes companion files like subtitles, and can delete items removed on the source.',
                    history: 'History sync copies watch state to your mapped users. It syncs played status, play counts, resume positions, last played dates, and favorites, keeping the highest play count and the most recent activity from either server.',
                    metadata: 'Metadata sync copies item metadata onto matching items, matched by file path. It updates titles, overviews, ratings, genres, tags, studios, people, and images, and you enable each category on the Settings tab.',
                    people: 'People sync copies person metadata onto matching people, matched by name. It updates biographies, provider IDs, and profile images.',
                    users: 'User sync copies user settings to your mapped users. It covers permissions, playback and display preferences, and profile images, and translates library permissions through your library mappings.',
                    queue: 'Changes travelling between servers as they happen. Outbound rows are owed to a peer in Push or Sync mode until it reports them done. Inbound rows came from a peer and wait to be applied here. A paused peer shows why, and the scheduled tasks still catch anything the queue misses.'
                };
                descEl.textContent = descriptions[viewName] || '';
            }

            this.currentView = viewName;

            // Reconnect IntersectionObserver on the incoming tab's table
            // (restores infinite scroll after the container is visible again)
            var incomingModule = this._getTableModule(viewName);
            if (incomingModule && incomingModule.table && incomingModule.table.reconnectObserver) {
                incomingModule.table.reconnectObserver();
            }

            // Lazy-initialize the controller for this view (wait for shared module)
            if (!this.initialized[viewName]) {
                this.initialized[viewName] = true;
                _sharedPromise.then(function() {
                    switch (viewName) {
                        case 'content':
                            ContentPageController.init();
                            break;
                        case 'history':
                            openSyncView(HistorySyncTableModule);
                            break;
                        case 'metadata':
                            openSyncView(MetadataSyncTableModule);
                            break;
                        case 'users':
                            openSyncView(UserSyncTableModule);
                            break;
                        case 'people':
                            openSyncView(PeopleSyncTableModule);
                            break;
                        case 'queue':
                            QueueModule.init();
                            break;
                    }
                });
            }
        }
    };

    // ============================================
    // SYNC TABLE MODULE FACTORY
    // ============================================
    // Builds the module behind one Sync view. Every view shares the table setup, the status counts,
    // the Refresh and Sync buttons, retrying errors, the bulk and modal actions, and the page load.
    // A spec supplies what differs: element ids, endpoints, columns, health figures, and the detail
    // modal. A method the spec defines replaces the shared one of the same name.

    var BASE_STATUSES = ['Synced', 'Queued', 'Errored', 'Ignored'];

    var DEFAULT_BULK_BUTTONS = [
        { id: 'Ignore', title: 'Ignore', icon: 'block', method: 'bulkIgnore' },
        { id: 'Queue', title: 'Queue', icon: 'playlist_add', className: 'jpk-button-submit', method: 'bulkQueue' }
    ];

    // An element id under a view's prefix. The Content view has no prefix, so its ids start lowercase.
    function prefixedId(prefix, name) {
        return prefix ? prefix + name : name.charAt(0).toLowerCase() + name.slice(1);
    }

    function statusFilters(statuses) {
        return {
            options: statuses.map(function(s) { return { value: s, label: s }; }),
            buildParams: function(filterValue) { return { status: filterValue }; }
        };
    }

    function setHealthLastSync(elementId, time) {
        var el = view.querySelector('#' + elementId);
        if (!el) return;
        if (time) {
            el.textContent = ServerSyncShared.formatRelativeTime(new Date(time));
            el.className = 'healthValue success';
        } else {
            el.textContent = 'Never';
            el.className = 'healthValue';
        }
    }

    function setHealthCount(elementId, count) {
        var el = view.querySelector('#' + elementId);
        if (!el) return;
        el.textContent = count;
        el.className = count > 0 ? 'healthValue success' : 'healthValue warning';
    }

    // The thumbnail, name, second line, and error preview every Item column shows.
    function renderRowInfo(thumb, name, line, item, status, nameTitle) {
        var errorPreview = '';
        if (status === 'Errored' && item.ErrorMessage) {
            errorPreview = '<div class="syncItemError" title="' + ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
        }
        var title = nameTitle === undefined ? name : nameTitle;
        return thumb +
            '<div class="syncItemInfo">' +
            '<div class="syncItemName"' + (title ? ' title="' + ServerSyncShared.escapeHtml(title) + '"' : '') + '>' + ServerSyncShared.escapeHtml(name) + '</div>' +
            (line === null ? '' : '<div class="syncItemPath">' + ServerSyncShared.escapeHtml(line) + '</div>') +
            errorPreview +
            '</div>';
    }

    // The orange badges for the categories that differ, or a gray one when none do.
    function renderChangeBadges(changes) {
        if (changes.length === 0) {
            return '<span class="jpk-badge gray">No changes</span>';
        }
        return changes.map(function(c) { return '<span class="jpk-badge orange">' + c + '</span>'; }).join(' ');
    }

    function createSyncTableModule(spec) {
        var name = spec.name;
        var ids = {
            refresh: 'btnRefresh' + name + 'Items',
            sync: 'btnTrigger' + name + 'Sync',
            retry: 'btnRetry' + name + 'Errors',
            bulk: function(id) { return 'btn' + name + 'Bulk' + id; },
            modal: function(id) { return spec.modalButtonPrefix + id; }
        };
        var bulkButtons = spec.bulkButtons || DEFAULT_BULK_BUTTONS;

        var module = {
            table: null,            // PaginatedTable instance
            currentModalItem: null, // Row or detail shown in the modal
            currentConfig: null,    // Plugin configuration, refreshed whenever the page is shown
            _initialized: false,
            _detailSeq: 0,          // Counts detail requests so only the newest reply fills the modal

            init: function(config) {
                if (this._initialized) return;
                this._initialized = true;

                var self = this;
                if (config !== undefined) {
                    self.currentConfig = config;
                }

                var options = {
                    containerId: spec.containerId,
                    endpoint: spec.listEndpoint,
                    columns: spec.columns(self),
                    selection: {
                        enabled: true,
                        idKey: spec.idKey || 'Id',
                        onSelectionChange: function(selectedIds) { self.updateBulkActionsVisibility(selectedIds.length); }
                    },
                    pagination: { pageSize: 50 },
                    filters: spec.filters || statusFilters(spec.filterStatuses || BASE_STATUSES),
                    actions: {
                        onRowClick: function(item) { self.showDetail(item); },
                        onReload: function() {
                            self.loadStatus();
                            self.loadHealthStats();
                        }
                    },
                    emptyState: { message: spec.emptyMessage }
                };
                if (spec.searchPlaceholder) {
                    options.search = { placeholder: spec.searchPlaceholder };
                }
                if (spec.tableOptions) {
                    Object.assign(options, spec.tableOptions(self));
                }

                this.table = createPaginatedTable(view, ServerSyncShared, options);
                this._bindModuleEvents();
                this._injectBulkActions();
            },

            _bindModuleEvents: function() {
                var self = this;
                var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler); };

                bind(ids.refresh, function() { self.runTask('refresh'); });
                bind(ids.sync, function() { self.runTask('sync'); });
                bind(ids.retry, function() { self.retryErrors(); });

                bind(ids.modal('Ignore'), function() { self.modalIgnore(); });
                bind(ids.modal('Queue'), function() { self.modalQueue(); });
                bind(ids.modal('Close'), function() { self.closeModal(); });
                (spec.extraModalButtons || []).forEach(function(b) {
                    bind(ids.modal(b.id), function() { self[b.method](); });
                });
            },

            _injectBulkActions: function() {
                var self = this;
                var container = this.table.getBulkActionsContainer();
                if (!container) return;

                container.innerHTML = bulkButtons.map(function(b) {
                    return '<button is="emby-button" type="button" id="' + ids.bulk(b.id) + '" class="raised ' + (b.className ? b.className + ' ' : '') +
                        'pt-bulk-icon-btn" title="' + b.title + '" disabled><span class="material-icons">' + b.icon + '</span></button>';
                }).join('');
                bulkButtons.forEach(function(b) {
                    container.querySelector('#' + ids.bulk(b.id)).addEventListener('click', function() { self[b.method](); });
                });
            },

            // Loads the status counts, the rows, and the health figures together.
            loadAll: function() {
                return Promise.all([this.loadStatus(), this.loadItems(), this.loadHealthStats()]);
            },

            loadStatus: function() {
                var self = this;
                return ServerSyncShared.apiRequest(spec.statusEndpoint, 'GET').then(function(status) {
                    self._showStatusCounts(status);
                }).catch(function() {
                    // The status endpoint is not available yet.
                });
            },

            _showStatusCounts: function(status) {
                BASE_STATUSES.forEach(function(s) {
                    var count = status[s] || 0;
                    var countEl = view.querySelector('#' + prefixedId(spec.prefix, s + 'Count'));
                    if (countEl) countEl.textContent = count;
                    var groupEl = view.querySelector('#' + prefixedId(spec.prefix, 'StatusGroup' + s));
                    if (groupEl) groupEl.setAttribute('title', s + ': ' + count);
                });

                var retryBtn = view.querySelector('#' + ids.retry);
                if (retryBtn) retryBtn.classList.toggle('hidden', !((status.Errored || 0) > 0));
            },

            loadHealthStats: function() {
                if (!spec.loadHealth) return Promise.resolve();
                return spec.loadHealth.call(this).catch(function() {
                    // Health figures are informational.
                });
            },

            loadItems: function() {
                return this.table.reload();
            },

            // Starts the module's Refresh or Sync task and follows its progress on the button.
            runTask: function(kind) {
                var self = this;
                var task = spec.tasks[kind];
                var label = kind === 'refresh' ? 'Refresh' : 'Sync';
                var btn = view.querySelector('#' + ids[kind]);
                if (!btn || !task) return;

                btn.disabled = true;
                btn.querySelector('span').textContent = 'Starting...';

                ServerSyncShared.apiRequest(task.endpoint, 'POST').then(function() {
                    _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, task.key, label, function() {
                        self.loadAll();
                    }));
                }).catch(function() {
                    ServerSyncShared.showAlert('Failed to start ' + (spec.taskNoun ? spec.taskNoun + ' ' : '') + label.toLowerCase() + ' task');
                    btn.querySelector('span').textContent = label;
                    btn.disabled = false;
                });
            },

            retryErrors: function() {
                var self = this;
                return retryAllErrored(spec.retryEndpoint, spec.retryNoun || spec.pluralNoun, function() {
                    return Promise.all([self.loadStatus(), self.loadItems()]);
                });
            },

            updateBulkActionsVisibility: function(count) {
                bulkButtons.forEach(function(b) {
                    var btn = view.querySelector('#' + ids.bulk(b.id));
                    if (btn) btn.disabled = count === 0;
                });
            },

            bulkIgnore: function() {
                this._bulk('Ignore', 'ignored');
            },

            bulkQueue: function() {
                this._bulk('Queue', 'queued');
            },

            _bulk: function(action, done) {
                var keys = this.table.getSelectedIds();
                if (keys.length === 0) return;

                var body = spec.selectionBody ? spec.selectionBody(keys) : { Ids: keys };
                this.postSelection(spec.itemsEndpoint + '/' + action, body,
                    keys.length + ' ' + spec.pluralNoun + ' ' + done,
                    'Failed to ' + action.toLowerCase() + ' ' + spec.failNoun);
            },

            // Posts an action on the selected rows and reloads. The done message may be a function of
            // the reply, and a null message shows nothing.
            postSelection: function(endpoint, body, doneMessage, failMessage) {
                var self = this;
                return ServerSyncShared.apiRequest(endpoint, 'POST', body).then(function(result) {
                    self.table.clearSelection();
                    self.loadStatus();
                    self.loadItems();
                    var message = typeof doneMessage === 'function' ? doneMessage(result) : doneMessage;
                    if (message) ServerSyncShared.showAlert(message);
                }).catch(function(err) {
                    console.error(failMessage + ':', err);
                    ServerSyncShared.showAlert(failMessage);
                });
            },

            closeModal: function() {
                view.querySelector('#' + spec.modalId).classList.add('hidden');
                this.currentModalItem = null;
                this._detailSeq++;
                this.table.refresh();
                this.loadStatus();
                this.loadHealthStats();
            },

            modalIgnore: function() {
                this.setModalStatus('Ignored');
            },

            modalQueue: function() {
                this.setModalStatus('Queued');
            },

            setModalStatus: function(status) {
                var self = this;
                var item = this.currentModalItem;
                if (!item) return;

                var request = spec.statusRequest(item, status);
                ServerSyncShared.apiRequest(request.endpoint, 'POST', request.body).then(function() {
                    self.closeModal();
                    ServerSyncShared.showAlert(spec.singularNoun + ' status updated to ' + status);
                }).catch(function(err) {
                    console.error('Failed to update status:', err);
                    ServerSyncShared.showAlert('Failed to update ' + spec.singularNoun.toLowerCase() + ' status');
                });
            }
        };

        Object.keys(spec.methods || {}).forEach(function(key) {
            module[key] = spec.methods[key];
        });
        return module;
    }

    // Opens a view: learns the local server's name and the config, then loads the module.
    function openSyncView(module) {
        return ServerSyncShared.fetchLocalServerName().then(function() {
            return ServerSyncShared.getConfig();
        }).then(function(config) {
            module.init(config);
        }, function() {
            module.init(null);
        }).then(function() {
            return module.loadAll();
        });
    }

    // ============================================
    // CONTENT SYNC TABLE MODULE
    // ============================================

    var SyncTableModule = createSyncTableModule({
        name: '',
        prefix: '',
        containerId: 'syncItemsTableContainer',
        listEndpoint: 'Items',
        statusEndpoint: 'Status',
        itemsEndpoint: '',
        modalId: 'itemDetailModal',
        modalButtonPrefix: 'btnModal',
        extraModalButtons: [
            { id: 'MarkSynced', method: 'modalMarkSynced' },
            { id: 'Delete', method: 'modalDelete' }
        ],
        bulkButtons: [
            { id: 'Ignore', title: 'Ignore', icon: 'block', method: 'bulkIgnore' },
            { id: 'MarkSynced', title: 'Mark Synced', icon: 'check_circle', method: 'bulkMarkSynced' },
            { id: 'Queue', title: 'Queue', icon: 'playlist_add', className: 'jpk-button-submit', method: 'bulkQueue' },
            { id: 'Delete', title: 'Delete from local server only', icon: 'delete', className: 'jpk-button-destructive', method: 'bulkDelete' }
        ],
        idKey: 'SourceItemId',
        tasks: {
            refresh: { endpoint: 'TriggerRefresh', key: 'ServerSyncUpdateTables' },
            sync: { endpoint: 'TriggerSync', key: 'ServerSyncDownloadContent' }
        },
        taskNoun: '',
        singularNoun: 'Item',
        pluralNoun: 'item(s)',
        failNoun: 'items',
        emptyMessage: 'No content items found. Run a refresh to scan for content.',
        filters: {
            options: [
                { value: 'Synced', label: 'Synced' },
                { value: 'Queued', label: 'Queued' },
                { value: 'Errored', label: 'Errored' },
                { value: 'Ignored', label: 'Ignored' },
                { value: 'Pending', label: 'Pending' },
                { value: 'Pending:Download', label: 'Pending Download', id: 'optPendingDownload', hidden: true },
                { value: 'Pending:Replacement', label: 'Pending Replace', id: 'optPendingReplacement', hidden: true },
                { value: 'Pending:Deletion', label: 'Pending Delete', id: 'optPendingDeletion', hidden: true },
                { value: 'Deleting', label: 'Deleting', id: 'optDeleting', hidden: true }
            ],
            buildParams: function(filterValue) {
                if (filterValue.indexOf(':') > -1) {
                    var parts = filterValue.split(':');
                    return { status: parts[0], pendingType: parts[1] };
                }
                return { status: filterValue };
            }
        },
        tableOptions: function(self) {
            return {
                getDisplayStatus: function(item) { return self.getDisplayStatus(item); },
                getStatusClass: function(item) { return self.getStatusClass(item); }
            };
        },
        columns: function() {
            return [
                {
                    key: 'name',
                    label: 'Item',
                    type: 'custom',
                    className: 'jpk-table-cell-with-thumb',
                    render: function(item) {
                        var sourcePath = item.SourcePath || '';
                        var libraries = (item.SourceLibraryName || 'Unknown') + ' → ' + (item.LocalLibraryName || 'Unknown');
                        return renderRowInfo(ServerSyncShared.renderItemThumb(item.SourceItemId, item.ServerKey),
                            ServerSyncShared.getFileName(sourcePath), libraries, item, item.Status, sourcePath);
                    }
                },
                {
                    key: 'details',
                    label: 'Details',
                    type: 'custom',
                    className: 'pt-cell-details',
                    render: function(item) {
                        if (item.Status === 'Synced') {
                            return '<span class="jpk-badge gray">No changes</span>';
                        }
                        var size = item.SourceSizeFormatted || '';
                        return size ? '<span class="jpk-badge purple">' + ServerSyncShared.escapeHtml(size) + '</span>' : '';
                    }
                },
                { key: 'Status', label: 'Status', type: 'status' }
            ];
        },
        loadHealth: function() {
            return Promise.all([
                ServerSyncShared.apiRequest('Stats', 'GET'),
                ServerSyncShared.getConfig(),
                ServerSyncShared.apiRequest('PendingSize', 'GET')
            ]).then(function(results) {
                var pendingSizeData = results[2];
                setHealthLastSync('healthLastSync', results[0].LastSyncEndTime);
                setHealthCount('healthLibraryCount', ServerSyncShared.allLibraryMappings(results[1]).length);

                var pendingCountEl = view.querySelector('#healthPendingCount');
                if (pendingSizeData && typeof pendingSizeData.TotalPendingBytes === 'number') {
                    pendingCountEl.textContent = ServerSyncShared.formatSize(pendingSizeData.TotalPendingBytes);
                    pendingCountEl.className = pendingSizeData.TotalPendingBytes > 0 ? 'healthValue warning' : 'healthValue';
                } else {
                    pendingCountEl.textContent = '0 B';
                    pendingCountEl.className = 'healthValue';
                }
            });
        },
        statusRequest: function(item, status) {
            return { endpoint: 'UpdateItemStatus', body: { SourceItemId: item.SourceItemId, Status: status } };
        },
        methods: {
            capabilities: null, // Server capabilities, such as CanDeleteItems

            loadCapabilities: function() {
                var self = this;
                return ServerSyncShared.apiRequest('Capabilities', 'GET').then(function(capabilities) {
                    self.capabilities = capabilities;
                    self.updateDeleteCapabilityVisibility(capabilities.CanDeleteItems);
                }).catch(function() {
                    self.capabilities = { CanDeleteItems: false };
                    self.updateDeleteCapabilityVisibility(false);
                });
            },

            updateDeleteCapabilityVisibility: function(canDelete) {
                var bulkDeleteBtn = view.querySelector('#btnBulkDelete');
                var modalDeleteBtn = view.querySelector('#btnModalDelete');

                if (bulkDeleteBtn) {
                    bulkDeleteBtn.style.display = canDelete ? 'inline-block' : 'none';
                }
                if (modalDeleteBtn) {
                    modalDeleteBtn.style.display = canDelete ? 'inline-block' : 'none';
                }
            },

            loadStatus: function() {
                var self = this;
                return ServerSyncShared.apiRequest('Status', 'GET').then(function(status) {
                    self._showStatusCounts(status);

                    var pendingDownload = status.PendingDownload || 0;
                    var pendingReplacement = status.PendingReplacement || 0;
                    var pendingDeletion = status.PendingDeletion || 0;
                    var totalPending = pendingDownload + pendingReplacement + pendingDeletion;
                    var show = function(countId, groupId, count, title) {
                        view.querySelector('#' + countId).textContent = count;
                        view.querySelector('#' + groupId).setAttribute('title', title);
                    };

                    show('pendingCount', 'statusGroupPending', totalPending,
                        'Pending: ' + totalPending + ' (Download: ' + pendingDownload + ', Replace: ' + pendingReplacement + ', Delete: ' + pendingDeletion + ')');
                    show('pendingDownloadCount', 'statusGroupPendingDownload', pendingDownload, 'Pending Download: ' + pendingDownload);
                    show('pendingReplacementCount', 'statusGroupPendingReplacement', pendingReplacement, 'Pending Replacement: ' + pendingReplacement);
                    show('pendingDeletionCount', 'statusGroupPendingDeletion', pendingDeletion, 'Pending Deletion: ' + pendingDeletion);
                    show('deletingCount', 'statusGroupDeleting', status.Deleting || 0, 'Deleting: ' + (status.Deleting || 0));
                }).catch(function() {
                    // The status endpoint is not available yet.
                });
            },

            retryErrors: function() {
                var self = this;
                ServerSyncShared.apiRequest('RetryErroredItems', 'POST', {}).then(function() {
                    ServerSyncShared.showAlert('Errored items queued for retry');
                    self.loadStatus();
                    self.loadItems();
                }).catch(function() {
                    ServerSyncShared.showAlert('Failed to retry errored items');
                });
            },

            bulkIgnore: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                this.postSelection('IgnoreItems', { SourceItemIds: ids }, ids.length + ' item(s) ignored', 'Failed to ignore items');
            },

            // Pending deletions cannot be queued, so they are left out.
            bulkQueue: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;

                var items = this.table.getItems();
                var queueable = ids.filter(function(id) {
                    var item = items.find(function(i) { return i.SourceItemId === id; });
                    return !(item && item.Status === 'Pending' && item.PendingType === 'Deletion');
                });
                if (queueable.length === 0) {
                    ServerSyncShared.showAlert('No items to queue (deletion items cannot be queued)');
                    return;
                }

                this.postSelection('QueueItems', { SourceItemIds: queueable }, queueable.length + ' item(s) queued', 'Failed to queue items');
            },

            // The server checks the local files exist before marking.
            bulkMarkSynced: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                this.postSelection('MarkSynced', { SourceItemIds: ids }, function(result) {
                    var msg = ((result && result.Synced) || 0) + ' item(s) marked as synced';
                    if (result && result.NotFound > 0) {
                        msg += ', ' + result.NotFound + ' local file(s) not found';
                    }
                    return msg;
                }, 'Failed to mark items as synced');
            },

            bulkDelete: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                if (!confirm('Delete ' + ids.length + ' item(s) from the local server? This cannot be undone.')) {
                    return;
                }
                this.postSelection('DeleteLocalItems', { SourceItemIds: ids }, function(result) {
                    return result && result.Deleted > 0 ? 'Deleted ' + result.Deleted + ' item(s)' : null;
                }, 'Failed to delete items');
            },

            getDisplayStatus: function(item) {
                if (item.Status === 'Pending' && item.PendingType) {
                    return 'Pending ' + item.PendingType;
                }
                return item.Status;
            },

            getStatusClass: function(item) {
                if (item.Status === 'Pending' && item.PendingType) {
                    return 'Pending-' + item.PendingType;
                }
                return item.Status;
            },

            showDetail: function(item) {
                this.showItemDetail(item.SourceItemId);
            },

            showItemDetail: function(sourceItemId) {
                var self = this;
                var items = this.table.getItems();
                var item = items.find(function(i) { return i.SourceItemId === sourceItemId; });
                if (!item) return;

                self.currentModalItem = item;

                view.querySelector('#modalTitle').textContent = ServerSyncShared.getFileName(item.SourcePath) || item.ItemName || 'Unknown';

                var statusBadge = view.querySelector('#modalStatusBadge');
                var displayStatus = self.getDisplayStatus(item);
                var statusClass = self.getStatusClass(item);
                statusBadge.textContent = displayStatus;
                statusBadge.className = 'itemModal-statusBadge ' + statusClass;

                var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, item && item.ServerKey);
                var localServerName = ServerSyncShared.localServerName || 'Local';
                view.querySelector('#modalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;

                if (item.LastSyncTime) {
                    var lastSync = new Date(item.LastSyncTime);
                    view.querySelector('#modalLastSync').textContent = ServerSyncShared.formatRelativeTime(lastSync);
                } else {
                    view.querySelector('#modalLastSync').textContent = '-';
                }

                var sourceLibrary = item.SourceLibraryName || 'Unknown';
                var localLibrary = item.LocalLibraryName || 'Unknown';
                view.querySelector('#modalLibraryMapping').textContent = sourceLibrary + ' \u2192 ' + localLibrary;

                var errorSection = view.querySelector('#modalErrorSection');
                if (item.Status === 'Errored' && item.ErrorMessage) {
                    view.querySelector('#modalError').textContent = item.ErrorMessage;
                    errorSection.classList.remove('hidden');
                } else {
                    errorSection.classList.add('hidden');
                }

                var retrySection = view.querySelector('#modalRetrySection');
                if (item.RetryCount > 0) {
                    view.querySelector('#modalRetryCount').textContent = item.RetryCount + ' attempt' + (item.RetryCount > 1 ? 's' : '');
                    retrySection.classList.remove('hidden');
                } else {
                    retrySection.classList.add('hidden');
                }

                var companionSection = view.querySelector('#modalCompanionFilesSection');
                if (item.CompanionFiles) {
                    var companionList = item.CompanionFiles.split(',').map(function(f) {
                        return f.trim();
                    }).filter(function(f) {
                        return f.length > 0;
                    });
                    if (companionList.length > 0) {
                        view.querySelector('#modalCompanionFiles').innerHTML = companionList.map(function(f) {
                            return '<div class="itemModal-companionItem">' +
                                '<span class="itemModal-companionIcon">&#128196;</span>' +
                                '<span class="itemModal-companionName">' + ServerSyncShared.escapeHtml(f) + '</span>' +
                                '</div>';
                        }).join('');
                        companionSection.classList.remove('hidden');
                    } else {
                        companionSection.classList.add('hidden');
                    }
                } else {
                    companionSection.classList.add('hidden');
                }

                view.querySelector('#modalSourcePath').textContent = item.SourcePath || 'N/A';
                view.querySelector('#modalSourceSize').textContent = ServerSyncShared.formatSize(item.SourceSize);

                var localPathEl = view.querySelector('#modalLocalPath');
                var localPathNoteEl = view.querySelector('#modalLocalPathNote');
                var localSizeRowEl = view.querySelector('#modalLocalSizeRow');
                var localSizeEl = view.querySelector('#modalLocalSize');
                var localExists = item.Status === 'Synced';

                if (item.LocalPath) {
                    localPathEl.textContent = item.LocalPath;
                    if (localExists) {
                        localPathNoteEl.textContent = '';
                        localPathNoteEl.style.display = 'none';
                        localSizeEl.textContent = ServerSyncShared.formatSize(item.LocalSize || item.SourceSize);
                        localSizeRowEl.style.display = 'block';
                    } else {
                        localPathNoteEl.textContent = 'File will be synced to this location';
                        localPathNoteEl.style.display = 'block';
                        localSizeRowEl.style.display = 'none';
                    }
                } else {
                    localPathEl.textContent = 'N/A';
                    localPathNoteEl.style.display = 'none';
                    localSizeRowEl.style.display = 'none';
                }

                var btnQueue = view.querySelector('#btnModalQueue');
                var btnIgnore = view.querySelector('#btnModalIgnore');
                var btnMarkSynced = view.querySelector('#btnModalMarkSynced');
                var modalDeleteBtn = view.querySelector('#btnModalDelete');
                var isPendingDeletion = item.Status === 'Pending' && item.PendingType === 'Deletion';
                var isPendingDownloadOrReplacement = item.Status === 'Pending' && (item.PendingType === 'Download' || item.PendingType === 'Replacement');
                var isSynced = item.Status === 'Synced';
                var isQueuedOrErrored = item.Status === 'Queued' || item.Status === 'Errored';

                var queueBtnSpan = btnQueue.querySelector('span');
                if (isSynced) {
                    queueBtnSpan.textContent = 'Resync';
                } else {
                    queueBtnSpan.textContent = 'Queue';
                }

                // Show Mark Synced only for Queued/Errored items
                btnMarkSynced.style.display = isQueuedOrErrored ? 'inline-block' : 'none';

                if (isPendingDeletion) {
                    btnQueue.style.display = 'none';
                    if (self.capabilities && self.capabilities.CanDeleteItems) {
                        modalDeleteBtn.style.display = 'inline-block';
                    }
                } else if (isPendingDownloadOrReplacement) {
                    btnQueue.style.display = 'inline-block';
                    modalDeleteBtn.style.display = 'none';
                } else {
                    btnQueue.style.display = 'inline-block';
                    if (self.capabilities && self.capabilities.CanDeleteItems) {
                        modalDeleteBtn.style.display = 'inline-block';
                    }
                }

                view.querySelector('#itemDetailModal').classList.remove('hidden');
            },

            // A pending deletion cannot be queued.
            modalQueue: function() {
                var item = this.currentModalItem;
                if (item && !(item.Status === 'Pending' && item.PendingType === 'Deletion')) {
                    this.setModalStatus('Queued');
                }
            },

            modalMarkSynced: function() {
                var self = this;
                if (!this.currentModalItem) return;

                ServerSyncShared.apiRequest('MarkSynced', 'POST', { SourceItemIds: [this.currentModalItem.SourceItemId] }).then(function(result) {
                    self.closeModal();
                    if (result && result.Synced > 0) {
                        ServerSyncShared.showAlert('Item marked as synced');
                    } else {
                        ServerSyncShared.showAlert('Local file not found, so it cannot be marked as synced');
                    }
                }).catch(function() {
                    ServerSyncShared.showAlert('Failed to mark item as synced');
                });
            },

            modalDelete: function() {
                var self = this;
                if (!this.currentModalItem) return;

                var fileName = ServerSyncShared.getFileName(this.currentModalItem.LocalPath || this.currentModalItem.SourcePath);
                if (!confirm('Delete "' + fileName + '" from the local server? This cannot be undone.')) {
                    return;
                }

                ServerSyncShared.apiRequest('DeleteLocalItems', 'POST', { SourceItemIds: [this.currentModalItem.SourceItemId] }).then(function() {
                    self.closeModal();
                    ServerSyncShared.showAlert('Item deleted');
                }).catch(function() {
                    ServerSyncShared.showAlert('Failed to delete item');
                });
            },

            updatePendingFilterVisibility: function(config) {
                var downloadMode = config.DownloadNewContentMode || 'Enabled';
                var replaceMode = config.ReplaceExistingContentMode || 'Enabled';
                var deleteMode = config.DeleteMissingContentMode || 'Disabled';

                var showPendingDownload = downloadMode === 'RequireApproval';
                var showPendingReplace = replaceMode === 'RequireApproval';
                // Automatic deletion still holds some deletions for approval, so both deletion states can
                // appear whenever deletion is on at all.
                var showPendingDelete = deleteMode !== 'Disabled';

                if (this.table) {
                    this.table.setFilterOptionVisible('optPendingDownload', showPendingDownload);
                    this.table.setFilterOptionVisible('optPendingReplacement', showPendingReplace);
                    this.table.setFilterOptionVisible('optPendingDeletion', showPendingDelete);
                    this.table.setFilterOptionVisible('optDeleting', showPendingDelete);
                }

                ServerSyncShared.setVisible('statusGroupPendingDownload', showPendingDownload);
                ServerSyncShared.setVisible('statusGroupPendingReplacement', showPendingReplace);
                ServerSyncShared.setVisible('statusGroupPendingDeletion', showPendingDelete);
                ServerSyncShared.setVisible('statusGroupDeleting', showPendingDelete);

                var showAnyPending = showPendingDownload || showPendingReplace || showPendingDelete;
                var pendingRow = view.querySelector('#pendingStatusRow');
                if (pendingRow) {
                    pendingRow.style.display = showAnyPending ? 'flex' : 'none';
                }
            },
        }
    });

    // ============================================
    // CONTENT PAGE CONTROLLER
    // ============================================

    var ContentPageController = {
        init: function() {
            SyncTableModule.init();
            SyncTableModule.loadCapabilities();
            ServerSyncShared.fetchLocalServerName();
            ServerSyncShared.getConfig().then(function(config) {
                SyncTableModule.currentConfig = config;
                SyncTableModule.updatePendingFilterVisibility(config);
            }).catch(function() {
                // Without a config the pending filters keep their default visibility.
            });
            SyncTableModule.loadAll();
        }
    };

    // ============================================
    // HISTORY SYNC TABLE MODULE
    // ============================================

    var HistorySyncTableModule = createSyncTableModule({
        name: 'History',
        prefix: 'history',
        containerId: 'historyItemsTableContainer',
        listEndpoint: 'HistoryItems',
        statusEndpoint: 'HistoryStatus',
        itemsEndpoint: 'HistoryItems',
        retryEndpoint: 'HistoryItems/Queue',
        modalId: 'historyItemDetailModal',
        modalButtonPrefix: 'btnHistoryModal',
        tasks: {
            refresh: { endpoint: 'TriggerHistoryRefresh', key: 'ServerSyncRefreshHistoryTable' },
            sync: { endpoint: 'TriggerHistorySync', key: 'ServerSyncMissingHistory' }
        },
        taskNoun: 'history',
        singularNoun: 'Item',
        pluralNoun: 'item(s)',
        retryNoun: 'history item(s)',
        failNoun: 'items',
        filterStatuses: ['Synced', 'Queued', 'Pending', 'Errored', 'Ignored'],
        emptyMessage: 'No history items found. Run a refresh to scan for watch history.',
        columns: function(self) {
            return [
                {
                    key: 'name',
                    label: 'Item',
                    type: 'custom',
                    className: 'jpk-table-cell-with-thumb',
                    render: function(item) {
                        var userMapping = self.findUserMapping(item);
                        var users = (userMapping ? userMapping.SourceUserName : 'Unknown') + ' → ' + (userMapping ? userMapping.LocalUserName : 'Unknown');
                        return renderRowInfo(ServerSyncShared.renderItemThumb(item.SourceItemId, item.ServerKey),
                            item.ItemName || 'Unknown', users, item, item.Status);
                    }
                },
                {
                    key: 'details',
                    label: 'Changes',
                    type: 'custom',
                    className: 'pt-cell-details',
                    render: function(item) {
                        if (!item.HasChanges) {
                            return '<span class="jpk-badge gray">No changes</span>';
                        }

                        var details = [];
                        if (item.MergedIsPlayed !== item.LocalIsPlayed) {
                            details.push('Played: ' + (item.MergedIsPlayed ? 'Yes' : 'No'));
                        }
                        if (item.MergedIsFavorite !== item.LocalIsFavorite) {
                            details.push('Favorite: ' + (item.MergedIsFavorite ? 'Yes' : 'No'));
                        }
                        if (item.MergedPlayCount !== item.LocalPlayCount) {
                            details.push('Count: ' + (item.MergedPlayCount || 0));
                        }
                        return ServerSyncShared.escapeHtml(details.join(', ') || 'Changes pending');
                    }
                },
                { key: 'Status', label: 'Status', type: 'status' }
            ];
        },
        loadHealth: function() {
            return ServerSyncShared.getConfig().then(function(config) {
                setHealthLastSync('historyHealthLastSync', config.LastHistorySyncTime);
                setHealthCount('historyHealthUserCount', ServerSyncShared.allUserMappings(config).filter(function(m) { return m.IsEnabled; }).length);
                setHealthCount('historyHealthLibraryCount', ServerSyncShared.allLibraryMappings(config).length);
            });
        },
        statusRequest: function(item, status) {
            return { endpoint: 'HistoryItems/UpdateStatus', body: { Id: item.Id, Status: status } };
        },
        methods: {
            showDetail: function(item) {
                this.showItemDetail(item.Id);
            },

            showItemDetail: function(itemId) {
                var self = this;
                var items = this.table.getItems();
                var item = items.find(function(i) { return i.Id === itemId; });

                if (!item) return;

                self.currentModalItem = item;

                view.querySelector('#historyModalTitle').textContent = item.ItemName || 'Unknown';

                var statusBadge = view.querySelector('#historyModalStatusBadge');
                statusBadge.textContent = item.Status;
                statusBadge.className = 'itemModal-statusBadge ' + item.Status;

                var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, item && item.ServerKey);
                var localServerName = ServerSyncShared.localServerName || 'Local';
                view.querySelector('#historyModalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;

                if (item.LastSyncTime) {
                    view.querySelector('#historyModalLastSync').textContent =
                        ServerSyncShared.formatRelativeTime(new Date(item.LastSyncTime));
                } else {
                    view.querySelector('#historyModalLastSync').textContent = '-';
                }
                showObjectVersion('historyModalVersion', { kind: 'History', localItemId: item.LocalItemId, localUserId: item.LocalUserId });

                var errorSection = view.querySelector('#historyModalErrorSection');
                if (item.Status === 'Errored' && item.ErrorMessage) {
                    view.querySelector('#historyModalError').textContent = item.ErrorMessage;
                    errorSection.classList.remove('hidden');
                } else {
                    errorSection.classList.add('hidden');
                }

                var userMapping = self.findUserMapping(item);
                var sourceUserName = userMapping ? userMapping.SourceUserName : 'Unknown';
                var localUserName = userMapping ? userMapping.LocalUserName : 'Unknown';

                view.querySelector('#historyModalSourceUserName').textContent = sourceUserName;
                view.querySelector('#historyModalSourceUserId').textContent = item.SourceUserId || '';
                view.querySelector('#historyModalLocalUserName').textContent = localUserName;
                view.querySelector('#historyModalLocalUserId').textContent = item.LocalUserId || '';

                view.querySelector('#historyModalSourceHeader').textContent = sourceServerName;
                view.querySelector('#historyModalLocalHeader').textContent = localServerName;

                self.setTableValue('historyModalSourcePlayed', item.SourceIsPlayed, 'bool');
                self.setTableValue('historyModalSourcePlayCount', item.SourcePlayCount, 'number');
                self.setTableValue('historyModalSourcePosition', item.SourcePlaybackPositionTicks, 'position');
                self.setTableValue('historyModalSourceLastPlayed', item.SourceLastPlayedDate, 'date');
                self.setTableValue('historyModalSourceFavorite', item.SourceIsFavorite, 'favorite');

                self.setTableValue('historyModalLocalPlayed', item.LocalIsPlayed, 'bool');
                self.setTableValue('historyModalLocalPlayCount', item.LocalPlayCount, 'number');
                self.setTableValue('historyModalLocalPosition', item.LocalPlaybackPositionTicks, 'position');
                self.setTableValue('historyModalLocalLastPlayed', item.LocalLastPlayedDate, 'date');
                self.setTableValue('historyModalLocalFavorite', item.LocalIsFavorite, 'favorite');

                self.setTableValue('historyModalMergedPlayed', item.MergedIsPlayed, 'bool');
                self.setTableValue('historyModalMergedPlayCount', item.MergedPlayCount, 'number');
                self.setTableValue('historyModalMergedPosition', item.MergedPlaybackPositionTicks, 'position');
                self.setTableValue('historyModalMergedLastPlayed', item.MergedLastPlayedDate, 'date');
                self.setTableValue('historyModalMergedFavorite', item.MergedIsFavorite, 'favorite');

                self.highlightChangedRow('historyModalRowPlayed', item.MergedIsPlayed, item.LocalIsPlayed);
                self.highlightChangedRow('historyModalRowFavorite', item.MergedIsFavorite, item.LocalIsFavorite);
                self.highlightChangedRow('historyModalRowPlayCount', item.MergedPlayCount, item.LocalPlayCount);
                self.highlightChangedRow('historyModalRowPosition', item.MergedPlaybackPositionTicks, item.LocalPlaybackPositionTicks);
                self.highlightChangedRow('historyModalRowLastPlayed', item.MergedLastPlayedDate, item.LocalLastPlayedDate);

                view.querySelector('#historyItemDetailModal').classList.remove('hidden');
            },

            setTableValue: function(elementId, value, type) {
                var el = view.querySelector('#' + elementId);
                if (!el) return;

                var text = '-';

                switch (type) {
                    case 'bool':
                    case 'favorite':
                        if (value === true) {
                            text = 'Yes';
                        } else if (value === false) {
                            text = 'No';
                        }
                        break;
                    case 'number':
                        text = (value !== null && value !== undefined) ? String(value) : '-';
                        break;
                    case 'position':
                        text = this.formatPosition(value);
                        break;
                    case 'date':
                        text = this.formatDate(value);
                        break;
                }

                el.textContent = text;
            },

            formatPosition: function(ticks) {
                if (!ticks || ticks === 0) return '-';
                var seconds = Math.floor(ticks / 10000000);
                var minutes = Math.floor(seconds / 60);
                var hours = Math.floor(minutes / 60);
                seconds = seconds % 60;
                minutes = minutes % 60;

                if (hours > 0) {
                    return hours + ':' + String(minutes).padStart(2, '0') + ':' + String(seconds).padStart(2, '0');
                }
                return minutes + ':' + String(seconds).padStart(2, '0');
            },

            formatDate: function(dateStr) {
                if (!dateStr) return '-';
                try {
                    return ServerSyncShared.formatRelativeTime(new Date(dateStr));
                } catch (e) {
                    return dateStr;
                }
            },

            highlightChangedRow: function(rowId, mergedValue, localValue) {
                var row = view.querySelector('#' + rowId);
                if (!row) return;

                var merged = (mergedValue === null || mergedValue === undefined) ? null : mergedValue;
                var local = (localValue === null || localValue === undefined) ? null : localValue;

                var isChanged = merged !== local;

                if (isChanged) {
                    row.classList.add('historySyncModal-changedRow');
                } else {
                    row.classList.remove('historySyncModal-changedRow');
                }
            },

            // Finds the user mapping a row was synced through. Only the mappings of the row's own server are
            // searched, since another server can map the same ids to other users, and a row with no server key
            // belongs to the first scan server. The source user id must match. The local user id is used only
            // when the row has no source user id, because several source users can map onto one local user.
            findUserMapping: function(item) {
                if (!this.currentConfig || !item) return null;
                var server = item.ServerKey
                    ? ((this.currentConfig.Servers || []).find(function(s) { return s.Key === item.ServerKey; }) || null)
                    : (ServerSyncShared.scanServers(this.currentConfig)[0] || null);
                if (!server) return null;

                // Stored ids may have been written with or without dashes and in either case.
                var normalize = function(id) { return String(id || '').replace(/-/g, '').toLowerCase(); };
                var mappings = server.UserMappings || [];
                if (item.SourceUserId) {
                    var sourceId = normalize(item.SourceUserId);
                    return mappings.find(function(m) { return normalize(m.SourceUserId) === sourceId; }) || null;
                }
                if (item.LocalUserId) {
                    var localId = normalize(item.LocalUserId);
                    return mappings.find(function(m) { return normalize(m.LocalUserId) === localId; }) || null;
                }
                return null;
            },
        }
    });

    // ============================================
    // METADATA SYNC TABLE MODULE
    // ============================================

    var MetadataSyncTableModule = createSyncTableModule({
        name: 'Metadata',
        prefix: 'metadata',
        containerId: 'metadataSyncItemsTableContainer',
        listEndpoint: 'MetadataItems',
        statusEndpoint: 'MetadataStatus',
        itemsEndpoint: 'MetadataItems',
        retryEndpoint: 'MetadataItems/Queue',
        modalId: 'metadataSyncItemDetailModal',
        modalButtonPrefix: 'btnMetadataSyncModal',
        tasks: {
            refresh: { endpoint: 'TriggerMetadataRefresh', key: 'ServerSyncRefreshMetadataTable' },
            sync: { endpoint: 'TriggerMetadataSync', key: 'ServerSyncMissingMetadata' }
        },
        taskNoun: 'metadata',
        singularNoun: 'Item',
        pluralNoun: 'item(s)',
        retryNoun: 'metadata item(s)',
        failNoun: 'items',
        searchPlaceholder: 'Search items...',
        emptyMessage: 'No metadata items found. Run a refresh to scan for metadata.',
        columns: function(self) {
            return [
                {
                    key: 'item',
                    label: 'Item',
                    type: 'custom',
                    className: 'jpk-table-cell-with-thumb',
                    render: function(item) {
                        var libraries = (item.SourceLibraryName || 'Unknown') + ' → ' + (item.LocalLibraryName || 'Unknown');
                        return renderRowInfo(ServerSyncShared.renderItemThumb(item.SourceItemId, item.ServerKey),
                            item.ItemName || 'Unknown', libraries, item, item.Status);
                    }
                },
                {
                    key: 'changes',
                    label: 'Changes',
                    type: 'custom',
                    className: 'pt-cell-details',
                    render: function(item) {
                        // Only categories enabled in the config count.
                        var config = self.currentConfig || {};
                        var changes = [];
                        if (config.MetadataSyncMetadata !== false && item.HasMetadataChanges) changes.push('Metadata');
                        if (config.MetadataSyncImages !== false && item.HasImagesChanges) changes.push('Images');
                        if (config.MetadataSyncPeople === true && item.HasPeopleChanges) changes.push('People');
                        if (config.MetadataSyncStudios !== false && item.HasStudiosChanges) changes.push('Studios');
                        return renderChangeBadges(changes);
                    }
                },
                { key: 'Status', label: 'Status', type: 'status' }
            ];
        },
        loadHealth: function() {
            return ServerSyncShared.getConfig().then(function(config) {
                setHealthLastSync('metadataHealthLastSync', config.LastMetadataSyncTime);
                setHealthCount('metadataHealthLibraryCount', ServerSyncShared.allLibraryMappings(config).length);
            });
        },
        statusRequest: function(item, status) {
            return { endpoint: 'MetadataItems/UpdateStatus', body: { Id: item.Id, Status: status } };
        },
        methods: {
            showDetail: function(item) {
                this.showItemDetail(item.Id);
            },

            showItemDetail: function(itemId) {
                var self = this;
                // Only the newest click may fill the modal. A slower reply for an earlier row, or one that
                // lands after the modal was closed, is dropped instead of showing the wrong item.
                var seq = ++self._detailSeq;

                ServerSyncShared.apiRequest('MetadataItems/' + itemId).then(function(item) {
                    if (seq !== self._detailSeq) return;
                    if (!item) {
                        ServerSyncShared.showAlert('Item not found');
                        return;
                    }

                    self.currentModalItem = item;

                    view.querySelector('#metadataSyncModalTitle').textContent = item.ItemName || 'Unknown';

                    var statusBadge = view.querySelector('#metadataSyncModalStatusBadge');
                    statusBadge.textContent = item.Status;
                    statusBadge.className = 'itemModal-statusBadge ' + item.Status;

                    var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, item && item.ServerKey);
                    var localServerName = ServerSyncShared.localServerName || 'Local';
                    view.querySelector('#metadataSyncModalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;

                    if (item.LastSyncTime) {
                        view.querySelector('#metadataSyncModalLastSync').textContent =
                            ServerSyncShared.formatRelativeTime(new Date(item.LastSyncTime));
                    } else {
                        view.querySelector('#metadataSyncModalLastSync').textContent = '-';
                    }
                    showObjectVersion('metadataSyncModalVersion', { kind: 'Metadata', localItemId: item.LocalItemId });

                    var sourceLib = item.SourceLibraryName || 'Unknown';
                    var localLib = item.LocalLibraryName || 'Unknown';
                    view.querySelector('#metadataSyncModalLibrary').textContent = sourceLib + ' \u2192 ' + localLib;

                    var errorSection = view.querySelector('#metadataSyncModalErrorSection');
                    if (item.Status === 'Errored' && item.ErrorMessage) {
                        view.querySelector('#metadataSyncModalError').textContent = item.ErrorMessage;
                        errorSection.classList.remove('hidden');
                    } else {
                        errorSection.classList.add('hidden');
                    }

                    view.querySelector('#metadataSyncModalSourceHeader').textContent = sourceServerName;
                    view.querySelector('#metadataSyncModalLocalHeader').textContent = localServerName;

                    self.buildChangesSummary(item);

                    view.querySelector('#metadataSyncModalSourcePath').textContent = item.SourcePath || '-';
                    view.querySelector('#metadataSyncModalLocalPath').textContent = item.LocalPath || '-';

                    self.buildPropertyTable(item);

                    view.querySelector('#metadataSyncItemDetailModal').classList.remove('hidden');
                }).catch(function(err) {
                    if (seq !== self._detailSeq) return;
                    console.error('Failed to load metadata item details:', err);
                    ServerSyncShared.showAlert('Failed to load item details');
                });
            },

            buildChangesSummary: function(item) {
                var container = view.querySelector('#metadataSyncModalChangesSummary');
                var config = this.currentConfig || {};
                var html = '';

                var metadataEnabled = config.MetadataSyncMetadata !== false;
                var genresEnabled = config.MetadataSyncGenres !== false;
                var tagsEnabled = config.MetadataSyncTags !== false;
                var studiosEnabled = config.MetadataSyncStudios !== false;
                var peopleEnabled = config.MetadataSyncPeople === true;
                var imagesEnabled = config.MetadataSyncImages !== false;

                var sourceMetadata = parseJsonSafe(item.SourceMetadataValue) || {};
                var localMetadata = parseJsonSafe(item.LocalMetadataValue) || {};

                if (metadataEnabled) {
                    var hasMetadataChanges = item.HasMetadataChanges === true;
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasMetadataChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Metadata: ' + (hasMetadataChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (genresEnabled) {
                    var sourceGenres = sourceMetadata.Genres || [];
                    var localGenres = localMetadata.Genres || [];
                    var hasGenresChanges = JSON.stringify(sourceGenres.slice().sort()) !== JSON.stringify(localGenres.slice().sort());
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasGenresChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Genres: ' + (hasGenresChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (tagsEnabled) {
                    var sourceTags = sourceMetadata.Tags || [];
                    var localTags = localMetadata.Tags || [];
                    var hasTagsChanges = JSON.stringify(sourceTags.slice().sort()) !== JSON.stringify(localTags.slice().sort());
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasTagsChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Tags: ' + (hasTagsChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (imagesEnabled) {
                    var hasImagesChanges = item.HasImagesChanges === true;
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasImagesChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Images: ' + (hasImagesChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (peopleEnabled) {
                    var hasPeopleChanges = item.HasPeopleChanges === true;
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasPeopleChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'People: ' + (hasPeopleChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (studiosEnabled) {
                    var hasStudiosChanges = item.HasStudiosChanges === true;
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasStudiosChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Studios: ' + (hasStudiosChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                container.innerHTML = html;
            },

            buildPropertyTable: function(item) {
                var self = this;
                var tbody = view.querySelector('#metadataSyncModalTableBody');
                var config = this.currentConfig || {};
                var html = '';

                var metadataEnabled = config.MetadataSyncMetadata !== false;
                var genresEnabled = config.MetadataSyncGenres !== false;
                var tagsEnabled = config.MetadataSyncTags !== false;
                var studiosEnabled = config.MetadataSyncStudios !== false;
                var peopleEnabled = config.MetadataSyncPeople === true;
                var imagesEnabled = config.MetadataSyncImages !== false;

                var sourceMetadata = parseJsonSafe(item.SourceMetadataValue) || {};
                var localMetadata = parseJsonSafe(item.LocalMetadataValue) || {};
                var sourceImages = parseJsonSafe(item.SourceImagesValue);
                var localImages = parseJsonSafe(item.LocalImagesValue);
                var sourcePeople = parseJsonSafe(item.SourcePeopleValue);
                var localPeople = parseJsonSafe(item.LocalPeopleValue);
                var sourceStudios = parseJsonSafe(item.SourceStudiosValue);
                var localStudios = parseJsonSafe(item.LocalStudiosValue);

                if (metadataEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Metadata</td></tr>';
                    html += self.buildCoreMetadataRows(sourceMetadata, localMetadata);
                    if (item.HasMetadataChanges) {
                        html += self.buildChangeDetailRow(item.MetadataChangesDetail);
                    }
                }

                if (genresEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Genres</td></tr>';
                    html += self.buildArrayComparisonRow('Genres', sourceMetadata.Genres, localMetadata.Genres);
                }

                if (tagsEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Tags</td></tr>';
                    html += self.buildArrayComparisonRow('Tags', sourceMetadata.Tags, localMetadata.Tags);
                }

                if (studiosEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Studios</td></tr>';
                    html += self.buildArrayComparisonRow('Studios', sourceStudios, localStudios);
                }

                if (peopleEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">People</td></tr>';
                    html += self.buildPeopleComparisonRow(sourcePeople, localPeople);
                }

                if (imagesEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Images</td></tr>';
                    html += self.buildImagesRows(sourceImages, localImages, item);
                    if (item.HasImagesChanges) {
                        html += self.buildChangeDetailRow(item.ImagesChangesDetail);
                    }
                }

                if (html === '') {
                    html = '<tr><td colspan="4" style="text-align: center; opacity: 0.5;">No sync categories are enabled</td></tr>';
                }

                tbody.innerHTML = html;
            },

            // Full-width muted note naming the server-computed reason a category's
            // badge says Changes. The visible rows aggregate (image size sums) or
            // render a fixed field list, so without this a badge can be
            // unexplainable from the UI.
            buildChangeDetailRow: function(text) {
                if (!text) return '';
                return '<tr><td colspan="4" style="opacity:0.6;font-size:0.85em;">Changed: ' +
                    ServerSyncShared.escapeHtml(text) + '</td></tr>';
            },

            buildCoreMetadataRows: function(source, local) {
                var self = this;
                var html = '';

                var metadataFields = [
                    { key: 'Name', label: 'Name' },
                    { key: 'OriginalTitle', label: 'Original Title' },
                    { key: 'ForcedSortName', label: 'Forced Sort Name' },
                    { key: 'Overview', label: 'Overview', truncate: true },
                    { key: 'Tagline', label: 'Tagline' },
                    { key: 'OfficialRating', label: 'Parental Rating' },
                    { key: 'CustomRating', label: 'Custom Rating' },
                    { key: 'CommunityRating', label: 'Community Rating' },
                    { key: 'CriticRating', label: 'Critic Rating' },
                    { key: 'PremiereDate', label: 'Release Date', isDate: true },
                    { key: 'EndDate', label: 'End Date', isDate: true },
                    { key: 'ProductionYear', label: 'Year' },
                    { key: 'AspectRatio', label: 'Aspect Ratio' },
                    { key: 'Video3DFormat', label: '3D Format' },
                    { key: 'IndexNumber', label: 'Index Number' },
                    { key: 'ParentIndexNumber', label: 'Parent Index Number' },
                    { key: 'PreferredMetadataCountryCode', label: 'Country/Region' },
                    { key: 'PreferredMetadataLanguage', label: 'Preferred Language' },
                    { key: 'LockData', label: 'Lock Item', isBoolean: true },
                    { key: 'LockedFields', label: 'Locked Fields', isArray: true }
                ];

                metadataFields.forEach(function(field) {
                    var sourceVal = source[field.key];
                    var localVal = local[field.key];

                    var sourceDisplay = self.formatMetadataValue(sourceVal, field);
                    var localDisplay = self.formatMetadataValue(localVal, field);

                    var isChanged = self.normalizeForComparison(sourceVal, field) !== self.normalizeForComparison(localVal, field);
                    var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                    var mergedDisplay = sourceDisplay;

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(field.label) + '</td>';
                    html += '<td class="historyCompareTable-value">' + sourceDisplay + '</td>';
                    html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                    html += '<td class="historyCompareTable-value historyCompareTable-merged">' + mergedDisplay + '</td>';
                    html += '</tr>';
                });

                var sourceProviders = source.ProviderIds || {};
                var localProviders = local.ProviderIds || {};
                var allProviderKeys = Object.keys(sourceProviders).concat(Object.keys(localProviders));
                var uniqueKeys = [];
                allProviderKeys.forEach(function(k) {
                    if (uniqueKeys.indexOf(k) === -1) uniqueKeys.push(k);
                });
                uniqueKeys.sort();

                uniqueKeys.forEach(function(key) {
                    var srcVal = sourceProviders[key] != null ? String(sourceProviders[key]) : '';
                    var lclVal = localProviders[key] != null ? String(localProviders[key]) : '';
                    var srcDisplay = srcVal || '-';
                    var lclDisplay = lclVal || '-';
                    var isChanged = srcVal !== lclVal;
                    var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(key) + '</td>';
                    html += '<td class="historyCompareTable-value">' + ServerSyncShared.escapeHtml(srcDisplay) + '</td>';
                    html += '<td class="historyCompareTable-value">' + ServerSyncShared.escapeHtml(lclDisplay) + '</td>';
                    html += '<td class="historyCompareTable-value historyCompareTable-merged">' + ServerSyncShared.escapeHtml(srcDisplay) + '</td>';
                    html += '</tr>';
                });

                if (uniqueKeys.length === 0) {
                    html += '<tr>';
                    html += '<td class="historyCompareTable-property">Provider IDs</td>';
                    html += '<td class="historyCompareTable-value">-</td>';
                    html += '<td class="historyCompareTable-value">-</td>';
                    html += '<td class="historyCompareTable-value historyCompareTable-merged">-</td>';
                    html += '</tr>';
                }

                return html;
            },

            buildArrayComparisonRow: function(label, sourceArray, localArray) {
                var html = '';
                var sourceItems = Array.isArray(sourceArray) ? sourceArray : [];
                var localItems = Array.isArray(localArray) ? localArray : [];

                var sourceCount = sourceItems.length;
                var localCount = localItems.length;

                var countChanged = sourceCount !== localCount;
                var countRowClass = countChanged ? 'metadataSyncModal-changedRow' : '';
                html += '<tr class="' + countRowClass + '">';
                html += '<td class="historyCompareTable-property">Count</td>';
                html += '<td class="historyCompareTable-value">' + sourceCount + '</td>';
                html += '<td class="historyCompareTable-value">' + localCount + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceCount + '</td>';
                html += '</tr>';

                var sourceDisplay = sourceItems.length > 0 ? ServerSyncShared.escapeHtml(sourceItems.join(', ')) : '-';
                var localDisplay = localItems.length > 0 ? ServerSyncShared.escapeHtml(localItems.join(', ')) : '-';

                var sourceSorted = sourceItems.slice().sort().join(',');
                var localSorted = localItems.slice().sort().join(',');
                var itemsChanged = sourceSorted !== localSorted;
                var itemsRowClass = itemsChanged ? 'metadataSyncModal-changedRow' : '';

                html += '<tr class="' + itemsRowClass + '">';
                html += '<td class="historyCompareTable-property">Items</td>';
                html += '<td class="historyCompareTable-value">' + sourceDisplay + '</td>';
                html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceDisplay + '</td>';
                html += '</tr>';

                return html;
            },

            buildPeopleComparisonRow: function(sourcePeople, localPeople) {
                var html = '';

                var sourceNames = [];
                var localNames = [];

                if (Array.isArray(sourcePeople)) {
                    sourcePeople.forEach(function(person) {
                        if (person && person.Name) {
                            sourceNames.push(person.Name);
                        }
                    });
                }

                if (Array.isArray(localPeople)) {
                    localPeople.forEach(function(person) {
                        if (person && person.Name) {
                            localNames.push(person.Name);
                        }
                    });
                }

                var sourceCount = sourceNames.length;
                var localCount = localNames.length;

                var countChanged = sourceCount !== localCount;
                var countRowClass = countChanged ? 'metadataSyncModal-changedRow' : '';
                html += '<tr class="' + countRowClass + '">';
                html += '<td class="historyCompareTable-property">Count</td>';
                html += '<td class="historyCompareTable-value">' + sourceCount + '</td>';
                html += '<td class="historyCompareTable-value">' + localCount + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceCount + '</td>';
                html += '</tr>';

                var sourceDisplay = sourceNames.length > 0 ? ServerSyncShared.escapeHtml(sourceNames.join(', ')) : '-';
                var localDisplay = localNames.length > 0 ? ServerSyncShared.escapeHtml(localNames.join(', ')) : '-';

                var sourceSorted = sourceNames.slice().sort().join(',');
                var localSorted = localNames.slice().sort().join(',');
                var itemsChanged = sourceSorted !== localSorted;
                var itemsRowClass = itemsChanged ? 'metadataSyncModal-changedRow' : '';

                html += '<tr class="' + itemsRowClass + '">';
                html += '<td class="historyCompareTable-property">Items</td>';
                html += '<td class="historyCompareTable-value">' + sourceDisplay + '</td>';
                html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceDisplay + '</td>';
                html += '</tr>';

                return html;
            },

            formatMetadataValue: function(value, field) {
                if (field.isBoolean) {
                    if (value === true) return 'Yes';
                    if (value === false) return 'No';
                    return '-';
                }

                if (this.isEmpty(value)) return '-';

                if (field.isArray && Array.isArray(value)) {
                    if (value.length === 0) return '-';
                    return ServerSyncShared.escapeHtml(value.join(', '));
                }

                if (field.isDate) {
                    return this.formatDateOnly(value);
                }

                if (field.truncate && typeof value === 'string' && value.length > 100) {
                    return ServerSyncShared.escapeHtml(value.substring(0, 100) + '...');
                }

                return ServerSyncShared.escapeHtml(String(value));
            },

            formatDateOnly: function(dateStr) {
                if (!dateStr) return '-';
                try {
                    var date = new Date(dateStr);
                    return date.toISOString().split('T')[0];
                } catch (e) {
                    return ServerSyncShared.escapeHtml(String(dateStr));
                }
            },

            normalizeForComparison: function(value, field) {
                if (field.isBoolean) {
                    if (value === true) return 'true';
                    if (value === false) return 'false';
                    return '';
                }

                if (this.isEmpty(value)) return '';

                if (field.isArray && Array.isArray(value)) {
                    return value.slice().sort().join(',');
                }

                if (field.isDate) {
                    try {
                        var date = new Date(value);
                        return date.toISOString().split('T')[0];
                    } catch (e) {
                        return String(value);
                    }
                }

                return String(value);
            },

            isEmpty: function(value) {
                if (value === null || value === undefined) return true;
                if (value === '') return true;
                if (Array.isArray(value) && value.length === 0) return true;
                return false;
            },

            buildImagesRows: function(sourceImages, localImages, item) {
                var self = this;
                var html = '';

                var allTypes = new Set();
                if (sourceImages && typeof sourceImages === 'object') {
                    Object.keys(sourceImages).forEach(function(type) { allTypes.add(type); });
                }
                if (localImages && typeof localImages === 'object') {
                    Object.keys(localImages).forEach(function(type) { allTypes.add(type); });
                }

                var typesArray = Array.from(allTypes).sort();
                typesArray.forEach(function(imageType) {
                    var srcImgs = sourceImages && sourceImages[imageType] ? sourceImages[imageType] : [];
                    var localImgs = localImages && localImages[imageType] ? localImages[imageType] : [];

                    var srcCount = Array.isArray(srcImgs) ? srcImgs.length : 0;
                    var localCount = Array.isArray(localImgs) ? localImgs.length : 0;

                    var srcSize = 0;
                    if (Array.isArray(srcImgs)) {
                        srcImgs.forEach(function(img) {
                            if (img && img.Size) srcSize += img.Size;
                        });
                    }

                    var localSize = 0;
                    if (Array.isArray(localImgs)) {
                        localImgs.forEach(function(img) {
                            if (img && img.Size) localSize += img.Size;
                        });
                    }

                    var srcDisplay = formatImageDisplay(srcSize, srcCount);
                    var localDisplay = formatImageDisplay(localSize, localCount);

                    var isChanged = srcCount !== localCount || srcSize !== localSize;
                    var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(imageType) + '</td>';
                    html += '<td class="historyCompareTable-value">' + srcDisplay + '</td>';
                    html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                    html += '<td class="historyCompareTable-value historyCompareTable-merged">' + srcDisplay + '</td>';
                    html += '</tr>';
                });

                if (typesArray.length === 0) {
                    html += '<tr><td colspan="4" style="text-align: center; opacity: 0.5;">No images</td></tr>';
                }

                return html;
            },
        }
    });

    // ============================================
    // USER SYNC TABLE MODULE
    // ============================================

    // A user row is keyed by its source and local user, joined by a bar.
    function userMappingsFromKeys(keys) {
        return keys.map(function(key) {
            var parts = key.split('|');
            return { SourceUserId: parts[0], LocalUserId: parts[1] };
        });
    }

    var UserSyncTableModule = createSyncTableModule({
        name: 'User',
        prefix: 'user',
        containerId: 'userSyncItemsTableContainer',
        listEndpoint: 'UserSyncUsers',
        statusEndpoint: 'UserStatus',
        itemsEndpoint: 'UserSyncUsers',
        retryEndpoint: 'UserItems/Queue',
        modalId: 'userSyncItemDetailModal',
        modalButtonPrefix: 'btnUserSyncModal',
        idKey: function(item) { return item.SourceUserId + '|' + item.LocalUserId; },
        selectionBody: function(keys) { return { UserMappings: userMappingsFromKeys(keys) }; },
        tasks: {
            refresh: { endpoint: 'TriggerUserRefresh', key: 'ServerSyncRefreshUserTable' },
            sync: { endpoint: 'TriggerUserSync', key: 'ServerSyncMissingUserData' }
        },
        taskNoun: 'user',
        singularNoun: 'User',
        pluralNoun: 'user(s)',
        retryNoun: 'user item(s)',
        failNoun: 'users',
        searchPlaceholder: 'Search users...',
        emptyMessage: 'No user sync items found. Run a refresh to scan for user data.',
        columns: function(self) {
            return [
                {
                    key: 'user',
                    label: 'User',
                    type: 'custom',
                    className: 'jpk-table-cell-with-thumb',
                    render: function(item) {
                        var users = (item.SourceUserName || 'Unknown') + ' → ' + (item.LocalUserName || 'Unknown');
                        var servers = ServerSyncShared.serverNameFor(self.currentConfig, item.ServerKey) + ' → ' + (ServerSyncShared.localServerName || 'Unknown');
                        return renderRowInfo(ServerSyncShared.renderUserThumb(item.SourceUserId, item.ServerKey),
                            users, servers, item, item.OverallStatus, '');
                    }
                },
                {
                    key: 'changes',
                    label: 'Changes',
                    type: 'custom',
                    className: 'pt-cell-details',
                    render: function(item) {
                        // Only categories enabled in the config count.
                        var config = self.currentConfig || {};
                        var changes = [];
                        if (config.UserSyncPolicy !== false && item.PolicyHasChanges) changes.push('Policy');
                        if (config.UserSyncConfiguration !== false && item.ConfigurationHasChanges) changes.push('Configuration');
                        if (config.UserSyncProfileImage !== false && item.ProfileImageHasChanges) changes.push('Image');
                        return renderChangeBadges(changes);
                    }
                },
                { key: 'OverallStatus', label: 'Status', type: 'status' }
            ];
        },
        loadHealth: function() {
            return ServerSyncShared.apiRequest('UserStatus', 'GET').then(function(status) {
                if (!status) return;
                setHealthLastSync('userHealthLastSync', status.LastSyncTime);
                setHealthCount('userHealthUserCount', (status.Synced || 0) + (status.Queued || 0) + (status.Errored || 0) + (status.Ignored || 0));
            });
        },
        statusRequest: function(item, status) {
            return {
                endpoint: 'UserSyncUsers/' + (status === 'Ignored' ? 'Ignore' : 'Queue'),
                body: { UserMappings: [{ SourceUserId: item.SourceUserId, LocalUserId: item.LocalUserId }] }
            };
        },
        methods: {
            showDetail: function(item) {
                this.showUserDetail(item);
            },

            showUserDetail: function(item) {
                var self = this;
                var sourceUserId = item.SourceUserId;
                var localUserId = item.LocalUserId;
                // Only the newest click may fill the modal. A slower reply for an earlier row, or one that
                // lands after the modal was closed, is dropped instead of showing the wrong user.
                var seq = ++self._detailSeq;

                ServerSyncShared.apiRequest('UserSyncUsers/' + encodeURIComponent(sourceUserId) + '/' + encodeURIComponent(localUserId)).then(function(detail) {
                    if (seq !== self._detailSeq) return;
                    if (!detail) {
                        ServerSyncShared.showAlert('User not found');
                        return;
                    }

                    self.currentModalItem = detail;

                    view.querySelector('#userSyncModalTitle').textContent =
                        (detail.SourceUserName || 'Unknown') + ' \u2192 ' + (detail.LocalUserName || 'Unknown');

                    var statusBadge = view.querySelector('#userSyncModalStatusBadge');
                    statusBadge.textContent = detail.OverallStatus || 'Unknown';
                    statusBadge.className = 'itemModal-statusBadge ' + (detail.OverallStatus || 'unknown');

                    var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, detail && detail.ServerKey);
                    var localServerName = ServerSyncShared.localServerName || 'Local';
                    view.querySelector('#userSyncModalServerMapping').textContent =
                        sourceServerName + ' \u2192 ' + localServerName;

                    var infoGrid = view.querySelector('#userSyncModalInfoGrid');
                    infoGrid.classList.remove('hidden');
                    view.querySelector('#userSyncModalLastSync').textContent = detail.LastSyncTime
                        ? ServerSyncShared.formatRelativeTime(new Date(detail.LastSyncTime))
                        : '-';
                    showObjectVersion('userSyncModalVersion', { kind: 'Users', localUserId: detail.LocalUserId });

                    var errorSection = view.querySelector('#userSyncModalErrorSection');
                    if (detail.OverallStatus === 'Errored' && detail.ErrorMessage) {
                        errorSection.classList.remove('hidden');
                        view.querySelector('#userSyncModalError').textContent = detail.ErrorMessage;
                    } else {
                        errorSection.classList.add('hidden');
                    }

                    view.querySelector('#userSyncModalSourceUser').textContent = detail.SourceUserName || 'Unknown';
                    view.querySelector('#userSyncModalSourceUserId').textContent = detail.SourceUserId || '';
                    view.querySelector('#userSyncModalLocalUser').textContent = detail.LocalUserName || 'Unknown';
                    view.querySelector('#userSyncModalLocalUserId').textContent = detail.LocalUserId || '';

                    view.querySelector('#userSyncModalSourceHeader').textContent = sourceServerName;
                    view.querySelector('#userSyncModalLocalHeader').textContent = localServerName;

                    self.buildChangesSummary(detail);

                    var tbody = view.querySelector('#userSyncModalTableBody');
                    tbody.innerHTML = '';

                    if (detail.PolicyEnabled) {
                        if (detail.PolicyItem) {
                            tbody.appendChild(self._createSectionHeader('Policy'));
                            self._addPropertyRows(tbody, detail.PolicyItem);
                        } else {
                            tbody.appendChild(self._createDisabledRow('Policy', 'Not available'));
                        }
                    }

                    if (detail.ConfigurationEnabled) {
                        if (detail.ConfigurationItem) {
                            tbody.appendChild(self._createSectionHeader('Configuration'));
                            self._addPropertyRows(tbody, detail.ConfigurationItem);
                        } else {
                            tbody.appendChild(self._createDisabledRow('Configuration', 'Not available'));
                        }
                    }

                    if (detail.ProfileImageEnabled) {
                        if (detail.ProfileImageItem) {
                            tbody.appendChild(self._createSectionHeader('Profile Image'));
                            self._addProfileImageRow(tbody, detail.ProfileImageItem);
                        } else {
                            tbody.appendChild(self._createDisabledRow('Profile Image', 'Not available'));
                        }
                    }

                    view.querySelector('#userSyncItemDetailModal').classList.remove('hidden');
                }).catch(function(err) {
                    if (seq !== self._detailSeq) return;
                    console.error('Failed to load user detail:', err);
                    ServerSyncShared.showAlert('Failed to load user details');
                });
            },

            _createSectionHeader: function(title) {
                var row = document.createElement('tr');
                row.className = 'userSyncModal-sectionHeader';
                var cell = document.createElement('td');
                cell.colSpan = 4;
                cell.textContent = title;
                row.appendChild(cell);
                return row;
            },

            _createDisabledRow: function(category, message) {
                var row = document.createElement('tr');
                row.className = 'userSyncModal-disabledRow';
                var cell = document.createElement('td');
                cell.colSpan = 4;
                cell.innerHTML = '<span style="opacity: 0.5;">' + ServerSyncShared.escapeHtml(category) + ': ' + ServerSyncShared.escapeHtml(message) + '</span>';
                row.appendChild(cell);
                return row;
            },

            _addPropertyRows: function(tbody, item) {
                var self = this;

                var sourceObj = parseJsonSafe(item.SourceValue);
                var localObj = parseJsonSafe(item.LocalValue);
                var mergedObj = parseJsonSafe(item.MergedValue);

                if (!sourceObj && !localObj && !mergedObj) {
                    var row = document.createElement('tr');
                    if (item.HasChanges) {
                        row.className = 'userSyncModal-changedRow';
                    }

                    var propCell = document.createElement('td');
                    propCell.className = 'historyCompareTable-property';
                    propCell.textContent = item.PropertyCategory || 'Value';

                    var sourceCell = document.createElement('td');
                    sourceCell.className = 'historyCompareTable-value';
                    sourceCell.textContent = self._formatValue(item.SourceValue);

                    var localCell = document.createElement('td');
                    localCell.className = 'historyCompareTable-value';
                    localCell.textContent = self._formatValue(item.LocalValue);

                    var mergedCell = document.createElement('td');
                    mergedCell.className = 'historyCompareTable-value historyCompareTable-merged';
                    mergedCell.textContent = self._formatValue(item.MergedValue);

                    row.appendChild(propCell);
                    row.appendChild(sourceCell);
                    row.appendChild(localCell);
                    row.appendChild(mergedCell);
                    tbody.appendChild(row);
                    return;
                }

                var allKeys = new Set();
                if (sourceObj) Object.keys(sourceObj).forEach(function(k) { allKeys.add(k); });
                if (localObj) Object.keys(localObj).forEach(function(k) { allKeys.add(k); });
                if (mergedObj) Object.keys(mergedObj).forEach(function(k) { allKeys.add(k); });

                allKeys.forEach(function(key) {
                    var sourceVal = sourceObj ? sourceObj[key] : undefined;
                    var localVal = localObj ? localObj[key] : undefined;
                    var mergedVal = mergedObj ? mergedObj[key] : sourceVal;

                    var row = document.createElement('tr');
                    var isChanged = JSON.stringify(sourceVal) !== JSON.stringify(localVal);

                    if (isChanged) {
                        row.className = 'userSyncModal-changedRow';
                    }

                    var propCell = document.createElement('td');
                    propCell.className = 'historyCompareTable-property';
                    propCell.textContent = key;

                    var sourceCell = document.createElement('td');
                    sourceCell.className = 'historyCompareTable-value';
                    sourceCell.textContent = self._formatValue(sourceVal);

                    var localCell = document.createElement('td');
                    localCell.className = 'historyCompareTable-value';
                    localCell.textContent = self._formatValue(localVal);

                    var mergedCell = document.createElement('td');
                    mergedCell.className = 'historyCompareTable-value historyCompareTable-merged';
                    mergedCell.textContent = self._formatValue(mergedVal);

                    row.appendChild(propCell);
                    row.appendChild(sourceCell);
                    row.appendChild(localCell);
                    row.appendChild(mergedCell);

                    tbody.appendChild(row);
                });
            },

            _addProfileImageRow: function(tbody, item) {
                var row = document.createElement('tr');
                var isChanged = item.HasChanges;

                if (isChanged) {
                    row.className = 'userSyncModal-changedRow';
                }

                var propCell = document.createElement('td');
                propCell.className = 'historyCompareTable-property';
                propCell.textContent = 'Profile Image';

                var sourceDisplay = item.SourceImageSizeFormatted || (item.SourceImageSize > 0 ? item.SourceImageSize + ' bytes' : 'None');
                var localDisplay = item.LocalImageSizeFormatted || (item.LocalImageSize > 0 ? item.LocalImageSize + ' bytes' : 'None');
                var mergedDisplay = sourceDisplay;

                var sourceCell = document.createElement('td');
                sourceCell.className = 'historyCompareTable-value';
                sourceCell.textContent = sourceDisplay;

                var localCell = document.createElement('td');
                localCell.className = 'historyCompareTable-value';
                localCell.textContent = localDisplay;

                var mergedCell = document.createElement('td');
                mergedCell.className = 'historyCompareTable-value historyCompareTable-merged';
                mergedCell.textContent = mergedDisplay;

                row.appendChild(propCell);
                row.appendChild(sourceCell);
                row.appendChild(localCell);
                row.appendChild(mergedCell);

                tbody.appendChild(row);
            },

            buildChangesSummary: function(detail) {
                var container = view.querySelector('#userSyncModalChangesSummary');
                var config = this.currentConfig || {};
                var html = '';

                var policyEnabled = config.UserSyncPolicy !== false;
                var configurationEnabled = config.UserSyncConfiguration !== false;
                var profileImageEnabled = config.UserSyncProfileImage !== false;

                if (configurationEnabled) {
                    var hasConfigChanges = detail.ConfigurationItem && detail.ConfigurationItem.HasChanges === true;
                    html += '<span class="userSyncModal-changesBadge ' + (hasConfigChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Configuration: ' + (hasConfigChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (policyEnabled) {
                    var hasPolicyChanges = detail.PolicyItem && detail.PolicyItem.HasChanges === true;
                    html += '<span class="userSyncModal-changesBadge ' + (hasPolicyChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Policy: ' + (hasPolicyChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (profileImageEnabled) {
                    var hasImageChanges = detail.ProfileImageItem && detail.ProfileImageItem.HasChanges === true;
                    html += '<span class="userSyncModal-changesBadge ' + (hasImageChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Image: ' + (hasImageChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                container.innerHTML = html;
            },

            _formatValue: function(value) {
                if (value === null || value === undefined) {
                    return '-';
                }
                if (typeof value === 'boolean') {
                    return value ? 'Yes' : 'No';
                }
                if (Array.isArray(value)) {
                    return value.length > 0 ? value.join(', ') : '-';
                }
                return String(value);
            },
        }
    });

    // ============================================
    // PEOPLE SYNC TABLE MODULE
    // ============================================

    var PeopleSyncTableModule = createSyncTableModule({
        name: 'People',
        prefix: 'people',
        containerId: 'peopleSyncItemsTableContainer',
        listEndpoint: 'PeopleItems',
        statusEndpoint: 'PeopleStatus',
        itemsEndpoint: 'PeopleItems',
        retryEndpoint: 'PeopleItems/Queue',
        modalId: 'peopleSyncItemDetailModal',
        modalButtonPrefix: 'btnPeopleSyncModal',
        idKey: function(item) { return String(item.Id); },
        selectionBody: function(keys) { return { Ids: keys.map(function(k) { return parseInt(k, 10); }) }; },
        tasks: {
            refresh: { endpoint: 'TriggerPeopleRefresh', key: 'ServerSyncRefreshPeopleTable' },
            sync: { endpoint: 'TriggerPeopleSync', key: 'ServerSyncMissingPeople' }
        },
        taskNoun: 'people',
        singularNoun: 'Person',
        pluralNoun: 'person(s)',
        failNoun: 'people',
        searchPlaceholder: 'Search people...',
        emptyMessage: 'No people sync items found. Run a refresh to scan for people.',
        columns: function() {
            return [
                {
                    key: 'person',
                    label: 'Person',
                    type: 'custom',
                    className: 'jpk-table-cell-with-thumb',
                    render: function(item) {
                        return renderRowInfo(ServerSyncShared.renderPersonThumb(item.SourcePersonId, item.ServerKey),
                            item.PersonName || 'Unknown', null, item, item.Status, '');
                    }
                },
                {
                    key: 'changes',
                    label: 'Changes',
                    type: 'custom',
                    className: 'pt-cell-details',
                    render: function(item) {
                        var changes = [];
                        if (item.HasChanges && item.HasMetadataChanges) changes.push('Metadata');
                        if (item.HasChanges && item.HasImagesChanges) changes.push('Images');
                        return renderChangeBadges(changes);
                    }
                },
                { key: 'Status', label: 'Status', type: 'status' }
            ];
        },
        loadHealth: function() {
            return ServerSyncShared.apiRequest('PeopleStatus', 'GET').then(function(status) {
                if (!status) return;
                setHealthLastSync('peopleHealthLastSync', status.LastSyncTime);
                setHealthCount('peopleHealthPersonCount', status.PersonCount || 0);
            });
        },
        statusRequest: function(item, status) {
            return { endpoint: 'PeopleItems/' + (status === 'Ignored' ? 'Ignore' : 'Queue'), body: { Ids: [item.Id] } };
        },
        methods: {
            showDetail: function(item) {
                this.showPersonDetail(item);
            },

            showPersonDetail: function(item) {
                var self = this;
                // Only the newest click may fill the modal. A slower reply for an earlier row, or one that
                // lands after the modal was closed, is dropped instead of showing the wrong person.
                var seq = ++self._detailSeq;

                ServerSyncShared.apiRequest('PeopleItems/' + item.Id).then(function(detail) {
                    if (seq !== self._detailSeq) return;
                    if (!detail) {
                        ServerSyncShared.showAlert('Person not found');
                        return;
                    }

                    self.currentModalItem = detail;

                    view.querySelector('#peopleSyncModalTitle').textContent = detail.PersonName || 'Unknown';

                    var statusBadge = view.querySelector('#peopleSyncModalStatusBadge');
                    statusBadge.textContent = detail.Status || 'Unknown';
                    statusBadge.className = 'itemModal-statusBadge ' + (detail.Status || 'unknown');

                    if (detail.LastSyncTime) {
                        view.querySelector('#peopleSyncModalLastSync').textContent =
                            ServerSyncShared.formatRelativeTime(new Date(detail.LastSyncTime));
                    } else {
                        view.querySelector('#peopleSyncModalLastSync').textContent = '-';
                    }
                    showObjectVersion('peopleSyncModalVersion', { kind: 'People', name: detail.PersonName });

                    var errorSection = view.querySelector('#peopleSyncModalErrorSection');
                    if (detail.Status === 'Errored' && detail.ErrorMessage) {
                        errorSection.classList.remove('hidden');
                        view.querySelector('#peopleSyncModalError').textContent = detail.ErrorMessage;
                    } else {
                        errorSection.classList.add('hidden');
                    }

                    var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, detail && detail.ServerKey);
                    var localServerName = ServerSyncShared.localServerName || 'Local';
                    view.querySelector('#peopleSyncModalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;
                    view.querySelector('#peopleSyncModalSourceHeader').textContent = sourceServerName;
                    view.querySelector('#peopleSyncModalLocalHeader').textContent = localServerName;

                    var summaryHtml = '';
                    summaryHtml += '<span class="peopleSyncBadge ' + (detail.HasMetadataChanges ? 'has-changes' : 'no-changes') + '">Metadata: ' + (detail.HasMetadataChanges ? 'Changes' : 'Synced') + '</span> ';
                    summaryHtml += '<span class="peopleSyncBadge ' + (detail.HasImagesChanges ? 'has-changes' : 'no-changes') + '">Images: ' + (detail.HasImagesChanges ? 'Changes' : 'Synced') + '</span>';
                    view.querySelector('#peopleSyncModalChangesSummary').innerHTML = summaryHtml;

                    var tbody = view.querySelector('#peopleSyncModalTableBody');
                    tbody.innerHTML = '';

                    var sourceMetadata = parseJsonSafe(detail.SourceMetadataValue) || {};
                    var localMetadata = parseJsonSafe(detail.LocalMetadataValue) || {};

                    self._addSectionHeader(tbody, 'Metadata');

                    var metadataFields = [
                        { key: 'Name', label: 'Name' },
                        { key: 'OriginalTitle', label: 'Original Title' },
                        { key: 'ForcedSortName', label: 'Forced Sort Name' },
                        { key: 'Overview', label: 'Overview', truncate: true },
                        { key: 'PremiereDate', label: 'Birth Date', isDate: true },
                        { key: 'EndDate', label: 'Death Date', isDate: true },
                        { key: 'ProductionYear', label: 'Year' },
                        { key: 'LockData', label: 'Lock Item' },
                        { key: 'LockedFields', label: 'Locked Fields' }
                    ];

                    metadataFields.forEach(function(field) {
                        var sv = sourceMetadata[field.key];
                        var lv = localMetadata[field.key];
                        var sourceDisplay = self._formatFieldValue(sv, field);
                        var localDisplay = self._formatFieldValue(lv, field);
                        var isChanged = sourceDisplay !== localDisplay;
                        self._addComparisonRow(tbody, field.label, sourceDisplay, localDisplay, isChanged);
                    });

                    var sourceTags = Array.isArray(sourceMetadata.Tags) ? sourceMetadata.Tags : [];
                    var localTags = Array.isArray(localMetadata.Tags) ? localMetadata.Tags : [];
                    var tagsChanged = JSON.stringify(sourceTags.slice().sort()) !== JSON.stringify(localTags.slice().sort());
                    // The comparison cells are filled with textContent, so the values go in unescaped.
                    self._addComparisonRow(tbody, 'Tags',
                        sourceTags.length > 0 ? sourceTags.join(', ') : '-',
                        localTags.length > 0 ? localTags.join(', ') : '-',
                        tagsChanged);

                    var sourceProviders = sourceMetadata.ProviderIds || {};
                    var localProviders = localMetadata.ProviderIds || {};
                    var allKeys = Object.keys(sourceProviders).concat(Object.keys(localProviders));
                    var uniqueKeys = [];
                    allKeys.forEach(function(k) { if (uniqueKeys.indexOf(k) === -1) uniqueKeys.push(k); });
                    uniqueKeys.sort();

                    if (uniqueKeys.length > 0) {
                        uniqueKeys.forEach(function(key) {
                            var sv = sourceProviders[key] != null ? String(sourceProviders[key]) : '-';
                            var lv = localProviders[key] != null ? String(localProviders[key]) : '-';
                            self._addComparisonRow(tbody, key, sv, lv, sv !== lv);
                        });
                    } else {
                        self._addComparisonRow(tbody, 'Provider IDs', '-', '-', false);
                    }

                    if (detail.HasMetadataChanges) {
                        self._addChangeDetailRow(tbody, detail.MetadataChangesDetail);
                    }

                    self._addSectionHeader(tbody, 'Images');

                    var sourceImages = parseJsonSafe(detail.SourceImagesValue);
                    var localImages = parseJsonSafe(detail.LocalImagesValue);
                    if (sourceImages) {
                        Object.keys(sourceImages).forEach(function(imageType) {
                            var sourceList = sourceImages[imageType] || [];
                            var localList = (localImages && localImages[imageType]) || [];
                            var sourceSize = sourceList.reduce(function(sum, img) { return sum + (img.Size || 0); }, 0);
                            var localSize = localList.reduce(function(sum, img) { return sum + (img.Size || 0); }, 0);
                            var sizeChanged = sourceList.length !== localList.length || (sourceSize > 0 && localSize > 0 && sourceSize !== localSize);
                            // The same formatter as the Metadata modal, so a size the server did not send shows
                            // as a count of images rather than as zero bytes, which would read as an empty image.
                            self._addComparisonRow(tbody, imageType,
                                formatImageDisplay(sourceSize, sourceList.length),
                                formatImageDisplay(localSize, localList.length),
                                sizeChanged);
                        });
                    } else {
                        self._addComparisonRow(tbody, 'Images', '-', localImages ? 'Present' : '-', false);
                    }

                    if (detail.HasImagesChanges) {
                        self._addChangeDetailRow(tbody, detail.ImagesChangesDetail);
                    }

                    view.querySelector('#peopleSyncItemDetailModal').classList.remove('hidden');
                }).catch(function(err) {
                    if (seq !== self._detailSeq) return;
                    console.error('Failed to load person detail:', err);
                    ServerSyncShared.showAlert('Failed to load person details');
                });
            },

            // Same role as the metadata modal's buildChangeDetailRow, DOM-style.
            _addChangeDetailRow: function(tbody, text) {
                if (!text) return;
                var row = document.createElement('tr');
                var cell = document.createElement('td');
                cell.colSpan = 4;
                cell.style.opacity = '0.6';
                cell.style.fontSize = '0.85em';
                cell.textContent = 'Changed: ' + text;
                row.appendChild(cell);
                tbody.appendChild(row);
            },

            _addSectionHeader: function(tbody, title) {
                var row = document.createElement('tr');
                row.className = 'metadataSyncModal-sectionHeader';
                var cell = document.createElement('td');
                cell.colSpan = 4;
                cell.textContent = title;
                row.appendChild(cell);
                tbody.appendChild(row);
            },

            // Returns plain text. The People modal writes every cell with textContent, so escaping here would
            // show the operator HTML entity codes instead of characters such as an ampersand.
            _formatFieldValue: function(val, field) {
                if (val == null) return '-';
                if (field && field.isDate && typeof val === 'string') {
                    try {
                        var d = new Date(val);
                        return isNaN(d.getTime()) ? val : d.toLocaleDateString();
                    } catch (e) { return String(val); }
                }
                if (field && field.truncate && typeof val === 'string' && val.length > 200) {
                    return val.substring(0, 200) + '...';
                }
                if (Array.isArray(val)) {
                    return val.length > 0 ? val.join(', ') : '-';
                }
                if (typeof val === 'boolean') return val ? 'Yes' : 'No';
                if (typeof val === 'object') return JSON.stringify(val);
                return String(val);
            },

            _addComparisonRow: function(tbody, property, sourceVal, localVal, isChanged) {
                var row = document.createElement('tr');
                if (isChanged) row.className = 'metadataSyncModal-changedRow';

                var propCell = document.createElement('td');
                propCell.className = 'historyCompareTable-property';
                propCell.textContent = property;

                var sourceCell = document.createElement('td');
                sourceCell.className = 'historyCompareTable-value';
                sourceCell.textContent = sourceVal || '-';

                var localCell = document.createElement('td');
                localCell.className = 'historyCompareTable-value';
                localCell.textContent = localVal || '-';

                var afterSyncCell = document.createElement('td');
                afterSyncCell.className = 'historyCompareTable-value historyCompareTable-merged';
                afterSyncCell.textContent = sourceVal || '-';

                row.appendChild(propCell);
                row.appendChild(sourceCell);
                row.appendChild(localCell);
                row.appendChild(afterSyncCell);
                tbody.appendChild(row);
            },
        }
    });

    // ============================================
    // VALUE HELPERS (shared by the detail modals)
    // ============================================

    // Parses a stored JSON value, or returns null when it is missing or not JSON. Some user sync values
    // are plain strings rather than JSON, so a failed parse is expected and is not logged.
    function parseJsonSafe(value) {
        if (!value || typeof value !== 'string') return null;
        try {
            return JSON.parse(value);
        } catch (e) {
            return null;
        }
    }

    // One image type as shown in a comparison cell. A size the server did not send shows as a count of
    // images, since "1 (0 B)" would read as an empty image when the image is fine.
    function formatImageDisplay(size, count) {
        if (count === 0) return '-';
        if (!size) {
            return count === 1 ? '1 image' : count + ' images';
        }
        var sizeStr = ServerSyncShared.formatSize(size);
        return count > 1 ? sizeStr + ' (' + count + ')' : sizeStr;
    }

    // ============================================
    // RETRY ERRORED ROWS (shared by the History, Metadata, Users, and People tables)
    // ============================================
    // Posts an empty id list with Status Errored to a module's Queue endpoint, which asks the server to
    // requeue every errored row itself. Listing ids from the page instead would only cover the rows that
    // happen to be loaded. The counts and rows reload whether or not the request worked, since the table
    // should show what the server holds now, and every promise in the chain is returned so nothing fails
    // without an alert.
    function retryAllErrored(endpoint, noun, reload) {
        return ServerSyncShared.apiRequest(endpoint, 'POST', { Ids: [], Status: 'Errored' }).then(function(result) {
            var updated = result && typeof result.Updated === 'number' ? result.Updated : null;
            if (updated === 0) {
                ServerSyncShared.showAlert('No errored ' + noun + ' to retry');
            } else if (updated !== null) {
                ServerSyncShared.showAlert(updated + ' errored ' + noun + ' queued for retry');
            } else {
                ServerSyncShared.showAlert('Errored ' + noun + ' queued for retry');
            }
        }, function(err) {
            console.error('Retry of errored rows failed:', err);
            ServerSyncShared.showAlert('Failed to retry errored ' + noun);
        }).then(function() {
            return reload();
        }).catch(function(err) {
            console.error('Reload after retry failed:', err);
        });
    }

    // ============================================
    // OBJECT VERSION (shared by the detail modals)
    // ============================================
    // Which server last edited an object and when, from ServerSync/Hints/Version. An object no one
    // has edited since versions began shows as not recorded.

    // Requests per version element. Opening a second row before the first row's version arrives must
    // not let the older reply land last and label the second row with the first row's editor.
    var _versionSeq = {};

    function showObjectVersion(elementId, params) {
        var el = view.querySelector('#' + elementId);
        if (!el) return;
        var seq = (_versionSeq[elementId] || 0) + 1;
        _versionSeq[elementId] = seq;
        el.textContent = '\u2026';
        el.title = '';
        var query = Object.keys(params).filter(function (k) { return params[k]; }).map(function (k) {
            return encodeURIComponent(k) + '=' + encodeURIComponent(params[k]);
        }).join('&');
        ServerSyncShared.apiRequest('Hints/Version?' + query, 'GET').then(function (version) {
            if (_versionSeq[elementId] !== seq) return;
            if (!version || !version.Timestamp) {
                el.textContent = 'Not recorded';
                return;
            }
            var where = version.IsThisServer ? 'here' : 'on ' + (version.ServerName || version.ServerId);
            el.textContent = ServerSyncShared.formatRelativeTime(new Date(version.Timestamp)) + ' ' + where;
            el.title = new Date(version.Timestamp).toLocaleString();
        }).catch(function () {
            if (_versionSeq[elementId] !== seq) return;
            el.textContent = 'Not recorded';
        });
    }

    // ============================================
    // CHANGE QUEUE MODULE
    // ============================================
    // Both hint queues and every peer's delivery state, read from
    // ServerSync/Hints. Rows are few and short lived, so a plain table that
    // reloads itself every few seconds fits better than the paginated table.

    var QueueModule = {
        _timer: null,
        _tick: null,
        // A poll still in flight is not started again, and a picture that has not changed is not rebuilt.
        _loading: false,
        _signature: null,
        // Numbers every load so only the newest reply renders.
        _loadSeq: 0,
        // Posters fetched through the image proxy, kept for the life of the page so a refresh does not
        // fetch every poster again.
        _proxyCache: {},
        _maxRows: 200,
        _bound: false,
        _config: null,
        _data: null,
        _states: { Gathering: true, Pending: true, Sent: true, Received: true, Failed: true },
        _dir: 'all',
        _peer: null,

        init: function() {
            var self = this;
            if (!this._bound) {
                this._bound = true;
                view.querySelector('#btnQueueRefresh').addEventListener('click', function() { self.load(true); });
                view.querySelector('#btnQueueRun').addEventListener('click', function() { self.runNow(); });
                view.querySelector('#btnQueueUnmatchedDismiss').addEventListener('click', function() { self.dismissUnmatched(); });
                view.querySelector('#queueList').addEventListener('click', function(e) { self._onDiscard(e); });
                view.querySelectorAll('.qCards .qCard').forEach(function(card) {
                    card.addEventListener('click', function() {
                        self._states[card.dataset.state] = !self._states[card.dataset.state];
                        card.classList.toggle('on', self._states[card.dataset.state]);
                        self.renderList();
                    });
                });
                view.querySelector('#queueFilters').addEventListener('click', function(e) {
                    var btn = e.target.closest('.qFilter');
                    if (!btn) return;
                    if (btn.dataset.dir) { self._dir = btn.dataset.dir; self._peer = null; }
                    else { self._peer = self._peer === btn.dataset.peer ? null : btn.dataset.peer; self._dir = 'all'; }
                    self._reflectFilters();
                    self.renderList();
                });
            }
            this.load();
        },

        startAutoRefresh: function() {
            var self = this;
            this.stopAutoRefresh();
            this._timer = setInterval(function() { self.load(); }, 8000);
            // Countdowns tick between refreshes.
            this._tick = setInterval(function() { self._tickChips(); }, 1000);
        },

        stopAutoRefresh: function() {
            if (this._timer) { clearInterval(this._timer); this._timer = null; }
            if (this._tick) { clearInterval(this._tick); this._tick = null; }
        },

        load: function(force) {
            var self = this;
            if (!ServerSyncShared || (self._loading && !force)) return Promise.resolve();
            self._loading = true;
            // A forced load can start while a poll is still in flight, and the poll's older reply must not
            // land last and put back a picture the forced load already replaced.
            var seq = ++self._loadSeq;
            return ServerSyncShared.apiRequest('Hints', 'GET').then(function(data) {
                if (seq !== self._loadSeq) return;
                var signature = JSON.stringify(data || {});
                if (!force && signature === self._signature) return;
                self._signature = signature;
                self._data = data || {};
                self.render();
            }).catch(function() {
                // Leave the last picture in place. The next tick tries again.
            }).then(function() {
                // Only the newest load clears the flag, so an older reply finishing first does not let the
                // next poll start while the newest load is still in flight.
                if (seq === self._loadSeq) self._loading = false;
            });
        },

        // Fetches a poster through the image proxy once per page and remembers it.
        _loadProxy: function(imgId, cacheKey, itemId, peerKey) {
            var self = this;
            var url = ApiClient.getUrl('ServerSync/ImageProxy', { itemId: itemId, user: 'false', maxHeight: 128, serverKey: peerKey });
            ApiClient.fetch({ url: url, type: 'GET' }).then(function(response) {
                if (!response || !response.ok) throw new Error('image fetch failed');
                return response.blob();
            }).then(function(blob) {
                var objectUrl = URL.createObjectURL(blob);
                self._proxyCache[cacheKey] = objectUrl;
                var img = document.getElementById(imgId);
                if (img) img.src = objectUrl;
            }).catch(function() {
                self._proxyCache[cacheKey] = '';
                var img = document.getElementById(imgId);
                if (img) {
                    img.style.display = 'none';
                    if (img.nextElementSibling) img.nextElementSibling.style.display = 'flex';
                }
            });
        },

        runNow: function() {
            var self = this;
            var btn = view.querySelector('#btnQueueRun');
            btn.disabled = true;
            ServerSyncShared.apiRequest('Hints/Run', 'POST').then(function() {
                return self.load(true);
            }).then(function() {
                ServerSyncShared.showAlert('Delivered and applied what was due');
            }).catch(function() {
                ServerSyncShared.showAlert('The run failed. See the server log');
            }).then(function() {
                btn.disabled = false;
            });
        },

        dismissUnmatched: function() {
            var self = this;
            var btn = view.querySelector('#btnQueueUnmatchedDismiss');
            btn.disabled = true;
            ServerSyncShared.apiRequest('Hints/Unmatched', 'DELETE').then(function() {
                return self.load(true);
            }).catch(function() {
                ServerSyncShared.showAlert('Could not dismiss the warning');
            }).then(function() {
                btn.disabled = false;
            });
        },

        render: function() {
            var data = this._data || {};
            var outbound = data.Outbound || [];
            var inbound = data.Inbound || [];
            var peers = data.Peers || [];
            var counts = data.OutboundCounts || {};
            var count = function(state) {
                if (counts[state] !== undefined) return counts[state];
                return outbound.filter(function(r) { return QueueModule._stateName(r.State) === state; }).length;
            };
            view.querySelector('#queuePendingCount').textContent = count('Pending');
            view.querySelector('#queueSentCount').textContent = count('Sent');
            view.querySelector('#queueFailedCount').textContent = count('Failed');
            view.querySelector('#queueInboundCount').textContent = data.InboundCount !== undefined ? data.InboundCount : inbound.length;
            view.querySelector('#queueGatheringCount').textContent = (data.Gathering || []).length || data.Pending || 0;

            var peerRow = view.querySelector('#queuePeerRow');
            peerRow.innerHTML = peers.map(function(p) {
                var paused = p.PausedUntil && new Date(p.PausedUntil) > new Date();
                var state = paused ? 'Paused until ' + QueueModule._time(p.PausedUntil)
                    : p.LastAttempt ? 'Delivered ' + QueueModule._ago(p.LastAttempt) : 'Nothing sent yet';
                var sends = p.Sends === null || p.Sends === undefined ? 'Sends everything until the peer says what it applies'
                    : p.Sends.length === 0 ? 'Sends nothing: every module is off there'
                    : 'Sends ' + p.Sends.map(QueueModule._kindLabel).join(', ');
                return '<div class="healthCard queuePeerCard">' +
                    '<span class="healthLabel">' + ServerSyncShared.escapeHtml(p.Name || p.Key) + '</span>' +
                    '<span class="healthValue' + (paused ? ' paused' : '') + '">' + ServerSyncShared.escapeHtml(state) + '</span>' +
                    '<div class="queuePeerReason">' + ServerSyncShared.escapeHtml(sends) + '</div>' +
                    (p.Reason ? '<div class="queuePeerReason">' + ServerSyncShared.escapeHtml(p.Reason) + '</div>' : '') +
                    '</div>';
            }).join('');
            view.querySelector('#queuePeers').classList.toggle('hidden', peers.length === 0);
            view.querySelector('#queueNoPeers').classList.toggle('hidden', peers.length > 0);

            var unmatchedEl = view.querySelector('#queueUnmatched');
            var unmatched = data.Unmatched || 0;
            unmatchedEl.classList.toggle('hidden', unmatched === 0);
            if (unmatched > 0) {
                unmatchedEl.querySelector('#queueUnmatchedText').textContent = unmatched + ' local change' + (unmatched === 1 ? '' : 's') + ' matched no library or user mapping on any Push or Sync server, so nothing was sent for ' + (unmatched === 1 ? 'it' : 'them') +
                    (data.LastUnmatched ? '. Last: ' + data.LastUnmatched : '') + '. Saving the Libraries or Users step of those servers checks ' + (unmatched === 1 ? 'it' : 'them') + ' again.';
            }

            // Peer filters follow the peers that exist.
            var filters = view.querySelector('#queueFilters');
            filters.querySelectorAll('[data-peer]').forEach(function(b) { b.remove(); });
            peers.forEach(function(p) {
                var b = document.createElement('button');
                b.type = 'button'; b.className = 'qFilter'; b.dataset.peer = p.Key; b.textContent = p.Name || p.Key;
                filters.appendChild(b);
            });
            this._reflectFilters();
            this.renderList();
        },

        _reflectFilters: function() {
            var self = this;
            view.querySelectorAll('#queueFilters .qFilter').forEach(function(b) {
                b.classList.toggle('on', b.dataset.dir ? (self._peer === null && b.dataset.dir === self._dir) : b.dataset.peer === self._peer);
            });
        },

        // Every change as one row, whatever lane it is in, newest activity first.
        _rows: function() {
            var data = this._data || {};
            var rows = [];
            (data.Gathering || []).forEach(function(g) {
                rows.push({ lane: 'gathering', state: 'Gathering', dir: 'out', at: g.EditedAt, kind: g.Kind, recorded: g.Recorded,
                    title: g.Title || g.Change, subtitle: g.Subtitle, itemType: g.ItemType, itemId: g.ItemId, userId: g.UserId, userName: g.UserName, dueAt: g.DueAt, what: QueueModule._what(g.Kind) });
            });
            (data.Outbound || []).forEach(function(r) {
                var state = QueueModule._stateName(r.State);
                rows.push({ lane: 'out', state: state, dir: 'out', at: r.SentAt || r.CreatedAt, kind: r.Kind, recorded: r.Recorded, id: r.Id, peerKey: r.PeerKey, peerName: r.PeerName || r.PeerKey,
                    title: r.Title || QueueModule._fileName(r.ItemPath) || r.UserName || r.Key, subtitle: r.Subtitle, itemType: r.ItemType, itemId: r.ItemId, userId: r.UserId, userName: r.UserName,
                    attempts: r.Attempts, lastError: r.LastError, nextAttempt: r.NextAttempt, sentAt: r.SentAt, what: QueueModule._what(r.Kind) });
            });
            (data.Inbound || []).forEach(function(r) {
                var origin = QueueModule._origin(r.OriginServerId);
                rows.push({ lane: 'in', state: 'Received', dir: 'in', at: r.ReceivedAt, kind: r.Kind, recorded: r.Recorded, id: r.Id, peerKey: origin ? origin.Key : null, peerName: origin ? (origin.Name || origin.ServerName || origin.Url) : r.OriginServerId,
                    title: QueueModule._fileName(r.ItemPath) || r.UserName || r.Key, subtitle: null, itemType: r.ItemType, originItemId: r.ItemId, userName: r.UserName,
                    attempts: r.Attempts, lastError: r.LastError, receivedAt: r.ReceivedAt, what: QueueModule._what(r.Kind) });
            });
            rows.sort(function(a, b) { return new Date(b.at || 0) - new Date(a.at || 0); });
            return rows;
        },

        renderList: function() {
            var self = this;
            var rows = this._rows().filter(function(r) {
                if (!self._states[r.state]) return false;
                if (self._peer) return r.peerKey === self._peer;
                return self._dir === 'all' || r.dir === self._dir;
            });
            var list = view.querySelector('#queueList');
            var shown = rows.slice(0, self._maxRows);
            list.innerHTML = shown.map(function(r) { return QueueModule._row(r); }).join('') +
                (rows.length > shown.length
                    ? '<div class="qMore">Showing the latest ' + shown.length + ' of ' + rows.length + '. Filter by state or server to see others.</div>'
                    : '');
            view.querySelector('#queueEmpty').classList.toggle('hidden', rows.length > 0);
            list.classList.toggle('hidden', rows.length === 0);
        },

        _row: function(r) {
            var esc = ServerSyncShared.escapeHtml;
            var isPerson = r.kind === 'People' || r.kind === 2;
            var isUser = r.kind === 'Users' || r.kind === 4;
            // Jellyfin's own shapes: a person is round, an episode or a home video is wide, the rest are posters.
            var shape = isPerson || isUser ? ' round' : QueueModule._isWide(r.itemType) ? ' wide' : '';
            var icon = isUser || isPerson ? 'person' : QueueModule._isWide(r.itemType) ? 'tv' : 'movie';
            var holder = '<div class="qThumbHolder' + shape + '"><span class="material-icons">' + icon + '</span></div>';
            var thumb;
            if (r.itemId && !isUser) {
                var url = ApiClient.getImageUrl(r.itemId, { type: 'Primary', maxHeight: 128 });
                thumb = '<img class="qThumb' + shape + '" loading="lazy" src="' + esc(url) + '" alt="" onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'flex\'" />' +
                    holder.replace('<div ', '<div style="display:none" ');
            } else if (r.originItemId && r.peerKey && !isUser) {
                var cacheKey = r.peerKey + '|' + r.originItemId;
                var cached = QueueModule._proxyCache[cacheKey];
                if (cached) {
                    thumb = '<img class="qThumb' + shape + '" src="' + esc(cached) + '" alt="" />' + holder.replace('<div ', '<div style="display:none" ');
                } else if (cached === '') {
                    thumb = holder;
                } else {
                    var id = 'ss-q-thumb-' + Math.random().toString(36).slice(2);
                    setTimeout(function() { QueueModule._loadProxy(id, cacheKey, r.originItemId, r.peerKey); }, 0);
                    thumb = '<img id="' + id + '" class="qThumb' + shape + '" alt="" />' + holder.replace('<div ', '<div style="display:none" ');
                }
            } else {
                thumb = holder;
            }

            var who = '';
            if (r.userName && (r.kind === 'History' || r.kind === 0)) {
                var avatar = r.userId ? '<img class="qAvatar" src="' + esc(ApiClient.getUserImageUrl(r.userId, { type: 'Primary', height: 32 })) + '" alt="" onerror="this.style.visibility=\'hidden\'" />' : '<i class="qAvatar"></i>';
                who = '<span class="qWho">' + avatar + esc(r.userName) + '</span>';
            }
            var where = r.dir === 'in' ? 'from ' + esc(r.peerName || 'a peer') : (r.peerName ? 'to ' + esc(r.peerName) : '');
            var sub = who + '<span>' + esc(r.what) + (where ? ' · ' + where : '') + '</span>';
            var badges = '<span class="jpk-badge ' + QueueModule._kindColor(r.kind) + '">' + esc(QueueModule._kindLabelTitle(r.kind)) + '</span>' +
                (r.recorded === false ? '<span class="jpk-badge gray">provider</span>' : '');

            var chip, meta = '', err = false, act = '';
            if (r.state === 'Gathering') {
                chip = '<span class="qChip gathering" data-due="' + esc(r.dueAt || '') + '" data-edited="' + esc(r.at || '') + '"><i class="qRing"></i><span class="qChipText">Gathering · ' + QueueModule._countdown(r.dueAt) + '</span></span>';
                meta = 'edited ' + QueueModule._ago(r.at);
            } else if (r.state === 'Pending') {
                chip = '<span class="qChip pending">Pending' + (r.attempts > 0 ? ' · retry ' + QueueModule._time(r.nextAttempt) : ' · ' + esc(r.peerName)) + '</span>';
                if (r.attempts > 0) { meta = (r.lastError || 'retrying') + ', attempt ' + r.attempts; err = true; } else { meta = 'waiting for delivery'; }
                act = '<button type="button" class="qAct" data-lane="Outbound" data-discard="' + r.id + '" title="Discard this hint. The scheduled task still covers the change.">✕</button>';
            } else if (r.state === 'Sent') {
                chip = '<span class="qChip sent">Sent · waiting for ' + esc(r.peerName) + '</span>';
                meta = 'accepted ' + QueueModule._ago(r.sentAt);
                act = '<button type="button" class="qAct" data-lane="Outbound" data-discard="' + r.id + '" title="Discard this hint. The scheduled task still covers the change.">✕</button>';
            } else if (r.state === 'Failed') {
                chip = '<span class="qChip failed">Failed · ' + esc(r.peerName) + '</span>';
                meta = r.lastError || 'rejected'; err = true;
                act = '<button type="button" class="qAct" data-lane="Outbound" data-discard="' + r.id + '" title="Discard this hint.">✕</button>';
            } else {
                chip = '<span class="qChip received">Received · ' + (r.attempts > 0 ? 'retrying' : 'applying next pass') + '</span>';
                if (r.attempts > 0) { meta = (r.lastError || 'retrying') + ', attempt ' + r.attempts; err = true; } else { meta = QueueModule._ago(r.receivedAt); }
                act = '<button type="button" class="qAct" data-lane="Inbound" data-discard="' + r.id + '" title="Discard this hint. The scheduled task still covers the change.">✕</button>';
            }

            var title = esc(r.title || '') + (r.subtitle ? ' <span class="qEp"><i class="qSep">· </i>' + esc(r.subtitle) + '</span>' : '');
            return '<div class="qRow">' +
                '<div class="qThumbWrap">' + thumb + '</div>' +
                '<div class="qInfo"><div class="qTitle">' + title + '</div><div class="qSub">' + sub + '</div><div class="qBadges">' + badges + '</div></div>' +
                '<div class="qRight">' + chip + '<span class="qMeta' + (err ? ' err' : '') + '">' + esc(meta) + '</span>' + act + '</div>' +
                '</div>';
        },

        // Episodes, home videos, trailers, and recordings carry a landscape primary image in Jellyfin.
        _isWide: function(itemType) {
            return itemType === 'Episode' || itemType === 'Video' || itemType === 'Trailer' || itemType === 'Recording' || itemType === 'MusicVideo';
        },

        // The gathering countdown moves every second without a round trip.
        _tickChips: function() {
            view.querySelectorAll('#queueList .qChip.gathering').forEach(function(chip) {
                var due = chip.getAttribute('data-due');
                var edited = chip.getAttribute('data-edited');
                if (!due) return;
                var text = chip.querySelector('.qChipText');
                if (text) text.textContent = 'Gathering · ' + QueueModule._countdown(due);
                var ring = chip.querySelector('.qRing');
                if (ring && edited) {
                    var total = new Date(due) - new Date(edited);
                    var done = Date.now() - new Date(edited);
                    ring.style.setProperty('--p', Math.max(0, Math.min(100, total > 0 ? 100 * done / total : 100)) + '%');
                }
            });
        },

        _countdown: function(iso) {
            if (!iso) return '';
            var s = Math.round((new Date(iso) - Date.now()) / 1000);
            if (s <= 0) return 'sending now';
            return s < 120 ? 'sends in ' + s + 's' : 'sends in ' + Math.round(s / 60) + 'm';
        },

        _origin: function(serverId) {
            var servers = (this._config && this._config.Servers) || [];
            return servers.find(function(s) { return s.ServerId === serverId; }) || null;
        },

        _what: function(kind) {
            return { 0: 'watch history', 1: 'metadata', 2: 'biography and images', 3: 'new file', 4: 'user settings',
                History: 'watch history', Metadata: 'metadata', People: 'biography and images', Content: 'new file', Users: 'user settings' }[kind] || String(kind);
        },

        _kindLabel: function(kind) {
            return { History: 'watch history', Metadata: 'metadata', People: 'people', Content: 'files', Users: 'user settings' }[kind] || String(kind).toLowerCase();
        },

        _kindLabelTitle: function(kind) {
            return { 0: 'Watch history', 1: 'Metadata', 2: 'Person', 3: 'File', 4: 'User', History: 'Watch history', Metadata: 'Metadata', People: 'Person', Content: 'File', Users: 'User' }[kind] || String(kind);
        },

        _kindColor: function(kind) {
            return { 0: 'purple', 1: 'blue', 2: 'green', 3: 'orange', 4: 'gray', History: 'purple', Metadata: 'blue', People: 'green', Content: 'orange', Users: 'gray' }[kind] || 'gray';
        },

        _stateName: function(state) {
            var names = { 0: 'Pending', 1: 'Sent', 2: 'Failed' };
            return names[state] || String(state);
        },

        _fileName: function(path) {
            if (!path) return '';
            var parts = path.split(/[\\/]/);
            var last = parts[parts.length - 1] || path;
            return last.replace(/\.[^.]+$/, '');
        },

        _ago: function(iso) {
            if (!iso) return '';
            var seconds = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
            if (seconds < 60) return seconds + 's ago';
            if (seconds < 3600) return Math.round(seconds / 60) + 'm ago';
            if (seconds < 86400) return Math.round(seconds / 3600) + 'h ago';
            return Math.round(seconds / 86400) + 'd ago';
        },

        _time: function(iso) {
            if (!iso) return '';
            var d = new Date(iso);
            var diff = d.getTime() - Date.now();
            if (diff > 0 && diff < 86400000) {
                var minutes = Math.round(diff / 60000);
                return minutes < 1 ? 'in under a minute' : minutes < 60 ? 'in ' + minutes + 'm' : 'in ' + Math.round(minutes / 60) + 'h';
            }
            return d.toLocaleString();
        },

        _onDiscard: function(e) {
            var btn = e.target.closest('[data-discard]');
            if (!btn) return;
            var self = this;
            btn.disabled = true;
            ServerSyncShared.apiRequest('Hints/' + btn.getAttribute('data-lane') + '/' + btn.getAttribute('data-discard'), 'DELETE').then(function() {
                ServerSyncShared.showAlert('Hint discarded');
                return self.load(true);
            }).catch(function() {
                ServerSyncShared.showAlert('Could not discard the hint');
                btn.disabled = false;
            });
        }
    };

    // ============================================
    // INITIAL PAGE SETUP
    // ============================================
    // viewshow may have already fired before we got here,
    // so set tabs and trigger init immediately on first load.

    _pageReady = true;
    LibraryMenu.setTabs('serversync', 0, getTabs);
    SyncViewManager.init();
}

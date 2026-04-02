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

    // Track active poll intervals so we can clean them up on viewhide
    var _activePollIntervals = [];

    view.addEventListener('viewshow', onViewShow);
    view.addEventListener('viewhide', function () {
        // Clear any running pollTaskProgress intervals
        _activePollIntervals.forEach(function(id) { clearInterval(id); });
        _activePollIntervals.length = 0;

        // Disconnect IntersectionObservers on all PaginatedTable instances
        var modules = [SyncTableModule, HistorySyncTableModule, MetadataSyncTableModule, UserSyncTableModule, PeopleSyncTableModule];
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

    var _sharedPromise = import('/web/configurationpage?name=serversync_shared.js').then(function(shared) {
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

        // Initialize the view manager: bind dropdown listener and apply config-based filtering
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

            // Always re-fetch config to pick up changes from Settings tab
            _sharedPromise.then(function() {
                ServerSyncShared.getConfig().then(function(config) {
                    self._applyEnabledTypes(config);
                }).catch(function() {
                    // Config fetch failed — show all options as fallback
                    self._selectFirstEnabled();
                });
            });
        },

        // Filter dropdown options based on enabled sync types in config.
        // Hides/disables options whose config key (e.g. EnableContentSync) is false.
        _applyEnabledTypes: function(config) {
            var dropdown = view.querySelector('#syncTypeDropdown');
            if (!dropdown) return;

            var options = dropdown.querySelectorAll('option');
            var enabledCount = 0;

            for (var i = 0; i < options.length; i++) {
                var opt = options[i];
                var configKey = opt.getAttribute('data-config-key');
                var isEnabled = !configKey || (config && config[configKey]);

                if (isEnabled) {
                    opt.style.display = '';
                    opt.disabled = false;
                    enabledCount++;
                } else {
                    opt.style.display = 'none';
                    opt.disabled = true;
                }
            }

            var syncTypeContainer = view.querySelector('#syncTypeContainer');
            var noSyncMessage = view.querySelector('#noSyncTypesMessage');
            var titleEl = view.querySelector('#syncPageTitle');

            if (enabledCount === 0) {
                // No sync types enabled — show empty state
                if (syncTypeContainer) syncTypeContainer.classList.add('hidden');
                if (noSyncMessage) noSyncMessage.classList.remove('hidden');
                if (titleEl) titleEl.textContent = 'Sync';

                // Hide all sync views
                var views = view.querySelectorAll('.syncView');
                for (var j = 0; j < views.length; j++) {
                    views[j].classList.add('hidden');
                }
            } else {
                if (syncTypeContainer) syncTypeContainer.classList.remove('hidden');
                if (noSyncMessage) noSyncMessage.classList.add('hidden');
                this._selectFirstEnabled();
            }
        },

        // Select the first visible/enabled option and switch to it.
        // Preserves the current view if it's still enabled.
        _selectFirstEnabled: function() {
            var dropdown = view.querySelector('#syncTypeDropdown');
            if (!dropdown) return;

            // Check if current view is still enabled
            if (this.currentView) {
                var currentOpt = dropdown.querySelector('option[value="' + this.currentView + '"]');
                if (currentOpt && !currentOpt.disabled) {
                    // Current view is still valid — just ensure dropdown matches
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

            // Current view is disabled or not set — find first enabled option
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

        // Map view names to their table module for observer management
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

        // Switch to a different sync view: hide all views, show the target,
        // update page title, and lazy-initialize the view's controller
        switchView: function(viewName) {
            if (this.currentView === viewName) return;

            // Disconnect IntersectionObserver on the outgoing tab's table
            // (prevents stale triggers while the container is hidden)
            var outgoingModule = this._getTableModule(this.currentView);
            if (outgoingModule && outgoingModule.table && outgoingModule.table.disconnectObserver) {
                outgoingModule.table.disconnectObserver();
            }

            // Hide all views
            var views = view.querySelectorAll('.syncView');
            for (var i = 0; i < views.length; i++) {
                views[i].classList.add('hidden');
            }

            // Show selected view
            var targetView = view.querySelector('#syncView-' + viewName);
            if (targetView) {
                targetView.classList.remove('hidden');
            }

            // Update page title
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
                titleEl.textContent = 'Sync - ' + displayName;
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
                            HistoryPageController.init();
                            break;
                        case 'metadata':
                            MetadataPageController.init();
                            break;
                        case 'users':
                            UsersPageController.init();
                            break;
                        case 'people':
                            PeoplePageController.init();
                            break;
                    }
                });
            }
        }
    };

    // ============================================
    // CONTENT SYNC TABLE MODULE
    // ============================================

    var SyncTableModule = {
        table: null,            // PaginatedTable instance
        currentModalItem: null, // Item shown in the detail modal
        capabilities: null,     // Server capabilities (e.g. CanDeleteItems)
        currentConfig: null,    // Cached plugin configuration
        _initialized: false,    // Prevents duplicate initialization

        // Create the PaginatedTable, bind action buttons, and inject bulk-action buttons
        init: function() {
            if (this._initialized) {
                return;
            }
            this._initialized = true;

            var self = this;

            this.table = createPaginatedTable(view, ServerSyncShared, {
                containerId: 'syncItemsTableContainer',
                endpoint: 'Items',

                columns: [
                    {
                        key: 'name',
                        label: 'Item',
                        type: 'custom',
                        className: 'pt-cell-with-thumb',
                        render: function(item) {
                            var sourcePath = item.SourcePath || '';
                            var sourceLibrary = item.SourceLibraryName || 'Unknown';
                            var localLibrary = item.LocalLibraryName || 'Unknown';
                            var libraryDisplay = sourceLibrary + ' \u2192 ' + localLibrary;

                            var errorPreview = '';
                            if (item.Status === 'Errored' && item.ErrorMessage) {
                                errorPreview = '<div class="syncItemError" title="' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
                            }

                            return ServerSyncShared.renderItemThumb(item.SourceServerUrl, item.SourceServerApiKey, item.SourceItemId) +
                                '<div class="syncItemInfo">' +
                                '<div class="syncItemName" title="' +
                                    ServerSyncShared.escapeHtml(sourcePath) + '">' +
                                    ServerSyncShared.escapeHtml(ServerSyncShared.getFileName(sourcePath)) + '</div>' +
                                '<div class="syncItemPath">' +
                                    ServerSyncShared.escapeHtml(libraryDisplay) + '</div>' +
                                errorPreview +
                            '</div>';
                        }
                    },
                    {
                        key: 'details',
                        label: 'Details',
                        type: 'custom',
                        className: 'pt-cell-details',
                        render: function(item) {
                            if (item.Status === 'Synced') {
                                return '<span style="opacity: 0.5;">No changes</span>';
                            }
                            return ServerSyncShared.escapeHtml(item.SourceSizeFormatted || '');
                        }
                    },
                    {
                        key: 'Status',
                        label: 'Status',
                        type: 'status'
                    }
                ],

                selection: {
                    enabled: true,
                    idKey: 'SourceItemId',
                    onSelectionChange: function(selectedIds) {
                        self.updateBulkActionsVisibility(selectedIds.length);
                    }
                },

                pagination: {
                    pageSize: 50
                },

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

                actions: {
                    onRowClick: function(item) {
                        self.showItemDetail(item.SourceItemId);
                    },
                    onReload: function() {
                        self.loadSyncStatus();
                        self.loadHealthStats();
                    }
                },

                getDisplayStatus: function(item) {
                    return self.getDisplayStatus(item);
                },

                getStatusClass: function(item) {
                    return self.getStatusClass(item);
                },

                emptyState: {
                    message: 'No content items found. Run a refresh to scan for content.'
                }
            });

            this._bindModuleEvents();
            this._injectBulkActions();
        },

        // Bind click handlers for action buttons and modal buttons
        _bindModuleEvents: function() {
            var self = this;
            var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler, 'SyncTableModule'); };

            // Action bar buttons
            bind('btnRefreshItems', function() { self.refreshSyncTable(); });
            bind('btnTriggerSync', function() { self.triggerSync(); });
            bind('btnRetryErrors', function() { self.retryErrors(); });

            // Modal action buttons
            bind('btnModalIgnore', function() { self.modalIgnore(); });
            bind('btnModalQueue', function() { self.modalQueue(); });
            bind('btnModalMarkSynced', function() { self.modalMarkSynced(); });
            bind('btnModalDelete', function() { self.modalDelete(); });
            bind('btnModalClose', function() { self.closeModal(); });
        },

        // Inject bulk-action buttons (Ignore, Queue, Delete) into the PaginatedTable header
        _injectBulkActions: function() {
            var self = this;
            var bulkContainer = this.table.getBulkActionsContainer();
            if (!bulkContainer) return;

            bulkContainer.innerHTML =
                '<button is="emby-button" type="button" id="btnBulkIgnore" class="raised pt-bulk-icon-btn" title="Ignore" disabled><span class="material-icons">block</span></button>' +
                '<button is="emby-button" type="button" id="btnBulkMarkSynced" class="raised pt-bulk-icon-btn" title="Mark Synced" disabled><span class="material-icons">check_circle</span></button>' +
                '<button is="emby-button" type="button" id="btnBulkQueue" class="raised button-primary pt-bulk-icon-btn" title="Queue" disabled><span class="material-icons">playlist_add</span></button>' +
                '<button is="emby-button" type="button" id="btnBulkDelete" class="raised button-destructive pt-bulk-icon-btn" title="Delete from local server only" disabled><span class="material-icons">delete</span></button>';

            bulkContainer.querySelector('#btnBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
            bulkContainer.querySelector('#btnBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
            bulkContainer.querySelector('#btnBulkMarkSynced').addEventListener('click', function() { self.bulkMarkSynced(); });
            bulkContainer.querySelector('#btnBulkDelete').addEventListener('click', function() { self.bulkDelete(); });
        },

        // Fetch server capabilities (e.g. whether item deletion is supported)
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

        // Show or hide delete buttons based on server capability
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

        // Load health dashboard data: last sync time, library count, and pending download size
        loadHealthStats: function() {
            return Promise.all([
                ServerSyncShared.apiRequest('Stats', 'GET'),
                ServerSyncShared.getConfig(),
                ServerSyncShared.apiRequest('PendingSize', 'GET')
            ]).then(function(results) {
                var stats = results[0];
                var config = results[1];
                var pendingSizeData = results[2];

                var lastSyncEl = view.querySelector('#healthLastSync');
                if (stats.LastSyncEndTime) {
                    var lastSync = new Date(stats.LastSyncEndTime);
                    lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(lastSync);
                    lastSyncEl.className = 'healthValue success';
                } else {
                    lastSyncEl.textContent = 'Never';
                    lastSyncEl.className = 'healthValue';
                }

                var libraryCountEl = view.querySelector('#healthLibraryCount');
                var libraryMappings = config.LibraryMappings || [];
                libraryCountEl.textContent = libraryMappings.length;
                libraryCountEl.className = libraryMappings.length > 0 ? 'healthValue success' : 'healthValue warning';

                var pendingCountEl = view.querySelector('#healthPendingCount');
                if (pendingSizeData && typeof pendingSizeData.TotalPendingBytes === 'number') {
                    pendingCountEl.textContent = ServerSyncShared.formatSize(pendingSizeData.TotalPendingBytes);
                    pendingCountEl.className = pendingSizeData.TotalPendingBytes > 0 ? 'healthValue warning' : 'healthValue';
                } else {
                    pendingCountEl.textContent = '0 B';
                    pendingCountEl.className = 'healthValue';
                }
            }).catch(function() {
                // Ignore errors
            });
        },

        // Fetch status counts from the API and update all status cards and tooltips
        loadSyncStatus: function() {
            return ServerSyncShared.apiRequest('Status', 'GET').then(function(status) {
                var syncedCount = status.Synced || 0;
                var queuedCount = status.Queued || 0;
                var erroredCount = status.Errored || 0;
                var ignoredCount = status.Ignored || 0;
                var pendingDownloadCount = status.PendingDownload || 0;
                var pendingReplacementCount = status.PendingReplacement || 0;
                var pendingDeletionCount = status.PendingDeletion || 0;
                var deletingCount = status.Deleting || 0;

                var totalPendingCount = pendingDownloadCount + pendingReplacementCount + pendingDeletionCount;

                view.querySelector('#syncedCount').textContent = syncedCount;
                view.querySelector('#statusGroupSynced').setAttribute('title', 'Synced: ' + syncedCount);

                view.querySelector('#pendingCount').textContent = totalPendingCount;
                view.querySelector('#statusGroupPending').setAttribute('title', 'Pending: ' + totalPendingCount + ' (Download: ' + pendingDownloadCount + ', Replace: ' + pendingReplacementCount + ', Delete: ' + pendingDeletionCount + ')');

                view.querySelector('#queuedCount').textContent = queuedCount;
                view.querySelector('#statusGroupQueued').setAttribute('title', 'Queued: ' + queuedCount);

                view.querySelector('#erroredCount').textContent = erroredCount;
                view.querySelector('#statusGroupErrored').setAttribute('title', 'Errored: ' + erroredCount);

                view.querySelector('#ignoredCount').textContent = ignoredCount;
                view.querySelector('#statusGroupIgnored').setAttribute('title', 'Ignored: ' + ignoredCount);

                view.querySelector('#pendingDownloadCount').textContent = pendingDownloadCount;
                view.querySelector('#statusGroupPendingDownload').setAttribute('title', 'Pending Download: ' + pendingDownloadCount);

                view.querySelector('#pendingReplacementCount').textContent = pendingReplacementCount;
                view.querySelector('#statusGroupPendingReplacement').setAttribute('title', 'Pending Replacement: ' + pendingReplacementCount);

                view.querySelector('#pendingDeletionCount').textContent = pendingDeletionCount;
                view.querySelector('#statusGroupPendingDeletion').setAttribute('title', 'Pending Deletion: ' + pendingDeletionCount);

                view.querySelector('#deletingCount').textContent = deletingCount;
                view.querySelector('#statusGroupDeleting').setAttribute('title', 'Deleting: ' + deletingCount);

                var retryBtn = view.querySelector('#btnRetryErrors');
                if (erroredCount > 0) {
                    retryBtn.classList.remove('hidden');
                } else {
                    retryBtn.classList.add('hidden');
                }
            }).catch(function() {
                // Status endpoint not available yet
            });
        },

        // Reload the PaginatedTable data from the API
        loadSyncItems: function() {
            return this.table.reload();
        },

        // Start the content download task, then poll for progress
        triggerSync: function() {
            var self = this;
            var btn = view.querySelector('#btnTriggerSync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerSync', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncDownloadContent', 'Sync', function() {
                    self.loadSyncStatus();
                    self.loadSyncItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start sync task');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            });
        },

        // Start the table refresh task (re-scans source server), then poll for progress
        refreshSyncTable: function() {
            var self = this;
            var btn = view.querySelector('#btnRefreshItems');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerRefresh', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncUpdateTables', 'Refresh', function() {
                    self.loadSyncStatus();
                    self.loadSyncItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        // Re-queue all errored items for retry
        retryErrors: function() {
            var self = this;

            ServerSyncShared.apiRequest('RetryErroredItems', 'POST', {}).then(function() {
                ServerSyncShared.showAlert('Errored items queued for retry');
                self.loadSyncStatus();
                self.loadSyncItems();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to retry errored items');
            });
        },

        // Enable/disable bulk action buttons based on selection count
        updateBulkActionsVisibility: function(count) {
            var hasSelection = count > 0;
            var ignoreBtn = view.querySelector('#btnBulkIgnore');
            var queueBtn = view.querySelector('#btnBulkQueue');
            var markSyncedBtn = view.querySelector('#btnBulkMarkSynced');
            var deleteBtn = view.querySelector('#btnBulkDelete');

            if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
            if (queueBtn) queueBtn.disabled = !hasSelection;
            if (markSyncedBtn) markSyncedBtn.disabled = !hasSelection;
            if (deleteBtn) deleteBtn.disabled = !hasSelection;
        },

        // Bulk ignore all selected items
        bulkIgnore: function() {
            this.bulkAction('IgnoreItems');
        },

        // Bulk queue selected items (excludes pending-deletion items which cannot be queued)
        bulkQueue: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            var items = this.table.getItems();
            var filteredIds = ids.filter(function(id) {
                var item = items.find(function(i) { return i.SourceItemId === id; });
                if (item && item.Status === 'Pending' && item.PendingType === 'Deletion') {
                    return false;
                }
                return true;
            });

            if (filteredIds.length === 0) {
                ServerSyncShared.showAlert('No items to queue (deletion items cannot be queued)');
                return;
            }

            ServerSyncShared.apiRequest('QueueItems', 'POST', { SourceItemIds: filteredIds }).then(function() {
                self.table.clearSelection();
                self.loadSyncStatus();
                self.loadSyncItems();
                ServerSyncShared.showAlert(filteredIds.length + ' item(s) queued');
            }).catch(function(err) {
                console.error('Bulk queue failed:', err);
                ServerSyncShared.showAlert('Failed to queue items');
            });
        },

        // Bulk mark selected items as synced (verifies local files exist on server)
        bulkMarkSynced: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest('MarkSynced', 'POST', { SourceItemIds: ids }).then(function(result) {
                self.table.clearSelection();
                self.loadSyncStatus();
                self.loadSyncItems();
                var msg = (result.Synced || 0) + ' item(s) marked as synced';
                if (result.NotFound > 0) {
                    msg += ', ' + result.NotFound + ' local file(s) not found';
                }
                ServerSyncShared.showAlert(msg);
            }).catch(function(err) {
                console.error('Bulk mark synced failed:', err);
                ServerSyncShared.showAlert('Failed to mark items as synced');
            });
        },

        // Bulk delete selected items from the local server (requires confirmation)
        bulkDelete: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            if (!confirm('Delete ' + ids.length + ' item(s) from the local server? This cannot be undone.')) {
                return;
            }

            ServerSyncShared.apiRequest('DeleteLocalItems', 'POST', { SourceItemIds: ids }).then(function(result) {
                self.table.clearSelection();
                self.loadSyncStatus();
                self.loadSyncItems();
                if (result && result.Deleted > 0) {
                    ServerSyncShared.showAlert('Deleted ' + result.Deleted + ' item(s)');
                }
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to delete items');
            });
        },

        // Generic bulk action: send selected IDs to the given API endpoint
        bulkAction: function(endpoint) {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest(endpoint, 'POST', { SourceItemIds: ids }).then(function() {
                self.table.clearSelection();
                self.loadSyncStatus();
                self.loadSyncItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) updated');
            }).catch(function(err) {
                console.error('Bulk action failed:', err);
                ServerSyncShared.showAlert('Failed to update items');
            });
        },

        // Return human-readable status text (e.g. "Pending Download" for pending subtypes)
        getDisplayStatus: function(item) {
            if (item.Status === 'Pending' && item.PendingType) {
                return 'Pending ' + item.PendingType;
            }
            return item.Status;
        },

        // Return CSS class for status badge (e.g. "Pending-Download" for pending subtypes)
        getStatusClass: function(item) {
            if (item.Status === 'Pending' && item.PendingType) {
                return 'Pending-' + item.PendingType;
            }
            return item.Status;
        },

        // Open the detail modal for a content item — populates all fields from the item data
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

            var sourceServerName = (self.currentConfig && self.currentConfig.SourceServerName) || 'Source';
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
                queueBtnSpan.textContent = 'Re-sync';
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

        // Close the content detail modal and refresh the table
        closeModal: function() {
            view.querySelector('#itemDetailModal').classList.add('hidden');
            this.currentModalItem = null;
            this.table.refresh();
            this.loadHealthStats();
        },

        // Set the current modal item to Ignored status
        modalIgnore: function() {
            if (this.currentModalItem) {
                this.updateItemStatus(this.currentModalItem.SourceItemId, 'Ignored');
            }
        },

        // Queue the current modal item for sync (blocks pending-deletion items)
        modalQueue: function() {
            if (this.currentModalItem) {
                if (this.currentModalItem.Status === 'Pending' && this.currentModalItem.PendingType === 'Deletion') {
                    return;
                }
                this.updateItemStatus(this.currentModalItem.SourceItemId, 'Queued');
            }
        },

        // Mark the current modal item as synced (verifies local file exists on server)
        modalMarkSynced: function() {
            var self = this;
            if (!this.currentModalItem) return;

            ServerSyncShared.apiRequest('MarkSynced', 'POST', { SourceItemIds: [this.currentModalItem.SourceItemId] }).then(function(result) {
                self.closeModal();
                self.loadSyncStatus();
                self.loadSyncItems();
                if (result && result.Synced > 0) {
                    ServerSyncShared.showAlert('Item marked as synced');
                } else {
                    ServerSyncShared.showAlert('Local file not found - cannot mark as synced');
                }
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to mark item as synced');
            });
        },

        // Delete the current modal item from the local server (requires confirmation)
        modalDelete: function() {
            var self = this;
            if (!this.currentModalItem) return;

            var fileName = ServerSyncShared.getFileName(this.currentModalItem.LocalPath || this.currentModalItem.SourcePath);
            if (!confirm('Delete "' + fileName + '" from the local server? This cannot be undone.')) {
                return;
            }

            ServerSyncShared.apiRequest('DeleteLocalItems', 'POST', { SourceItemIds: [this.currentModalItem.SourceItemId] }).then(function() {
                self.closeModal();
                self.loadSyncStatus();
                self.loadSyncItems();
                ServerSyncShared.showAlert('Item deleted');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to delete item');
            });
        },

        // Send a status update for a single item, then close modal and refresh
        updateItemStatus: function(sourceItemId, status) {
            var self = this;

            ServerSyncShared.apiRequest('UpdateItemStatus', 'POST', { SourceItemId: sourceItemId, Status: status }).then(function() {
                self.closeModal();
                self.loadSyncStatus();
                self.loadSyncItems();
                ServerSyncShared.showAlert('Item status updated to ' + status);
            }).catch(function(err) {
                console.error('Failed to update item status:', err);
                ServerSyncShared.showAlert('Failed to update item status');
            });
        },

        // Show/hide pending subtype filters and status cards based on approval mode settings
        updatePendingFilterVisibility: function(config) {
            var downloadMode = config.DownloadNewContentMode || 'Enabled';
            var replaceMode = config.ReplaceExistingContentMode || 'Enabled';
            var deleteMode = config.DeleteMissingContentMode || 'Disabled';

            var showPendingDownload = downloadMode === 'RequireApproval';
            var showPendingReplace = replaceMode === 'RequireApproval';
            var showPendingDelete = deleteMode === 'RequireApproval';

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
        }
    };

    // ============================================
    // CONTENT PAGE CONTROLLER
    // ============================================

    var ContentPageController = {
        // Initialize the content view: load capabilities, config, status, items, and health
        init: function() {
            SyncTableModule.init();
            SyncTableModule.loadCapabilities();

            ServerSyncShared.fetchLocalServerName();

            ServerSyncShared.getConfig().then(function(config) {
                SyncTableModule.currentConfig = config;
                SyncTableModule.updatePendingFilterVisibility(config);
            }).catch(function() {
                // Config fetch failed — continue without pending filter visibility
            });

            SyncTableModule.loadSyncStatus();
            SyncTableModule.loadSyncItems();
            SyncTableModule.loadHealthStats();
        }
    };

    // ============================================
    // HISTORY SYNC TABLE MODULE
    // ============================================

    var HistorySyncTableModule = {
        table: null,            // PaginatedTable instance
        currentModalItem: null, // Item shown in the detail modal
        currentConfig: null,    // Cached plugin configuration (includes UserMappings)
        _initialized: false,    // Prevents duplicate initialization

        // Create the PaginatedTable, bind action buttons, and inject bulk-action buttons
        init: function(config) {
            if (this._initialized) {
                return;
            }
            this._initialized = true;

            var self = this;
            self.currentConfig = config;

            this.table = createPaginatedTable(view, ServerSyncShared, {
                containerId: 'historyItemsTableContainer',
                endpoint: 'HistoryItems',

                columns: [
                    {
                        key: 'name',
                        label: 'Item',
                        type: 'custom',
                        className: 'pt-cell-with-thumb',
                        render: function(item) {
                            var itemName = item.ItemName || 'Unknown';
                            var userMapping = self.findUserMapping(item.SourceUserId, item.LocalUserId);
                            var sourceUserName = userMapping ? userMapping.SourceUserName : 'Unknown';
                            var localUserName = userMapping ? userMapping.LocalUserName : 'Unknown';
                            var userDisplay = sourceUserName + ' \u2192 ' + localUserName;

                            var errorPreview = '';
                            if (item.Status === 'Errored' && item.ErrorMessage) {
                                errorPreview = '<div class="syncItemError" title="' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
                            }

                            return ServerSyncShared.renderItemThumb(item.SourceServerUrl, item.SourceServerApiKey, item.SourceItemId) +
                                '<div class="syncItemInfo">' +
                                '<div class="syncItemName" title="' + ServerSyncShared.escapeHtml(itemName) + '">' +
                                ServerSyncShared.escapeHtml(itemName) + '</div>' +
                                '<div class="syncItemPath">' + ServerSyncShared.escapeHtml(userDisplay) + '</div>' +
                                errorPreview +
                                '</div>';
                        }
                    },
                    {
                        key: 'details',
                        label: 'Changes',
                        type: 'custom',
                        className: 'pt-cell-details',
                        render: function(item) {
                            if (!item.HasChanges) {
                                return '<span style="opacity: 0.5;">No changes</span>';
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
                    {
                        key: 'Status',
                        label: 'Status',
                        type: 'status'
                    }
                ],

                selection: {
                    enabled: true,
                    idKey: 'Id',
                    onSelectionChange: function(selectedIds) {
                        self.updateBulkActionsVisibility(selectedIds.length);
                    }
                },

                pagination: {
                    pageSize: 50
                },

                filters: {
                    options: [
                        { value: 'Synced', label: 'Synced' },
                        { value: 'Queued', label: 'Queued' },
                        { value: 'Pending', label: 'Pending' },
                        { value: 'Errored', label: 'Errored' },
                        { value: 'Ignored', label: 'Ignored' }
                    ],
                    buildParams: function(filterValue) {
                        return { status: filterValue };
                    }
                },

                actions: {
                    onRowClick: function(item) {
                        self.showItemDetail(item.Id);
                    },
                    onReload: function() {
                        self.loadHistoryStatus();
                        self.loadHealthStats();
                    }
                },

                emptyState: {
                    message: 'No history items found. Run a refresh to scan for watch history.'
                }
            });

            this._bindModuleEvents();
            this._injectBulkActions();
        },

        // Bind click handlers for action buttons and modal buttons
        _bindModuleEvents: function() {
            var self = this;
            var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler, 'HistorySyncTableModule'); };

            // Action bar buttons
            bind('btnRefreshHistoryItems', function() { self.refreshHistoryTable(); });
            bind('btnTriggerHistorySync', function() { self.triggerHistorySync(); });
            bind('btnRetryHistoryErrors', function() { self.retryErrors(); });

            // Modal action buttons
            bind('btnHistoryModalIgnore', function() { self.modalIgnore(); });
            bind('btnHistoryModalQueue', function() { self.modalQueue(); });
            bind('btnHistoryModalClose', function() { self.closeModal(); });
        },

        // Inject bulk-action buttons (Ignore, Queue) into the PaginatedTable header
        _injectBulkActions: function() {
            var self = this;
            var bulkContainer = this.table.getBulkActionsContainer();
            if (!bulkContainer) return;

            bulkContainer.innerHTML =
                '<button is="emby-button" type="button" id="btnHistoryBulkIgnore" class="raised pt-bulk-icon-btn" title="Ignore" disabled><span class="material-icons">block</span></button>' +
                '<button is="emby-button" type="button" id="btnHistoryBulkQueue" class="raised button-primary pt-bulk-icon-btn" title="Queue" disabled><span class="material-icons">playlist_add</span></button>';

            view.querySelector('#btnHistoryBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
            view.querySelector('#btnHistoryBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
        },

        // Fetch status counts and update status cards and tooltips
        loadHistoryStatus: function() {
            return ServerSyncShared.apiRequest('HistoryStatus', 'GET').then(function(status) {
                view.querySelector('#historySyncedCount').textContent = status.Synced || 0;
                view.querySelector('#historyQueuedCount').textContent = status.Queued || 0;
                view.querySelector('#historyErroredCount').textContent = status.Errored || 0;
                view.querySelector('#historyIgnoredCount').textContent = status.Ignored || 0;

                view.querySelector('#historyStatusGroupSynced').setAttribute('title', 'Synced: ' + (status.Synced || 0));
                view.querySelector('#historyStatusGroupQueued').setAttribute('title', 'Queued: ' + (status.Queued || 0));
                view.querySelector('#historyStatusGroupErrored').setAttribute('title', 'Errored: ' + (status.Errored || 0));
                view.querySelector('#historyStatusGroupIgnored').setAttribute('title', 'Ignored: ' + (status.Ignored || 0));

                var retryBtn = view.querySelector('#btnRetryHistoryErrors');
                if ((status.Errored || 0) > 0) {
                    retryBtn.classList.remove('hidden');
                } else {
                    retryBtn.classList.add('hidden');
                }
            }).catch(function() {
                // Status endpoint not available yet
            });
        },

        // Load health dashboard: last sync time, user count, and library count
        loadHealthStats: function() {
            return ServerSyncShared.getConfig().then(function(config) {
                var lastSyncEl = view.querySelector('#historyHealthLastSync');
                if (config.LastHistorySyncTime) {
                    var lastSync = new Date(config.LastHistorySyncTime);
                    lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(lastSync);
                    lastSyncEl.className = 'healthValue success';
                } else {
                    lastSyncEl.textContent = 'Never';
                    lastSyncEl.className = 'healthValue';
                }

                var userCountEl = view.querySelector('#historyHealthUserCount');
                var enabledUsers = (config.UserMappings || []).filter(function(m) { return m.IsEnabled; }).length;
                userCountEl.textContent = enabledUsers;
                userCountEl.className = enabledUsers > 0 ? 'healthValue success' : 'healthValue warning';

                var libraryCountEl = view.querySelector('#historyHealthLibraryCount');
                var libraryMappings = config.LibraryMappings || [];
                libraryCountEl.textContent = libraryMappings.length;
                libraryCountEl.className = libraryMappings.length > 0 ? 'healthValue success' : 'healthValue warning';
            }).catch(function() {
                // Ignore errors
            });
        },

        // Reload the PaginatedTable data from the API
        loadHistoryItems: function() {
            return this.table.reload();
        },

        // Start the history table refresh task, then poll for progress
        refreshHistoryTable: function() {
            var self = this;
            var btn = view.querySelector('#btnRefreshHistoryItems');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerHistoryRefresh', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncRefreshHistoryTable', 'Refresh', function() {
                    self.loadHistoryStatus();
                    self.loadHistoryItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start history refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        // Start the history sync task, then poll for progress
        triggerHistorySync: function() {
            var self = this;
            var btn = view.querySelector('#btnTriggerHistorySync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerHistorySync', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncMissingHistory', 'Sync', function() {
                    self.loadHistoryStatus();
                    self.loadHistoryItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start history sync task');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            });
        },

        // Re-queue all errored history items for retry (empty Ids + Status filter = retry all)
        retryErrors: function() {
            var self = this;

            ServerSyncShared.apiRequest('HistoryItems/Queue', 'POST', { Ids: [], Status: 'Errored' }).then(function() {
                ServerSyncShared.showAlert('Errored history items queued for retry');
                self.loadHistoryStatus();
                self.loadHistoryItems();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to retry errored items');
            });
        },

        // Enable/disable bulk action buttons based on selection count
        updateBulkActionsVisibility: function(count) {
            var hasSelection = count > 0;
            var ignoreBtn = view.querySelector('#btnHistoryBulkIgnore');
            var queueBtn = view.querySelector('#btnHistoryBulkQueue');

            if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
            if (queueBtn) queueBtn.disabled = !hasSelection;
        },

        // Bulk ignore all selected history items
        bulkIgnore: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest('HistoryItems/Ignore', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadHistoryStatus();
                self.loadHistoryItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) ignored');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to ignore items');
            });
        },

        // Bulk queue all selected history items
        bulkQueue: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest('HistoryItems/Queue', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadHistoryStatus();
                self.loadHistoryItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) queued');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to queue items');
            });
        },

        // Open the detail modal for a history item — populates user info, comparison table, etc.
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

            var sourceServerName = (self.currentConfig && self.currentConfig.SourceServerName) || 'Source';
            var localServerName = ServerSyncShared.localServerName || 'Local';
            view.querySelector('#historyModalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;

            if (item.LastSyncTime) {
                view.querySelector('#historyModalLastSync').textContent =
                    ServerSyncShared.formatRelativeTime(new Date(item.LastSyncTime));
            } else {
                view.querySelector('#historyModalLastSync').textContent = '-';
            }

            var errorSection = view.querySelector('#historyModalErrorSection');
            if (item.Status === 'Errored' && item.ErrorMessage) {
                view.querySelector('#historyModalError').textContent = item.ErrorMessage;
                errorSection.classList.remove('hidden');
            } else {
                errorSection.classList.add('hidden');
            }

            var userMapping = self.findUserMapping(item.SourceUserId, item.LocalUserId);
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

        // Set a comparison table cell value with type-specific formatting
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

        // Convert Jellyfin ticks to human-readable timestamp (H:MM:SS or M:SS)
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

        // Format a date string as relative time (e.g. "5 minutes ago")
        formatDate: function(dateStr) {
            if (!dateStr) return '-';
            try {
                return ServerSyncShared.formatRelativeTime(new Date(dateStr));
            } catch (e) {
                return dateStr;
            }
        },

        // Highlight a comparison table row if the merged value differs from the local value
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

        // Look up a user mapping from config by source or local user ID
        findUserMapping: function(sourceUserId, localUserId) {
            if (!this.currentConfig || !this.currentConfig.UserMappings) return null;
            return this.currentConfig.UserMappings.find(function(m) {
                return m.SourceUserId === sourceUserId || m.LocalUserId === localUserId;
            });
        },

        // Close the history detail modal and refresh the table
        closeModal: function() {
            view.querySelector('#historyItemDetailModal').classList.add('hidden');
            this.currentModalItem = null;
            this.table.refresh();
            this.loadHistoryStatus();
        },

        // Set the current modal item to Ignored status
        modalIgnore: function() {
            if (this.currentModalItem) {
                this.updateItemStatus(this.currentModalItem.Id, 'Ignored');
            }
        },

        // Queue the current modal item for sync
        modalQueue: function() {
            if (this.currentModalItem) {
                this.updateItemStatus(this.currentModalItem.Id, 'Queued');
            }
        },

        // Send a status update for a single item, then close modal and refresh
        updateItemStatus: function(itemId, status) {
            var self = this;

            ServerSyncShared.apiRequest('HistoryItems/UpdateStatus', 'POST', { Id: itemId, Status: status }).then(function() {
                self.closeModal();
                self.loadHistoryStatus();
                self.loadHistoryItems();
                ServerSyncShared.showAlert('Item status updated to ' + status);
            }).catch(function(err) {
                console.error('Failed to update item status:', err);
                ServerSyncShared.showAlert('Failed to update item status');
            });
        }
    };

    // ============================================
    // HISTORY PAGE CONTROLLER
    // ============================================

    var HistoryPageController = {
        currentConfig: null,

        // Initialize the history view: fetch server name, then load config and data
        init: function() {
            this.loadConfig();
        },

        // Load config, pass to table module, then load status/items/health
        loadConfig: function() {
            var self = this;

            ServerSyncShared.fetchLocalServerName().then(function() {
                return ServerSyncShared.getConfig();
            }).then(function(config) {
                self.currentConfig = config;

                HistorySyncTableModule.currentConfig = config;
                HistorySyncTableModule.init(config);

                HistorySyncTableModule.loadHistoryStatus();
                HistorySyncTableModule.loadHistoryItems();
                HistorySyncTableModule.loadHealthStats();
            }).catch(function() {
                // Config fetch failed — initialize table without config
                HistorySyncTableModule.init(null);
                HistorySyncTableModule.loadHistoryStatus();
                HistorySyncTableModule.loadHistoryItems();
                HistorySyncTableModule.loadHealthStats();
            });
        }
    };

    // ============================================
    // METADATA SYNC TABLE MODULE
    // ============================================

    var MetadataSyncTableModule = {
        table: null,            // PaginatedTable instance
        currentModalItem: null, // Item shown in the detail modal (fetched via separate API call)
        currentConfig: null,    // Cached plugin configuration (includes sync category toggles)
        _initialized: false,    // Prevents duplicate initialization

        // Create the PaginatedTable, bind action buttons, and inject bulk-action buttons
        init: function(config) {
            if (this._initialized) {
                return;
            }
            this._initialized = true;

            var self = this;
            self.currentConfig = config;

            this.table = createPaginatedTable(view, ServerSyncShared, {
                containerId: 'metadataSyncItemsTableContainer',
                endpoint: 'MetadataItems',

                columns: [
                    {
                        key: 'item',
                        label: 'Item',
                        type: 'custom',
                        className: 'pt-cell-with-thumb',
                        render: function(item) {
                            var itemName = item.ItemName || 'Unknown';
                            var sourceLib = item.SourceLibraryName || 'Unknown';
                            var localLib = item.LocalLibraryName || 'Unknown';
                            var libraryDisplay = sourceLib + ' \u2192 ' + localLib;

                            var errorPreview = '';
                            if (item.Status === 'Errored' && item.ErrorMessage) {
                                errorPreview = '<div class="syncItemError" title="' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
                            }

                            return ServerSyncShared.renderItemThumb(item.SourceServerUrl, item.SourceServerApiKey, item.SourceItemId) +
                                '<div class="syncItemInfo">' +
                                '<div class="syncItemName" title="' + ServerSyncShared.escapeHtml(itemName) + '">' +
                                ServerSyncShared.escapeHtml(itemName) + '</div>' +
                                '<div class="syncItemPath">' + ServerSyncShared.escapeHtml(libraryDisplay) + '</div>' +
                                errorPreview +
                                '</div>';
                        }
                    },
                    {
                        key: 'changes',
                        label: 'Changes',
                        type: 'custom',
                        className: 'pt-cell-details',
                        render: function(item) {
                            if (!item.HasChanges) {
                                return '<span style="opacity: 0.5;">No changes</span>';
                            }
                            return ServerSyncShared.escapeHtml(item.ChangesSummary || 'Changes pending');
                        }
                    },
                    {
                        key: 'Status',
                        label: 'Status',
                        type: 'status'
                    }
                ],

                selection: {
                    enabled: true,
                    idKey: 'Id',
                    onSelectionChange: function(selectedIds) {
                        self.updateBulkActionsVisibility(selectedIds.length);
                    }
                },

                pagination: {
                    pageSize: 50
                },

                filters: {
                    options: [
                        { value: 'Synced', label: 'Synced' },
                        { value: 'Queued', label: 'Queued' },
                        { value: 'Errored', label: 'Errored' },
                        { value: 'Ignored', label: 'Ignored' }
                    ],
                    buildParams: function(filterValue) {
                        return { status: filterValue };
                    }
                },

                search: {
                    placeholder: 'Search items...'
                },

                actions: {
                    onRowClick: function(item) {
                        self.showItemDetail(item.Id);
                    },
                    onReload: function() {
                        self.loadMetadataStatus();
                        self.loadHealthStats();
                    }
                },

                emptyState: {
                    message: 'No metadata items found. Run a refresh to scan for metadata.'
                }
            });

            this._bindModuleEvents();
            this._injectBulkActions();
        },

        // Bind click handlers for action buttons and modal buttons
        _bindModuleEvents: function() {
            var self = this;
            var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler, 'MetadataSyncTableModule'); };

            bind('btnRefreshMetadataItems', function() { self.refreshMetadataTable(); });
            bind('btnTriggerMetadataSync', function() { self.triggerMetadataSync(); });
            bind('btnRetryMetadataErrors', function() { self.retryErrors(); });

            bind('btnMetadataSyncModalIgnore', function() { self.modalIgnore(); });
            bind('btnMetadataSyncModalQueue', function() { self.modalQueue(); });
            bind('btnMetadataSyncModalClose', function() { self.closeModal(); });
        },

        // Inject bulk-action buttons (Ignore, Queue) into the PaginatedTable header
        _injectBulkActions: function() {
            var self = this;
            var bulkContainer = this.table.getBulkActionsContainer();
            if (!bulkContainer) return;

            bulkContainer.innerHTML =
                '<button is="emby-button" type="button" id="btnMetadataBulkIgnore" class="raised pt-bulk-icon-btn" title="Ignore" disabled><span class="material-icons">block</span></button>' +
                '<button is="emby-button" type="button" id="btnMetadataBulkQueue" class="raised button-primary pt-bulk-icon-btn" title="Queue" disabled><span class="material-icons">playlist_add</span></button>';

            view.querySelector('#btnMetadataBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
            view.querySelector('#btnMetadataBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
        },

        // Fetch status counts and update status cards and tooltips
        loadMetadataStatus: function() {
            return ServerSyncShared.apiRequest('MetadataStatus', 'GET').then(function(status) {
                view.querySelector('#metadataSyncedCount').textContent = status.Synced || 0;
                view.querySelector('#metadataQueuedCount').textContent = status.Queued || 0;
                view.querySelector('#metadataErroredCount').textContent = status.Errored || 0;
                view.querySelector('#metadataIgnoredCount').textContent = status.Ignored || 0;

                view.querySelector('#metadataStatusGroupSynced').setAttribute('title', 'Synced: ' + (status.Synced || 0));
                view.querySelector('#metadataStatusGroupQueued').setAttribute('title', 'Queued: ' + (status.Queued || 0));
                view.querySelector('#metadataStatusGroupErrored').setAttribute('title', 'Errored: ' + (status.Errored || 0));
                view.querySelector('#metadataStatusGroupIgnored').setAttribute('title', 'Ignored: ' + (status.Ignored || 0));

                var retryBtn = view.querySelector('#btnRetryMetadataErrors');
                if ((status.Errored || 0) > 0) {
                    retryBtn.classList.remove('hidden');
                } else {
                    retryBtn.classList.add('hidden');
                }
            }).catch(function() {
                // Status endpoint not available yet
            });
        },

        // Load health dashboard: last sync time and library count
        loadHealthStats: function() {
            return Promise.all([
                ServerSyncShared.getConfig(),
                ServerSyncShared.apiRequest('MetadataStatus', 'GET')
            ]).then(function(results) {
                var config = results[0];

                var lastSyncEl = view.querySelector('#metadataHealthLastSync');
                if (config.LastMetadataSyncTime) {
                    var lastSync = new Date(config.LastMetadataSyncTime);
                    lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(lastSync);
                    lastSyncEl.className = 'healthValue success';
                } else {
                    lastSyncEl.textContent = 'Never';
                    lastSyncEl.className = 'healthValue';
                }

                var libraryCountEl = view.querySelector('#metadataHealthLibraryCount');
                var libraryMappings = config.LibraryMappings || [];
                libraryCountEl.textContent = libraryMappings.length;
                libraryCountEl.className = libraryMappings.length > 0 ? 'healthValue success' : 'healthValue warning';
            }).catch(function() {
                // Ignore errors
            });
        },

        // Reload the PaginatedTable data from the API
        loadMetadataItems: function() {
            return this.table.reload();
        },

        // Start the metadata table refresh task, then poll for progress
        refreshMetadataTable: function() {
            var self = this;
            var btn = view.querySelector('#btnRefreshMetadataItems');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerMetadataRefresh', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncRefreshMetadataTable', 'Refresh', function() {
                    self.loadMetadataStatus();
                    self.loadMetadataItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start metadata refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        // Start the metadata sync task, then poll for progress
        triggerMetadataSync: function() {
            var self = this;
            var btn = view.querySelector('#btnTriggerMetadataSync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerMetadataSync', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncMissingMetadata', 'Sync', function() {
                    self.loadMetadataStatus();
                    self.loadMetadataItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start metadata sync task');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            });
        },

        // Re-queue all errored metadata items for retry (empty Ids + Status filter = retry all)
        retryErrors: function() {
            var self = this;

            ServerSyncShared.apiRequest('MetadataItems/Queue', 'POST', { Ids: [], Status: 'Errored' }).then(function() {
                ServerSyncShared.showAlert('Errored metadata items queued for retry');
                self.loadMetadataStatus();
                self.loadMetadataItems();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to retry errored items');
            });
        },

        // Enable/disable bulk action buttons based on selection count
        updateBulkActionsVisibility: function(count) {
            var hasSelection = count > 0;
            var ignoreBtn = view.querySelector('#btnMetadataBulkIgnore');
            var queueBtn = view.querySelector('#btnMetadataBulkQueue');

            if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
            if (queueBtn) queueBtn.disabled = !hasSelection;
        },

        // Bulk ignore all selected metadata items
        bulkIgnore: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest('MetadataItems/Ignore', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadMetadataStatus();
                self.loadMetadataItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) ignored');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to ignore items');
            });
        },

        // Bulk queue all selected metadata items
        bulkQueue: function() {
            var self = this;
            var ids = this.table.getSelectedIds();
            if (ids.length === 0) return;

            ServerSyncShared.apiRequest('MetadataItems/Queue', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadMetadataStatus();
                self.loadMetadataItems();
                ServerSyncShared.showAlert(ids.length + ' item(s) queued');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to queue items');
            });
        },

        // Fetch full item detail from API and open the metadata detail modal
        showItemDetail: function(itemId) {
            var self = this;

            ServerSyncShared.apiRequest('MetadataItems/' + itemId).then(function(item) {
                if (!item) {
                    ServerSyncShared.showAlert('Item not found');
                    return;
                }

                self.currentModalItem = item;

                view.querySelector('#metadataSyncModalTitle').textContent = item.ItemName || 'Unknown';

                var statusBadge = view.querySelector('#metadataSyncModalStatusBadge');
                statusBadge.textContent = item.Status;
                statusBadge.className = 'itemModal-statusBadge ' + item.Status;

                var sourceServerName = (self.currentConfig && self.currentConfig.SourceServerName) || 'Source';
                var localServerName = ServerSyncShared.localServerName || 'Local';
                view.querySelector('#metadataSyncModalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;

                if (item.LastSyncTime) {
                    view.querySelector('#metadataSyncModalLastSync').textContent =
                        ServerSyncShared.formatRelativeTime(new Date(item.LastSyncTime));
                } else {
                    view.querySelector('#metadataSyncModalLastSync').textContent = '-';
                }

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
                console.error('Failed to load metadata item details:', err);
                ServerSyncShared.showAlert('Failed to load item details');
            });
        },

        // Build change summary badges (Metadata, Genres, Tags, Images, People, Studios)
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

            var sourceMetadata = this.parseJsonSafe(item.SourceMetadataValue) || {};
            var localMetadata = this.parseJsonSafe(item.LocalMetadataValue) || {};

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

        // Build the full comparison property table with section headers for each category
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

            var sourceMetadata = self.parseJsonSafe(item.SourceMetadataValue) || {};
            var localMetadata = self.parseJsonSafe(item.LocalMetadataValue) || {};
            var sourceImages = self.parseJsonSafe(item.SourceImagesValue);
            var localImages = self.parseJsonSafe(item.LocalImagesValue);
            var sourcePeople = self.parseJsonSafe(item.SourcePeopleValue);
            var localPeople = self.parseJsonSafe(item.LocalPeopleValue);
            var sourceStudios = self.parseJsonSafe(item.SourceStudiosValue);
            var localStudios = self.parseJsonSafe(item.LocalStudiosValue);

            if (metadataEnabled) {
                html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Metadata</td></tr>';
                html += self.buildCoreMetadataRows(sourceMetadata, localMetadata);
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
            }

            if (html === '') {
                html = '<tr><td colspan="4" style="text-align: center; opacity: 0.5;">No sync categories are enabled</td></tr>';
            }

            tbody.innerHTML = html;
        },

        // Safely parse a JSON string, returning null on failure
        parseJsonSafe: function(jsonString) {
            if (!jsonString) return null;
            try {
                return JSON.parse(jsonString);
            } catch (e) {
                console.error('Failed to parse JSON:', e);
                return null;
            }
        },

        // Build comparison rows for core metadata fields (Name, Overview, ratings, etc.) and provider IDs
        buildCoreMetadataRows: function(source, local) {
            var self = this;
            var html = '';

            var metadataFields = [
                { key: 'Name', label: 'Name' },
                { key: 'OriginalTitle', label: 'Original Title' },
                { key: 'SortName', label: 'Sort Name' },
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

        // Build count + items comparison rows for simple string arrays (Genres, Tags)
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

        // Build count + items comparison rows for people objects (extracts Name from each)
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

        // Format a metadata value for display based on field type (boolean, date, array, etc.)
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

        // Format a date string to ISO date only (YYYY-MM-DD)
        formatDateOnly: function(dateStr) {
            if (!dateStr) return '-';
            try {
                var date = new Date(dateStr);
                return date.toISOString().split('T')[0];
            } catch (e) {
                return ServerSyncShared.escapeHtml(String(dateStr));
            }
        },

        // Normalize a value to a canonical string for change detection comparison
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

        // Check if a value is null, undefined, empty string, or empty array
        isEmpty: function(value) {
            if (value === null || value === undefined) return true;
            if (value === '') return true;
            if (Array.isArray(value) && value.length === 0) return true;
            return false;
        },

        // Build comparison rows for image types (Primary, Backdrop, etc.) with size/count info
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

                var srcDisplay = self.formatImageDisplay(srcSize, srcCount);
                var localDisplay = self.formatImageDisplay(localSize, localCount);

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

        // Format image info for display: size with optional count (e.g. "1.5 MB (3)")
        formatImageDisplay: function(size, count) {
            if (count === 0) return '-';
            if (!size || size === 0) {
                return count === 1 ? '1 image' : count + ' images';
            }
            var sizeStr = ServerSyncShared.formatSize(size);
            if (count > 1) {
                return sizeStr + ' (' + count + ')';
            }
            return sizeStr;
        },

        // Close the metadata detail modal and refresh the table
        closeModal: function() {
            view.querySelector('#metadataSyncItemDetailModal').classList.add('hidden');
            this.currentModalItem = null;
            this.table.refresh();
            this.loadMetadataStatus();
            this.loadHealthStats();
        },

        // Set the current modal item to Ignored status
        modalIgnore: function() {
            if (this.currentModalItem) {
                this.updateItemStatus(this.currentModalItem.Id, 'Ignored');
            }
        },

        // Queue the current modal item for sync
        modalQueue: function() {
            if (this.currentModalItem) {
                this.updateItemStatus(this.currentModalItem.Id, 'Queued');
            }
        },

        // Send a status update for a single item, then close modal and refresh
        updateItemStatus: function(itemId, status) {
            var self = this;

            ServerSyncShared.apiRequest('MetadataItems/UpdateStatus', 'POST', { Id: itemId, Status: status }).then(function() {
                self.closeModal();
                self.loadMetadataStatus();
                self.loadMetadataItems();
                ServerSyncShared.showAlert('Item status updated to ' + status);
            }).catch(function(err) {
                console.error('Failed to update item status:', err);
                ServerSyncShared.showAlert('Failed to update item status');
            });
        }
    };

    // ============================================
    // METADATA PAGE CONTROLLER
    // ============================================

    var MetadataPageController = {
        currentConfig: null,

        // Initialize the metadata view: fetch server name, then load config and data
        init: function() {
            var self = this;
            self.loadConfig();
        },

        // Load config, pass to table module, then load status/items/health
        loadConfig: function() {
            var self = this;

            ServerSyncShared.fetchLocalServerName().then(function() {
                return ServerSyncShared.getConfig();
            }).then(function(config) {
                self.currentConfig = config;

                MetadataSyncTableModule.currentConfig = config;
                MetadataSyncTableModule.init(config);

                MetadataSyncTableModule.loadMetadataStatus();
                MetadataSyncTableModule.loadMetadataItems();
                MetadataSyncTableModule.loadHealthStats();
            }).catch(function() {
                // Config fetch failed — initialize table without config
                MetadataSyncTableModule.init(null);
                MetadataSyncTableModule.loadMetadataStatus();
                MetadataSyncTableModule.loadMetadataItems();
                MetadataSyncTableModule.loadHealthStats();
            });
        }
    };

    // ============================================
    // USER SYNC TABLE MODULE
    // ============================================

    var UserSyncTableModule = {
        table: null,             // PaginatedTable instance
        currentModalDetail: null,// Detail object from API (separate from list item — fetched per-user)
        currentConfig: null,     // Cached plugin configuration (includes sync category toggles)
        _initialized: false,     // Prevents duplicate initialization

        // Create the PaginatedTable, bind action buttons, and inject bulk-action buttons
        init: function(config) {
            if (this._initialized) {
                return;
            }
            this._initialized = true;

            var self = this;
            self.currentConfig = config;

            this.table = createPaginatedTable(view, ServerSyncShared, {
                containerId: 'userSyncItemsTableContainer',
                endpoint: 'UserSyncUsers',

                columns: [
                    {
                        key: 'user',
                        label: 'User',
                        type: 'custom',
                        className: 'pt-cell-with-thumb',
                        render: function(item) {
                            var sourceUserName = item.SourceUserName || 'Unknown';
                            var localUserName = item.LocalUserName || 'Unknown';
                            var sourceServerName = (self.currentConfig && self.currentConfig.SourceServerName) || 'Unknown';
                            var localServerName = ServerSyncShared.localServerName || 'Unknown';

                            var errorPreview = '';
                            if (item.OverallStatus === 'Errored' && item.ErrorMessage) {
                                errorPreview = '<div class="syncItemError" title="' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
                            }

                            return ServerSyncShared.renderUserThumb(item.SourceServerUrl, item.SourceServerApiKey, item.SourceUserId) +
                                '<div class="syncItemInfo">' +
                                '<div class="syncItemName">' + ServerSyncShared.escapeHtml(sourceUserName) + ' \u2192 ' + ServerSyncShared.escapeHtml(localUserName) + '</div>' +
                                '<div class="syncItemPath">' + ServerSyncShared.escapeHtml(sourceServerName) + ' \u2192 ' + ServerSyncShared.escapeHtml(localServerName) + '</div>' +
                                errorPreview +
                                '</div>';
                        }
                    },
                    {
                        key: 'changes',
                        label: 'Changes',
                        type: 'custom',
                        className: 'pt-cell-details',
                        render: function(item) {
                            if (!item.HasChanges) {
                                return '<span style="opacity: 0.5;">No Changes</span>';
                            }
                            return ServerSyncShared.escapeHtml(item.TotalChanges || 'Changes pending');
                        }
                    },
                    {
                        key: 'OverallStatus',
                        label: 'Status',
                        type: 'status'
                    }
                ],

                selection: {
                    enabled: true,
                    idKey: function(item) {
                        return item.SourceUserId + '|' + item.LocalUserId;
                    },
                    onSelectionChange: function(selectedIds) {
                        self.updateBulkActionsVisibility(selectedIds.length);
                    }
                },

                pagination: {
                    pageSize: 50
                },

                filters: {
                    options: [
                        { value: 'Synced', label: 'Synced' },
                        { value: 'Queued', label: 'Queued' },
                        { value: 'Errored', label: 'Errored' },
                        { value: 'Ignored', label: 'Ignored' }
                    ],
                    buildParams: function(filterValue) {
                        return { status: filterValue };
                    }
                },

                search: {
                    placeholder: 'Search users...'
                },

                actions: {
                    onRowClick: function(item) {
                        self.showUserDetail(item);
                    },
                    onReload: function() {
                        self.loadUserStatus();
                        self.loadHealthStats();
                    }
                },

                emptyState: {
                    message: 'No user sync items found. Run a refresh to scan for user data.'
                }
            });

            this._bindModuleEvents();
            this._injectBulkActions();
        },

        // Bind click handlers for action buttons and modal buttons
        _bindModuleEvents: function() {
            var self = this;
            var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler, 'UserSyncTableModule'); };

            bind('btnRefreshUserItems', function() { self.triggerRefresh(); });
            bind('btnTriggerUserSync', function() { self.triggerSync(); });
            bind('btnRetryUserErrors', function() { self.retryErrors(); });

            bind('btnUserSyncModalIgnore', function() { self.modalIgnore(); });
            bind('btnUserSyncModalQueue', function() { self.modalQueue(); });
            bind('btnUserSyncModalClose', function() { self.closeModal(); });
        },

        // Inject bulk-action buttons (Ignore, Queue) into the PaginatedTable header
        _injectBulkActions: function() {
            var self = this;
            var bulkContainer = this.table.getBulkActionsContainer();
            if (!bulkContainer) return;

            bulkContainer.innerHTML =
                '<button is="emby-button" type="button" id="btnUserBulkIgnore" class="raised pt-bulk-icon-btn" title="Ignore" disabled><span class="material-icons">block</span></button>' +
                '<button is="emby-button" type="button" id="btnUserBulkQueue" class="raised button-primary pt-bulk-icon-btn" title="Queue" disabled><span class="material-icons">playlist_add</span></button>';

            view.querySelector('#btnUserBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
            view.querySelector('#btnUserBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
        },

        // Fetch status counts and update status cards and tooltips
        loadUserStatus: function() {
            return ServerSyncShared.apiRequest('UserStatus', 'GET').then(function(status) {
                view.querySelector('#userSyncedCount').textContent = status.Synced || 0;
                view.querySelector('#userQueuedCount').textContent = status.Queued || 0;
                view.querySelector('#userErroredCount').textContent = status.Errored || 0;
                view.querySelector('#userIgnoredCount').textContent = status.Ignored || 0;

                view.querySelector('#userStatusGroupSynced').setAttribute('title', 'Synced: ' + (status.Synced || 0));
                view.querySelector('#userStatusGroupQueued').setAttribute('title', 'Queued: ' + (status.Queued || 0));
                view.querySelector('#userStatusGroupErrored').setAttribute('title', 'Errored: ' + (status.Errored || 0));
                view.querySelector('#userStatusGroupIgnored').setAttribute('title', 'Ignored: ' + (status.Ignored || 0));

                var retryBtn = view.querySelector('#btnRetryUserErrors');
                if ((status.Errored || 0) > 0) {
                    retryBtn.classList.remove('hidden');
                } else {
                    retryBtn.classList.add('hidden');
                }
            }).catch(function() {
                // Status endpoint not available yet
            });
        },

        // Load health dashboard: last sync time and total user count
        loadHealthStats: function() {
            return ServerSyncShared.apiRequest('UserStatus', 'GET').then(function(status) {
                if (status) {
                    var lastSyncEl = view.querySelector('#userHealthLastSync');
                    if (status.LastSyncTime) {
                        lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(new Date(status.LastSyncTime));
                        lastSyncEl.className = 'healthValue success';
                    } else {
                        lastSyncEl.textContent = 'Never';
                        lastSyncEl.className = 'healthValue';
                    }

                    var userCount = (status.Synced || 0) + (status.Queued || 0) + (status.Errored || 0) + (status.Ignored || 0);
                    var userCountEl = view.querySelector('#userHealthUserCount');
                    userCountEl.textContent = userCount;
                    userCountEl.className = userCount > 0 ? 'healthValue success' : 'healthValue warning';
                }
            }).catch(function() {
                // Ignore errors
            });
        },

        // Reload the PaginatedTable data from the API
        loadUserItems: function() {
            return this.table.reload();
        },

        // Start the user table refresh task, then poll for progress
        triggerRefresh: function() {
            var self = this;
            var btn = view.querySelector('#btnRefreshUserItems');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerUserRefresh', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncRefreshUserTable', 'Refresh', function() {
                    self.loadUserStatus();
                    self.loadUserItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start user refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        // Start the user sync task, then poll for progress
        triggerSync: function() {
            var self = this;
            var btn = view.querySelector('#btnTriggerUserSync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerUserSync', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncMissingUserData', 'Sync', function() {
                    self.loadUserStatus();
                    self.loadUserItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start user sync task');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            });
        },

        // Re-queue all errored user items for retry (empty Ids + Status filter = retry all)
        retryErrors: function() {
            var self = this;

            ServerSyncShared.apiRequest('UserItems/Queue', 'POST', { Ids: [], Status: 'Errored' }).then(function() {
                ServerSyncShared.showAlert('Errored user items queued for retry');
                self.loadUserStatus();
                self.loadUserItems();
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to retry errored items');
            });
        },

        // Enable/disable bulk action buttons based on selection count
        updateBulkActionsVisibility: function(count) {
            var hasSelection = count > 0;
            var ignoreBtn = view.querySelector('#btnUserBulkIgnore');
            var queueBtn = view.querySelector('#btnUserBulkQueue');

            if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
            if (queueBtn) queueBtn.disabled = !hasSelection;
        },

        // Bulk ignore selected users (composite key: "sourceId|localId")
        bulkIgnore: function() {
            var self = this;
            var selectedKeys = this.table.getSelectedIds();
            if (selectedKeys.length === 0) return;

            var userMappings = selectedKeys.map(function(key) {
                var parts = key.split('|');
                return { SourceUserId: parts[0], LocalUserId: parts[1] };
            });

            ServerSyncShared.apiRequest('UserSyncUsers/Ignore', 'POST', { UserMappings: userMappings }).then(function() {
                self.table.clearSelection();
                self.loadUserStatus();
                self.loadUserItems();
                ServerSyncShared.showAlert(selectedKeys.length + ' user(s) ignored');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to ignore users');
            });
        },

        // Bulk queue selected users (composite key: "sourceId|localId")
        bulkQueue: function() {
            var self = this;
            var selectedKeys = this.table.getSelectedIds();
            if (selectedKeys.length === 0) return;

            var userMappings = selectedKeys.map(function(key) {
                var parts = key.split('|');
                return { SourceUserId: parts[0], LocalUserId: parts[1] };
            });

            ServerSyncShared.apiRequest('UserSyncUsers/Queue', 'POST', { UserMappings: userMappings }).then(function() {
                self.table.clearSelection();
                self.loadUserStatus();
                self.loadUserItems();
                ServerSyncShared.showAlert(selectedKeys.length + ' user(s) queued');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to queue users');
            });
        },

        // Fetch full user detail from API and open the user detail modal
        showUserDetail: function(item) {
            var self = this;
            var sourceUserId = item.SourceUserId;
            var localUserId = item.LocalUserId;

            ServerSyncShared.apiRequest('UserSyncUsers/' + encodeURIComponent(sourceUserId) + '/' + encodeURIComponent(localUserId)).then(function(detail) {
                if (!detail) {
                    ServerSyncShared.showAlert('User not found');
                    return;
                }

                self.currentModalDetail = detail;

                view.querySelector('#userSyncModalTitle').textContent =
                    (detail.SourceUserName || 'Unknown') + ' \u2192 ' + (detail.LocalUserName || 'Unknown');

                var statusBadge = view.querySelector('#userSyncModalStatusBadge');
                statusBadge.textContent = detail.OverallStatus || 'Unknown';
                statusBadge.className = 'itemModal-statusBadge ' + (detail.OverallStatus || 'unknown');

                var sourceServerName = (self.currentConfig && self.currentConfig.SourceServerName) || 'Source';
                var localServerName = ServerSyncShared.localServerName || 'Local';
                view.querySelector('#userSyncModalServerMapping').textContent =
                    sourceServerName + ' \u2192 ' + localServerName;

                var infoGrid = view.querySelector('#userSyncModalInfoGrid');
                if (detail.LastSyncTime) {
                    infoGrid.classList.remove('hidden');
                    view.querySelector('#userSyncModalLastSync').textContent =
                        ServerSyncShared.formatRelativeTime(new Date(detail.LastSyncTime));
                } else {
                    infoGrid.classList.add('hidden');
                }

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
                console.error('Failed to load user detail:', err);
                ServerSyncShared.showAlert('Failed to load user details');
            });
        },

        // Create a section header row for the comparison table (e.g. "Policy", "Configuration")
        _createSectionHeader: function(title) {
            var row = document.createElement('tr');
            row.className = 'userSyncModal-sectionHeader';
            var cell = document.createElement('td');
            cell.colSpan = 4;
            cell.textContent = title;
            row.appendChild(cell);
            return row;
        },

        // Create a disabled/unavailable row for a sync category
        _createDisabledRow: function(category, message) {
            var row = document.createElement('tr');
            row.className = 'userSyncModal-disabledRow';
            var cell = document.createElement('td');
            cell.colSpan = 4;
            cell.innerHTML = '<span style="opacity: 0.5;">' + ServerSyncShared.escapeHtml(category) + ': ' + ServerSyncShared.escapeHtml(message) + '</span>';
            row.appendChild(cell);
            return row;
        },

        // Parse JSON values and add comparison rows for each property key
        _addPropertyRows: function(tbody, item) {
            var self = this;

            var sourceObj = self._parseJson(item.SourceValue);
            var localObj = self._parseJson(item.LocalValue);
            var mergedObj = self._parseJson(item.MergedValue);

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

        // Safely parse a JSON string, returning null on failure
        _parseJson: function(str) {
            if (!str || typeof str !== 'string') return null;
            try {
                return JSON.parse(str);
            } catch (e) {
                return null;
            }
        },

        // Add a comparison row for profile image (shows size info for source vs local)
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

        // Build change summary badges (Configuration, Policy, Image)
        buildChangesSummary: function(detail) {
            var container = view.querySelector('#userSyncModalChangesSummary');
            var config = this.currentConfig || {};
            var html = '';

            var policyEnabled = config.SyncUserPolicy !== false;
            var configurationEnabled = config.SyncUserConfiguration !== false;
            var profileImageEnabled = config.SyncUserProfileImage !== false;

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

        // Format a value for display in the comparison table (handles booleans, arrays, etc.)
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

        // Close the user detail modal and refresh the table
        closeModal: function() {
            view.querySelector('#userSyncItemDetailModal').classList.add('hidden');
            this.currentModalDetail = null;
            this.table.reload();
            this.loadUserStatus();
        },

        // Set the current modal user to Ignored status
        modalIgnore: function() {
            var self = this;
            var detail = self.currentModalDetail;
            if (!detail) return;

            ServerSyncShared.apiRequest('UserSyncUsers/Ignore', 'POST', {
                UserMappings: [{ SourceUserId: detail.SourceUserId, LocalUserId: detail.LocalUserId }]
            }).then(function() {
                self.closeModal();
                self.loadUserStatus();
                self.loadUserItems();
                ServerSyncShared.showAlert('User ignored');
            }).catch(function(err) {
                console.error('Failed to ignore user:', err);
                ServerSyncShared.showAlert('Failed to ignore user');
            });
        },

        // Queue the current modal user for sync
        modalQueue: function() {
            var self = this;
            var detail = self.currentModalDetail;
            if (!detail) return;

            ServerSyncShared.apiRequest('UserSyncUsers/Queue', 'POST', {
                UserMappings: [{ SourceUserId: detail.SourceUserId, LocalUserId: detail.LocalUserId }]
            }).then(function() {
                self.closeModal();
                self.loadUserStatus();
                self.loadUserItems();
                ServerSyncShared.showAlert('User queued');
            }).catch(function(err) {
                console.error('Failed to queue user:', err);
                ServerSyncShared.showAlert('Failed to queue user');
            });
        }
    };

    // ============================================
    // USERS PAGE CONTROLLER
    // ============================================

    var UsersPageController = {
        currentConfig: null,

        // Initialize the users view: fetch server name, then load config and data
        init: function() {
            var self = this;
            self.loadConfig();
        },

        // Load config, pass to table module, then load status/items/health
        loadConfig: function() {
            var self = this;

            ServerSyncShared.fetchLocalServerName().then(function() {
                return ServerSyncShared.getConfig();
            }).then(function(config) {
                self.currentConfig = config;

                UserSyncTableModule.currentConfig = config;
                UserSyncTableModule.init(config);

                UserSyncTableModule.loadUserStatus();
                UserSyncTableModule.loadUserItems();
                UserSyncTableModule.loadHealthStats();
            }).catch(function() {
                // Config fetch failed — initialize table without config
                UserSyncTableModule.init(null);
                UserSyncTableModule.loadUserStatus();
                UserSyncTableModule.loadUserItems();
                UserSyncTableModule.loadHealthStats();
            });
        }
    };

    // ============================================
    // PEOPLE SYNC TABLE MODULE
    // ============================================

    var PeopleSyncTableModule = {
        table: null,
        currentModalItem: null,
        currentConfig: null,
        _initialized: false,

        init: function(config) {
            if (this._initialized) return;
            this._initialized = true;

            var self = this;
            self.currentConfig = config;

            this.table = createPaginatedTable(view, ServerSyncShared, {
                containerId: 'peopleSyncItemsTableContainer',
                endpoint: 'PeopleItems',

                columns: [
                    {
                        key: 'person',
                        label: 'Person',
                        type: 'custom',
                        className: 'pt-cell-with-thumb',
                        render: function(item) {
                            var errorPreview = '';
                            if (item.Status === 'Errored' && item.ErrorMessage) {
                                errorPreview = '<div class="syncItemError" title="' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                                    ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
                            }
                            return ServerSyncShared.renderPersonThumb(item.SourceServerUrl, item.SourceServerApiKey, item.SourcePersonId) +
                                '<div class="syncItemInfo">' +
                                '<div class="syncItemName">' + ServerSyncShared.escapeHtml(item.PersonName || 'Unknown') + '</div>' +
                                errorPreview +
                                '</div>';
                        }
                    },
                    {
                        key: 'changes',
                        label: 'Changes',
                        type: 'custom',
                        className: 'pt-cell-details',
                        render: function(item) {
                            if (!item.HasChanges) {
                                return '<span style="opacity: 0.5;">No Changes</span>';
                            }
                            var badges = [];
                            if (item.HasMetadataChanges) badges.push('<span class="peopleSyncBadge has-changes">Metadata</span>');
                            if (item.HasImagesChanges) badges.push('<span class="peopleSyncBadge has-changes">Images</span>');
                            return badges.length > 0 ? badges.join(' ') : '<span style="opacity: 0.5;">No Changes</span>';
                        }
                    },
                    {
                        key: 'Status',
                        label: 'Status',
                        type: 'status'
                    }
                ],

                selection: {
                    enabled: true,
                    idKey: function(item) { return String(item.Id); },
                    onSelectionChange: function(selectedIds) {
                        self.updateBulkActionsVisibility(selectedIds.length);
                    }
                },

                pagination: { pageSize: 50 },

                filters: {
                    options: [
                        { value: 'Synced', label: 'Synced' },
                        { value: 'Queued', label: 'Queued' },
                        { value: 'Errored', label: 'Errored' },
                        { value: 'Ignored', label: 'Ignored' }
                    ],
                    buildParams: function(filterValue) {
                        return { status: filterValue };
                    }
                },

                search: { placeholder: 'Search people...' },

                actions: {
                    onRowClick: function(item) { self.showPersonDetail(item); },
                    onReload: function() {
                        self.loadPeopleStatus();
                        self.loadHealthStats();
                    }
                },

                emptyState: {
                    message: 'No people sync items found. Run a refresh to scan for people.'
                }
            });

            this._bindModuleEvents();
            this._injectBulkActions();
        },

        _bindModuleEvents: function() {
            var self = this;
            var bind = function(id, handler) { ServerSyncShared.bindClick(id, handler, 'PeopleSyncTableModule'); };

            bind('btnRefreshPeopleItems', function() { self.triggerRefresh(); });
            bind('btnTriggerPeopleSync', function() { self.triggerSync(); });
            bind('btnRetryPeopleErrors', function() { self.retryErrors(); });

            bind('btnPeopleSyncModalIgnore', function() { self.modalIgnore(); });
            bind('btnPeopleSyncModalQueue', function() { self.modalQueue(); });
            bind('btnPeopleSyncModalClose', function() { self.closeModal(); });
        },

        _injectBulkActions: function() {
            var self = this;
            var bulkContainer = this.table.getBulkActionsContainer();
            if (!bulkContainer) return;

            bulkContainer.innerHTML =
                '<button is="emby-button" type="button" id="btnPeopleBulkIgnore" class="raised pt-bulk-icon-btn" title="Ignore" disabled><span class="material-icons">block</span></button>' +
                '<button is="emby-button" type="button" id="btnPeopleBulkQueue" class="raised button-primary pt-bulk-icon-btn" title="Queue" disabled><span class="material-icons">playlist_add</span></button>';

            view.querySelector('#btnPeopleBulkIgnore').addEventListener('click', function() { self.bulkIgnore(); });
            view.querySelector('#btnPeopleBulkQueue').addEventListener('click', function() { self.bulkQueue(); });
        },

        loadPeopleStatus: function() {
            return ServerSyncShared.apiRequest('PeopleStatus', 'GET').then(function(status) {
                view.querySelector('#peopleSyncedCount').textContent = status.Synced || 0;
                view.querySelector('#peopleQueuedCount').textContent = status.Queued || 0;
                view.querySelector('#peopleErroredCount').textContent = status.Errored || 0;
                view.querySelector('#peopleIgnoredCount').textContent = status.Ignored || 0;

                view.querySelector('#peopleStatusGroupSynced').setAttribute('title', 'Synced: ' + (status.Synced || 0));
                view.querySelector('#peopleStatusGroupQueued').setAttribute('title', 'Queued: ' + (status.Queued || 0));
                view.querySelector('#peopleStatusGroupErrored').setAttribute('title', 'Errored: ' + (status.Errored || 0));
                view.querySelector('#peopleStatusGroupIgnored').setAttribute('title', 'Ignored: ' + (status.Ignored || 0));

                var retryBtn = view.querySelector('#btnRetryPeopleErrors');
                if ((status.Errored || 0) > 0) {
                    retryBtn.classList.remove('hidden');
                } else {
                    retryBtn.classList.add('hidden');
                }
            }).catch(function() {});
        },

        loadHealthStats: function() {
            return ServerSyncShared.apiRequest('PeopleStatus', 'GET').then(function(status) {
                if (status) {
                    var lastSyncEl = view.querySelector('#peopleHealthLastSync');
                    if (status.LastSyncTime) {
                        lastSyncEl.textContent = ServerSyncShared.formatRelativeTime(new Date(status.LastSyncTime));
                        lastSyncEl.className = 'healthValue success';
                    } else {
                        lastSyncEl.textContent = 'Never';
                        lastSyncEl.className = 'healthValue';
                    }

                    var personCount = status.PersonCount || 0;
                    var personCountEl = view.querySelector('#peopleHealthPersonCount');
                    personCountEl.textContent = personCount;
                    personCountEl.className = personCount > 0 ? 'healthValue success' : 'healthValue warning';
                }
            }).catch(function() {});
        },

        loadPeopleItems: function() {
            return this.table.reload();
        },

        triggerRefresh: function() {
            var self = this;
            var btn = view.querySelector('#btnRefreshPeopleItems');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerPeopleRefresh', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncRefreshPeopleTable', 'Refresh', function() {
                    self.loadPeopleStatus();
                    self.loadPeopleItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start people refresh task');
                btn.querySelector('span').textContent = 'Refresh';
                btn.disabled = false;
            });
        },

        triggerSync: function() {
            var self = this;
            var btn = view.querySelector('#btnTriggerPeopleSync');
            btn.disabled = true;
            btn.querySelector('span').textContent = 'Starting...';

            ServerSyncShared.apiRequest('TriggerPeopleSync', 'POST').then(function() {
                _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, 'ServerSyncMissingPeople', 'Sync', function() {
                    self.loadPeopleStatus();
                    self.loadPeopleItems();
                    self.loadHealthStats();
                }));
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to start people sync task');
                btn.querySelector('span').textContent = 'Sync';
                btn.disabled = false;
            });
        },

        retryErrors: function() {
            var self = this;
            // Get all errored items and queue them
            ServerSyncShared.apiRequest('PeopleItems?status=Errored&take=200', 'GET').then(function(result) {
                if (result && result.Items && result.Items.length > 0) {
                    var ids = result.Items.map(function(item) { return item.Id; });
                    ServerSyncShared.apiRequest('PeopleItems/Queue', 'POST', { Ids: ids }).then(function() {
                        ServerSyncShared.showAlert('Errored people queued for retry');
                        self.loadPeopleStatus();
                        self.loadPeopleItems();
                    });
                }
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to retry errored items');
            });
        },

        updateBulkActionsVisibility: function(count) {
            var hasSelection = count > 0;
            var ignoreBtn = view.querySelector('#btnPeopleBulkIgnore');
            var queueBtn = view.querySelector('#btnPeopleBulkQueue');
            if (ignoreBtn) ignoreBtn.disabled = !hasSelection;
            if (queueBtn) queueBtn.disabled = !hasSelection;
        },

        bulkIgnore: function() {
            var self = this;
            var selectedKeys = this.table.getSelectedIds();
            if (selectedKeys.length === 0) return;

            var ids = selectedKeys.map(function(k) { return parseInt(k, 10); });

            ServerSyncShared.apiRequest('PeopleItems/Ignore', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadPeopleStatus();
                self.loadPeopleItems();
                ServerSyncShared.showAlert(selectedKeys.length + ' person(s) ignored');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to ignore people');
            });
        },

        bulkQueue: function() {
            var self = this;
            var selectedKeys = this.table.getSelectedIds();
            if (selectedKeys.length === 0) return;

            var ids = selectedKeys.map(function(k) { return parseInt(k, 10); });

            ServerSyncShared.apiRequest('PeopleItems/Queue', 'POST', { Ids: ids }).then(function() {
                self.table.clearSelection();
                self.loadPeopleStatus();
                self.loadPeopleItems();
                ServerSyncShared.showAlert(selectedKeys.length + ' person(s) queued');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to queue people');
            });
        },

        showPersonDetail: function(item) {
            var self = this;

            ServerSyncShared.apiRequest('PeopleItems/' + item.Id).then(function(detail) {
                if (!detail) {
                    ServerSyncShared.showAlert('Person not found');
                    return;
                }

                self.currentModalItem = detail;

                view.querySelector('#peopleSyncModalTitle').textContent = detail.PersonName || 'Unknown';

                var statusBadge = view.querySelector('#peopleSyncModalStatusBadge');
                statusBadge.textContent = detail.Status || 'Unknown';
                statusBadge.className = 'itemModal-statusBadge ' + (detail.Status || 'unknown');

                var infoGrid = view.querySelector('#peopleSyncModalInfoGrid');
                if (detail.LastSyncTime) {
                    infoGrid.classList.remove('hidden');
                    view.querySelector('#peopleSyncModalLastSync').textContent =
                        ServerSyncShared.formatRelativeTime(new Date(detail.LastSyncTime));
                } else {
                    infoGrid.classList.add('hidden');
                }

                var errorSection = view.querySelector('#peopleSyncModalErrorSection');
                if (detail.Status === 'Errored' && detail.ErrorMessage) {
                    errorSection.classList.remove('hidden');
                    view.querySelector('#peopleSyncModalError').textContent = detail.ErrorMessage;
                } else {
                    errorSection.classList.add('hidden');
                }

                // Build changes summary badges
                var summaryHtml = '';
                summaryHtml += '<span class="peopleSyncBadge ' + (detail.HasMetadataChanges ? 'has-changes' : 'no-changes') + '">Metadata: ' + (detail.HasMetadataChanges ? 'Changes' : 'Synced') + '</span> ';
                summaryHtml += '<span class="peopleSyncBadge ' + (detail.HasImagesChanges ? 'has-changes' : 'no-changes') + '">Images: ' + (detail.HasImagesChanges ? 'Changes' : 'Synced') + '</span>';
                view.querySelector('#peopleSyncModalChangesSummary').innerHTML = summaryHtml;

                // Build comparison table
                var tbody = view.querySelector('#peopleSyncModalTableBody');
                tbody.innerHTML = '';

                var sourceMetadata = self._parseJson(detail.SourceMetadataValue) || {};
                var localMetadata = self._parseJson(detail.LocalMetadataValue) || {};

                // Metadata section header
                self._addSectionHeader(tbody, 'Metadata');

                // Metadata fields
                var metadataFields = [
                    { key: 'Name', label: 'Name' },
                    { key: 'OriginalTitle', label: 'Original Title' },
                    { key: 'SortName', label: 'Sort Name' },
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

                // Tags row
                var sourceTags = Array.isArray(sourceMetadata.Tags) ? sourceMetadata.Tags : [];
                var localTags = Array.isArray(localMetadata.Tags) ? localMetadata.Tags : [];
                var tagsChanged = JSON.stringify(sourceTags.slice().sort()) !== JSON.stringify(localTags.slice().sort());
                self._addComparisonRow(tbody, 'Tags',
                    sourceTags.length > 0 ? ServerSyncShared.escapeHtml(sourceTags.join(', ')) : '-',
                    localTags.length > 0 ? ServerSyncShared.escapeHtml(localTags.join(', ')) : '-',
                    tagsChanged);

                // Provider IDs rows
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

                // Images section
                self._addSectionHeader(tbody, 'Images');

                var sourceImages = self._parseJson(detail.SourceImagesValue);
                var localImages = self._parseJson(detail.LocalImagesValue);
                if (sourceImages) {
                    Object.keys(sourceImages).forEach(function(imageType) {
                        var sourceList = sourceImages[imageType] || [];
                        var localList = (localImages && localImages[imageType]) || [];
                        var sourceSize = sourceList.reduce(function(sum, img) { return sum + (img.Size || 0); }, 0);
                        var localSize = localList.reduce(function(sum, img) { return sum + (img.Size || 0); }, 0);
                        var sizeChanged = sourceList.length !== localList.length || (sourceSize > 0 && localSize > 0 && sourceSize !== localSize);
                        self._addComparisonRow(tbody, imageType,
                            sourceList.length + ' (' + self._formatBytes(sourceSize) + ')',
                            localList.length + ' (' + self._formatBytes(localSize) + ')',
                            sizeChanged);
                    });
                } else {
                    self._addComparisonRow(tbody, 'Images', '-', localImages ? 'Present' : '-', false);
                }

                view.querySelector('#peopleSyncItemDetailModal').classList.remove('hidden');
            }).catch(function(err) {
                console.error('Failed to load person detail:', err);
                ServerSyncShared.showAlert('Failed to load person details');
            });
        },

        _addSectionHeader: function(tbody, title) {
            var row = document.createElement('tr');
            row.className = 'peopleSyncModal-sectionHeader';
            var cell = document.createElement('td');
            cell.colSpan = 3;
            cell.textContent = title;
            row.appendChild(cell);
            tbody.appendChild(row);
        },

        _formatFieldValue: function(val, field) {
            if (val == null) return '-';
            if (field && field.isDate && typeof val === 'string') {
                try {
                    var d = new Date(val);
                    return isNaN(d.getTime()) ? val : d.toLocaleDateString();
                } catch (e) { return String(val); }
            }
            if (field && field.truncate && typeof val === 'string' && val.length > 200) {
                return ServerSyncShared.escapeHtml(val.substring(0, 200) + '...');
            }
            if (Array.isArray(val)) {
                return val.length > 0 ? ServerSyncShared.escapeHtml(val.join(', ')) : '-';
            }
            if (typeof val === 'boolean') return val ? 'Yes' : 'No';
            if (typeof val === 'object') return JSON.stringify(val);
            return ServerSyncShared.escapeHtml(String(val));
        },

        _formatBytes: function(bytes) {
            if (!bytes || bytes === 0) return '0 B';
            var units = ['B', 'KB', 'MB', 'GB'];
            var i = 0;
            var size = bytes;
            while (size >= 1024 && i < units.length - 1) { size /= 1024; i++; }
            return size.toFixed(i > 0 ? 1 : 0) + ' ' + units[i];
        },

        _addComparisonRow: function(tbody, property, sourceVal, localVal, isChanged) {
            var row = document.createElement('tr');
            if (isChanged) row.className = 'peopleSyncModal-changedRow';

            var propCell = document.createElement('td');
            propCell.className = 'peopleSyncModal-property';
            propCell.textContent = property;

            var sourceCell = document.createElement('td');
            sourceCell.className = 'peopleSyncModal-value';
            sourceCell.textContent = sourceVal || '-';

            var localCell = document.createElement('td');
            localCell.className = 'peopleSyncModal-value';
            localCell.textContent = localVal || '-';

            row.appendChild(propCell);
            row.appendChild(sourceCell);
            row.appendChild(localCell);
            tbody.appendChild(row);
        },

        _parseJson: function(str) {
            if (!str || typeof str !== 'string') return null;
            try { return JSON.parse(str); } catch (e) { return null; }
        },

        _truncate: function(str, maxLen) {
            if (!str) return '-';
            return str.length > maxLen ? str.substring(0, maxLen) + '...' : str;
        },

        closeModal: function() {
            view.querySelector('#peopleSyncItemDetailModal').classList.add('hidden');
            this.currentModalItem = null;
            this.table.reload();
            this.loadPeopleStatus();
        },

        modalIgnore: function() {
            var self = this;
            var item = self.currentModalItem;
            if (!item) return;

            ServerSyncShared.apiRequest('PeopleItems/Ignore', 'POST', { Ids: [item.Id] }).then(function() {
                self.closeModal();
                ServerSyncShared.showAlert('Person ignored');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to ignore person');
            });
        },

        modalQueue: function() {
            var self = this;
            var item = self.currentModalItem;
            if (!item) return;

            ServerSyncShared.apiRequest('PeopleItems/Queue', 'POST', { Ids: [item.Id] }).then(function() {
                self.closeModal();
                ServerSyncShared.showAlert('Person queued');
            }).catch(function() {
                ServerSyncShared.showAlert('Failed to queue person');
            });
        }
    };

    // ============================================
    // PEOPLE PAGE CONTROLLER
    // ============================================

    var PeoplePageController = {
        currentConfig: null,

        init: function() {
            this.loadConfig();
        },

        loadConfig: function() {
            var self = this;

            ServerSyncShared.fetchLocalServerName().then(function() {
                return ServerSyncShared.getConfig();
            }).then(function(config) {
                self.currentConfig = config;

                PeopleSyncTableModule.currentConfig = config;
                PeopleSyncTableModule.init(config);

                PeopleSyncTableModule.loadPeopleStatus();
                PeopleSyncTableModule.loadPeopleItems();
                PeopleSyncTableModule.loadHealthStats();
            }).catch(function() {
                PeopleSyncTableModule.init(null);
                PeopleSyncTableModule.loadPeopleStatus();
                PeopleSyncTableModule.loadPeopleItems();
                PeopleSyncTableModule.loadHealthStats();
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

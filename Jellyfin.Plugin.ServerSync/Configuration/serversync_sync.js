// ============================================
// SERVER SYNC PLUGIN - UNIFIED SYNC PAGE CONTROLLER
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
        _activePollIntervals.forEach(function(handle) {
            if (handle && typeof handle.cancel === 'function') handle.cancel();
        });
        _activePollIntervals.length = 0;

        // The page's modules are built once their imports land, so a hide before then has nothing to stop.
        if (QueueModule) QueueModule.stopAutoRefresh();
        [SyncTableModule, HistorySyncTableModule, MetadataSyncTableModule, UserSyncTableModule, PeopleSyncTableModule].forEach(function(mod) {
            if (mod && mod.table && mod.table.disconnectObserver) {
                mod.table.disconnectObserver();
            }
        });
    });

    // ============================================
    // SHARED AND PAGE MODULE IMPORTS (deferred)
    // ============================================
    // The shared helpers and each part of this page live in their own resource. Every part is built
    // from one context once all of them have loaded, and everything that needs them waits on
    // _sharedPromise.

    var ServerSyncShared = null;
    var openSyncView = null;
    var SyncTableModule = null;
    var ContentPageController = null;
    var HistorySyncTableModule = null;
    var MetadataSyncTableModule = null;
    var UserSyncTableModule = null;
    var PeopleSyncTableModule = null;
    var QueueModule = null;

    // Relative specifiers so a server hosted under a base URL still resolves them.
    var _sharedPromise = Promise.all([
        import('./configurationpage?name=serversync_shared.js'),
        import('./configurationpage?name=serversync_sync_table.js'),
        import('./configurationpage?name=serversync_sync_content.js'),
        import('./configurationpage?name=serversync_sync_history.js'),
        import('./configurationpage?name=serversync_sync_metadata.js'),
        import('./configurationpage?name=serversync_sync_users.js'),
        import('./configurationpage?name=serversync_sync_people.js'),
        import('./configurationpage?name=serversync_sync_queue.js')
    ]).then(function(loaded) {
        var shared = loaded[0];
        ServerSyncShared = shared.createServerSyncShared(view);
        // A config load that turns content sync on or off adds or drops the Storage tab. A page the
        // dashboard keeps hidden leaves the tabs to the page on screen.
        ServerSyncShared.onTabsChanged = function () {
            if (!view.classList.contains('hide')) LibraryMenu.setTabs('serversync', 0, getTabs);
        };

        var ctx = {
            view: view,
            shared: ServerSyncShared,
            createPaginatedTable: shared.createPaginatedTable,
            bindDialog: shared.bindDialog,
            activePollIntervals: _activePollIntervals
        };
        var kit = loaded[1].createSyncTableKit(ctx);
        openSyncView = kit.openSyncView;

        SyncTableModule = loaded[2].createContentSyncTableModule(ctx, kit);
        ContentPageController = loaded[2].createContentPageController(ctx, SyncTableModule);
        HistorySyncTableModule = loaded[3].createHistorySyncTableModule(ctx, kit);
        MetadataSyncTableModule = loaded[4].createMetadataSyncTableModule(ctx, kit);
        UserSyncTableModule = loaded[5].createUserSyncTableModule(ctx, kit);
        PeopleSyncTableModule = loaded[6].createPeopleSyncTableModule(ctx, kit);
        QueueModule = loaded[7].createQueueModule(ctx);
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
                    dropdown.value = this.currentView;
                    // A view that was never initialized is entered again, which needs currentView cleared.
                    var cv = this.currentView;
                    if (!this.initialized[cv]) {
                        this.currentView = null;
                        this.switchView(cv);
                    }
                    return;
                }
            }

            // The current view is disabled or not set, so the first enabled option opens.
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

            // The queue view refreshes itself while it is showing. Before the page's modules load there is
            // no queue yet, and init starts its refresh once they land if the queue is the open view.
            if (QueueModule) {
                if (viewName === 'queue') {
                    QueueModule.startAutoRefresh();
                } else {
                    QueueModule.stopAutoRefresh();
                }
            }

            var descEl = view.querySelector('#syncTypeDescription');
            if (descEl) {
                var descriptions = {
                    content: 'Media files copied or linked into mapped libraries from your Pull and Sync servers.',
                    history: 'Watch state of mapped users: played status, play count, resume position, last played date, and favorites.',
                    metadata: 'Item metadata and images, copied onto local items matched by file path.',
                    people: 'Biographies, provider IDs, and photos of people, matched by name.',
                    users: 'Permissions, preferences, and profile images of mapped users. Library permissions follow your library mappings.',
                    queue: 'Changes sent between servers as they happen. Outbound rows wait for a Push or Sync server to finish them; inbound rows wait to be applied here.'
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
    // INITIAL PAGE SETUP
    // ============================================
    // viewshow may have fired before this module ran, so the first load sets the tabs and
    // initializes right away.

    _pageReady = true;
    LibraryMenu.setTabs('serversync', 0, getTabs);
    SyncViewManager.init();
}

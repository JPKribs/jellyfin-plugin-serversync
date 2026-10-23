// ============================================
// SERVER SYNC PLUGIN - SYNC PAGE: TABLE MODULE FACTORY
// ============================================
// Loaded by serversync_sync.js once the shared module is in. createSyncTableKit(ctx) returns the
// factory every Sync view is built from, plus the row, health, and value helpers the views share.
// ctx carries the view, the shared helper bag, the paginated table and dialog builders, and the
// list of running task pollers the page stops on viewhide.
// ============================================

export function createSyncTableKit(ctx) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;
    var createPaginatedTable = ctx.createPaginatedTable;
    var bindDialog = ctx.bindDialog;
    var _activePollIntervals = ctx.activePollIntervals;

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
            _dialog: null,          // The detail modal, opened by showDetail and closed by closeModal
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
                // The close button carries jpk-dialog-close, so the dialog closes on it, on Escape, and on
                // a backdrop click alike, and every close runs the same refresh.
                self._dialog = bindDialog(spec.modalId, { root: view, onClose: function() { self._onModalClosed(); } });
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

            // A status change can land after the admin already closed the modal, and still refreshes.
            closeModal: function() {
                if (this._dialog.isOpen()) {
                    this._dialog.close();
                } else {
                    this._onModalClosed();
                }
            },

            _onModalClosed: function() {
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
    // Which server last edited an object and when, from Sync/Hints/Version. An object no one
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

    return {
        createSyncTableModule: createSyncTableModule,
        openSyncView: openSyncView,
        renderRowInfo: renderRowInfo,
        renderChangeBadges: renderChangeBadges,
        setHealthLastSync: setHealthLastSync,
        setHealthCount: setHealthCount,
        parseJsonSafe: parseJsonSafe,
        formatImageDisplay: formatImageDisplay,
        retryAllErrored: retryAllErrored,
        showObjectVersion: showObjectVersion
    };
}

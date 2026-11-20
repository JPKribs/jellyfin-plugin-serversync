// serversync_sync_table.js
// The table kit every Sync view is built from. Loaded by serversync_sync.js once the shared module is in.

// createSyncTableKit
// Builds the factory every Sync view is built from, plus the row, health, and value helpers the views share.
//
// Inputs
// ctx [object]: The page context, with the view, the shared helper bag, the paginated table and dialog builders,
// and activePollIntervals, the list of running task pollers the page stops on viewhide.
//
// A module spec names its messages in spec.messages, as keys of the plugin's strings: startRefreshFailed,
// startSyncFailed, ignoredOne, ignoredMany, queuedOne, queuedMany, ignoreFailed, queueFailed, retryNone,
// retriedOne, retriedMany, retriedUnknown, retryFailed, statusUpdated, and statusFailed. A count fills {0},
// and statusUpdated takes the status name as {0}. Every other text in a spec arrives translated.
//
// Output
// [object]: A handle with createSyncTableModule, openSyncView, renderRowInfo, renderChangeBadges,
// setHealthLastSync, setHealthCount, parseJsonSafe, formatImageDisplay, showObjectVersion, and statusName.
export function createSyncTableKit(ctx) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;
    var createPaginatedTable = ctx.createPaginatedTable;
    var bindDialog = ctx.bindDialog;
    var _activePollIntervals = ctx.activePollIntervals;

    // Builds the module behind one Sync view. Every view shares the table setup, the status counts,
    // the Refresh and Sync buttons, retrying errors, the bulk and modal actions, and the page load.
    // A spec supplies what differs: element ids, endpoints, columns, health figures, and the detail
    // modal. A method the spec defines replaces the shared one of the same name.

    var BASE_STATUSES = ['Synced', 'Queued', 'Errored', 'Ignored'];

    // The string key of each status the kit names.
    var STATUS_KEYS = { Synced: 'StatusSynced', Queued: 'StatusQueued', Errored: 'StatusErrored', Ignored: 'StatusIgnored', Pending: 'StatusPending' };

    var DEFAULT_BULK_BUTTONS = [
        { id: 'Ignore', title: ServerSyncShared.t.tr('ButtonIgnore'), icon: 'block', method: 'bulkIgnore' },
        { id: 'Queue', title: ServerSyncShared.t.tr('ButtonQueue'), icon: 'playlist_add', className: 'base-button primary', method: 'bulkQueue' }
    ];

    // statusName
    // Names a status in the viewer's language.
    //
    // Inputs
    // status [string]: The status as the server sends it, such as Queued.
    //
    // Output
    // [string]: The name, or the status itself when it has no string.
    function statusName(status) {
        return STATUS_KEYS[status] ? ServerSyncShared.t.tr(STATUS_KEYS[status]) : status;
    }

    // tr
    // Reads one of the plugin's strings.
    //
    // Inputs
    // key [string]: The string's key.
    //
    // Output
    // [string]: The text in the viewer's language.
    function tr(key) {
        return ServerSyncShared.t.tr(key);
    }

    // An element id under a view's prefix. The Content view has no prefix, so its ids start lowercase.
    function prefixedId(prefix, name) {
        return prefix ? prefix + name : name.charAt(0).toLowerCase() + name.slice(1);
    }

    function statusFilters(statuses) {
        return {
            options: statuses.map(function(s) { return { value: s, label: statusName(s) }; }),
            buildParams: function(filterValue) { return { status: filterValue }; }
        };
    }

    function setHealthLastSync(elementId, time) {
        var el = view.querySelector('#' + elementId);
        if (!el) return;
        if (time) {
            el.textContent = ServerSyncShared.formatRelativeTime(new Date(time));
            el.className = 'serversync-health-value';
        } else {
            el.textContent = ServerSyncShared.t.tr('Never');
            el.className = 'serversync-health-value';
        }
    }

    function setHealthCount(elementId, count) {
        var el = view.querySelector('#' + elementId);
        if (!el) return;
        el.textContent = count;
        el.className = count > 0 ? 'serversync-health-value serversync-success' : 'serversync-health-value serversync-warning';
    }

    // The name, second line, and error preview every Item column shows. Base draws the thumbnail from the column's image.
    function renderRowInfo(name, line, item, status, nameTitle) {
        var errorPreview = '';
        if (status === 'Errored' && item.ErrorMessage) {
            errorPreview = '<div class="serversync-item-error" title="' + ServerSyncShared.escapeHtml(item.ErrorMessage) + '">' +
                ServerSyncShared.escapeHtml(item.ErrorMessage) + '</div>';
        }
        var title = nameTitle === undefined ? name : nameTitle;
        return '<div class="serversync-item-info">' +
            '<div class="serversync-item-name"' + (title ? ' title="' + ServerSyncShared.escapeHtml(title) + '"' : '') + '>' + ServerSyncShared.escapeHtml(name) + '</div>' +
            (line === null ? '' : '<div class="serversync-item-path">' + ServerSyncShared.escapeHtml(line) + '</div>') +
            errorPreview +
            '</div>';
    }

    // The orange badges for the categories that differ, or a gray one when none do.
    function renderChangeBadges(changes) {
        if (changes.length === 0) {
            return '<span class="base-badge gray">' + ServerSyncShared.escapeHtml(ServerSyncShared.t.tr('BadgeNoChanges')) + '</span>';
        }
        return changes.map(function(c) { return '<span class="base-badge orange">' + c + '</span>'; }).join(' ');
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
                // The close button carries base-dialog-close, so the dialog closes on it, on Escape, and on
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
                        'serversync-bulk-icon-btn" title="' + ServerSyncShared.escapeHtml(b.title) + '" aria-label="' + ServerSyncShared.escapeHtml(b.title) + '" disabled><span class="material-icons" aria-hidden="true">' + b.icon + '</span></button>';
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
                    if (groupEl) groupEl.setAttribute('title', ServerSyncShared.t.format('StatusCountTitle', statusName(s), count));
                });

                var retryBtn = view.querySelector('#' + ids.retry);
                if (retryBtn) retryBtn.classList.toggle('hidden', !(status.Errored > 0));
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
                var label = kind === 'refresh' ? ServerSyncShared.t.tr('Refresh') : ServerSyncShared.t.tr('TabSync');
                var btn = view.querySelector('#' + ids[kind]);
                if (!btn || !task) return;

                btn.disabled = true;
                btn.querySelector('span').textContent = ServerSyncShared.t.tr('StatusStarting');

                ServerSyncShared.apiRequest('Tasks/' + task.key + '/Run', 'POST').then(function() {
                    _activePollIntervals.push(ServerSyncShared.pollTaskProgress(btn, task.key, label, function() {
                        self.loadAll();
                    }));
                }).catch(function() {
                    ServerSyncShared.syncStatus(tr(kind === 'refresh' ? spec.messages.startRefreshFailed : spec.messages.startSyncFailed), true);
                    btn.querySelector('span').textContent = label;
                    btn.disabled = false;
                });
            },

            retryErrors: function() {
                var self = this;
                return retryAllErrored(spec.retryEndpoint, spec.messages, function() {
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
                var m = spec.messages;
                this._bulk('Ignore', function(n) { return ServerSyncShared.plural(n, m.ignoredOne, m.ignoredMany); }, tr(m.ignoreFailed));
            },

            bulkQueue: function() {
                var m = spec.messages;
                this._bulk('Queue', function(n) { return ServerSyncShared.plural(n, m.queuedOne, m.queuedMany); }, tr(m.queueFailed));
            },

            _bulk: function(action, done, failMessage) {
                var keys = this.table.getSelectedIds();
                if (keys.length === 0) return;

                var body = spec.selectionBody ? spec.selectionBody(keys) : { Ids: keys };
                this.postSelection(spec.itemsEndpoint + '/' + action, body, done(keys.length), failMessage);
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
                    if (message) ServerSyncShared.syncStatus(message, false);
                }).catch(function(err) {
                    console.error(failMessage + ':', err);
                    ServerSyncShared.syncStatus(failMessage, true);
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
                    ServerSyncShared.syncStatus(ServerSyncShared.t.format(spec.messages.statusUpdated, statusName(status)), false);
                }).catch(function(err) {
                    console.error('Failed to update status:', err);
                    ServerSyncShared.syncStatus(tr(spec.messages.statusFailed), true);
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
            return ServerSyncShared.plural(count, 'ImageCountOne', 'ImageCountMany');
        }
        var sizeStr = ServerSyncShared.formatSize(size);
        return count > 1 ? sizeStr + ' (' + count + ')' : sizeStr;
    }

    // Posts an empty id list with Status Errored to a module's Queue endpoint, which asks the server to
    // requeue every errored row itself. Listing ids from the page instead would only cover the rows that
    // happen to be loaded. The counts and rows reload whether or not the request worked, since the table
    // should show what the server holds now, and every promise in the chain is returned so nothing fails
    // without a status.
    function retryAllErrored(endpoint, messages, reload) {
        return ServerSyncShared.apiRequest(endpoint, 'POST', { Ids: [], Status: 'Errored' }).then(function(result) {
            var updated = result && typeof result.Updated === 'number' ? result.Updated : null;
            if (updated === 0) {
                ServerSyncShared.syncStatus(tr(messages.retryNone), false);
            } else if (updated !== null) {
                ServerSyncShared.syncStatus(ServerSyncShared.plural(updated, messages.retriedOne, messages.retriedMany), false);
            } else {
                ServerSyncShared.syncStatus(tr(messages.retriedUnknown), false);
            }
        }, function(err) {
            console.error('Retry of errored rows failed:', err);
            ServerSyncShared.syncStatus(tr(messages.retryFailed), true);
        }).then(function() {
            return reload();
        }).catch(function(err) {
            console.error('Reload after retry failed:', err);
        });
    }

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
                el.textContent = ServerSyncShared.t.tr('VersionNotRecorded');
                return;
            }
            var when = ServerSyncShared.formatRelativeTime(new Date(version.Timestamp));
            el.textContent = version.IsThisServer
                ? ServerSyncShared.t.format('VersionHere', when)
                : ServerSyncShared.t.format('VersionOnServer', when, version.ServerName || version.ServerId);
            el.title = new Date(version.Timestamp).toLocaleString();
        }).catch(function () {
            if (_versionSeq[elementId] !== seq) return;
            el.textContent = ServerSyncShared.t.tr('VersionNotRecorded');
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
        showObjectVersion: showObjectVersion,
        statusName: statusName
    };
}

// serversync_sync_history.js
// The History sync table and its detail modal. Built by serversync_sync.js from the page context
// and the table kit in serversync_sync_table.js.

// createHistorySyncTableModule
// Builds the History sync view: its table, status counts, health figures, and detail modal.
//
// Inputs
// ctx [object]: The page context from serversync_sync.js.
// kit [object]: The table kit from createSyncTableKit.
//
// Output
// [object]: The view's module, with table, currentConfig, init, loadAll, loadStatus, loadItems, and the
// modal and bulk actions.
export function createHistorySyncTableModule(ctx, kit) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;
    var createSyncTableModule = kit.createSyncTableModule;
    var renderRowInfo = kit.renderRowInfo;
    var setHealthLastSync = kit.setHealthLastSync;
    var setHealthCount = kit.setHealthCount;
    var showObjectVersion = kit.showObjectVersion;
    var statusName = kit.statusName;
    var t = ServerSyncShared.t;

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
            refresh: { key: 'ServerSyncRefreshHistoryTable' },
            sync: { key: 'ServerSyncMissingHistory' }
        },
        messages: {
            startRefreshFailed: 'MessageStartHistoryRefreshFailed',
            startSyncFailed: 'MessageStartHistorySyncFailed',
            ignoredOne: 'ToastItemsIgnoredOne',
            ignoredMany: 'ToastItemsIgnoredMany',
            queuedOne: 'ToastItemsQueuedOne',
            queuedMany: 'ToastItemsQueuedMany',
            ignoreFailed: 'ErrorIgnoreItems',
            queueFailed: 'ErrorQueueItems',
            retryNone: 'MessageNoErroredHistoryItems',
            retriedOne: 'MessageErroredHistoryItemsRetriedOne',
            retriedMany: 'MessageErroredHistoryItemsRetriedMany',
            retriedUnknown: 'MessageErroredHistoryItemsRetried',
            retryFailed: 'ErrorRetryHistoryItems',
            statusUpdated: 'ToastItemStatusUpdated',
            statusFailed: 'ErrorItemStatusUpdate'
        },
        filterStatuses: ['Synced', 'Queued', 'Pending', 'Errored', 'Ignored'],
        emptyMessage: t.tr('EmptyHistoryItems'),
        columns: function(self) {
            return [
                {
                    key: 'name',
                    label: t.tr('ColumnItem'),
                    type: 'custom',
                    className: 'base-table-cell-with-thumb',
                    image: function(item) { return ServerSyncShared.proxyImage(item.SourceItemId, false, item.ServerKey); },
                    render: function(item) {
                        var userMapping = self.findUserMapping(item);
                        var users = t.format('MappingArrow', userMapping ? userMapping.SourceUserName : t.tr('Unknown'), userMapping ? userMapping.LocalUserName : t.tr('Unknown'));
                        return renderRowInfo(item.ItemName || t.tr('Unknown'), users, item, item.Status);
                    }
                },
                {
                    key: 'details',
                    label: t.tr('ColumnChanges'),
                    type: 'custom',
                    className: 'serversync-details-cell',
                    render: function(item) {
                        if (!item.HasChanges) {
                            return '<span class="base-badge gray">' + ServerSyncShared.escapeHtml(t.tr('BadgeNoChanges')) + '</span>';
                        }

                        var details = [];
                        if (item.MergedIsPlayed !== item.LocalIsPlayed) {
                            details.push(t.format('ChangePlayed', t.tr(item.MergedIsPlayed ? 'Yes' : 'No')));
                        }
                        if (item.MergedIsFavorite !== item.LocalIsFavorite) {
                            details.push(t.format('ChangeFavorite', t.tr(item.MergedIsFavorite ? 'Yes' : 'No')));
                        }
                        if (item.MergedPlayCount !== item.LocalPlayCount) {
                            details.push(t.format('ChangeCount', item.MergedPlayCount || 0));
                        }
                        var joined = details.reduce(function(text, part) { return text ? t.format('ListSeparator', text, part) : part; }, '');
                        return ServerSyncShared.escapeHtml(joined || t.tr('MessageChangesPending'));
                    }
                },
                { key: 'Status', label: t.tr('HeaderStatus'), type: 'status' }
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
            return { endpoint: 'HistoryItems/' + (status === 'Ignored' ? 'Ignore' : 'Queue'), body: { Ids: [item.Id] } };
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

                view.querySelector('#historyModalTitle').textContent = item.ItemName || t.tr('Unknown');

                var statusBadge = view.querySelector('#historyModalStatusBadge');
                statusBadge.textContent = statusName(item.Status);
                statusBadge.className = 'base-table-status-badge ' + item.Status;

                var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, item.ServerKey);
                var localServerName = ServerSyncShared.localServerName || t.tr('ServerLocal');
                view.querySelector('#historyModalServerMapping').textContent = t.format('MappingArrow', sourceServerName, localServerName);

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
                var sourceUserName = userMapping ? userMapping.SourceUserName : t.tr('Unknown');
                var localUserName = userMapping ? userMapping.LocalUserName : t.tr('Unknown');

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

                self._dialog.open();
            },

            setTableValue: function(elementId, value, type) {
                var el = view.querySelector('#' + elementId);
                if (!el) return;

                var text = '-';

                switch (type) {
                    case 'bool':
                    case 'favorite':
                        if (value === true) {
                            text = t.tr('Yes');
                        } else if (value === false) {
                            text = t.tr('No');
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
                if (!ticks) return '-';
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

                row.classList.toggle('serversync-modal-changed-row', merged !== local);
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

    return HistorySyncTableModule;
}

// ============================================
// SERVER SYNC PLUGIN - SYNC PAGE: HISTORY VIEW
// ============================================
// The History Sync table and its detail modal. Built by serversync_sync.js from the page context
// and the table kit in serversync_sync_table.js.
// ============================================

export function createHistorySyncTableModule(ctx, kit) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;
    var createSyncTableModule = kit.createSyncTableModule;
    var renderRowInfo = kit.renderRowInfo;
    var setHealthLastSync = kit.setHealthLastSync;
    var setHealthCount = kit.setHealthCount;
    var showObjectVersion = kit.showObjectVersion;

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

    return HistorySyncTableModule;
}

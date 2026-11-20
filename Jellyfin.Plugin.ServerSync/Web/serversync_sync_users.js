// serversync_sync_users.js
// The User Sync table and its detail modal. Built by serversync_sync.js from the page context
// and the table kit in serversync_sync_table.js.

// createUserSyncTableModule
// Builds the User Sync view: its table, status counts, health figures, and detail modal.
//
// Inputs
// ctx [object]: The page context from serversync_sync.js.
// kit [object]: The table kit from createSyncTableKit.
//
// Output
// [object]: The view's module, with table, currentConfig, init, loadAll, loadStatus, loadItems, and the
// modal and bulk actions.
// hasSynced
// Says whether a last sync time is a real date. A user that never synced carries the default date of year 1.
//
// Inputs
// value [string]: The last sync time the server sent, or nothing.
//
// Output
// [boolean]: True when the user has synced.
export function hasSynced(value) {
    if (!value) return false;
    var year = new Date(value).getUTCFullYear();
    return !isNaN(year) && year > 1970;
}

export function createUserSyncTableModule(ctx, kit) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;
    var createSyncTableModule = kit.createSyncTableModule;
    var renderRowInfo = kit.renderRowInfo;
    var renderChangeBadges = kit.renderChangeBadges;
    var setHealthLastSync = kit.setHealthLastSync;
    var setHealthCount = kit.setHealthCount;
    var parseJsonSafe = kit.parseJsonSafe;
    var showObjectVersion = kit.showObjectVersion;
    var t = ServerSyncShared.t;

    // statusText
    // Names a row status in the viewer's language, or Unknown when there is none.
    //
    // Inputs
    // status [string]: The status the server sent, such as Synced.
    //
    // Output
    // [string]: The status to show.
    function statusText(status) {
        return status ? kit.statusName(status) : t.tr('Unknown');
    }

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
            refresh: { key: 'ServerSyncRefreshUserTable' },
            sync: { key: 'ServerSyncMissingUserData' }
        },
        messages: {
            startRefreshFailed: 'ErrorStartUserRefresh',
            startSyncFailed: 'ErrorStartUserSync',
            ignoredOne: 'ToastUsersIgnoredOne',
            ignoredMany: 'ToastUsersIgnoredMany',
            queuedOne: 'ToastUsersQueuedOne',
            queuedMany: 'ToastUsersQueuedMany',
            ignoreFailed: 'ErrorIgnoreUsers',
            queueFailed: 'ErrorQueueUsers',
            retryNone: 'ToastNoErroredUserItems',
            retriedOne: 'ToastErroredUserItemsRetriedOne',
            retriedMany: 'ToastErroredUserItemsRetriedMany',
            retriedUnknown: 'ToastErroredUserItemsRetried',
            retryFailed: 'ErrorRetryUserItems',
            statusUpdated: 'ToastUserStatusUpdated',
            statusFailed: 'ErrorUserStatusUpdate'
        },
        searchPlaceholder: t.tr('PlaceholderSearchUsers'),
        emptyMessage: t.tr('EmptyUserItems'),
        columns: function(self) {
            return [
                {
                    key: 'user',
                    label: t.tr('HeaderUser'),
                    type: 'custom',
                    className: 'base-table-cell-with-thumb',
                    image: function(item) { return ServerSyncShared.proxyImage(item.SourceUserId, true, item.ServerKey); },
                    imageShape: 'round',
                    render: function(item) {
                        var users = (item.SourceUserName || t.tr('Unknown')) + ' → ' + (item.LocalUserName || t.tr('Unknown'));
                        var servers = ServerSyncShared.serverNameFor(self.currentConfig, item.ServerKey) + ' → ' + (ServerSyncShared.localServerName || t.tr('Unknown'));
                        return renderRowInfo(users, servers, item, item.OverallStatus, '');
                    }
                },
                {
                    key: 'changes',
                    label: t.tr('ColumnChanges'),
                    type: 'custom',
                    className: 'serversync-details-cell',
                    render: function(item) {
                        // Only categories enabled in the config count.
                        var config = self.currentConfig || {};
                        var changes = [];
                        if (config.UserSyncPolicy !== false && item.PolicyHasChanges) changes.push(t.tr('ChangePolicy'));
                        if (config.UserSyncConfiguration !== false && item.ConfigurationHasChanges) changes.push(t.tr('ChangeConfiguration'));
                        if (config.UserSyncProfileImage !== false && item.ProfileImageHasChanges) changes.push(t.tr('Image'));
                        return renderChangeBadges(changes);
                    }
                },
                { key: 'OverallStatus', label: t.tr('HeaderStatus'), type: 'status' }
            ];
        },
        loadHealth: function() {
            ServerSyncShared.getConfig().then(function(config) {
                setHealthLastSync('userHealthLastSync', config.LastUserSyncTime);
            });
            return ServerSyncShared.apiRequest('UserStatus', 'GET').then(function(status) {
                if (!status) return;
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
                        ServerSyncShared.syncStatus(t.tr('ErrorUserNotFound'), true);
                        return;
                    }

                    self.currentModalItem = detail;

                    view.querySelector('#userSyncModalTitle').textContent =
                        (detail.SourceUserName || t.tr('Unknown')) + ' \u2192 ' + (detail.LocalUserName || t.tr('Unknown'));

                    var statusBadge = view.querySelector('#userSyncModalStatusBadge');
                    statusBadge.textContent = statusText(detail.OverallStatus);
                    statusBadge.className = 'base-table-status-badge ' + (detail.OverallStatus || 'unknown');

                    var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, detail.ServerKey);
                    var localServerName = ServerSyncShared.localServerName || t.tr('ServerLocal');
                    view.querySelector('#userSyncModalServerMapping').textContent =
                        sourceServerName + ' \u2192 ' + localServerName;

                    view.querySelector('#userSyncModalInfoGrid').classList.remove('hidden');
                    view.querySelector('#userSyncModalLastSync').textContent = hasSynced(detail.LastSyncTime)
                        ? ServerSyncShared.formatRelativeTime(new Date(detail.LastSyncTime))
                        : t.tr('Never');
                    showObjectVersion('userSyncModalVersion', { kind: 'Users', localUserId: detail.LocalUserId });

                    var errorSection = view.querySelector('#userSyncModalErrorSection');
                    if (detail.OverallStatus === 'Errored' && detail.ErrorMessage) {
                        errorSection.classList.remove('hidden');
                        view.querySelector('#userSyncModalError').textContent = detail.ErrorMessage;
                    } else {
                        errorSection.classList.add('hidden');
                    }

                    view.querySelector('#userSyncModalSourceUser').textContent = detail.SourceUserName || t.tr('Unknown');
                    view.querySelector('#userSyncModalSourceUserId').textContent = detail.SourceUserId || '';
                    view.querySelector('#userSyncModalLocalUser').textContent = detail.LocalUserName || t.tr('Unknown');
                    view.querySelector('#userSyncModalLocalUserId').textContent = detail.LocalUserId || '';

                    view.querySelector('#userSyncModalSourceHeader').textContent = sourceServerName;
                    view.querySelector('#userSyncModalLocalHeader').textContent = localServerName;

                    self.buildChangesSummary(detail);

                    var tbody = view.querySelector('#userSyncModalTableBody');
                    tbody.innerHTML = '';

                    if (detail.PolicyEnabled) {
                        if (detail.PolicyItem) {
                            tbody.appendChild(self._createSectionHeader(t.tr('ChangePolicy')));
                            self._addPropertyRows(tbody, detail.PolicyItem);
                        } else {
                            tbody.appendChild(self._createDisabledRow(t.tr('ChangePolicy')));
                        }
                    }

                    if (detail.ConfigurationEnabled) {
                        if (detail.ConfigurationItem) {
                            tbody.appendChild(self._createSectionHeader(t.tr('ChangeConfiguration')));
                            self._addPropertyRows(tbody, detail.ConfigurationItem);
                        } else {
                            tbody.appendChild(self._createDisabledRow(t.tr('ChangeConfiguration')));
                        }
                    }

                    if (detail.ProfileImageEnabled) {
                        if (detail.ProfileImageItem) {
                            tbody.appendChild(self._createSectionHeader(t.tr('HeaderProfileImage')));
                            self._addProfileImageRow(tbody, detail.ProfileImageItem);
                        } else {
                            tbody.appendChild(self._createDisabledRow(t.tr('HeaderProfileImage')));
                        }
                    }

                    self._dialog.open();
                }).catch(function(err) {
                    if (seq !== self._detailSeq) return;
                    console.error('Failed to load user detail:', err);
                    ServerSyncShared.syncStatus(t.tr('ErrorUserDetailsLoad'), true);
                });
            },

            _createSectionHeader: function(title) {
                var row = document.createElement('tr');
                row.className = 'serversync-modal-section-header';
                var cell = document.createElement('td');
                cell.colSpan = 4;
                cell.textContent = title;
                row.appendChild(cell);
                return row;
            },

            _createDisabledRow: function(category) {
                var row = document.createElement('tr');
                row.className = 'serversync-modal-disabled-row';
                var cell = document.createElement('td');
                cell.colSpan = 4;
                cell.innerHTML = '<span class="serversync-muted">' + ServerSyncShared.escapeHtml(t.format('RowCategoryNotAvailable', category)) + '</span>';
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
                        row.className = 'serversync-modal-changed-row';
                    }

                    var propCell = document.createElement('td');
                    propCell.className = 'serversync-compare-table-property';
                    propCell.textContent = item.PropertyCategory || t.tr('LabelValue');

                    var sourceCell = document.createElement('td');
                    sourceCell.className = 'serversync-compare-table-value';
                    sourceCell.textContent = self._formatValue(item.SourceValue);

                    var localCell = document.createElement('td');
                    localCell.className = 'serversync-compare-table-value';
                    localCell.textContent = self._formatValue(item.LocalValue);

                    var mergedCell = document.createElement('td');
                    mergedCell.className = 'serversync-compare-table-value serversync-compare-table-merged';
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
                        row.className = 'serversync-modal-changed-row';
                    }

                    var propCell = document.createElement('td');
                    propCell.className = 'serversync-compare-table-property';
                    propCell.textContent = key;

                    var sourceCell = document.createElement('td');
                    sourceCell.className = 'serversync-compare-table-value';
                    sourceCell.textContent = self._formatValue(sourceVal);

                    var localCell = document.createElement('td');
                    localCell.className = 'serversync-compare-table-value';
                    localCell.textContent = self._formatValue(localVal);

                    var mergedCell = document.createElement('td');
                    mergedCell.className = 'serversync-compare-table-value serversync-compare-table-merged';
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
                if (item.HasChanges) {
                    row.className = 'serversync-modal-changed-row';
                }

                var propCell = document.createElement('td');
                propCell.className = 'serversync-compare-table-property';
                propCell.textContent = t.tr('HeaderProfileImage');

                var sourceDisplay = item.SourceImageSizeFormatted || (item.SourceImageSize > 0 ? t.format('ImageSizeBytes', item.SourceImageSize) : t.tr('None'));
                var localDisplay = item.LocalImageSizeFormatted || (item.LocalImageSize > 0 ? t.format('ImageSizeBytes', item.LocalImageSize) : t.tr('None'));

                var sourceCell = document.createElement('td');
                sourceCell.className = 'serversync-compare-table-value';
                sourceCell.textContent = sourceDisplay;

                var localCell = document.createElement('td');
                localCell.className = 'serversync-compare-table-value';
                localCell.textContent = localDisplay;

                var mergedCell = document.createElement('td');
                mergedCell.className = 'serversync-compare-table-value serversync-compare-table-merged';
                mergedCell.textContent = sourceDisplay;

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
                var badge = function(label, item) {
                    var changed = !!item && item.HasChanges === true;
                    html += ServerSyncShared.badge(changed ? 'orange' : 'green', t.format(changed ? 'BadgeCategoryChanges' : 'BadgeCategorySynced', label));
                };

                if (config.UserSyncConfiguration !== false) badge(t.tr('ChangeConfiguration'), detail.ConfigurationItem);
                if (config.UserSyncPolicy !== false) badge(t.tr('ChangePolicy'), detail.PolicyItem);
                if (config.UserSyncProfileImage !== false) badge(t.tr('Image'), detail.ProfileImageItem);

                container.innerHTML = html;
            },

            _formatValue: function(value) {
                if (value === null || value === undefined) {
                    return '-';
                }
                if (typeof value === 'boolean') {
                    return value ? t.tr('Yes') : t.tr('No');
                }
                if (Array.isArray(value)) {
                    return value.length > 0 ? value.join(', ') : '-';
                }
                return String(value);
            },
        }
    });

    return UserSyncTableModule;
}

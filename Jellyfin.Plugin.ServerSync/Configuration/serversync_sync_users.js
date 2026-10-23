// ============================================
// SERVER SYNC PLUGIN - SYNC PAGE: USERS VIEW
// ============================================
// The User Sync table and its detail modal. Built by serversync_sync.js from the page context
// and the table kit in serversync_sync_table.js.
// ============================================

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

                    self._dialog.open();
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

    return UserSyncTableModule;
}

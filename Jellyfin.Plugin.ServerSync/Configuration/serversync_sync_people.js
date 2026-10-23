// ============================================
// SERVER SYNC PLUGIN - SYNC PAGE: PEOPLE VIEW
// ============================================
// The People Sync table and its detail modal. Built by serversync_sync.js from the page context
// and the table kit in serversync_sync_table.js.
// ============================================

export function createPeopleSyncTableModule(ctx, kit) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;
    var createSyncTableModule = kit.createSyncTableModule;
    var renderRowInfo = kit.renderRowInfo;
    var renderChangeBadges = kit.renderChangeBadges;
    var setHealthLastSync = kit.setHealthLastSync;
    var setHealthCount = kit.setHealthCount;
    var parseJsonSafe = kit.parseJsonSafe;
    var formatImageDisplay = kit.formatImageDisplay;
    var showObjectVersion = kit.showObjectVersion;

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

                    self._dialog.open();
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

    return PeopleSyncTableModule;
}

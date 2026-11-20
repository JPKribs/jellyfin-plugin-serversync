// serversync_sync_people.js
// The People Sync table and its detail modal. Built by serversync_sync.js from the page context
// and the table kit in serversync_sync_table.js.

// createPeopleSyncTableModule
// Builds the People Sync view: its table, status counts, health figures, and detail modal.
//
// Inputs
// ctx [object]: The page context from serversync_sync.js.
// kit [object]: The table kit from createSyncTableKit.
//
// Output
// [object]: The view's module, with table, currentConfig, init, loadAll, loadStatus, loadItems, and the
// modal and bulk actions.
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
            refresh: { key: 'ServerSyncRefreshPeopleTable' },
            sync: { key: 'ServerSyncMissingPeople' }
        },
        messages: {
            startRefreshFailed: 'ErrorStartPeopleRefresh',
            startSyncFailed: 'ErrorStartPeopleSync',
            ignoredOne: 'ToastPeopleIgnoredOne',
            ignoredMany: 'ToastPeopleIgnoredMany',
            queuedOne: 'ToastPeopleQueuedOne',
            queuedMany: 'ToastPeopleQueuedMany',
            ignoreFailed: 'ErrorIgnorePeople',
            queueFailed: 'ErrorQueuePeople',
            retryNone: 'ToastNoErroredPeople',
            retriedOne: 'ToastErroredPeopleRetriedOne',
            retriedMany: 'ToastErroredPeopleRetriedMany',
            retriedUnknown: 'ToastErroredPeopleRetried',
            retryFailed: 'ErrorRetryPeople',
            statusUpdated: 'ToastPersonStatusUpdated',
            statusFailed: 'ErrorPersonStatusUpdate'
        },
        searchPlaceholder: t.tr('PlaceholderSearchPeople'),
        emptyMessage: t.tr('EmptyPeopleItems'),
        columns: function() {
            return [
                {
                    key: 'person',
                    label: t.tr('Person'),
                    type: 'custom',
                    className: 'base-table-cell-with-thumb',
                    image: function(item) { return ServerSyncShared.proxyImage(item.SourcePersonId, false, item.ServerKey); },
                    render: function(item) {
                        return renderRowInfo(item.PersonName || t.tr('Unknown'), null, item, item.Status, '');
                    }
                },
                {
                    key: 'changes',
                    label: t.tr('ColumnChanges'),
                    type: 'custom',
                    className: 'serversync-details-cell',
                    render: function(item) {
                        var changes = [];
                        if (item.HasChanges && item.HasMetadataChanges) changes.push(t.tr('LabelMetadata'));
                        if (item.HasChanges && item.HasImagesChanges) changes.push(t.tr('Images'));
                        return renderChangeBadges(changes);
                    }
                },
                { key: 'Status', label: t.tr('HeaderStatus'), type: 'status' }
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
                        ServerSyncShared.syncStatus(t.tr('ErrorPersonNotFound'), true);
                        return;
                    }

                    self.currentModalItem = detail;

                    view.querySelector('#peopleSyncModalTitle').textContent = detail.PersonName || t.tr('Unknown');

                    var statusBadge = view.querySelector('#peopleSyncModalStatusBadge');
                    statusBadge.textContent = statusText(detail.Status);
                    statusBadge.className = 'base-table-status-badge ' + (detail.Status || 'unknown');

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

                    var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, detail.ServerKey);
                    var localServerName = ServerSyncShared.localServerName || t.tr('ServerLocal');
                    view.querySelector('#peopleSyncModalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;
                    view.querySelector('#peopleSyncModalSourceHeader').textContent = sourceServerName;
                    view.querySelector('#peopleSyncModalLocalHeader').textContent = localServerName;

                    var summaryHtml = '';
                    summaryHtml += ServerSyncShared.badge(detail.HasMetadataChanges ? 'orange' : 'green', t.format(detail.HasMetadataChanges ? 'BadgeCategoryChanges' : 'BadgeCategorySynced', t.tr('LabelMetadata')));
                    summaryHtml += ServerSyncShared.badge(detail.HasImagesChanges ? 'orange' : 'green', t.format(detail.HasImagesChanges ? 'BadgeCategoryChanges' : 'BadgeCategorySynced', t.tr('Images')));
                    view.querySelector('#peopleSyncModalChangesSummary').innerHTML = summaryHtml;

                    var tbody = view.querySelector('#peopleSyncModalTableBody');
                    tbody.innerHTML = '';

                    var sourceMetadata = parseJsonSafe(detail.SourceMetadataValue) || {};
                    var localMetadata = parseJsonSafe(detail.LocalMetadataValue) || {};

                    self._addSectionHeader(tbody, t.tr('LabelMetadata'));

                    var metadataFields = [
                        { key: 'Name', label: t.tr('Name') },
                        { key: 'OriginalTitle', label: t.tr('FieldOriginalTitle') },
                        { key: 'ForcedSortName', label: t.tr('FieldForcedSortName') },
                        { key: 'Overview', label: t.tr('Overview'), truncate: true },
                        { key: 'PremiereDate', label: t.tr('FieldBirthDate'), isDate: true },
                        { key: 'EndDate', label: t.tr('FieldDeathDate'), isDate: true },
                        { key: 'ProductionYear', label: t.tr('LabelYear') },
                        { key: 'LockData', label: t.tr('FieldLockItem') },
                        { key: 'LockedFields', label: t.tr('FieldLockedFields') }
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
                    self._addComparisonRow(tbody, t.tr('Tags'),
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
                        self._addComparisonRow(tbody, t.tr('FieldProviderIds'), '-', '-', false);
                    }

                    if (detail.HasMetadataChanges) {
                        self._addChangeDetailRow(tbody, detail.MetadataChangesDetail);
                    }

                    self._addSectionHeader(tbody, t.tr('Images'));

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
                        self._addComparisonRow(tbody, t.tr('Images'), '-', localImages ? t.tr('ImagePresent') : '-', false);
                    }

                    if (detail.HasImagesChanges) {
                        self._addChangeDetailRow(tbody, detail.ImagesChangesDetail);
                    }

                    self._dialog.open();
                }).catch(function(err) {
                    if (seq !== self._detailSeq) return;
                    console.error('Failed to load person detail:', err);
                    ServerSyncShared.syncStatus(t.tr('ErrorPersonDetailsLoad'), true);
                });
            },

            // Same role as the metadata modal's buildChangeDetailRow, DOM-style.
            _addChangeDetailRow: function(tbody, text) {
                if (!text) return;
                var row = document.createElement('tr');
                var cell = document.createElement('td');
                cell.colSpan = 4;
                cell.classList.add('serversync-note-cell');
                cell.textContent = t.format('NoteChanged', text);
                row.appendChild(cell);
                tbody.appendChild(row);
            },

            _addSectionHeader: function(tbody, title) {
                var row = document.createElement('tr');
                row.className = 'serversync-modal-section-header';
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
                    return val.substring(0, 200) + '…';
                }
                if (Array.isArray(val)) {
                    return val.length > 0 ? val.join(', ') : '-';
                }
                if (typeof val === 'boolean') return val ? t.tr('Yes') : t.tr('No');
                if (typeof val === 'object') return JSON.stringify(val);
                return String(val);
            },

            _addComparisonRow: function(tbody, property, sourceVal, localVal, isChanged) {
                var row = document.createElement('tr');
                if (isChanged) row.className = 'serversync-modal-changed-row';

                var propCell = document.createElement('td');
                propCell.className = 'serversync-compare-table-property';
                propCell.textContent = property;

                var sourceCell = document.createElement('td');
                sourceCell.className = 'serversync-compare-table-value';
                sourceCell.textContent = sourceVal || '-';

                var localCell = document.createElement('td');
                localCell.className = 'serversync-compare-table-value';
                localCell.textContent = localVal || '-';

                var afterSyncCell = document.createElement('td');
                afterSyncCell.className = 'serversync-compare-table-value serversync-compare-table-merged';
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

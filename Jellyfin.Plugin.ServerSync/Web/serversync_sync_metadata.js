// serversync_sync_metadata.js
// The Metadata sync table and its detail modal. Built by serversync_sync.js from the page context
// and the table kit in serversync_sync_table.js.

// createMetadataSyncTableModule
// Builds the Metadata sync view: its table, status counts, health figures, and detail modal.
//
// Inputs
// ctx [object]: The page context from serversync_sync.js.
// kit [object]: The table kit from createSyncTableKit.
//
// Output
// [object]: The view's module, with table, currentConfig, init, loadAll, loadStatus, loadItems, and the
// modal and bulk actions.
// formatSegmentTime
// Writes a time in ticks as minutes and seconds, with hours when it has any.
//
// Inputs
// ticks [number]: The time, in ticks of 100 nanoseconds.
//
// Output
// [string]: The time, such as 1:05 or 1:02:05.
function formatSegmentTime(ticks) {
    var total = Math.max(0, Math.floor((Number(ticks) || 0) / 10000000));
    var hours = Math.floor(total / 3600);
    var minutes = Math.floor((total % 3600) / 60);
    var seconds = total % 60;
    var pad = function(n) { return n < 10 ? '0' + n : String(n); };
    return hours > 0 ? hours + ':' + pad(minutes) + ':' + pad(seconds) : minutes + ':' + pad(seconds);
}

export function createMetadataSyncTableModule(ctx, kit) {
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

    // sectionRow
    // Builds the heading row of one category in the comparison table.
    //
    // Inputs
    // title [string]: The category's name, in the viewer's language.
    //
    // Output
    // [string]: The row's HTML.
    function sectionRow(title) {
        return '<tr class="serversync-modal-section-header"><td colspan="4">' + ServerSyncShared.escapeHtml(title) + '</td></tr>';
    }

    var MetadataSyncTableModule = createSyncTableModule({
        name: 'Metadata',
        prefix: 'metadata',
        containerId: 'metadataSyncItemsTableContainer',
        listEndpoint: 'MetadataItems',
        statusEndpoint: 'MetadataStatus',
        itemsEndpoint: 'MetadataItems',
        retryEndpoint: 'MetadataItems/Queue',
        modalId: 'metadataSyncItemDetailModal',
        modalButtonPrefix: 'btnMetadataSyncModal',
        tasks: {
            refresh: { key: 'ServerSyncRefreshMetadataTable' },
            sync: { key: 'ServerSyncMissingMetadata' }
        },
        messages: {
            startRefreshFailed: 'ErrorStartMetadataRefresh',
            startSyncFailed: 'ErrorStartMetadataSync',
            ignoredOne: 'ToastItemsIgnoredOne',
            ignoredMany: 'ToastItemsIgnoredMany',
            queuedOne: 'ToastItemsQueuedOne',
            queuedMany: 'ToastItemsQueuedMany',
            ignoreFailed: 'ErrorIgnoreItems',
            queueFailed: 'ErrorQueueItems',
            retryNone: 'ToastNoErroredMetadata',
            retriedOne: 'ToastErroredMetadataRetriedOne',
            retriedMany: 'ToastErroredMetadataRetriedMany',
            retriedUnknown: 'ToastErroredMetadataRetried',
            retryFailed: 'ErrorRetryMetadata',
            statusUpdated: 'ToastItemStatusUpdated',
            statusFailed: 'ErrorItemStatusUpdate'
        },
        search: true,
        emptyMessage: t.tr('EmptyMetadataItems'),
        columns: function(self) {
            return [
                {
                    key: 'item',
                    label: t.tr('ColumnItem'),
                    type: 'custom',
                    className: 'base-table-cell-with-thumb',
                    image: function(item) { return ServerSyncShared.proxyImage(item.SourceItemId, false, item.ServerKey); },
                    render: function(item) {
                        var libraries = (item.SourceLibraryName || t.tr('Unknown')) + ' → ' + (item.LocalLibraryName || t.tr('Unknown'));
                        return renderRowInfo(item.ItemName || t.tr('Unknown'), libraries, item, item.Status);
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
                        if (config.MetadataSyncMetadata !== false && item.HasMetadataChanges) changes.push(t.tr('LabelMetadata'));
                        if (config.MetadataSyncImages !== false && item.HasImagesChanges) changes.push(t.tr('Images'));
                        if (config.MetadataSyncPeople === true && item.HasPeopleChanges) changes.push(t.tr('People'));
                        if (config.MetadataSyncStudios !== false && item.HasStudiosChanges) changes.push(t.tr('Studios'));
                        if (config.MetadataSyncSegmentProvider === true && item.HasSegmentsChanges) changes.push(t.tr('ChangeSegments'));
                        return renderChangeBadges(changes);
                    }
                },
                { key: 'Status', label: t.tr('HeaderStatus'), type: 'status' }
            ];
        },
        loadHealth: function() {
            return ServerSyncShared.getConfig().then(function(config) {
                setHealthLastSync('metadataHealthLastSync', config.LastMetadataSyncTime);
                setHealthCount('metadataHealthLibraryCount', ServerSyncShared.allLibraryMappings(config).length);
            });
        },
        statusRequest: function(item, status) {
            return { endpoint: 'MetadataItems/' + (status === 'Ignored' ? 'Ignore' : 'Queue'), body: { Ids: [item.Id] } };
        },
        methods: {
            showDetail: function(item) {
                this.showItemDetail(item.Id);
            },

            showItemDetail: function(itemId) {
                var self = this;
                // Only the newest click may fill the modal. A slower reply for an earlier row, or one that
                // lands after the modal was closed, is dropped instead of showing the wrong item.
                var seq = ++self._detailSeq;

                ServerSyncShared.apiRequest('MetadataItems/' + itemId).then(function(item) {
                    if (seq !== self._detailSeq) return;
                    if (!item) {
                        ServerSyncShared.syncStatus(t.tr('ErrorItemNotFound'), true);
                        return;
                    }

                    self.currentModalItem = item;

                    view.querySelector('#metadataSyncModalTitle').textContent = item.ItemName || t.tr('Unknown');

                    var statusBadge = view.querySelector('#metadataSyncModalStatusBadge');
                    statusBadge.textContent = item.Status ? kit.statusName(item.Status) : t.tr('Unknown');
                    statusBadge.className = 'base-table-status-badge ' + item.Status;

                    var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, item.ServerKey);
                    var localServerName = ServerSyncShared.localServerName || t.tr('ServerLocal');
                    view.querySelector('#metadataSyncModalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;

                    if (item.LastSyncTime) {
                        view.querySelector('#metadataSyncModalLastSync').textContent =
                            ServerSyncShared.formatRelativeTime(new Date(item.LastSyncTime));
                    } else {
                        view.querySelector('#metadataSyncModalLastSync').textContent = '-';
                    }
                    showObjectVersion('metadataSyncModalVersion', { kind: 'Metadata', localItemId: item.LocalItemId });

                    var sourceLib = item.SourceLibraryName || t.tr('Unknown');
                    var localLib = item.LocalLibraryName || t.tr('Unknown');
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

                    self._dialog.open();
                }).catch(function(err) {
                    if (seq !== self._detailSeq) return;
                    console.error('Failed to load metadata item details:', err);
                    ServerSyncShared.syncStatus(t.tr('ErrorItemDetailsLoad'), true);
                });
            },

            buildChangesSummary: function(item) {
                var container = view.querySelector('#metadataSyncModalChangesSummary');
                var config = this.currentConfig || {};
                var html = '';
                var badge = function(label, changed) {
                    html += ServerSyncShared.badge(changed ? 'orange' : 'green', t.format(changed ? 'BadgeCategoryChanges' : 'BadgeCategorySynced', label));
                };

                var sourceMetadata = parseJsonSafe(item.SourceMetadataValue) || {};
                var localMetadata = parseJsonSafe(item.LocalMetadataValue) || {};
                var differs = function(source, local) {
                    return JSON.stringify((source || []).slice().sort()) !== JSON.stringify((local || []).slice().sort());
                };

                if (config.MetadataSyncMetadata !== false) badge(t.tr('LabelMetadata'), item.HasMetadataChanges === true);
                if (config.MetadataSyncGenres !== false) badge(t.tr('Genres'), differs(sourceMetadata.Genres, localMetadata.Genres));
                if (config.MetadataSyncTags !== false) badge(t.tr('Tags'), differs(sourceMetadata.Tags, localMetadata.Tags));
                if (config.MetadataSyncImages !== false) badge(t.tr('Images'), item.HasImagesChanges === true);
                if (config.MetadataSyncPeople === true) badge(t.tr('People'), item.HasPeopleChanges === true);
                if (config.MetadataSyncStudios !== false) badge(t.tr('Studios'), item.HasStudiosChanges === true);
                if (config.MetadataSyncSegmentProvider === true) badge(t.tr('ChangeSegments'), item.HasSegmentsChanges === true);

                container.innerHTML = html;
            },

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
                var segmentsEnabled = config.MetadataSyncSegmentProvider === true;

                var sourceMetadata = parseJsonSafe(item.SourceMetadataValue) || {};
                var localMetadata = parseJsonSafe(item.LocalMetadataValue) || {};
                var sourceImages = parseJsonSafe(item.SourceImagesValue);
                var localImages = parseJsonSafe(item.LocalImagesValue);
                var sourcePeople = parseJsonSafe(item.SourcePeopleValue);
                var localPeople = parseJsonSafe(item.LocalPeopleValue);
                var sourceStudios = parseJsonSafe(item.SourceStudiosValue);
                var localStudios = parseJsonSafe(item.LocalStudiosValue);

                if (metadataEnabled) {
                    html += sectionRow(t.tr('LabelMetadata'));
                    html += self.buildCoreMetadataRows(sourceMetadata, localMetadata);
                    if (item.HasMetadataChanges) {
                        html += self.buildChangeDetailRow(item.MetadataChangesDetail);
                    }
                }

                if (genresEnabled) {
                    html += sectionRow(t.tr('Genres'));
                    html += self.buildArrayComparisonRow(t.tr('Genres'), sourceMetadata.Genres, localMetadata.Genres);
                }

                if (tagsEnabled) {
                    html += sectionRow(t.tr('Tags'));
                    html += self.buildArrayComparisonRow(t.tr('Tags'), sourceMetadata.Tags, localMetadata.Tags);
                }

                if (studiosEnabled) {
                    html += sectionRow(t.tr('Studios'));
                    html += self.buildArrayComparisonRow(t.tr('Studios'), sourceStudios, localStudios);
                }

                if (peopleEnabled) {
                    html += sectionRow(t.tr('People'));
                    html += self.buildPeopleComparisonRow(sourcePeople, localPeople);
                }

                if (imagesEnabled) {
                    html += sectionRow(t.tr('Images'));
                    html += self.buildImagesRows(sourceImages, localImages);
                    if (item.HasImagesChanges) {
                        html += self.buildChangeDetailRow(item.ImagesChangesDetail);
                    }
                }

                if (segmentsEnabled) {
                    html += sectionRow(t.tr('HeaderMediaSegments'));
                    html += self.buildSegmentsRows(parseJsonSafe(item.SourceSegmentsValue), parseJsonSafe(item.LocalSegmentsValue));
                }

                if (html === '') {
                    html = '<tr><td colspan="4" class="serversync-empty-cell">' + ServerSyncShared.escapeHtml(t.tr('EmptyNoSyncCategories')) + '</td></tr>';
                }

                tbody.innerHTML = html;
            },

            // Full-width muted note naming the server-computed reason a category's
            // badge says Changes. The visible rows aggregate (image size sums) or
            // render a fixed field list, so without this a badge can be
            // unexplainable from the UI.
            buildChangeDetailRow: function(text) {
                if (!text) return '';
                return '<tr><td colspan="4" class="serversync-note-cell">' +
                    ServerSyncShared.escapeHtml(t.format('NoteChanged', text)) + '</td></tr>';
            },

            buildCoreMetadataRows: function(source, local) {
                var self = this;
                var html = '';

                var metadataFields = [
                    { key: 'Name', label: t.tr('Name') },
                    { key: 'OriginalTitle', label: t.tr('LabelOriginalTitle') },
                    { key: 'ForcedSortName', label: t.tr('FieldForcedSortName') },
                    { key: 'Overview', label: t.tr('Overview'), truncate: true },
                    { key: 'Tagline', label: t.tr('LabelTagline') },
                    { key: 'OfficialRating', label: t.tr('OptionParentalRating') },
                    { key: 'CustomRating', label: t.tr('LabelCustomRating') },
                    { key: 'CommunityRating', label: t.tr('OptionCommunityRating') },
                    { key: 'CriticRating', label: t.tr('FieldCriticRating') },
                    { key: 'PremiereDate', label: t.tr('OptionReleaseDate'), isDate: true },
                    { key: 'EndDate', label: t.tr('LabelEndDate'), isDate: true },
                    { key: 'ProductionYear', label: t.tr('LabelYear') },
                    { key: 'AspectRatio', label: t.tr('AspectRatio') },
                    { key: 'Video3DFormat', label: t.tr('Label3DFormat') },
                    { key: 'IndexNumber', label: t.tr('IndexNumber') },
                    { key: 'ParentIndexNumber', label: t.tr('FieldParentIndexNumber') },
                    { key: 'PreferredMetadataCountryCode', label: t.tr('LabelCountry') },
                    { key: 'PreferredMetadataLanguage', label: t.tr('FieldPreferredLanguage') },
                    { key: 'LockData', label: t.tr('FieldLockItem'), isBoolean: true },
                    { key: 'LockedFields', label: t.tr('FieldLockedFields'), isArray: true }
                ];

                metadataFields.forEach(function(field) {
                    var sourceVal = source[field.key];
                    var localVal = local[field.key];

                    var sourceDisplay = self.formatMetadataValue(sourceVal, field);
                    var localDisplay = self.formatMetadataValue(localVal, field);

                    var isChanged = self.normalizeForComparison(sourceVal, field) !== self.normalizeForComparison(localVal, field);
                    var rowClass = isChanged ? 'serversync-modal-changed-row' : '';

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="serversync-compare-table-property">' + ServerSyncShared.escapeHtml(field.label) + '</td>';
                    html += '<td class="serversync-compare-table-value">' + sourceDisplay + '</td>';
                    html += '<td class="serversync-compare-table-value">' + localDisplay + '</td>';
                    html += '<td class="serversync-compare-table-value serversync-compare-table-merged">' + sourceDisplay + '</td>';
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
                    var rowClass = isChanged ? 'serversync-modal-changed-row' : '';

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="serversync-compare-table-property">' + ServerSyncShared.escapeHtml(key) + '</td>';
                    html += '<td class="serversync-compare-table-value">' + ServerSyncShared.escapeHtml(srcDisplay) + '</td>';
                    html += '<td class="serversync-compare-table-value">' + ServerSyncShared.escapeHtml(lclDisplay) + '</td>';
                    html += '<td class="serversync-compare-table-value serversync-compare-table-merged">' + ServerSyncShared.escapeHtml(srcDisplay) + '</td>';
                    html += '</tr>';
                });

                if (uniqueKeys.length === 0) {
                    html += '<tr>';
                    html += '<td class="serversync-compare-table-property">' + ServerSyncShared.escapeHtml(t.tr('FieldProviderIds')) + '</td>';
                    html += '<td class="serversync-compare-table-value">-</td>';
                    html += '<td class="serversync-compare-table-value">-</td>';
                    html += '<td class="serversync-compare-table-value serversync-compare-table-merged">-</td>';
                    html += '</tr>';
                }

                return html;
            },

            buildArrayComparisonRow: function(label, sourceArray, localArray) {
                var html = '';
                var sourceItems = Array.isArray(sourceArray) ? sourceArray : [];
                var localItems = Array.isArray(localArray) ? localArray : [];

                var sourceCount = sourceItems.length;
                var localCount = localItems.length;

                var countChanged = sourceCount !== localCount;
                var countRowClass = countChanged ? 'serversync-modal-changed-row' : '';
                html += '<tr class="' + countRowClass + '">';
                html += '<td class="serversync-compare-table-property">' + ServerSyncShared.escapeHtml(t.tr('FieldCount')) + '</td>';
                html += '<td class="serversync-compare-table-value">' + sourceCount + '</td>';
                html += '<td class="serversync-compare-table-value">' + localCount + '</td>';
                html += '<td class="serversync-compare-table-value serversync-compare-table-merged">' + sourceCount + '</td>';
                html += '</tr>';

                var sourceDisplay = sourceItems.length > 0 ? ServerSyncShared.escapeHtml(sourceItems.join(', ')) : '-';
                var localDisplay = localItems.length > 0 ? ServerSyncShared.escapeHtml(localItems.join(', ')) : '-';

                var sourceSorted = sourceItems.slice().sort().join(',');
                var localSorted = localItems.slice().sort().join(',');
                var itemsChanged = sourceSorted !== localSorted;
                var itemsRowClass = itemsChanged ? 'serversync-modal-changed-row' : '';

                html += '<tr class="' + itemsRowClass + '">';
                html += '<td class="serversync-compare-table-property">' + ServerSyncShared.escapeHtml(t.tr('Items')) + '</td>';
                html += '<td class="serversync-compare-table-value">' + sourceDisplay + '</td>';
                html += '<td class="serversync-compare-table-value">' + localDisplay + '</td>';
                html += '<td class="serversync-compare-table-value serversync-compare-table-merged">' + sourceDisplay + '</td>';
                html += '</tr>';

                return html;
            },

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
                var countRowClass = countChanged ? 'serversync-modal-changed-row' : '';
                html += '<tr class="' + countRowClass + '">';
                html += '<td class="serversync-compare-table-property">' + ServerSyncShared.escapeHtml(t.tr('FieldCount')) + '</td>';
                html += '<td class="serversync-compare-table-value">' + sourceCount + '</td>';
                html += '<td class="serversync-compare-table-value">' + localCount + '</td>';
                html += '<td class="serversync-compare-table-value serversync-compare-table-merged">' + sourceCount + '</td>';
                html += '</tr>';

                var sourceDisplay = sourceNames.length > 0 ? ServerSyncShared.escapeHtml(sourceNames.join(', ')) : '-';
                var localDisplay = localNames.length > 0 ? ServerSyncShared.escapeHtml(localNames.join(', ')) : '-';

                var sourceSorted = sourceNames.slice().sort().join(',');
                var localSorted = localNames.slice().sort().join(',');
                var itemsChanged = sourceSorted !== localSorted;
                var itemsRowClass = itemsChanged ? 'serversync-modal-changed-row' : '';

                html += '<tr class="' + itemsRowClass + '">';
                html += '<td class="serversync-compare-table-property">' + ServerSyncShared.escapeHtml(t.tr('Items')) + '</td>';
                html += '<td class="serversync-compare-table-value">' + sourceDisplay + '</td>';
                html += '<td class="serversync-compare-table-value">' + localDisplay + '</td>';
                html += '<td class="serversync-compare-table-value serversync-compare-table-merged">' + sourceDisplay + '</td>';
                html += '</tr>';

                return html;
            },

            formatMetadataValue: function(value, field) {
                if (field.isBoolean) {
                    if (value === true) return ServerSyncShared.escapeHtml(t.tr('Yes'));
                    if (value === false) return ServerSyncShared.escapeHtml(t.tr('No'));
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
                    return ServerSyncShared.escapeHtml(value.substring(0, 100) + '…');
                }

                return ServerSyncShared.escapeHtml(String(value));
            },

            formatDateOnly: function(dateStr) {
                if (!dateStr) return '-';
                try {
                    var date = new Date(dateStr);
                    return date.toISOString().split('T')[0];
                } catch (e) {
                    return ServerSyncShared.escapeHtml(String(dateStr));
                }
            },

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

            isEmpty: function(value) {
                if (value === null || value === undefined) return true;
                if (value === '') return true;
                if (Array.isArray(value) && value.length === 0) return true;
                return false;
            },

            // buildSegmentsRows
            // Builds one row for each media segment type on either side, with the times of each segment of that type.
            //
            // Inputs
            // source [object]: The segments the pool holds for the file, with a Segments list, or null.
            // local [object]: The segments the local segment table holds, with a Segments list, or null.
            //
            // Output
            // [string]: The rows' HTML.
            buildSegmentsRows: function(source, local) {
                var html = '';
                var timesOf = function(value, type) {
                    var list = (value && Array.isArray(value.Segments) ? value.Segments : []).filter(function(s) { return s && s.Type === type; });
                    return list.map(function(s) { return t.format('LibraryRoute', formatSegmentTime(s.StartTicks), formatSegmentTime(s.EndTicks)); }).join(', ');
                };

                var types = new Set();
                [source, local].forEach(function(value) {
                    (value && Array.isArray(value.Segments) ? value.Segments : []).forEach(function(s) { if (s && s.Type) types.add(s.Type); });
                });

                Array.from(types).sort().forEach(function(type) {
                    var src = timesOf(source, type);
                    var loc = timesOf(local, type);
                    var after = source ? src : loc;
                    html += '<tr class="' + (src !== loc && source ? 'serversync-modal-changed-row' : '') + '">';
                    html += '<td class="serversync-compare-table-property">' + ServerSyncShared.escapeHtml(type) + '</td>';
                    html += '<td class="serversync-compare-table-value">' + ServerSyncShared.escapeHtml(src || '-') + '</td>';
                    html += '<td class="serversync-compare-table-value">' + ServerSyncShared.escapeHtml(loc || '-') + '</td>';
                    html += '<td class="serversync-compare-table-value serversync-compare-table-merged">' + ServerSyncShared.escapeHtml(after || '-') + '</td>';
                    html += '</tr>';
                });

                if (types.size === 0) {
                    html += '<tr><td colspan="4" class="serversync-empty-cell">' + ServerSyncShared.escapeHtml(t.tr('EmptyNoMediaSegments')) + '</td></tr>';
                }

                return html;
            },

            buildImagesRows: function(sourceImages, localImages) {
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

                    var srcDisplay = formatImageDisplay(srcSize, srcCount);
                    var localDisplay = formatImageDisplay(localSize, localCount);

                    var isChanged = srcCount !== localCount || srcSize !== localSize;
                    var rowClass = isChanged ? 'serversync-modal-changed-row' : '';

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="serversync-compare-table-property">' + ServerSyncShared.escapeHtml(imageType) + '</td>';
                    html += '<td class="serversync-compare-table-value">' + srcDisplay + '</td>';
                    html += '<td class="serversync-compare-table-value">' + localDisplay + '</td>';
                    html += '<td class="serversync-compare-table-value serversync-compare-table-merged">' + srcDisplay + '</td>';
                    html += '</tr>';
                });

                if (typesArray.length === 0) {
                    html += '<tr><td colspan="4" class="serversync-empty-cell">' + ServerSyncShared.escapeHtml(t.tr('EmptyNoImages')) + '</td></tr>';
                }

                return html;
            },
        }
    });

    return MetadataSyncTableModule;
}

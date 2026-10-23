// ============================================
// SERVER SYNC PLUGIN - SYNC PAGE: METADATA VIEW
// ============================================
// The Metadata Sync table and its detail modal. Built by serversync_sync.js from the page context
// and the table kit in serversync_sync_table.js.
// ============================================

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

    // ============================================
    // METADATA SYNC TABLE MODULE
    // ============================================

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
            refresh: { endpoint: 'TriggerMetadataRefresh', key: 'ServerSyncRefreshMetadataTable' },
            sync: { endpoint: 'TriggerMetadataSync', key: 'ServerSyncMissingMetadata' }
        },
        taskNoun: 'metadata',
        singularNoun: 'Item',
        pluralNoun: 'item(s)',
        retryNoun: 'metadata item(s)',
        failNoun: 'items',
        searchPlaceholder: 'Search items...',
        emptyMessage: 'No metadata items found. Run a refresh to scan for metadata.',
        columns: function(self) {
            return [
                {
                    key: 'item',
                    label: 'Item',
                    type: 'custom',
                    className: 'jpk-table-cell-with-thumb',
                    render: function(item) {
                        var libraries = (item.SourceLibraryName || 'Unknown') + ' → ' + (item.LocalLibraryName || 'Unknown');
                        return renderRowInfo(ServerSyncShared.renderItemThumb(item.SourceItemId, item.ServerKey),
                            item.ItemName || 'Unknown', libraries, item, item.Status);
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
                        if (config.MetadataSyncMetadata !== false && item.HasMetadataChanges) changes.push('Metadata');
                        if (config.MetadataSyncImages !== false && item.HasImagesChanges) changes.push('Images');
                        if (config.MetadataSyncPeople === true && item.HasPeopleChanges) changes.push('People');
                        if (config.MetadataSyncStudios !== false && item.HasStudiosChanges) changes.push('Studios');
                        return renderChangeBadges(changes);
                    }
                },
                { key: 'Status', label: 'Status', type: 'status' }
            ];
        },
        loadHealth: function() {
            return ServerSyncShared.getConfig().then(function(config) {
                setHealthLastSync('metadataHealthLastSync', config.LastMetadataSyncTime);
                setHealthCount('metadataHealthLibraryCount', ServerSyncShared.allLibraryMappings(config).length);
            });
        },
        statusRequest: function(item, status) {
            return { endpoint: 'MetadataItems/UpdateStatus', body: { Id: item.Id, Status: status } };
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
                        ServerSyncShared.showAlert('Item not found');
                        return;
                    }

                    self.currentModalItem = item;

                    view.querySelector('#metadataSyncModalTitle').textContent = item.ItemName || 'Unknown';

                    var statusBadge = view.querySelector('#metadataSyncModalStatusBadge');
                    statusBadge.textContent = item.Status;
                    statusBadge.className = 'itemModal-statusBadge ' + item.Status;

                    var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, item && item.ServerKey);
                    var localServerName = ServerSyncShared.localServerName || 'Local';
                    view.querySelector('#metadataSyncModalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;

                    if (item.LastSyncTime) {
                        view.querySelector('#metadataSyncModalLastSync').textContent =
                            ServerSyncShared.formatRelativeTime(new Date(item.LastSyncTime));
                    } else {
                        view.querySelector('#metadataSyncModalLastSync').textContent = '-';
                    }
                    showObjectVersion('metadataSyncModalVersion', { kind: 'Metadata', localItemId: item.LocalItemId });

                    var sourceLib = item.SourceLibraryName || 'Unknown';
                    var localLib = item.LocalLibraryName || 'Unknown';
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
                    ServerSyncShared.showAlert('Failed to load item details');
                });
            },

            buildChangesSummary: function(item) {
                var container = view.querySelector('#metadataSyncModalChangesSummary');
                var config = this.currentConfig || {};
                var html = '';

                var metadataEnabled = config.MetadataSyncMetadata !== false;
                var genresEnabled = config.MetadataSyncGenres !== false;
                var tagsEnabled = config.MetadataSyncTags !== false;
                var studiosEnabled = config.MetadataSyncStudios !== false;
                var peopleEnabled = config.MetadataSyncPeople === true;
                var imagesEnabled = config.MetadataSyncImages !== false;

                var sourceMetadata = parseJsonSafe(item.SourceMetadataValue) || {};
                var localMetadata = parseJsonSafe(item.LocalMetadataValue) || {};

                if (metadataEnabled) {
                    var hasMetadataChanges = item.HasMetadataChanges === true;
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasMetadataChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Metadata: ' + (hasMetadataChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (genresEnabled) {
                    var sourceGenres = sourceMetadata.Genres || [];
                    var localGenres = localMetadata.Genres || [];
                    var hasGenresChanges = JSON.stringify(sourceGenres.slice().sort()) !== JSON.stringify(localGenres.slice().sort());
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasGenresChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Genres: ' + (hasGenresChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (tagsEnabled) {
                    var sourceTags = sourceMetadata.Tags || [];
                    var localTags = localMetadata.Tags || [];
                    var hasTagsChanges = JSON.stringify(sourceTags.slice().sort()) !== JSON.stringify(localTags.slice().sort());
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasTagsChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Tags: ' + (hasTagsChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (imagesEnabled) {
                    var hasImagesChanges = item.HasImagesChanges === true;
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasImagesChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Images: ' + (hasImagesChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (peopleEnabled) {
                    var hasPeopleChanges = item.HasPeopleChanges === true;
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasPeopleChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'People: ' + (hasPeopleChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

                if (studiosEnabled) {
                    var hasStudiosChanges = item.HasStudiosChanges === true;
                    html += '<span class="metadataSyncModal-changesBadge ' + (hasStudiosChanges ? 'has-changes' : 'no-changes') + '">';
                    html += 'Studios: ' + (hasStudiosChanges ? 'Changes' : 'Synced');
                    html += '</span>';
                }

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

                var sourceMetadata = parseJsonSafe(item.SourceMetadataValue) || {};
                var localMetadata = parseJsonSafe(item.LocalMetadataValue) || {};
                var sourceImages = parseJsonSafe(item.SourceImagesValue);
                var localImages = parseJsonSafe(item.LocalImagesValue);
                var sourcePeople = parseJsonSafe(item.SourcePeopleValue);
                var localPeople = parseJsonSafe(item.LocalPeopleValue);
                var sourceStudios = parseJsonSafe(item.SourceStudiosValue);
                var localStudios = parseJsonSafe(item.LocalStudiosValue);

                if (metadataEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Metadata</td></tr>';
                    html += self.buildCoreMetadataRows(sourceMetadata, localMetadata);
                    if (item.HasMetadataChanges) {
                        html += self.buildChangeDetailRow(item.MetadataChangesDetail);
                    }
                }

                if (genresEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Genres</td></tr>';
                    html += self.buildArrayComparisonRow('Genres', sourceMetadata.Genres, localMetadata.Genres);
                }

                if (tagsEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Tags</td></tr>';
                    html += self.buildArrayComparisonRow('Tags', sourceMetadata.Tags, localMetadata.Tags);
                }

                if (studiosEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Studios</td></tr>';
                    html += self.buildArrayComparisonRow('Studios', sourceStudios, localStudios);
                }

                if (peopleEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">People</td></tr>';
                    html += self.buildPeopleComparisonRow(sourcePeople, localPeople);
                }

                if (imagesEnabled) {
                    html += '<tr class="metadataSyncModal-sectionHeader"><td colspan="4">Images</td></tr>';
                    html += self.buildImagesRows(sourceImages, localImages, item);
                    if (item.HasImagesChanges) {
                        html += self.buildChangeDetailRow(item.ImagesChangesDetail);
                    }
                }

                if (html === '') {
                    html = '<tr><td colspan="4" style="text-align: center; opacity: 0.5;">No sync categories are enabled</td></tr>';
                }

                tbody.innerHTML = html;
            },

            // Full-width muted note naming the server-computed reason a category's
            // badge says Changes. The visible rows aggregate (image size sums) or
            // render a fixed field list, so without this a badge can be
            // unexplainable from the UI.
            buildChangeDetailRow: function(text) {
                if (!text) return '';
                return '<tr><td colspan="4" style="opacity:0.6;font-size:0.85em;">Changed: ' +
                    ServerSyncShared.escapeHtml(text) + '</td></tr>';
            },

            buildCoreMetadataRows: function(source, local) {
                var self = this;
                var html = '';

                var metadataFields = [
                    { key: 'Name', label: 'Name' },
                    { key: 'OriginalTitle', label: 'Original Title' },
                    { key: 'ForcedSortName', label: 'Forced Sort Name' },
                    { key: 'Overview', label: 'Overview', truncate: true },
                    { key: 'Tagline', label: 'Tagline' },
                    { key: 'OfficialRating', label: 'Parental Rating' },
                    { key: 'CustomRating', label: 'Custom Rating' },
                    { key: 'CommunityRating', label: 'Community Rating' },
                    { key: 'CriticRating', label: 'Critic Rating' },
                    { key: 'PremiereDate', label: 'Release Date', isDate: true },
                    { key: 'EndDate', label: 'End Date', isDate: true },
                    { key: 'ProductionYear', label: 'Year' },
                    { key: 'AspectRatio', label: 'Aspect Ratio' },
                    { key: 'Video3DFormat', label: '3D Format' },
                    { key: 'IndexNumber', label: 'Index Number' },
                    { key: 'ParentIndexNumber', label: 'Parent Index Number' },
                    { key: 'PreferredMetadataCountryCode', label: 'Country/Region' },
                    { key: 'PreferredMetadataLanguage', label: 'Preferred Language' },
                    { key: 'LockData', label: 'Lock Item', isBoolean: true },
                    { key: 'LockedFields', label: 'Locked Fields', isArray: true }
                ];

                metadataFields.forEach(function(field) {
                    var sourceVal = source[field.key];
                    var localVal = local[field.key];

                    var sourceDisplay = self.formatMetadataValue(sourceVal, field);
                    var localDisplay = self.formatMetadataValue(localVal, field);

                    var isChanged = self.normalizeForComparison(sourceVal, field) !== self.normalizeForComparison(localVal, field);
                    var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                    var mergedDisplay = sourceDisplay;

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(field.label) + '</td>';
                    html += '<td class="historyCompareTable-value">' + sourceDisplay + '</td>';
                    html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                    html += '<td class="historyCompareTable-value historyCompareTable-merged">' + mergedDisplay + '</td>';
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
                    var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(key) + '</td>';
                    html += '<td class="historyCompareTable-value">' + ServerSyncShared.escapeHtml(srcDisplay) + '</td>';
                    html += '<td class="historyCompareTable-value">' + ServerSyncShared.escapeHtml(lclDisplay) + '</td>';
                    html += '<td class="historyCompareTable-value historyCompareTable-merged">' + ServerSyncShared.escapeHtml(srcDisplay) + '</td>';
                    html += '</tr>';
                });

                if (uniqueKeys.length === 0) {
                    html += '<tr>';
                    html += '<td class="historyCompareTable-property">Provider IDs</td>';
                    html += '<td class="historyCompareTable-value">-</td>';
                    html += '<td class="historyCompareTable-value">-</td>';
                    html += '<td class="historyCompareTable-value historyCompareTable-merged">-</td>';
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
                var countRowClass = countChanged ? 'metadataSyncModal-changedRow' : '';
                html += '<tr class="' + countRowClass + '">';
                html += '<td class="historyCompareTable-property">Count</td>';
                html += '<td class="historyCompareTable-value">' + sourceCount + '</td>';
                html += '<td class="historyCompareTable-value">' + localCount + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceCount + '</td>';
                html += '</tr>';

                var sourceDisplay = sourceItems.length > 0 ? ServerSyncShared.escapeHtml(sourceItems.join(', ')) : '-';
                var localDisplay = localItems.length > 0 ? ServerSyncShared.escapeHtml(localItems.join(', ')) : '-';

                var sourceSorted = sourceItems.slice().sort().join(',');
                var localSorted = localItems.slice().sort().join(',');
                var itemsChanged = sourceSorted !== localSorted;
                var itemsRowClass = itemsChanged ? 'metadataSyncModal-changedRow' : '';

                html += '<tr class="' + itemsRowClass + '">';
                html += '<td class="historyCompareTable-property">Items</td>';
                html += '<td class="historyCompareTable-value">' + sourceDisplay + '</td>';
                html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceDisplay + '</td>';
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
                var countRowClass = countChanged ? 'metadataSyncModal-changedRow' : '';
                html += '<tr class="' + countRowClass + '">';
                html += '<td class="historyCompareTable-property">Count</td>';
                html += '<td class="historyCompareTable-value">' + sourceCount + '</td>';
                html += '<td class="historyCompareTable-value">' + localCount + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceCount + '</td>';
                html += '</tr>';

                var sourceDisplay = sourceNames.length > 0 ? ServerSyncShared.escapeHtml(sourceNames.join(', ')) : '-';
                var localDisplay = localNames.length > 0 ? ServerSyncShared.escapeHtml(localNames.join(', ')) : '-';

                var sourceSorted = sourceNames.slice().sort().join(',');
                var localSorted = localNames.slice().sort().join(',');
                var itemsChanged = sourceSorted !== localSorted;
                var itemsRowClass = itemsChanged ? 'metadataSyncModal-changedRow' : '';

                html += '<tr class="' + itemsRowClass + '">';
                html += '<td class="historyCompareTable-property">Items</td>';
                html += '<td class="historyCompareTable-value">' + sourceDisplay + '</td>';
                html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                html += '<td class="historyCompareTable-value historyCompareTable-merged">' + sourceDisplay + '</td>';
                html += '</tr>';

                return html;
            },

            formatMetadataValue: function(value, field) {
                if (field.isBoolean) {
                    if (value === true) return 'Yes';
                    if (value === false) return 'No';
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
                    return ServerSyncShared.escapeHtml(value.substring(0, 100) + '...');
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

            buildImagesRows: function(sourceImages, localImages, item) {
                var self = this;
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
                    var rowClass = isChanged ? 'metadataSyncModal-changedRow' : '';

                    html += '<tr class="' + rowClass + '">';
                    html += '<td class="historyCompareTable-property">' + ServerSyncShared.escapeHtml(imageType) + '</td>';
                    html += '<td class="historyCompareTable-value">' + srcDisplay + '</td>';
                    html += '<td class="historyCompareTable-value">' + localDisplay + '</td>';
                    html += '<td class="historyCompareTable-value historyCompareTable-merged">' + srcDisplay + '</td>';
                    html += '</tr>';
                });

                if (typesArray.length === 0) {
                    html += '<tr><td colspan="4" style="text-align: center; opacity: 0.5;">No images</td></tr>';
                }

                return html;
            },
        }
    });

    return MetadataSyncTableModule;
}

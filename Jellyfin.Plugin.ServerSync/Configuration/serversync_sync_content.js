// serversync_sync_content.js
// The Content Sync table and the controller that opens it. Built by serversync_sync.js from the
// page context and the table kit in serversync_sync_table.js.

// createContentSyncTableModule
// Builds the Content Sync view: its table, status counts, health figures, detail modal, and bulk actions.
//
// Inputs
// ctx [object]: The page context from serversync_sync.js.
// kit [object]: The table kit from createSyncTableKit.
//
// Output
// [object]: The view's module, with table, currentConfig, init, loadAll, loadStatus, loadItems, and the
// modal and bulk actions.
export function createContentSyncTableModule(ctx, kit) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;
    var confirmDialog = ctx.confirmDialog;
    var createSyncTableModule = kit.createSyncTableModule;
    var renderRowInfo = kit.renderRowInfo;
    var setHealthLastSync = kit.setHealthLastSync;
    var setHealthCount = kit.setHealthCount;

    var SyncTableModule = createSyncTableModule({
        name: '',
        prefix: '',
        containerId: 'syncItemsTableContainer',
        listEndpoint: 'Items',
        statusEndpoint: 'Status',
        itemsEndpoint: '',
        modalId: 'itemDetailModal',
        modalButtonPrefix: 'btnModal',
        extraModalButtons: [
            { id: 'MarkSynced', method: 'modalMarkSynced' },
            { id: 'Keep', method: 'modalKeep' },
            { id: 'Delete', method: 'modalDelete' }
        ],
        bulkButtons: [
            { id: 'Ignore', title: 'Ignore', icon: 'block', method: 'bulkIgnore' },
            { id: 'MarkSynced', title: 'Mark Synced', icon: 'check_circle', method: 'bulkMarkSynced' },
            { id: 'Queue', title: 'Queue', icon: 'playlist_add', className: 'jpk-button-submit', method: 'bulkQueue' },
            { id: 'MakeCopy', title: 'Keep a Copy', icon: 'push_pin', method: 'bulkMakeCopy' },
            { id: 'Delete', title: 'Delete Local File', icon: 'delete', className: 'jpk-button-destructive', method: 'bulkDelete' }
        ],
        idKey: 'SourceItemId',
        tasks: {
            refresh: { key: 'ServerSyncUpdateTables' },
            sync: { key: 'ServerSyncDownloadContent' }
        },
        taskNoun: '',
        singularNoun: 'Item',
        nounOne: 'item',
        nounMany: 'items',
        failNoun: 'items',
        emptyMessage: 'No content items found. Run a refresh to scan for content.',
        filters: {
            options: [
                { value: 'Synced', label: 'Synced' },
                { value: 'Linked', label: 'Linked' },
                { value: 'Queued', label: 'Queued' },
                { value: 'Errored', label: 'Errored' },
                { value: 'Ignored', label: 'Ignored' },
                { value: 'Pending', label: 'Pending' },
                { value: 'Cached', label: 'Cached' },
                { value: 'Pending:Download', label: 'Pending Download', id: 'optPendingDownload', hidden: true },
                { value: 'Pending:Replacement', label: 'Pending Replacement', id: 'optPendingReplacement', hidden: true },
                { value: 'Pending:Deletion', label: 'Pending Deletion', id: 'optPendingDeletion', hidden: true },
                { value: 'Deleting', label: 'Deleting', id: 'optDeleting', hidden: true }
            ],
            buildParams: function(filterValue) {
                if (filterValue === 'Cached') {
                    return { cached: true };
                }
                // A synced row is a copy or a link, so the two are filtered apart.
                if (filterValue === 'Synced') {
                    return { status: 'Synced', storage: 'Copy' };
                }
                if (filterValue === 'Linked') {
                    return { status: 'Synced', storage: 'Link' };
                }
                if (filterValue.indexOf(':') > -1) {
                    var parts = filterValue.split(':');
                    return { status: parts[0], pendingType: parts[1] };
                }
                return { status: filterValue };
            }
        },
        tableOptions: function(self) {
            return {
                getDisplayStatus: function(item) { return self.getDisplayStatus(item); },
                getStatusClass: function(item) { return self.getStatusClass(item); }
            };
        },
        columns: function() {
            return [
                {
                    key: 'name',
                    label: 'Item',
                    type: 'custom',
                    className: 'jpk-table-cell-with-thumb',
                    render: function(item) {
                        var sourcePath = item.SourcePath || '';
                        var libraries = (item.SourceLibraryName || 'Unknown') + ' → ' + (item.LocalLibraryName || 'Unknown');
                        return renderRowInfo(ServerSyncShared.renderItemThumb(item.SourceItemId, item.ServerKey),
                            ServerSyncShared.getFileName(sourcePath), libraries, item, item.Status, sourcePath);
                    }
                },
                {
                    key: 'details',
                    label: 'Details',
                    type: 'custom',
                    className: 'serversync-details-cell',
                    render: function(item) {
                        if (item.Status === 'Synced') {
                            return '<span class="jpk-badge gray">No Changes</span>';
                        }
                        var size = item.SourceSizeFormatted || '';
                        return size ? '<span class="jpk-badge purple">' + ServerSyncShared.escapeHtml(size) + '</span>' : '';
                    }
                },
                { key: 'Status', label: 'Status', type: 'status' }
            ];
        },
        loadHealth: function() {
            return Promise.all([
                ServerSyncShared.getConfig(),
                ServerSyncShared.apiRequest('PendingSize', 'GET')
            ]).then(function(results) {
                var pendingSizeData = results[1];
                setHealthLastSync('healthLastSync', results[0].LastSyncEndTime);
                setHealthCount('healthLibraryCount', ServerSyncShared.allLibraryMappings(results[0]).length);

                var pendingCountEl = view.querySelector('#healthPendingCount');
                if (pendingSizeData && typeof pendingSizeData.TotalPendingBytes === 'number') {
                    pendingCountEl.textContent = ServerSyncShared.formatSize(pendingSizeData.TotalPendingBytes);
                    pendingCountEl.className = pendingSizeData.TotalPendingBytes > 0 ? 'serversync-health-value warning' : 'serversync-health-value';
                } else {
                    pendingCountEl.textContent = '0 B';
                    pendingCountEl.className = 'serversync-health-value';
                }
            });
        },
        statusRequest: function(item, status) {
            return { endpoint: status === 'Ignored' ? 'IgnoreItems' : 'QueueItems', body: { SourceItemIds: [item.SourceItemId] } };
        },
        methods: {
            loadStatus: function() {
                var self = this;
                return ServerSyncShared.apiRequest('Status', 'GET').then(function(status) {
                    self._showStatusCounts(status);

                    var pendingDownload = status.PendingDownload || 0;
                    var pendingReplacement = status.PendingReplacement || 0;
                    var pendingDeletion = status.PendingDeletion || 0;
                    var totalPending = pendingDownload + pendingReplacement + pendingDeletion;
                    var show = function(countId, groupId, count, title) {
                        view.querySelector('#' + countId).textContent = count;
                        view.querySelector('#' + groupId).setAttribute('title', title);
                    };

                    show('pendingCount', 'statusGroupPending', totalPending,
                        'Pending: ' + totalPending + ' (Download: ' + pendingDownload + ', Replacement: ' + pendingReplacement + ', Deletion: ' + pendingDeletion + ')');
                    show('pendingDownloadCount', 'statusGroupPendingDownload', pendingDownload, 'Pending Download: ' + pendingDownload);
                    show('pendingReplacementCount', 'statusGroupPendingReplacement', pendingReplacement, 'Pending Replacement: ' + pendingReplacement);
                    show('pendingDeletionCount', 'statusGroupPendingDeletion', pendingDeletion, 'Pending Deletion: ' + pendingDeletion);
                    show('deletingCount', 'statusGroupDeleting', status.Deleting || 0, 'Deleting: ' + (status.Deleting || 0));
                }).catch(function() {
                    // The status endpoint is not available yet.
                });
            },

            retryErrors: function() {
                var self = this;
                ServerSyncShared.apiRequest('RetryErroredItems', 'POST', {}).then(function() {
                    ServerSyncShared.syncStatus('Errored items queued for retry.', false);
                    self.loadStatus();
                    self.loadItems();
                }).catch(function() {
                    ServerSyncShared.syncStatus('Could not retry the errored items.', true);
                });
            },

            bulkIgnore: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                this.postSelection('IgnoreItems', { SourceItemIds: ids }, ServerSyncShared.plural(ids.length, 'item', 'items') + ' ignored.', 'Could not ignore the items.');
            },

            // Pending deletions cannot be queued, so they are left out.
            bulkQueue: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;

                var items = this.table.getItems();
                var queueable = ids.filter(function(id) {
                    var item = items.find(function(i) { return i.SourceItemId === id; });
                    return !(item && item.Status === 'Pending' && item.PendingType === 'Deletion');
                });
                if (queueable.length === 0) {
                    ServerSyncShared.syncStatus('No items to queue. Items pending deletion cannot be queued.', false);
                    return;
                }

                this.postSelection('QueueItems', { SourceItemIds: queueable }, ServerSyncShared.plural(queueable.length, 'item', 'items') + ' queued.', 'Could not queue the items.');
            },

            // The server checks the local files exist before marking.
            bulkMarkSynced: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                this.postSelection('MarkSynced', { SourceItemIds: ids }, function(result) {
                    var msg = ServerSyncShared.plural((result && result.Synced) || 0, 'item', 'items') + ' marked as synced';
                    if (result && result.NotFound > 0) {
                        msg += ', ' + ServerSyncShared.plural(result.NotFound, 'local file', 'local files') + ' not found';
                    }
                    return msg + '.';
                }, 'Could not mark the items as synced.');
            },

            // Only links can become copies. The server skips anything else, and moves cached files into place now.
            bulkMakeCopy: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                this.postSelection('PromoteItems', { SourceItemIds: ids }, this.keepMessage, 'Could not keep the local copies.');
            },

            modalKeep: function() {
                var self = this;
                if (!this.currentModalItem) return;
                ServerSyncShared.apiRequest('PromoteItems', 'POST', { SourceItemIds: [this.currentModalItem.SourceItemId] }).then(function(result) {
                    self.closeModal();
                    ServerSyncShared.syncStatus(self.keepMessage(result), false);
                }).catch(function() {
                    ServerSyncShared.syncStatus('Could not keep a local copy.', true);
                });
            },

            keepMessage: function(result) {
                var promoted = (result && result.Promoted) || 0;
                var fromCache = (result && result.FromCache) || 0;
                var msg = fromCache > 0
                    ? ServerSyncShared.plural(fromCache, 'cached link is', 'cached links are') + ' being moved into the library now'
                    : ServerSyncShared.plural(promoted, 'link', 'links') + ' will be copied on the next sync';
                if (fromCache > 0 && promoted > fromCache) {
                    msg += ', and ' + (promoted - fromCache) + ' will be downloaded';
                }
                if (result && result.Skipped > 0) {
                    msg += ', ' + ServerSyncShared.plural(result.Skipped, 'item was not a link', 'items were not links');
                }
                return msg + '.';
            },

            bulkDelete: function() {
                var self = this;
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                confirmDialog('Delete ' + ServerSyncShared.plural(ids.length, 'item', 'items') + ' from this server? This cannot be undone.', {
                    title: 'Delete Items',
                    confirmText: 'Delete',
                    destructive: true
                }).then(function(confirmed) {
                    if (!confirmed) return;
                    self.postSelection('DeleteLocalItems', { SourceItemIds: ids }, function(result) {
                        return result && result.Deleted > 0 ? 'Deleted ' + ServerSyncShared.plural(result.Deleted, 'item', 'items') + '.' : null;
                    }, 'Could not delete the items.');
                });
            },

            getDisplayStatus: function(item) {
                var linked = item.StorageMode === 'Link' || item.StorageMode === 'Promoting';
                if (item.Status === 'Synced') {
                    if (!linked) return 'Synced';
                    return item.StorageMode === 'Promoting' ? 'Linked (Copy Next)' : item.Cached ? 'Linked (Cached)' : 'Linked';
                }
                var suffix = item.StorageMode === 'Link' ? (item.Cached ? ' (Link, Cached)' : ' (Link)')
                    : item.StorageMode === 'Promoting' ? ' (Link to Copy)'
                    : item.StorageMode === 'Unkeeping' ? ' (Copy to Link)' : '';
                if (item.Status === 'Pending' && item.PendingType) {
                    return 'Pending ' + item.PendingType + suffix;
                }
                return item.Status + suffix;
            },

            getStatusClass: function(item) {
                if (item.Status === 'Pending' && item.PendingType) {
                    return 'Pending-' + item.PendingType;
                }
                return item.Status;
            },

            showDetail: function(item) {
                this.showItemDetail(item.SourceItemId);
            },

            showItemDetail: function(sourceItemId) {
                var self = this;
                var items = this.table.getItems();
                var item = items.find(function(i) { return i.SourceItemId === sourceItemId; });
                if (!item) return;

                self.currentModalItem = item;

                view.querySelector('#modalTitle').textContent = ServerSyncShared.getFileName(item.SourcePath) || item.ItemName || 'Unknown';

                var statusBadge = view.querySelector('#modalStatusBadge');
                var displayStatus = self.getDisplayStatus(item);
                var statusClass = self.getStatusClass(item);
                statusBadge.textContent = displayStatus;
                statusBadge.className = 'jpk-table-status-badge ' + statusClass;

                var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, item.ServerKey);
                var localServerName = ServerSyncShared.localServerName || 'Local';
                view.querySelector('#modalServerMapping').textContent = sourceServerName + ' \u2192 ' + localServerName;

                if (item.LastSyncTime) {
                    var lastSync = new Date(item.LastSyncTime);
                    view.querySelector('#modalLastSync').textContent = ServerSyncShared.formatRelativeTime(lastSync);
                } else {
                    view.querySelector('#modalLastSync').textContent = '-';
                }

                var sourceLibrary = item.SourceLibraryName || 'Unknown';
                var localLibrary = item.LocalLibraryName || 'Unknown';
                view.querySelector('#modalLibraryMapping').textContent = sourceLibrary + ' \u2192 ' + localLibrary;

                var errorSection = view.querySelector('#modalErrorSection');
                if (item.Status === 'Errored' && item.ErrorMessage) {
                    view.querySelector('#modalError').textContent = item.ErrorMessage;
                    errorSection.classList.remove('hidden');
                } else {
                    errorSection.classList.add('hidden');
                }

                var retrySection = view.querySelector('#modalRetrySection');
                if (item.RetryCount > 0) {
                    view.querySelector('#modalRetryCount').textContent = item.RetryCount + ' attempt' + (item.RetryCount > 1 ? 's' : '');
                    retrySection.classList.remove('hidden');
                } else {
                    retrySection.classList.add('hidden');
                }

                var companionSection = view.querySelector('#modalCompanionFilesSection');
                if (item.CompanionFiles) {
                    var companionList = item.CompanionFiles.split(',').map(function(f) {
                        return f.trim();
                    }).filter(function(f) {
                        return f.length > 0;
                    });
                    if (companionList.length > 0) {
                        view.querySelector('#modalCompanionFiles').innerHTML = companionList.map(function(f) {
                            return '<div class="serversync-companion-item">' +
                                '<span class="serversync-companion-icon material-icons" aria-hidden="true">description</span>' +
                                '<span class="serversync-companion-name">' + ServerSyncShared.escapeHtml(f) + '</span>' +
                                '</div>';
                        }).join('');
                        companionSection.classList.remove('hidden');
                    } else {
                        companionSection.classList.add('hidden');
                    }
                } else {
                    companionSection.classList.add('hidden');
                }

                view.querySelector('#modalSourcePath').textContent = item.SourcePath || 'N/A';
                view.querySelector('#modalSourceSize').textContent = ServerSyncShared.formatSize(item.SourceSize);

                var localPathEl = view.querySelector('#modalLocalPath');
                var localPathNoteEl = view.querySelector('#modalLocalPathNote');
                var localSizeRowEl = view.querySelector('#modalLocalSizeRow');
                var localSizeEl = view.querySelector('#modalLocalSize');
                var localExists = item.Status === 'Synced';

                if (item.LocalPath) {
                    localPathEl.textContent = item.LocalPath;
                    if (localExists) {
                        localPathNoteEl.textContent = '';
                        localPathNoteEl.classList.add('hidden');
                        localSizeEl.textContent = ServerSyncShared.formatSize(item.LocalSize || item.SourceSize);
                        localSizeRowEl.classList.remove('hidden');
                    } else {
                        localPathNoteEl.textContent = 'The file will be synced to this location.';
                        localPathNoteEl.classList.remove('hidden');
                        localSizeRowEl.classList.add('hidden');
                    }
                } else {
                    localPathEl.textContent = 'N/A';
                    localPathNoteEl.classList.add('hidden');
                    localSizeRowEl.classList.add('hidden');
                }

                var btnQueue = view.querySelector('#btnModalQueue');
                var btnMarkSynced = view.querySelector('#btnModalMarkSynced');
                var modalDeleteBtn = view.querySelector('#btnModalDelete');
                var isPendingDeletion = item.Status === 'Pending' && item.PendingType === 'Deletion';
                var isPendingDownloadOrReplacement = item.Status === 'Pending' && (item.PendingType === 'Download' || item.PendingType === 'Replacement');
                var isSynced = item.Status === 'Synced';
                var isQueuedOrErrored = item.Status === 'Queued' || item.Status === 'Errored';

                btnQueue.querySelector('span').textContent = isSynced ? 'Resync' : 'Queue';

                btnMarkSynced.classList.toggle('hidden', !isQueuedOrErrored);

                // Only a link can be kept as a copy, and not one already on its way or on its way out.
                view.querySelector('#btnModalKeep').classList.toggle('hidden', !(item.StorageMode === 'Link' && !isPendingDeletion
                    && item.Status !== 'Ignored' && item.Status !== 'Deleting'));

                if (isPendingDeletion) {
                    btnQueue.classList.add('hidden');
                    modalDeleteBtn.classList.remove('hidden');
                } else if (isPendingDownloadOrReplacement) {
                    btnQueue.classList.remove('hidden');
                    modalDeleteBtn.classList.add('hidden');
                } else {
                    btnQueue.classList.remove('hidden');
                    modalDeleteBtn.classList.remove('hidden');
                }

                self._dialog.open();
            },

            // A pending deletion cannot be queued.
            modalQueue: function() {
                var item = this.currentModalItem;
                if (item && !(item.Status === 'Pending' && item.PendingType === 'Deletion')) {
                    this.setModalStatus('Queued');
                }
            },

            modalMarkSynced: function() {
                var self = this;
                if (!this.currentModalItem) return;

                ServerSyncShared.apiRequest('MarkSynced', 'POST', { SourceItemIds: [this.currentModalItem.SourceItemId] }).then(function(result) {
                    self.closeModal();
                    if (result && result.Synced > 0) {
                        ServerSyncShared.syncStatus('Item marked as synced.', false);
                    } else {
                        ServerSyncShared.syncStatus('Local file not found, so it cannot be marked as synced.', true);
                    }
                }).catch(function() {
                    ServerSyncShared.syncStatus('Could not mark the item as synced.', true);
                });
            },

            modalDelete: function() {
                var self = this;
                if (!this.currentModalItem) return;

                var item = this.currentModalItem;
                var fileName = ServerSyncShared.getFileName(item.LocalPath || item.SourcePath);
                confirmDialog('Delete "' + fileName + '" from this server? This cannot be undone.', {
                    title: 'Delete File',
                    confirmText: 'Delete',
                    destructive: true
                }).then(function(confirmed) {
                    if (!confirmed) return;
                    return ServerSyncShared.apiRequest('DeleteLocalItems', 'POST', { SourceItemIds: [item.SourceItemId] }).then(function() {
                        self.closeModal();
                        ServerSyncShared.syncStatus('Item deleted.', false);
                    }).catch(function() {
                        ServerSyncShared.syncStatus('Could not delete the item.', true);
                    });
                });
            },

            updatePendingFilterVisibility: function(config) {
                var downloadMode = config.DownloadNewContentMode || 'Enabled';
                var replaceMode = config.ReplaceExistingContentMode || 'Enabled';
                var deleteMode = config.DeleteMissingContentMode || 'Disabled';

                var showPendingDownload = downloadMode === 'RequireApproval';
                var showPendingReplace = replaceMode === 'RequireApproval';
                // Automatic deletion still holds some deletions for approval, so both deletion states can
                // appear whenever deletion is on at all.
                var showPendingDelete = deleteMode !== 'Disabled';

                if (this.table) {
                    this.table.setFilterOptionVisible('optPendingDownload', showPendingDownload);
                    this.table.setFilterOptionVisible('optPendingReplacement', showPendingReplace);
                    this.table.setFilterOptionVisible('optPendingDeletion', showPendingDelete);
                    this.table.setFilterOptionVisible('optDeleting', showPendingDelete);
                }

                ServerSyncShared.setVisible('statusGroupPendingDownload', showPendingDownload);
                ServerSyncShared.setVisible('statusGroupPendingReplacement', showPendingReplace);
                ServerSyncShared.setVisible('statusGroupPendingDeletion', showPendingDelete);
                ServerSyncShared.setVisible('statusGroupDeleting', showPendingDelete);

                var showAnyPending = showPendingDownload || showPendingReplace || showPendingDelete;
                var pendingRow = view.querySelector('#pendingStatusRow');
                if (pendingRow) {
                    pendingRow.classList.toggle('hidden', !showAnyPending);
                }
            },
        }
    });

    return SyncTableModule;
}

// createContentPageController
// Builds the controller that opens the Content Sync view the first time it is shown.
//
// Inputs
// ctx [object]: The page context from serversync_sync.js.
// SyncTableModule [object]: The view's module from createContentSyncTableModule.
//
// Output
// [object]: A handle with init, which starts the table, reads the config, and loads the rows.
export function createContentPageController(ctx, SyncTableModule) {
    'use strict';

    var ServerSyncShared = ctx.shared;

    var ContentPageController = {
        init: function() {
            SyncTableModule.init();
            ServerSyncShared.fetchLocalServerName();
            ServerSyncShared.getConfig().then(function(config) {
                SyncTableModule.currentConfig = config;
                SyncTableModule.updatePendingFilterVisibility(config);
            }).catch(function() {
                // Without a config the pending filters keep their default visibility.
            });
            SyncTableModule.loadAll();
        }
    };

    return ContentPageController;
}

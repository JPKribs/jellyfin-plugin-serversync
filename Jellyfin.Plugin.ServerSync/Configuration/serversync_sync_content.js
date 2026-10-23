// ============================================
// SERVER SYNC PLUGIN - SYNC PAGE: CONTENT VIEW
// ============================================
// The Content Sync table and the controller that opens it. Built by serversync_sync.js from the
// page context and the table kit in serversync_sync_table.js.
// ============================================

export function createContentSyncTableModule(ctx, kit) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;
    var createSyncTableModule = kit.createSyncTableModule;
    var renderRowInfo = kit.renderRowInfo;
    var setHealthLastSync = kit.setHealthLastSync;
    var setHealthCount = kit.setHealthCount;

    // ============================================
    // CONTENT SYNC TABLE MODULE
    // ============================================

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
            { id: 'Delete', method: 'modalDelete' }
        ],
        bulkButtons: [
            { id: 'Ignore', title: 'Ignore', icon: 'block', method: 'bulkIgnore' },
            { id: 'MarkSynced', title: 'Mark Synced', icon: 'check_circle', method: 'bulkMarkSynced' },
            { id: 'Queue', title: 'Queue', icon: 'playlist_add', className: 'jpk-button-submit', method: 'bulkQueue' },
            { id: 'Delete', title: 'Delete from local server only', icon: 'delete', className: 'jpk-button-destructive', method: 'bulkDelete' }
        ],
        idKey: 'SourceItemId',
        tasks: {
            refresh: { endpoint: 'TriggerRefresh', key: 'ServerSyncUpdateTables' },
            sync: { endpoint: 'TriggerSync', key: 'ServerSyncDownloadContent' }
        },
        taskNoun: '',
        singularNoun: 'Item',
        pluralNoun: 'item(s)',
        failNoun: 'items',
        emptyMessage: 'No content items found. Run a refresh to scan for content.',
        filters: {
            options: [
                { value: 'Synced', label: 'Synced' },
                { value: 'Queued', label: 'Queued' },
                { value: 'Errored', label: 'Errored' },
                { value: 'Ignored', label: 'Ignored' },
                { value: 'Pending', label: 'Pending' },
                { value: 'Pending:Download', label: 'Pending Download', id: 'optPendingDownload', hidden: true },
                { value: 'Pending:Replacement', label: 'Pending Replace', id: 'optPendingReplacement', hidden: true },
                { value: 'Pending:Deletion', label: 'Pending Delete', id: 'optPendingDeletion', hidden: true },
                { value: 'Deleting', label: 'Deleting', id: 'optDeleting', hidden: true }
            ],
            buildParams: function(filterValue) {
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
                    className: 'pt-cell-details',
                    render: function(item) {
                        if (item.Status === 'Synced') {
                            return '<span class="jpk-badge gray">No changes</span>';
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
                ServerSyncShared.apiRequest('Stats', 'GET'),
                ServerSyncShared.getConfig(),
                ServerSyncShared.apiRequest('PendingSize', 'GET')
            ]).then(function(results) {
                var pendingSizeData = results[2];
                setHealthLastSync('healthLastSync', results[0].LastSyncEndTime);
                setHealthCount('healthLibraryCount', ServerSyncShared.allLibraryMappings(results[1]).length);

                var pendingCountEl = view.querySelector('#healthPendingCount');
                if (pendingSizeData && typeof pendingSizeData.TotalPendingBytes === 'number') {
                    pendingCountEl.textContent = ServerSyncShared.formatSize(pendingSizeData.TotalPendingBytes);
                    pendingCountEl.className = pendingSizeData.TotalPendingBytes > 0 ? 'healthValue warning' : 'healthValue';
                } else {
                    pendingCountEl.textContent = '0 B';
                    pendingCountEl.className = 'healthValue';
                }
            });
        },
        statusRequest: function(item, status) {
            return { endpoint: 'UpdateItemStatus', body: { SourceItemId: item.SourceItemId, Status: status } };
        },
        methods: {
            capabilities: null, // Server capabilities, such as CanDeleteItems

            loadCapabilities: function() {
                var self = this;
                return ServerSyncShared.apiRequest('Capabilities', 'GET').then(function(capabilities) {
                    self.capabilities = capabilities;
                    self.updateDeleteCapabilityVisibility(capabilities.CanDeleteItems);
                }).catch(function() {
                    self.capabilities = { CanDeleteItems: false };
                    self.updateDeleteCapabilityVisibility(false);
                });
            },

            updateDeleteCapabilityVisibility: function(canDelete) {
                var bulkDeleteBtn = view.querySelector('#btnBulkDelete');
                var modalDeleteBtn = view.querySelector('#btnModalDelete');

                if (bulkDeleteBtn) {
                    bulkDeleteBtn.style.display = canDelete ? 'inline-block' : 'none';
                }
                if (modalDeleteBtn) {
                    modalDeleteBtn.style.display = canDelete ? 'inline-block' : 'none';
                }
            },

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
                        'Pending: ' + totalPending + ' (Download: ' + pendingDownload + ', Replace: ' + pendingReplacement + ', Delete: ' + pendingDeletion + ')');
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
                    ServerSyncShared.showAlert('Errored items queued for retry');
                    self.loadStatus();
                    self.loadItems();
                }).catch(function() {
                    ServerSyncShared.showAlert('Failed to retry errored items');
                });
            },

            bulkIgnore: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                this.postSelection('IgnoreItems', { SourceItemIds: ids }, ids.length + ' item(s) ignored', 'Failed to ignore items');
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
                    ServerSyncShared.showAlert('No items to queue (deletion items cannot be queued)');
                    return;
                }

                this.postSelection('QueueItems', { SourceItemIds: queueable }, queueable.length + ' item(s) queued', 'Failed to queue items');
            },

            // The server checks the local files exist before marking.
            bulkMarkSynced: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                this.postSelection('MarkSynced', { SourceItemIds: ids }, function(result) {
                    var msg = ((result && result.Synced) || 0) + ' item(s) marked as synced';
                    if (result && result.NotFound > 0) {
                        msg += ', ' + result.NotFound + ' local file(s) not found';
                    }
                    return msg;
                }, 'Failed to mark items as synced');
            },

            bulkDelete: function() {
                var ids = this.table.getSelectedIds();
                if (ids.length === 0) return;
                if (!confirm('Delete ' + ids.length + ' item(s) from the local server? This cannot be undone.')) {
                    return;
                }
                this.postSelection('DeleteLocalItems', { SourceItemIds: ids }, function(result) {
                    return result && result.Deleted > 0 ? 'Deleted ' + result.Deleted + ' item(s)' : null;
                }, 'Failed to delete items');
            },

            getDisplayStatus: function(item) {
                if (item.Status === 'Pending' && item.PendingType) {
                    return 'Pending ' + item.PendingType;
                }
                return item.Status;
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
                statusBadge.className = 'itemModal-statusBadge ' + statusClass;

                var sourceServerName = ServerSyncShared.serverNameFor(self.currentConfig, item && item.ServerKey);
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
                            return '<div class="itemModal-companionItem">' +
                                '<span class="itemModal-companionIcon">&#128196;</span>' +
                                '<span class="itemModal-companionName">' + ServerSyncShared.escapeHtml(f) + '</span>' +
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
                        localPathNoteEl.style.display = 'none';
                        localSizeEl.textContent = ServerSyncShared.formatSize(item.LocalSize || item.SourceSize);
                        localSizeRowEl.style.display = 'block';
                    } else {
                        localPathNoteEl.textContent = 'File will be synced to this location';
                        localPathNoteEl.style.display = 'block';
                        localSizeRowEl.style.display = 'none';
                    }
                } else {
                    localPathEl.textContent = 'N/A';
                    localPathNoteEl.style.display = 'none';
                    localSizeRowEl.style.display = 'none';
                }

                var btnQueue = view.querySelector('#btnModalQueue');
                var btnIgnore = view.querySelector('#btnModalIgnore');
                var btnMarkSynced = view.querySelector('#btnModalMarkSynced');
                var modalDeleteBtn = view.querySelector('#btnModalDelete');
                var isPendingDeletion = item.Status === 'Pending' && item.PendingType === 'Deletion';
                var isPendingDownloadOrReplacement = item.Status === 'Pending' && (item.PendingType === 'Download' || item.PendingType === 'Replacement');
                var isSynced = item.Status === 'Synced';
                var isQueuedOrErrored = item.Status === 'Queued' || item.Status === 'Errored';

                var queueBtnSpan = btnQueue.querySelector('span');
                if (isSynced) {
                    queueBtnSpan.textContent = 'Resync';
                } else {
                    queueBtnSpan.textContent = 'Queue';
                }

                // Show Mark Synced only for Queued/Errored items
                btnMarkSynced.style.display = isQueuedOrErrored ? 'inline-block' : 'none';

                if (isPendingDeletion) {
                    btnQueue.style.display = 'none';
                    if (self.capabilities && self.capabilities.CanDeleteItems) {
                        modalDeleteBtn.style.display = 'inline-block';
                    }
                } else if (isPendingDownloadOrReplacement) {
                    btnQueue.style.display = 'inline-block';
                    modalDeleteBtn.style.display = 'none';
                } else {
                    btnQueue.style.display = 'inline-block';
                    if (self.capabilities && self.capabilities.CanDeleteItems) {
                        modalDeleteBtn.style.display = 'inline-block';
                    }
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
                        ServerSyncShared.showAlert('Item marked as synced');
                    } else {
                        ServerSyncShared.showAlert('Local file not found, so it cannot be marked as synced');
                    }
                }).catch(function() {
                    ServerSyncShared.showAlert('Failed to mark item as synced');
                });
            },

            modalDelete: function() {
                var self = this;
                if (!this.currentModalItem) return;

                var fileName = ServerSyncShared.getFileName(this.currentModalItem.LocalPath || this.currentModalItem.SourcePath);
                if (!confirm('Delete "' + fileName + '" from the local server? This cannot be undone.')) {
                    return;
                }

                ServerSyncShared.apiRequest('DeleteLocalItems', 'POST', { SourceItemIds: [this.currentModalItem.SourceItemId] }).then(function() {
                    self.closeModal();
                    ServerSyncShared.showAlert('Item deleted');
                }).catch(function() {
                    ServerSyncShared.showAlert('Failed to delete item');
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
                    pendingRow.style.display = showAnyPending ? 'flex' : 'none';
                }
            },
        }
    });

    return SyncTableModule;
}

export function createContentPageController(ctx, SyncTableModule) {
    'use strict';

    var ServerSyncShared = ctx.shared;

    // ============================================
    // CONTENT PAGE CONTROLLER
    // ============================================

    var ContentPageController = {
        init: function() {
            SyncTableModule.init();
            SyncTableModule.loadCapabilities();
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

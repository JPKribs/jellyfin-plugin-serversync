// ============================================
// SERVER SYNC PLUGIN - SHARED MODULE
// ============================================
// Shared utilities and PaginatedTable component
// used by all plugin pages (sync, settings).

// ============================================
// SHARED UTILITIES
// ============================================
// Note: getTabs() is defined locally in each page controller (sync, settings)
// because LibraryMenu.setTabs() must be called synchronously during the
// viewshow event — importing from shared would require an async import
// that resolves too late for Jellyfin's tab rendering.
// ============================================

export function createServerSyncShared(view) {
    return {
        pluginId: 'ebd650b5-6f4c-4ccb-b10d-23dffb3a7286',

        // Local server name (fetched once)
        localServerName: null,

        formatSize: function(bytes) {
            if (!bytes) return '0 B';
            var units = ['B', 'KB', 'MB', 'GB', 'TB'];
            var i = 0;
            while (bytes >= 1024 && i < units.length - 1) {
                bytes /= 1024;
                i++;
            }
            return bytes.toFixed(i > 0 ? 2 : 0) + ' ' + units[i];
        },

        formatRelativeTime: function(date) {
            var now = new Date();
            var diff = now - date;
            var minutes = Math.floor(diff / 60000);
            var hours = Math.floor(minutes / 60);
            var days = Math.floor(hours / 24);

            if (days > 0) return days + ' day' + (days > 1 ? 's' : '') + ' ago';
            if (hours > 0) return hours + ' hour' + (hours > 1 ? 's' : '') + ' ago';
            if (minutes > 0) return minutes + ' minute' + (minutes > 1 ? 's' : '') + ' ago';
            return 'Just now';
        },

        getFileName: function(path) {
            if (!path) return '';
            return path.split('/').pop().split('\\').pop();
        },

        escapeHtml: function(str) {
            if (!str) return '';
            return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        },

        buildSourceImageUrl: function(serverUrl, apiKey, itemId, imageType, maxHeight) {
            if (!serverUrl || !apiKey || !itemId) return null;
            imageType = imageType || 'Primary';
            maxHeight = maxHeight || 80;
            return serverUrl + '/Items/' + encodeURIComponent(itemId) + '/Images/' + imageType +
                '?maxHeight=' + maxHeight + '&api_key=' + encodeURIComponent(apiKey);
        },

        buildSourceUserImageUrl: function(serverUrl, apiKey, userId, maxHeight) {
            if (!serverUrl || !apiKey || !userId) return null;
            maxHeight = maxHeight || 80;
            return serverUrl + '/Users/' + encodeURIComponent(userId) + '/Images/Primary' +
                '?maxHeight=' + maxHeight + '&api_key=' + encodeURIComponent(apiKey);
        },

        // Starts as portrait (40×60); on load, if the image is landscape
        // (width > height), it swaps to the landscape class (106×60).
        renderItemThumb: function(serverUrl, apiKey, itemId) {
            var imgUrl = this.buildSourceImageUrl(serverUrl, apiKey, itemId, 'Primary', 80);
            if (!imgUrl) {
                return '<div class="pt-row-thumb-placeholder"><span class="material-icons">movie</span></div>';
            }
            return '<img class="pt-row-thumb pt-row-thumb-portrait" src="' + this.escapeHtml(imgUrl) +
                '" loading="lazy"' +
                ' onload="if(this.naturalWidth>this.naturalHeight){this.classList.remove(\'pt-row-thumb-portrait\');this.classList.add(\'pt-row-thumb-landscape\')}"' +
                ' onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'flex\'" />' +
                '<div class="pt-row-thumb-placeholder" style="display:none"><span class="material-icons">movie</span></div>';
        },

        renderUserThumb: function(serverUrl, apiKey, userId) {
            var imgUrl = this.buildSourceUserImageUrl(serverUrl, apiKey, userId, 80);
            if (!imgUrl) {
                return '<div class="pt-row-thumb-user-placeholder"><span class="material-icons">person</span></div>';
            }
            return '<img class="pt-row-thumb-user" src="' + this.escapeHtml(imgUrl) +
                '" loading="lazy" onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'flex\'" />' +
                '<div class="pt-row-thumb-user-placeholder" style="display:none"><span class="material-icons">person</span></div>';
        },

        // Always portrait (no landscape auto-detection) since person images are headshots.
        renderPersonThumb: function(serverUrl, apiKey, personId) {
            var imgUrl = this.buildSourceImageUrl(serverUrl, apiKey, personId, 'Primary', 80);
            if (!imgUrl) {
                return '<div class="pt-row-thumb-placeholder"><span class="material-icons">person</span></div>';
            }
            return '<img class="pt-row-thumb pt-row-thumb-portrait" src="' + this.escapeHtml(imgUrl) +
                '" loading="lazy"' +
                ' onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'flex\'" />' +
                '<div class="pt-row-thumb-placeholder" style="display:none"><span class="material-icons">person</span></div>';
        },

        showAlert: function(message) {
            Dashboard.alert(message);
        },

        getConfig: function() {
            return ApiClient.getPluginConfiguration(this.pluginId);
        },

        saveConfig: function(config) {
            return ApiClient.updatePluginConfiguration(this.pluginId, config);
        },

        fetchLocalServerName: function() {
            var self = this;
            return ApiClient.getPublicSystemInfo().then(function(info) {
                self.localServerName = info.ServerName || 'Local';
                return self.localServerName;
            }).catch(function() {
                self.localServerName = 'Local';
                return self.localServerName;
            });
        },

        apiRequest: function(endpoint, method, data) {
            var options = {
                url: ApiClient.getUrl('ServerSync/' + endpoint),
                type: method || 'GET',
                dataType: 'json'
            };

            if (data) {
                options.contentType = 'application/json';
                options.data = JSON.stringify(data);
            }

            return ApiClient.fetch(options).catch(function(error) {
                if (error && error.message && error.message.indexOf('JSON') !== -1) {
                    return null;
                }
                throw error;
            });
        },

        setVisible: function(elementOrId, visible) {
            var el = typeof elementOrId === 'string' ? view.querySelector('#' + elementOrId) : elementOrId;
            if (el) {
                if (visible) {
                    el.classList.remove('hidden');
                } else {
                    el.classList.add('hidden');
                }
            }
        },

        bindEvent: function(id, event, handler, moduleName) {
            var el = view.querySelector('#' + id);
            if (el) {
                el.addEventListener(event, handler);
            } else if (moduleName) {
                console.error(moduleName + ': #' + id + ' not found');
            }
            return el;
        },

        bindClick: function(id, handler, moduleName) {
            return this.bindEvent(id, 'click', handler, moduleName);
        },

        pollTaskProgress: function(btn, taskKey, label, onComplete) {
            var progressBar = btn.querySelector('.btn-progress');
            if (!progressBar) {
                progressBar = document.createElement('div');
                progressBar.className = 'btn-progress';
                btn.appendChild(progressBar);
            }
            progressBar.style.width = '0%';
            btn.disabled = true;
            var btnSpan = btn.querySelector('span');
            if (btnSpan) btnSpan.textContent = label + ' 0%';

            // Track whether we've seen the task running — don't treat
            // Idle as "complete" until the task has actually started.
            var hasSeenRunning = false;
            var pollCount = 0;
            var maxIdlePolls = 10; // Give up after ~15s if task never starts

            var pollInterval = setInterval(function() {
                pollCount++;
                ApiClient.getScheduledTasks().then(function(tasks) {
                    var task = tasks.find(function(t) { return t.Key === taskKey; });
                    if (!task) {
                        clearInterval(pollInterval);
                        if (btnSpan) btnSpan.textContent = label;
                        progressBar.style.width = '0%';
                        btn.disabled = false;
                        if (onComplete) onComplete();
                        return;
                    }

                    if (task.State === 'Running') {
                        hasSeenRunning = true;
                        var pct = Math.round(task.CurrentProgressPercentage || 0);
                        if (btnSpan) btnSpan.textContent = label + ' ' + pct + '%';
                        progressBar.style.width = pct + '%';
                    } else if (task.State === 'Idle') {
                        if (hasSeenRunning || pollCount >= maxIdlePolls) {
                            // Task finished (or never started after timeout)
                            clearInterval(pollInterval);
                            if (btnSpan) btnSpan.textContent = label;
                            progressBar.style.width = '0%';
                            btn.disabled = false;
                            if (onComplete) onComplete();
                        }
                        // Otherwise: task hasn't started yet, keep polling
                    }
                }).catch(function(err) {
                    console.error('pollTaskProgress error:', err);
                    clearInterval(pollInterval);
                    if (btnSpan) btnSpan.textContent = label;
                    progressBar.style.width = '0%';
                    btn.disabled = false;
                    if (onComplete) onComplete();
                });
            }, 1500);

            return pollInterval;
        }
    };
}

// ============================================
// PAGINATED TABLE COMPONENT
// ============================================

export function createPaginatedTable(view, ServerSyncShared, options) {
    var table = {
        options: {
            containerId: null,
            endpoint: '',
            columns: [],
            selection: { enabled: false, idKey: 'id' },
            pagination: { pageSize: 50 },
            filters: { options: [] },
            search: { enabled: true, placeholder: 'Search items...', debounceMs: 300 },
            actions: {},
            emptyState: { message: 'No items found' }
        },
        state: null,
        elements: {},
        searchTimeout: null
    };

    Object.keys(options).forEach(function(key) {
        if (typeof options[key] === 'object' && options[key] !== null && !Array.isArray(options[key])) {
            table.options[key] = Object.assign({}, table.options[key], options[key]);
        } else {
            table.options[key] = options[key];
        }
    });

    table.state = {
        items: [],
        totalCount: 0,
        currentPage: 1,
        pageSize: table.options.pagination.pageSize,
        searchQuery: '',
        filterValue: '',
        selectedIds: new Set(),
        isLoading: false,
        hasMore: true
    };

    // --------------------------------------------
    // Private Methods
    // --------------------------------------------

    function _getItemId(item) {
        var idKey = table.options.selection && table.options.selection.idKey || 'id';
        if (typeof idKey === 'function') {
            return idKey(item);
        }
        return item[idKey];
    }

    function _createStructure() {
        var container = view.querySelector('#' + table.options.containerId);
        if (!container) {
            console.error('PaginatedTable: Container not found:', table.options.containerId);
            return;
        }
        container.innerHTML = _buildHTML();
        _cacheElements(container);
    }

    function _cacheElements(container) {
        table.elements = {
            container: container,
            search: container.querySelector('.pt-search'),
            filter: container.querySelector('.pt-filter'),
            selectAll: container.querySelector('.pt-select-all'),
            selectedCount: container.querySelector('.pt-selected-count'),
            bulkActions: container.querySelector('.pt-bulk-actions'),
            reloadBtn: container.querySelector('.pt-reload-btn'),
            body: container.querySelector('.pt-body'),
            loadingMore: container.querySelector('.pt-loading-more'),
            scrollSentinel: container.querySelector('.pt-scroll-sentinel'),
            itemCount: container.querySelector('.pt-item-count')
        };
    }

    function _buildHTML() {
        var opts = table.options;
        var html = '<div class="pt-wrapper">';

        html += '<div class="pt-controls">';
        if (opts.search && opts.search.enabled !== false) {
            html += '<input type="text" class="pt-search" placeholder="' +
                ServerSyncShared.escapeHtml(opts.search.placeholder || 'Search items...') + '" />';
        }
        if (opts.filters && opts.filters.options && opts.filters.options.length > 0) {
            html += '<div class="pt-filter-wrapper">';
            html += '<select class="pt-filter">';
            html += '<option value="">All</option>';
            opts.filters.options.forEach(function(opt) {
                var style = opt.hidden ? ' style="display:none"' : '';
                var id = opt.id ? ' id="' + opt.id + '"' : '';
                html += '<option value="' + ServerSyncShared.escapeHtml(opt.value) + '"' + style + id + '>' +
                    ServerSyncShared.escapeHtml(opt.label) + '</option>';
            });
            html += '</select>';
            html += '<span class="pt-filter-arrow">&#9662;</span>';
            html += '</div>';
        }
        html += '</div>';

        if (opts.selection && opts.selection.enabled) {
            html += '<div class="pt-selection-header">';
            html += '<label class="pt-select-all-label">';
            html += '<input type="checkbox" class="pt-select-all" />';
            html += '<span class="pt-checkbox-custom"></span>';
            html += '<span class="pt-select-all-text">Select All</span>';
            html += '</label>';
            html += '<span class="pt-selected-count">0 selected</span>';
            html += '<div class="pt-bulk-actions"></div>';
            html += '<span class="pt-header-spacer"></span>';
            html += '<button type="button" class="pt-reload-btn" title="Reload">';
            html += '<span class="pt-reload-icon">&#8635;</span>';
            html += '</button>';
            html += '</div>';
        }

        html += '<div class="pt-body"></div>';

        html += '<div class="pt-loading-more" style="display:none;">Loading more...</div>';

        // Always in the DOM — observed by IntersectionObserver for infinite scroll.
        html += '<div class="pt-scroll-sentinel" style="height:1px;"></div>';

        html += '<div class="pt-footer">';
        html += '<span class="pt-item-count"></span>';
        html += '</div>';

        html += '</div>';
        return html;
    }

    function _bindEvents() {
        if (table.elements.search) {
            table.elements.search.addEventListener('input', function(e) {
                _handleSearchInput(e.target.value);
            });
        }

        if (table.elements.filter) {
            table.elements.filter.addEventListener('change', function(e) {
                publicAPI.setFilter(e.target.value);
            });
        }

        if (table.elements.selectAll) {
            table.elements.selectAll.addEventListener('change', function(e) {
                _toggleSelectAll(e.target.checked);
            });
        }

        if (table.elements.reloadBtn) {
            table.elements.reloadBtn.addEventListener('click', function() {
                _handleReload();
            });
        }

        // Use IntersectionObserver for infinite scroll — works regardless of
        // which ancestor element is the scroll container.
        if (table.elements.scrollSentinel) {
            table._scrollObserver = new IntersectionObserver(function(entries) {
                if (entries[0].isIntersecting && !table.state.isLoading && table.state.hasMore) {
                    _loadMore();
                }
            }, { rootMargin: '200px' });
            table._scrollObserver.observe(table.elements.scrollSentinel);
        }
    }

    function _handleSearchInput(value) {
        if (table.searchTimeout) {
            clearTimeout(table.searchTimeout);
        }
        var debounceMs = table.options.search.debounceMs || 300;
        table.searchTimeout = setTimeout(function() {
            publicAPI.setSearch(value);
        }, debounceMs);
    }

    function _handleReload() {
        var btn = table.elements.reloadBtn;

        if (btn) {
            btn.classList.add('spinning');
            btn.disabled = true;
        }

        table.state.items = [];
        table.state.currentPage = 1;
        table.state.hasMore = true;
        table.state.selectedIds.clear();

        publicAPI.load().then(function() {
            if (btn) {
                btn.classList.remove('spinning');
                btn.disabled = false;
            }
            if (table.options.actions && table.options.actions.onReload) {
                table.options.actions.onReload();
            }
        }).catch(function() {
            if (btn) {
                btn.classList.remove('spinning');
                btn.disabled = false;
            }
        });
    }

    function _loadMore() {
        if (!table.state.hasMore || table.state.isLoading) return;

        table.state.currentPage++;

        if (table.elements.loadingMore) {
            table.elements.loadingMore.style.display = 'block';
        }

        publicAPI.load().finally(function() {
            if (table.elements.loadingMore) {
                table.elements.loadingMore.style.display = 'none';
            }
        });
    }

    function _setLoading(loading) {
        if (table.elements.container) {
            if (loading && table.state.currentPage === 1) {
                table.elements.container.classList.add('pt-loading');
            } else {
                table.elements.container.classList.remove('pt-loading');
            }
        }
    }

    function _render() {
        _renderBody();
        _updateItemCount();
        _updateSelectionUI();
    }

    function _renderBody() {
        var state = table.state;
        var opts = table.options;
        var body = table.elements.body;

        if (!body) return;

        if (state.items.length === 0) {
            body.innerHTML = '<div class="pt-empty">' +
                ServerSyncShared.escapeHtml(opts.emptyState.message) + '</div>';
            return;
        }

        body.innerHTML = state.items.map(function(item) {
            return _renderRow(item);
        }).join('');

        _bindRowEvents();
    }

    function _renderRow(item) {
        var opts = table.options;
        var itemId = _getItemId(item);
        var visibleColumns = opts.columns.filter(function(col) { return !col.hidden; });

        var html = '<div class="pt-row" data-id="' + ServerSyncShared.escapeHtml(String(itemId)) + '">';

        if (opts.selection && opts.selection.enabled) {
            var checked = table.state.selectedIds.has(String(itemId)) ? ' checked' : '';
            html += '<div class="pt-cell pt-cell-checkbox">';
            html += '<input type="checkbox" class="pt-row-checkbox" data-id="' +
                ServerSyncShared.escapeHtml(String(itemId)) + '"' + checked + ' />';
            html += '</div>';
        }

        visibleColumns.forEach(function(col) {
            var value = item[col.key];
            var cellClass = 'pt-cell';
            if (col.type === 'status') cellClass += ' pt-cell-status';
            if (col.className) cellClass += ' ' + col.className;
            var content = '';

            if (col.type === 'status') {
                var displayStatus = _getDisplayStatus(item, value);
                var statusClass = _getStatusClass(item, value);
                content = '<button type="button" class="pt-status-badge pt-status-btn ' + statusClass + '" data-id="' +
                    ServerSyncShared.escapeHtml(String(itemId)) + '">' +
                    ServerSyncShared.escapeHtml(displayStatus) + '</button>';
            } else if (col.type === 'custom' && col.render) {
                content = col.render(item, value);
            } else {
                content = value !== null && value !== undefined ? ServerSyncShared.escapeHtml(String(value)) : '';
            }

            html += '<div class="' + cellClass + '">' + content + '</div>';
        });

        html += '</div>';
        return html;
    }

    function _getDisplayStatus(item, value) {
        if (table.options.getDisplayStatus) {
            return table.options.getDisplayStatus(item, value);
        }
        return value || '';
    }

    function _getStatusClass(item, value) {
        if (table.options.getStatusClass) {
            return table.options.getStatusClass(item, value);
        }
        return value || '';
    }

    function _bindRowEvents() {
        var body = table.elements.body;
        if (!body) return;

        body.querySelectorAll('.pt-row').forEach(function(row) {
            var handleRowAction = function(e) {
                if (e.target.type === 'checkbox' || e.target.classList.contains('pt-row-checkbox') ||
                    e.target.classList.contains('pt-status-btn')) {
                    return;
                }

                var id = row.dataset.id;
                var item = _getItemById(id);
                if (item && table.options.actions && table.options.actions.onRowClick) {
                    e.preventDefault();
                    e.stopPropagation();
                    table.options.actions.onRowClick(item);
                }
            };

            row.addEventListener('click', handleRowAction);
        });

        body.querySelectorAll('.pt-status-btn').forEach(function(btn) {
            btn.addEventListener('click', function(e) {
                e.preventDefault();
                e.stopPropagation();
                var id = btn.dataset.id;
                var item = _getItemById(id);
                if (item && table.options.actions && table.options.actions.onRowClick) {
                    table.options.actions.onRowClick(item);
                }
            });
        });

        body.querySelectorAll('.pt-row-checkbox').forEach(function(checkbox) {
            checkbox.addEventListener('change', function(e) {
                e.stopPropagation();
                var id = checkbox.dataset.id;
                if (checkbox.checked) {
                    table.state.selectedIds.add(id);
                } else {
                    table.state.selectedIds.delete(id);
                }
                _updateSelectionUI();
                _notifySelectionChange();
            });

            checkbox.addEventListener('click', function(e) {
                e.stopPropagation();
            });
        });
    }

    function _getItemById(id) {
        return table.state.items.find(function(item) {
            return String(_getItemId(item)) === String(id);
        });
    }

    function _updateItemCount() {
        if (table.elements.itemCount) {
            var loaded = table.state.items.length;
            var total = table.state.totalCount;

            if (total === 0) {
                table.elements.itemCount.textContent = '';
            } else if (loaded >= total) {
                table.elements.itemCount.textContent = total + ' items';
            } else {
                table.elements.itemCount.textContent = 'Showing ' + loaded + ' of ' + total + ' items';
            }
        }
    }

    function _updateSelectionUI() {
        var count = table.state.selectedIds.size;

        if (table.elements.selectedCount) {
            table.elements.selectedCount.textContent = count + ' selected';
        }

        if (table.elements.selectAll) {
            var allSelected = count > 0 && count === table.state.items.length;
            var someSelected = count > 0 && count < table.state.items.length;
            table.elements.selectAll.checked = allSelected;
            table.elements.selectAll.indeterminate = someSelected;
        }
    }

    function _toggleSelectAll(checked) {
        table.state.selectedIds.clear();

        if (checked) {
            table.state.items.forEach(function(item) {
                table.state.selectedIds.add(String(_getItemId(item)));
            });
        }

        if (table.elements.body) {
            table.elements.body.querySelectorAll('.pt-row-checkbox').forEach(function(cb) {
                cb.checked = checked;
            });
        }

        _updateSelectionUI();
        _notifySelectionChange();
    }

    function _notifySelectionChange() {
        if (table.options.selection && table.options.selection.onSelectionChange) {
            table.options.selection.onSelectionChange(publicAPI.getSelectedIds());
        }
    }

    // --------------------------------------------
    // Public API
    // --------------------------------------------

    var publicAPI = {
        reload: function() {
            table.state.items = [];
            table.state.currentPage = 1;
            table.state.hasMore = true;
            table.state.selectedIds.clear();
            return this.load();
        },

        load: function() {
            var state = table.state;
            var opts = table.options;

            if (state.isLoading) return Promise.resolve();
            state.isLoading = true;
            _setLoading(true);

            var params = [];
            params.push('skip=' + ((state.currentPage - 1) * state.pageSize));
            params.push('take=' + state.pageSize);

            if (state.searchQuery) {
                params.push('search=' + encodeURIComponent(state.searchQuery));
            }

            if (state.filterValue) {
                if (opts.filters && opts.filters.buildParams) {
                    var filterParams = opts.filters.buildParams(state.filterValue);
                    Object.keys(filterParams).forEach(function(key) {
                        if (filterParams[key] !== null && filterParams[key] !== undefined) {
                            params.push(key + '=' + encodeURIComponent(filterParams[key]));
                        }
                    });
                } else {
                    params.push('filter=' + encodeURIComponent(state.filterValue));
                }
            }

            var endpoint = opts.endpoint + (params.length ? '?' + params.join('&') : '');

            return ServerSyncShared.apiRequest(endpoint, 'GET').then(function(result) {
                var newItems = result.Items || [];
                state.totalCount = result.TotalCount || 0;

                if (state.currentPage === 1) {
                    state.items = newItems;
                } else {
                    state.items = state.items.concat(newItems);
                }

                state.hasMore = state.items.length < state.totalCount;

                _render();
                state.isLoading = false;
                _setLoading(false);
                return result;
            }).catch(function(err) {
                console.error('PaginatedTable load error:', err);
                state.isLoading = false;
                _setLoading(false);
                throw err;
            });
        },

        getSelectedIds: function() {
            return Array.from(table.state.selectedIds);
        },

        getSelectedItems: function() {
            return table.state.items.filter(function(item) {
                return table.state.selectedIds.has(String(_getItemId(item)));
            });
        },

        clearSelection: function() {
            table.state.selectedIds.clear();
            _updateSelectionUI();
            _notifySelectionChange();

            if (table.elements.body) {
                table.elements.body.querySelectorAll('.pt-row-checkbox').forEach(function(cb) {
                    cb.checked = false;
                });
            }
            if (table.elements.selectAll) {
                table.elements.selectAll.checked = false;
                table.elements.selectAll.indeterminate = false;
            }
        },

        refresh: function() {
            table.state.items = [];
            table.state.currentPage = 1;
            table.state.hasMore = true;
            return this.load();
        },

        setFilter: function(value) {
            table.state.filterValue = value;
            table.state.items = [];
            table.state.currentPage = 1;
            table.state.hasMore = true;
            this.clearSelection();
            return this.load();
        },

        setSearch: function(query) {
            table.state.searchQuery = query;
            table.state.items = [];
            table.state.currentPage = 1;
            table.state.hasMore = true;
            this.clearSelection();
            return this.load();
        },

        getItems: function() {
            return table.state.items;
        },

        getTotalCount: function() {
            return table.state.totalCount;
        },

        getBulkActionsContainer: function() {
            return table.elements.bulkActions;
        },

        setFilterOptionVisible: function(optionId, visible) {
            if (table.elements.filter) {
                var option = table.elements.filter.querySelector('#' + optionId);
                if (option) {
                    option.style.display = visible ? 'block' : 'none';
                }
            }
        },

        setFilterValue: function(value) {
            table.state.filterValue = value;
            if (table.elements.filter) {
                table.elements.filter.value = value;
            }
        },

        // Call on viewhide or tab switch to prevent stale state.
        disconnectObserver: function() {
            if (table._scrollObserver) {
                table._scrollObserver.disconnect();
                table._scrollObserver = null;
            }
        },

        // Call when the table's container becomes visible again.
        reconnectObserver: function() {
            if (table._scrollObserver || !table.elements.scrollSentinel) {
                return; // Already connected or no sentinel element
            }
            table._scrollObserver = new IntersectionObserver(function(entries) {
                if (entries[0].isIntersecting && !table.state.isLoading && table.state.hasMore) {
                    _loadMore();
                }
            }, { rootMargin: '200px' });
            table._scrollObserver.observe(table.elements.scrollSentinel);
        }
    };

    _createStructure();
    _bindEvents();

    return publicAPI;
}

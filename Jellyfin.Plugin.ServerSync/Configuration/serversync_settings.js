// ============================================
// SETTINGS - PAGE CONTROLLER
// ============================================

export default function (view) {
    'use strict';

    // ============================================
    // TAB NAVIGATION (local copy for synchronous access)
    // ============================================

    function getTabs() {
        return [
            { href: 'configurationpage?name=serversync_sync', name: 'Sync' },
            { href: 'configurationpage?name=serversync_servers', name: 'Servers' },
            { href: 'configurationpage?name=serversync_settings', name: 'Settings' }
        ];
    }

    // ============================================
    // SHARED MODULE IMPORT (deferred)
    // ============================================

    var ServerSyncShared = null;
    var createPaginatedTable = null;
    var _filterTableSeq = 0;
    var _filterThumbSeq = 0;
    // Relative specifier so a server hosted under a base URL still resolves it.
    var _sharedPromise = import('./configurationpage?name=serversync_shared.js').then(function(shared) {
        ServerSyncShared = shared.createServerSyncShared(view);
        createPaginatedTable = shared.createPaginatedTable;
    });

    // ============================================
    // CONSTANTS & STATE
    // ============================================
    var _initialized = false;

    var currentConfig = null;
    var sourceLibraries = [];
    var localLibraries = [];
    var sourceUsers = [];
    var localUsers = [];

    // ============================================
    // UTILITY ALIASES (delegate to shared module)
    // ============================================

    function escapeHtml(str) {
        return ServerSyncShared.escapeHtml(str);
    }

    function apiRequest(endpoint, method, data) {
        return ServerSyncShared.apiRequest(endpoint, method, data);
    }

    function setVisible(elementId, visible) {
        ServerSyncShared.setVisible(elementId, visible);
    }

    function bindClick(id, handler) {
        return ServerSyncShared.bindClick(id, handler);
    }

    function getEl(id) {
        return view.querySelector('#' + id);
    }

    function setChecked(id, value) {
        ServerSyncShared.setChecked(id, value);
    }

    function getChecked(id) {
        return ServerSyncShared.getChecked(id);
    }

    function setValue(id, value) {
        var el = getEl(id);
        if (el) el.value = value;
    }

    function getValue(id, fallback) {
        var el = getEl(id);
        return el ? el.value : (fallback || '');
    }

    function getIntValue(id, fallback) {
        var v = parseInt(getValue(id, ''), 10);
        return isNaN(v) ? fallback : v;
    }

    // Section saves re-fetch the config and apply only this section's
    // fields: a page-load snapshot posted whole would clobber values other
    // writers changed since — task-written timestamps/failure records and
    // the other dashboard tab's sections.
    function saveSection(mutator, successMessage, failureMessage) {
        return ServerSyncShared.getConfig().then(function (config) {
            mutator(config);
            currentConfig = config;
            return ServerSyncShared.saveConfig(config);
        }).then(function () {
            Dashboard.alert(successMessage);
        }).catch(function () {
            Dashboard.alert(failureMessage);
        });
    }

    // ============================================
    // SERVER LOOKUPS (the Servers tab owns the list; the content filter only reads it)
    // ============================================

    var servers = [];

    function requestFor(server, apiKeyOverride) {
        return {
            ServerUrl: server.Url,
            ApiKey: apiKeyOverride || (server.ApiKey ? server.ApiKey : ''),
            ServerKey: server.Key,
            AllowPrivateNetwork: server.AllowPrivateNetwork !== false,
            AuthenticatedUserId: server.AuthenticatedUserId || null
        };
    }

    // ============================================
    // WATCHED BY ALL FILTER USERS
    // ============================================

    // The watched by all filter lists the users of every scan server, each
    // tagged with its server, since user ids are only meaningful per server.
    var watchedFilterUsers = [];

    function fetchWatchedFilterUsers() {
        var scanServers = servers.filter(function (s) {
            return s.IsEnabled !== false && s.Url && s.ApiKey && (s.Mode === 'Pull' || s.Mode === 'Sync' || !s.Mode);
        });
        return Promise.all(scanServers.map(function (server) {
            return apiRequest('GetSourceUsers', 'POST', requestFor(server)).then(function (users) {
                return (users || []).map(function (u) {
                    return { Id: u.Id, Name: u.Name, ServerName: server.Name || server.ServerName || server.Url, ServerKey: server.Key };
                });
            }).catch(function () { return []; });
        })).then(function (lists) {
            watchedFilterUsers = [];
            lists.forEach(function (list) { watchedFilterUsers = watchedFilterUsers.concat(list); });
            renderWatchedFilterUsers(getCurrentWatchedFilterUserIds());
        });
    }

    function getCurrentWatchedFilterUserIds() {
        var rendered = collectWatchedFilterUsers();
        if (rendered.length > 0) {
            return rendered;
        }

        return (currentConfig && currentConfig.WatchedFilterUserIds) || [];
    }

    function renderWatchedFilterUsers(selectedIds) {
        var container = view.querySelector('#watchedFilterUsersList');
        if (!container) return;

        var selectedSet = {};
        (selectedIds || []).forEach(function(id) { selectedSet[id] = true; });

        container.innerHTML = '';

        if (!watchedFilterUsers || watchedFilterUsers.length === 0) {
            container.innerHTML = '<div class="filterBrowserStatus">No source users available. Add a server and connect to it to load users.</div>';
            return;
        }

        var list = document.createElement('div');
        list.className = 'filterItemsList';

        watchedFilterUsers.forEach(function(user) {
            var isSelected = !!selectedSet[user.Id];

            var itemEl = document.createElement('div');
            itemEl.className = 'filterItem watchedFilterUserItem' + (isSelected ? ' selected' : '');
            itemEl.dataset.userId = user.Id;

            var thumbHtml;
            if (ServerSyncShared && user.Id) {
                var userThumbId = 'ss-user-thumb-' + escapeHtml(String(user.Id));
                ServerSyncShared.scheduleProxyImage(userThumbId, user.Id, true, 120, user.ServerKey);
                thumbHtml = '<img id="' + userThumbId + '" class="filterItemThumb filterItemThumbSquare" />' +
                    '<div class="filterItemThumbPlaceholder filterItemThumbSquare" style="display:none"><span class="material-icons">person</span></div>';
            } else {
                thumbHtml = '<div class="filterItemThumbPlaceholder filterItemThumbSquare"><span class="material-icons">person</span></div>';
            }

            itemEl.innerHTML = thumbHtml +
                '<div class="filterItemInfo">' +
                    '<div class="filterItemName">' + escapeHtml(user.Name || '') + '</div>' +
                    '<div class="filterItemMeta">' + escapeHtml(user.ServerName || '') + '</div>' +
                '</div>' +
                '<div class="filterItemCheck"><span class="material-icons">' + (isSelected ? 'check_box' : 'check_box_outline_blank') + '</span></div>';

            itemEl.addEventListener('click', function() {
                toggleWatchedFilterUser(itemEl);
            });

            list.appendChild(itemEl);
        });

        container.appendChild(list);
    }

    function toggleWatchedFilterUser(itemEl) {
        var icon = itemEl.querySelector('.filterItemCheck .material-icons');
        if (itemEl.classList.contains('selected')) {
            itemEl.classList.remove('selected');
            if (icon) icon.textContent = 'check_box_outline_blank';
        } else {
            itemEl.classList.add('selected');
            if (icon) icon.textContent = 'check_box';
        }
    }

    function collectWatchedFilterUsers() {
        var ids = [];
        view.querySelectorAll('.watchedFilterUserItem.selected').forEach(function(row) {
            if (row.dataset.userId) {
                ids.push(row.dataset.userId);
            }
        });
        return ids;
    }

    // ============================================
    // SYNC SETTINGS MODULE
    // ============================================

    // --- Content Settings ---

    function loadContentSettings(config) {
        setChecked('chkEnableContentSync', config.EnableContentSync || false);
        setChecked('chkDetectUpdatedFiles', config.DetectUpdatedFiles !== false);
        setChecked('chkMirrorSyncedCollections', config.MirrorSyncedCollections !== false);
        setChecked('chkIncludeCompanionFiles', config.IncludeCompanionFiles || false);
        setChecked('chkSkipWatchedByAllUsers', config.SkipWatchedByAllUsers || false);
        renderWatchedFilterUsers(config.WatchedFilterUserIds || []);
        setValue('selDownloadNewContentMode', config.DownloadNewContentMode || 'Enabled');
        setValue('selReplaceExistingContentMode', config.ReplaceExistingContentMode || 'Enabled');
        setValue('selDeleteMissingContentMode', config.DeleteMissingContentMode || 'Disabled');
        setChecked('chkEnableRecyclingBin', config.EnableRecyclingBin || false);
        setValue('txtRecyclingBinPath', config.RecyclingBinPath || '');
        setValue('txtRecyclingBinRetentionDays', config.RecyclingBinRetentionDays || 7);
        setChecked('chkRemoveEmptyFolders', config.RemoveEmptyFoldersOnDelete || false);
        setValue('txtMaxConcurrentDownloads', config.MaxConcurrentDownloads || 2);
        setValue('txtMaxRetryCount', config.MaxRetryCount || 3);
        setValue('txtSizeMatchToleranceBytes', config.SizeMatchToleranceBytes || 0);
        setValue('txtTempDownloadPath', config.TempDownloadPath || '');
        setValue('txtMaxDownloadSpeed', config.MaxDownloadSpeed || 0);
        setValue('selDownloadSpeedUnit', config.DownloadSpeedUnit || 'MB');
        setValue('txtMinFreeDiskSpace', config.MinimumFreeDiskSpaceGb == null ? 10 : config.MinimumFreeDiskSpaceGb);
        setChecked('chkEnableBandwidthScheduling', config.EnableBandwidthScheduling || false);
        setValue('txtScheduledStartHour', config.ScheduledStartHour || 0);
        setValue('txtScheduledEndHour', config.ScheduledEndHour == null ? 6 : config.ScheduledEndHour);
        setValue('txtScheduledDownloadSpeed', config.ScheduledDownloadSpeed || 0);
        setValue('selScheduledDownloadSpeedUnit', config.ScheduledDownloadSpeedUnit || 'MB');

        ServerSyncShared.bindReveal('chkSkipWatchedByAllUsers', 'watchedFilterUsersSettings');
        ServerSyncShared.bindReveal('chkEnableRecyclingBin', 'recyclingBinSettings');
        ServerSyncShared.bindReveal('chkEnableBandwidthScheduling', 'bandwidthScheduleContainer');
    }

    function saveContentSettings() {
        saveSection(function (config) {
            config.EnableContentSync = getChecked('chkEnableContentSync');
            config.DetectUpdatedFiles = getChecked('chkDetectUpdatedFiles');
            config.MirrorSyncedCollections = getChecked('chkMirrorSyncedCollections');
            config.IncludeCompanionFiles = getChecked('chkIncludeCompanionFiles');
            config.SkipWatchedByAllUsers = getChecked('chkSkipWatchedByAllUsers');
            config.WatchedFilterUserIds = collectWatchedFilterUsers();
            config.DownloadNewContentMode = getValue('selDownloadNewContentMode', 'Enabled');
            config.ReplaceExistingContentMode = getValue('selReplaceExistingContentMode', 'Enabled');
            config.DeleteMissingContentMode = getValue('selDeleteMissingContentMode', 'Disabled');
            config.EnableRecyclingBin = getChecked('chkEnableRecyclingBin');
            config.RecyclingBinPath = getValue('txtRecyclingBinPath');
            config.RecyclingBinRetentionDays = getIntValue('txtRecyclingBinRetentionDays', 7);
            config.RemoveEmptyFoldersOnDelete = getChecked('chkRemoveEmptyFolders');
            config.MaxConcurrentDownloads = getIntValue('txtMaxConcurrentDownloads', 2);
            config.MaxRetryCount = getIntValue('txtMaxRetryCount', 3);
            config.SizeMatchToleranceBytes = getIntValue('txtSizeMatchToleranceBytes', 0);
            config.TempDownloadPath = getValue('txtTempDownloadPath') || null;
            config.MaxDownloadSpeed = getIntValue('txtMaxDownloadSpeed', 0);
            config.DownloadSpeedUnit = getValue('selDownloadSpeedUnit', 'MB');
            config.MinimumFreeDiskSpaceGb = getIntValue('txtMinFreeDiskSpace', 10);
            config.EnableBandwidthScheduling = getChecked('chkEnableBandwidthScheduling');
            config.ScheduledStartHour = getIntValue('txtScheduledStartHour', 0);
            config.ScheduledEndHour = getIntValue('txtScheduledEndHour', 6);
            config.ScheduledDownloadSpeed = getIntValue('txtScheduledDownloadSpeed', 0);
            config.ScheduledDownloadSpeedUnit = getValue('selScheduledDownloadSpeedUnit', 'MB');

        }, 'Content settings saved', 'Failed to save content settings');
    }

    // --- History Settings ---

    function loadHistorySettings(config) {
        setChecked('chkEnableHistorySync', config.EnableHistorySync || false);
        setChecked('chkHistorySyncPlayedStatus', config.HistorySyncPlayedStatus !== false);
        setChecked('chkHistorySyncPlaybackPosition', config.HistorySyncPlaybackPosition !== false);
        setChecked('chkHistorySyncPlayCount', config.HistorySyncPlayCount !== false);
        setChecked('chkHistorySyncLastPlayedDate', config.HistorySyncLastPlayedDate !== false);
        setChecked('chkHistorySyncFavorites', config.HistorySyncFavorites !== false);
        setChecked('chkHistorySyncNegotiate', config.HistorySyncNegotiate === true);
    }

    function saveHistorySettings() {
        saveSection(function (config) {
            config.EnableHistorySync = getChecked('chkEnableHistorySync');
            config.HistorySyncPlayedStatus = getChecked('chkHistorySyncPlayedStatus');
            config.HistorySyncPlaybackPosition = getChecked('chkHistorySyncPlaybackPosition');
            config.HistorySyncPlayCount = getChecked('chkHistorySyncPlayCount');
            config.HistorySyncLastPlayedDate = getChecked('chkHistorySyncLastPlayedDate');
            config.HistorySyncFavorites = getChecked('chkHistorySyncFavorites');
            config.HistorySyncNegotiate = getChecked('chkHistorySyncNegotiate');

        }, 'History settings saved', 'Failed to save history settings');
    }

    // --- Metadata Settings ---

    function loadMetadataSettings(config) {
        setChecked('chkEnableMetadataSync', config.EnableMetadataSync || false);
        setChecked('chkMetadataSyncMetadata', config.MetadataSyncMetadata !== false);
        setChecked('chkMetadataSyncGenres', config.MetadataSyncGenres !== false);
        setChecked('chkMetadataSyncTags', config.MetadataSyncTags !== false);
        setChecked('chkMetadataSyncStudios', config.MetadataSyncStudios !== false);
        setChecked('chkMetadataSyncPeople', config.MetadataSyncPeople === true);
        setChecked('chkMetadataSyncImages', config.MetadataSyncImages !== false);
        setChecked('chkMetadataSyncFolderItems', config.MetadataSyncFolderItems === true);
    }

    function saveMetadataSettings() {
        saveSection(function (config) {
            config.EnableMetadataSync = getChecked('chkEnableMetadataSync');
            config.MetadataSyncMetadata = getChecked('chkMetadataSyncMetadata');
            config.MetadataSyncGenres = getChecked('chkMetadataSyncGenres');
            config.MetadataSyncTags = getChecked('chkMetadataSyncTags');
            config.MetadataSyncStudios = getChecked('chkMetadataSyncStudios');
            config.MetadataSyncPeople = getChecked('chkMetadataSyncPeople');
            config.MetadataSyncImages = getChecked('chkMetadataSyncImages');
            config.MetadataSyncFolderItems = getChecked('chkMetadataSyncFolderItems');

        }, 'Metadata settings saved', 'Failed to save metadata settings');
    }

    // --- People Sync Settings ---

    function loadPeopleSettings(config) {
        setChecked('chkEnablePeopleSync', config.EnablePeopleSync === true);
        setChecked('chkPeopleSyncImages', config.PeopleSyncImages !== false);
    }

    function savePeopleSettings() {
        saveSection(function (config) {
            config.EnablePeopleSync = getChecked('chkEnablePeopleSync');
            config.PeopleSyncImages = getChecked('chkPeopleSyncImages');

        }, 'People settings saved', 'Failed to save people settings');
    }

    // --- User Sync Settings ---

    function loadUserSyncSettings(config) {
        setChecked('chkEnableUserSync', config.EnableUserSync || false);
        setChecked('chkUserSyncPolicy', config.UserSyncPolicy !== false);
        setChecked('chkUserSyncConfiguration', config.UserSyncConfiguration !== false);
        setChecked('chkUserSyncProfileImage', config.UserSyncProfileImage !== false);
    }

    function saveUserSyncSettings() {
        saveSection(function (config) {
            config.EnableUserSync = getChecked('chkEnableUserSync');
            config.UserSyncPolicy = getChecked('chkUserSyncPolicy');
            config.UserSyncConfiguration = getChecked('chkUserSyncConfiguration');
            config.UserSyncProfileImage = getChecked('chkUserSyncProfileImage');

        }, 'User sync settings saved', 'Failed to save user sync settings');
    }

    // --- Processing Settings ---

    function loadProcessingSettings(config) {
        setValue('txtRefreshParallelism', config.RefreshParallelism || 8);
        setChecked('chkDeepImageVerification', config.DeepImageVerification === true);
    }

    function saveProcessingSettings() {
        saveSection(function (config) {
            config.RefreshParallelism = Math.min(16, Math.max(1, getIntValue('txtRefreshParallelism', 8)));
            config.DeepImageVerification = getChecked('chkDeepImageVerification');

        }, 'Processing settings saved', 'Failed to save processing settings');
    }

    // ============================================
    // PAGE INITIALIZATION
    // ============================================

    function loadConfig() {
        ServerSyncShared.getConfig().then(function(config) {
            currentConfig = config;
            servers = config.Servers || [];

            loadContentSettings(config);
            loadHistorySettings(config);
            loadMetadataSettings(config);
            loadPeopleSettings(config);
            loadUserSyncSettings(config);
            loadProcessingSettings(config);
            fetchWatchedFilterUsers();
        }).catch(function() {
            Dashboard.alert('Failed to load plugin configuration');
        });
    }

    // ============================================
    // TROUBLESHOOTING: DATABASE RESET
    // ============================================

    function resetTable(endpoint, tableName) {
        if (!confirm('Are you sure you want to reset the ' + tableName + ' table?\n\nThis will delete all ' + tableName + ' tracking data and you will need to re-sync. This cannot be undone.')) {
            return;
        }

        ServerSyncShared.apiRequest(endpoint, 'POST').then(function() {
            ServerSyncShared.showAlert('The ' + tableName + ' table has been reset.');
        }).catch(function(err) {
            console.error(endpoint + ' error:', err);
            ServerSyncShared.showAlert('Failed to reset ' + tableName + ' table.');
        });
    }

    function resetEntireDatabase() {
        if (!confirm('Are you sure you want to reset the ENTIRE sync database?\n\nThis will delete ALL tracking data across all sync types (Content, History, Metadata, Users). You will need to re-sync everything from scratch. This cannot be undone.')) {
            return;
        }

        ServerSyncShared.apiRequest('ResetSyncDatabase', 'POST').then(function() {
            ServerSyncShared.showAlert('The entire sync database has been reset.');
        }).catch(function(err) {
            console.error('ResetSyncDatabase error:', err);
            ServerSyncShared.showAlert('Failed to reset sync database.');
        });
    }

    // ============================================
    // EVENT LISTENERS
    // ============================================

    view.addEventListener('viewshow', function () {
        LibraryMenu.setTabs('serversync', 2, getTabs);

        _sharedPromise.then(function() {
            if (!_initialized) {
                _initialized = true;

                ServerSyncShared.initCollapsibles();

                bindClick('btnSaveProcessing', saveProcessingSettings);

                bindClick('btnSaveContentSettings', saveContentSettings);
                bindClick('btnSaveHistorySettings', saveHistorySettings);
                bindClick('btnSaveMetadataSettings', saveMetadataSettings);
                bindClick('btnSavePeopleSettings', savePeopleSettings);
                bindClick('btnSaveUserSyncSettings', saveUserSyncSettings);

                bindClick('btnResetContentTable', function() { resetTable('ResetContentSyncDatabase', 'content sync'); });
                bindClick('btnResetHistoryTable', function() { resetTable('ResetHistorySyncDatabase', 'history sync'); });
                bindClick('btnResetMetadataTable', function() { resetTable('ResetMetadataSyncDatabase', 'metadata sync'); });
                bindClick('btnResetUserTable', function() { resetTable('ResetUserSyncDatabase', 'user sync'); });
                bindClick('btnResetPeopleTable', function() { resetTable('ResetPeopleSyncDatabase', 'people sync'); });
                bindClick('btnResetEntireDatabase', resetEntireDatabase);
            }

            loadConfig();
        });
    });
}

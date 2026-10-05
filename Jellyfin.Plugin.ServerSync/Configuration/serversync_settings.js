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

    // Helpers called by bare name, taken from the shared module once it loads. Every call happens after
    // the load, from a viewshow handler that waits for it.
    var escapeHtml, apiRequest, bindClick, setChecked, getChecked, setValue, getValue, getIntValue, requestFor;

    // Relative specifier so a server hosted under a base URL still resolves it.
    var _sharedPromise = import('./configurationpage?name=serversync_shared.js').then(function(shared) {
        ServerSyncShared = shared.createServerSyncShared(view);
        ({ escapeHtml, apiRequest, bindClick, setChecked, getChecked, setValue, getValue, getIntValue, requestFor } = ServerSyncShared.pageHelpers());
    });

    // ============================================
    // CONSTANTS & STATE
    // ============================================
    var _initialized = false;

    var currentConfig = null;

    // The config is replaced only once a save has landed, so the page never holds a change the server
    // did not keep.
    function saveSection(mutator, successMessage, failureMessage) {
        return ServerSyncShared.saveSection(mutator, successMessage, failureMessage).then(function (saved) {
            if (saved) currentConfig = saved;
            return !!saved;
        });
    }

    // ============================================
    // WATCHED BY ALL FILTER USERS
    // ============================================

    // The watched by all filter lists the users of every scan server, each
    // tagged with its server, since user ids are only meaningful per server.
    var watchedFilterUsers = [];

    // The Servers page owns the server list. This page only reads it from the config.
    function fetchWatchedFilterUsers() {
        var scanServers = ServerSyncShared.scanServers(currentConfig);
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
            // Only the users on screen can be judged. A saved id whose server did not answer this time is
            // kept as it was, so a failed user list never clears the filter and lets watched files back in.
            var shown = {};
            (watchedFilterUsers || []).forEach(function (u) { shown[u.Id] = true; });
            var unseen = (config.WatchedFilterUserIds || []).filter(function (id) { return !shown[id]; });
            config.WatchedFilterUserIds = collectWatchedFilterUsers().concat(unseen);
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
        setValue('txtHintDebounceSeconds', config.HintDebounceSeconds || 60);
        setValue('txtHintRetrySeconds', config.HintRetrySeconds || 30);
        setValue('txtHintRetryMaxMinutes', config.HintRetryMaxMinutes || 60);
        setValue('txtHintMaxRetries', config.HintMaxRetries || 0);
        setValue('txtHintRefusedRetryMinutes', config.HintRefusedRetryMinutes || 15);
        setValue('txtHintRefusedMaxRetries', config.HintRefusedMaxRetries || 0);
        setValue('txtRefreshParallelism', config.RefreshParallelism || 8);
        setChecked('chkDeepImageVerification', config.DeepImageVerification === true);
    }

    function saveProcessingSettings() {
        saveSection(function (config) {
            config.HintDebounceSeconds = Math.min(3600, Math.max(1, getIntValue('txtHintDebounceSeconds', 60)));
            config.HintRetrySeconds = Math.min(3600, Math.max(5, getIntValue('txtHintRetrySeconds', 30)));
            config.HintRetryMaxMinutes = Math.min(1440, Math.max(1, getIntValue('txtHintRetryMaxMinutes', 60)));
            config.HintMaxRetries = Math.min(1000, Math.max(0, getIntValue('txtHintMaxRetries', 0)));
            config.HintRefusedRetryMinutes = Math.min(1440, Math.max(1, getIntValue('txtHintRefusedRetryMinutes', 15)));
            config.HintRefusedMaxRetries = Math.min(1000, Math.max(0, getIntValue('txtHintRefusedMaxRetries', 0)));
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

// serversync_settings.js
// The Settings tab: bandwidth, processing, the whitelist, one section for each sync type, and table resets.

import * as serverSyncModule from './configurationpage?name=serversync_shared.js';

// serversync_settings
// Builds the Settings tab. It loads the config into each section, saves a section on its own, and resets
// sync tables after a question.
//
// Inputs
// view [HTMLElement]: The page the dashboard passes to the controller.
export default function (view) {
    'use strict';

    var ServerSyncShared = null;
    var confirmDialog = null;

    // Helpers called by bare name, taken from the shared bag.
    var escapeHtml, apiRequest, bindClick, setChecked, getChecked, setValue, getValue, getIntValue, requestFor, getEl, setVisible;

    // Base runs bindPage once and loadConfig each time the page shows, and sets the tabs.
    var made = serverSyncModule.createServerSyncPage(view, { page: 'serversync_settings', bind: function () { bindPage(); }, load: function () { loadConfig(); } });
    ServerSyncShared = made.shared;
    confirmDialog = serverSyncModule.confirmDialog;
    ({ escapeHtml, apiRequest, bindClick, setChecked, getChecked, setValue, getValue, getIntValue, requestFor, getEl, setVisible } = ServerSyncShared.pageHelpers());


    var currentConfig = null;

    // saveSection
    // Saves one section and reports the result on its status line. The config is replaced only once a save
    // has landed, so the page never holds a change the server did not keep.
    //
    // Inputs
    // mutator [function]: Changes the fresh config in place.
    // statusId [string]: The id of the status element beside the section's Save button.
    // successMessage [string]: What the status says when the save landed.
    // failureMessage [string]: What the status says when it did not.
    //
    // Output
    // [Promise]: Resolves true when the save landed.
    function saveSection(mutator, statusId, successMessage, failureMessage) {
        return ServerSyncShared.saveSection(mutator, statusId, successMessage, failureMessage).then(function (saved) {
            if (saved) currentConfig = saved;
            return !!saved;
        });
    }

    // The watched by all filter lists the users of every scan server, each
    // tagged with its server, since user ids are only meaningful per server.
    var watchedFilterUsers = [];

    // The Servers page owns the server list. This page only reads it from the config.
    function fetchWatchedFilterUsers() {
        var scanServers = ServerSyncShared.scanServers(currentConfig);
        return Promise.all(scanServers.map(function (server) {
            return apiRequest('GetSourceUsers', 'POST', requestFor(server)).then(function (users) {
                return (users || []).map(function (u) {
                    return { Id: u.Id, Name: u.Name, ServerName: ServerSyncShared.serverDisplayName(server), ServerKey: server.Key };
                });
            }).catch(function () { return []; });
        })).then(function (lists) {
            watchedFilterUsers = [];
            lists.forEach(function (list) { watchedFilterUsers = watchedFilterUsers.concat(list); });
            renderWatchedFilterUsers(getCurrentWatchedFilterUserIds());
        });
    }

    // The Checkbox List that holds the users, or null before the first draw.
    var watchedFilterList = null;

    // getCurrentWatchedFilterUserIds
    // Reads the picked users from the list, or from the config while the list has none.
    //
    // Output
    // [string[]]: The picked user ids.
    function getCurrentWatchedFilterUserIds() {
        var rendered = collectWatchedFilterUsers();
        if (rendered.length > 0) {
            return rendered;
        }

        return (currentConfig && currentConfig.WatchedFilterUserIds) || [];
    }

    // renderWatchedFilterUsers
    // Draws the users of every scan server as a Checkbox List, each with its server and its picture.
    //
    // Inputs
    // selectedIds [string[]]: The users to draw as picked.
    function renderWatchedFilterUsers(selectedIds) {
        var container = view.querySelector('#watchedFilterUsersList');
        if (!container) return;

        if (watchedFilterList) watchedFilterList.destroy();
        container.innerHTML = '';

        watchedFilterList = serverSyncModule.createCheckboxList({
            items: watchedFilterUsers,
            idKey: 'Id',
            value: selectedIds || [],
            searchKeys: ['Name', 'ServerName'],
            placeholder: 'Search Users',
            emptyMessage: 'No source users available. Add a server and connect to it to load users.',
            renderRow: function (user) {
                return {
                    label: user.Name || '',
                    sublabel: user.ServerName || '',
                    image: ServerSyncShared.proxyImage(user.Id, true, user.ServerKey)
                };
            }
        });
        container.appendChild(watchedFilterList.element);
    }

    // collectWatchedFilterUsers
    // Reads the picked users that are on screen.
    //
    // Output
    // [string[]]: The picked ids of the users the list shows.
    function collectWatchedFilterUsers() {
        if (!watchedFilterList) return [];
        var shown = {};
        watchedFilterUsers.forEach(function (u) { shown[u.Id] = true; });
        return watchedFilterList.getValue().filter(function (id) { return shown[id]; });
    }

    function loadContentSettings(config) {
        setChecked('chkEnableContentSync', config.EnableContentSync || false);
        setChecked('chkDetectUpdatedFiles', config.DetectUpdatedFiles !== false);
        setChecked('chkMirrorSyncedCollections', config.MirrorSyncedCollections !== false);
        setChecked('chkIncludeCompanionFiles', config.IncludeCompanionFiles || false);
        setChecked('chkTakeLinkImagesFromSource', config.TakeLinkImagesFromSource !== false);
        setChecked('chkStreamLinksDirectly', config.StreamLinksDirectly !== false);
        setValue('txtLinkMaxStreamsTotal', config.LinkMaxStreamsTotal || 0);

        // One row per server this one links to: a server it pulls from with a library kept as links.
        var takeRows = getEl('linkTakeRows');
        if (takeRows) {
            takeRows.innerHTML = ((config && config.Servers) || []).filter(function (server) {
                var pulls = server.Mode === 'Pull' || server.Mode === 'Sync' || server.Mode === 0 || server.Mode === 2;
                return server.IsEnabled !== false && pulls && (server.LibraryMappings || []).some(function (m) { return m.IsEnabled !== false && (m.StorageMode === 'Link' || m.StorageMode === 1); });
            }).map(function (server) {
                return '<div class="serversync-form-row" data-server-key="' + escapeHtml(server.Key) + '">' +
                    '<span class="serversync-form-label">From ' + escapeHtml(ServerSyncShared.serverDisplayName(server)) + '<span class="serversync-form-hint">Links this server takes from it at once</span></span>' +
                    '<span class="serversync-form-cell"><input is="emby-input" type="number" min="0" max="1000" class="serversync-form-input serversync-link-take" value="' + escapeHtml(String(server.LinkTakeMaxStreams || 0)) + '" /></span>' +
                '</div>';
            }).join('');
        }

        setValue('selLinkCacheSizing', config.LinkCacheSizing || (config.LinkCacheSizeGb > 0 ? 'Fixed' : 'Off'));
        setValue('txtLinkCacheSizeGb', config.LinkCacheSizeGb || 0);
        setValue('txtLinkCachePercent', config.LinkCachePercent || 50);
        setValue('txtLinkCachePath', config.LinkCachePath || '');
        setValue('txtLinkCacheRetentionDays', config.LinkCacheRetentionDays == null ? 30 : config.LinkCacheRetentionDays);
        setValue('txtLinkCachePreCacheEpisodes', config.LinkCachePreCacheEpisodes == null ? 2 : config.LinkCachePreCacheEpisodes);
        setValue('selLinkCacheRemovalOrder', config.LinkCacheRemovalOrder || 'LastPlayed');
        loadLinkCacheStatus();
        renderWatchedFilterUsers(config.WatchedFilterUserIds || []);
        setValue('selDownloadNewContentMode', config.DownloadNewContentMode || 'Enabled');
        setValue('selReplaceExistingContentMode', config.ReplaceExistingContentMode || 'Enabled');
        setValue('selMissingContent', config.MissingContentAction || 'Keep');
        setChecked('chkEnableRecyclingBin', config.EnableRecyclingBin || false);
        setValue('txtRecyclingBinPath', config.RecyclingBinPath || '');
        setValue('txtRecyclingBinRetentionDays', config.RecyclingBinRetentionDays || 7);
        setChecked('chkRemoveEmptyFolders', config.RemoveEmptyFoldersOnDelete || false);
        setValue('txtMaxConcurrentDownloads', config.MaxConcurrentDownloads || 2);
        setValue('txtTempDownloadPath', config.TempDownloadPath || '');
        setValue('txtMinFreeDiskSpace', config.MinimumFreeDiskSpaceGb == null ? 10 : config.MinimumFreeDiskSpaceGb);


        // The cache's other settings only matter while it is on, and each size choice shows its own field.
        var cacheSizing = getEl('selLinkCacheSizing');
        var reflectCache = function () {
            var sizing = getValue('selLinkCacheSizing', 'Off');
            setVisible('linkCacheOptions', sizing !== 'Off');
            setVisible('linkCacheFixedRow', sizing === 'Fixed');
            setVisible('linkCachePercentRow', sizing === 'Percent');
        };
        if (cacheSizing && !cacheSizing.dataset.bound) {
            cacheSizing.dataset.bound = '1';
            cacheSizing.addEventListener('change', reflectCache);
        }
        reflectCache();
        ServerSyncShared.bindReveal('chkEnableRecyclingBin', 'recyclingBinSettings');
    }

    // The servers a link streams to: Push and Sync servers allowed to link here.
    function linkingServers(config) {
        return ((config && config.Servers) || []).filter(function (server) {
            var mode = server.Mode;
            return server.IsEnabled !== false && server.AllowLinking === true
                && (mode === 'Push' || mode === 'Sync' || mode === 1 || mode === 2);
        });
    }

    function loadBandwidthSettings(config) {
        setValue('txtMaxDownloadSpeed', config.MaxDownloadSpeed || 0);
        setChecked('chkEnableBandwidthScheduling', config.EnableBandwidthScheduling || false);
        setValue('txtScheduledStartHour', config.ScheduledStartHour || 0);
        setValue('txtScheduledEndHour', config.ScheduledEndHour == null ? 6 : config.ScheduledEndHour);
        setValue('txtScheduledDownloadSpeed', config.ScheduledDownloadSpeed || 0);

        // One row per server allowed to link, in Mbps like a video's bitrate.
        var rows = getEl('streamSpeedRows');
        if (rows) {
            rows.innerHTML = linkingServers(config).map(function (server) {
                return '<div class="serversync-form-row" data-server-key="' + escapeHtml(server.Key) + '">' +
                    '<span class="serversync-form-label">Streams to ' + escapeHtml(ServerSyncShared.serverDisplayName(server)) + '<span class="serversync-form-hint">Each stream and cache download it gets</span></span>' +
                    '<span class="serversync-form-cell"><input is="emby-input" type="number" min="0" max="100000" class="serversync-form-input serversync-stream-speed" value="' + escapeHtml(String(server.LinkStreamSpeedMbps || 0)) + '" /><span class="serversync-unit">Mbps</span></span>' +
                    '<span class="serversync-form-cell serversync-schedule-cell"><input is="emby-input" type="number" min="0" max="100000" class="serversync-form-input serversync-stream-scheduled-speed" value="' + escapeHtml(String(server.LinkScheduledStreamSpeedMbps || 0)) + '" /><span class="serversync-unit">Mbps</span></span>' +
                '</div>';
            }).join('');
        }

        var scheduleToggle = getEl('chkEnableBandwidthScheduling');
        var reflectSchedule = function () {
            var on = getChecked('chkEnableBandwidthScheduling');
            view.querySelectorAll('.serversync-schedule-row').forEach(function (row) { row.classList.toggle('hidden', !on); });
            var table = getEl('bandwidthTable');
            if (table) table.classList.toggle('serversync-no-schedule', !on);
        };
        if (scheduleToggle && !scheduleToggle.dataset.bound) {
            scheduleToggle.dataset.bound = '1';
            scheduleToggle.addEventListener('change', reflectSchedule);
        }
        reflectSchedule();
    }

    function saveBandwidthSettings() {
        var speeds = {};
        view.querySelectorAll('#streamSpeedRows [data-server-key]').forEach(function (row) {
            speeds[row.getAttribute('data-server-key')] = {
                normal: Math.max(0, parseInt(row.querySelector('.serversync-stream-speed').value, 10) || 0),
                scheduled: Math.max(0, parseInt(row.querySelector('.serversync-stream-scheduled-speed').value, 10) || 0)
            };
        });
        saveSection(function (config) {
            config.MaxDownloadSpeed = getIntValue('txtMaxDownloadSpeed', 0);
            config.DownloadSpeedUnit = 'Mbps';
            config.EnableBandwidthScheduling = getChecked('chkEnableBandwidthScheduling');
            config.ScheduledStartHour = getIntValue('txtScheduledStartHour', 0);
            config.ScheduledEndHour = getIntValue('txtScheduledEndHour', 6);
            config.ScheduledDownloadSpeed = getIntValue('txtScheduledDownloadSpeed', 0);
            config.ScheduledDownloadSpeedUnit = 'Mbps';
            (config.Servers || []).forEach(function (server) {
                var speed = speeds[server.Key];
                if (speed) {
                    server.LinkStreamSpeedMbps = speed.normal;
                    server.LinkScheduledStreamSpeedMbps = speed.scheduled;
                }
            });
        }, 'saveBandwidthStatus', 'Bandwidth saved.', 'Could not save the bandwidth.');
    }

    // What the link cache holds now, shown under its settings.
    function loadLinkCacheStatus() {
        var status = getEl('linkCacheStatus');
        if (!status) return;
        apiRequest('LinkCache', 'GET').then(function (cache) {
            if (!cache || !cache.Enabled) {
                status.textContent = 'The cache is off.';
                return;
            }
            status.textContent = ServerSyncShared.plural(cache.Files, 'file', 'files') + ', ' + cache.UsedFormatted + ' of ' + cache.LimitFormatted + ' used'
                + (cache.Waiting ? ', ' + cache.Waiting + ' waiting to be cached' : '') + '. Kept at ' + cache.Location + '.';
        }).catch(function () {
            status.textContent = '';
        });
    }

    // clearLinkCache
    // Asks first, then deletes every file in the link cache.
    function clearLinkCache() {
        confirmDialog('Delete every cached file? Links keep playing from their sources.', {
            title: 'Delete Cached Files',
            confirmText: 'Delete',
            destructive: true
        }).then(function (confirmed) {
            if (!confirmed) return;
            apiRequest('LinkCache/Clear', 'POST').then(loadLinkCacheStatus).catch(loadLinkCacheStatus);
        });
    }

    function saveContentSettings() {
        saveSection(function (config) {
            config.EnableContentSync = getChecked('chkEnableContentSync');
            config.DetectUpdatedFiles = getChecked('chkDetectUpdatedFiles');
            config.MirrorSyncedCollections = getChecked('chkMirrorSyncedCollections');
            config.IncludeCompanionFiles = getChecked('chkIncludeCompanionFiles');
            config.TakeLinkImagesFromSource = getChecked('chkTakeLinkImagesFromSource');
            config.StreamLinksDirectly = getChecked('chkStreamLinksDirectly');
            // An empty or unreadable field keeps the saved number, and a fraction of a gigabyte rounds
            // up to one. A fixed amount of 0 uses all available space.
            config.LinkMaxStreamsTotal = Math.max(0, getIntValue('txtLinkMaxStreamsTotal', 0));
            var takes = {};
            view.querySelectorAll('#linkTakeRows [data-server-key]').forEach(function (row) {
                takes[row.getAttribute('data-server-key')] = Math.max(0, parseInt(row.querySelector('.serversync-link-take').value, 10) || 0);
            });
            (config.Servers || []).forEach(function (server) {
                if (Object.prototype.hasOwnProperty.call(takes, server.Key)) server.LinkTakeMaxStreams = takes[server.Key];
            });
            config.LinkCacheSizing = getValue('selLinkCacheSizing', 'Off');
            var cacheSize = parseFloat(getValue('txtLinkCacheSizeGb'));
            if (!isNaN(cacheSize)) {
                config.LinkCacheSizeGb = cacheSize > 0 ? Math.max(1, Math.floor(cacheSize)) : 0;
            }
            config.LinkCachePercent = Math.min(100, Math.max(1, getIntValue('txtLinkCachePercent', 50)));
            config.LinkCachePath = getValue('txtLinkCachePath') || '';
            config.LinkCacheRetentionDays = Math.max(0, getIntValue('txtLinkCacheRetentionDays', 30));
            config.LinkCachePreCacheEpisodes = Math.max(0, getIntValue('txtLinkCachePreCacheEpisodes', 2));
            config.LinkCacheRemovalOrder = getValue('selLinkCacheRemovalOrder', 'LastPlayed');
            // Only the users on screen can be judged. A saved id whose server did not answer this time is
            // kept as it was, so a failed user list never clears the filter and lets watched files back in.
            var shown = {};
            watchedFilterUsers.forEach(function (u) { shown[u.Id] = true; });
            var unseen = (config.WatchedFilterUserIds || []).filter(function (id) { return !shown[id]; });
            config.WatchedFilterUserIds = collectWatchedFilterUsers().concat(unseen);
            // Skipping is on exactly while users are picked.
            config.SkipWatchedByAllUsers = config.WatchedFilterUserIds.length > 0;
            config.DownloadNewContentMode = getValue('selDownloadNewContentMode', 'Enabled');
            config.ReplaceExistingContentMode = getValue('selReplaceExistingContentMode', 'Enabled');
            var missing = getValue('selMissingContent', 'Keep');
            config.MissingContentAction = missing;
            // The older field is kept matching, so an earlier value left in it is never read back.
            config.DeleteMissingContentMode = missing === 'Delete' ? 'Enabled' : missing === 'DeleteAfterApproval' ? 'RequireApproval' : 'Disabled';
            config.EnableRecyclingBin = getChecked('chkEnableRecyclingBin');
            config.RecyclingBinPath = getValue('txtRecyclingBinPath');
            config.RecyclingBinRetentionDays = getIntValue('txtRecyclingBinRetentionDays', 7);
            config.RemoveEmptyFoldersOnDelete = getChecked('chkRemoveEmptyFolders');
            config.MaxConcurrentDownloads = getIntValue('txtMaxConcurrentDownloads', 2);
            config.TempDownloadPath = getValue('txtTempDownloadPath') || null;
            config.MinimumFreeDiskSpaceGb = getIntValue('txtMinFreeDiskSpace', 10);
        }, 'saveContentStatus', 'Content settings saved.', 'Could not save the content settings.').then(loadLinkCacheStatus);
    }

    function loadHistorySettings(config) {
        needsUserMapping(config, 'chkEnableHistorySync', 'historyNeedsMapping', config.EnableHistorySync === true);
        setChecked('chkHistorySyncPlayedStatus', config.HistorySyncPlayedStatus !== false);
        setChecked('chkHistorySyncPlaybackPosition', config.HistorySyncPlaybackPosition !== false);
        setChecked('chkHistorySyncPlayCount', config.HistorySyncPlayCount !== false);
        setChecked('chkHistorySyncLastPlayedDate', config.HistorySyncLastPlayedDate !== false);
        setChecked('chkHistorySyncFavorites', config.HistorySyncFavorites !== false);
    }

    function saveHistorySettings() {
        saveSection(function (config) {
            config.EnableHistorySync = hasUserMapping(config) && getChecked('chkEnableHistorySync');
            config.HistorySyncPlayedStatus = getChecked('chkHistorySyncPlayedStatus');
            config.HistorySyncPlaybackPosition = getChecked('chkHistorySyncPlaybackPosition');
            config.HistorySyncPlayCount = getChecked('chkHistorySyncPlayCount');
            config.HistorySyncLastPlayedDate = getChecked('chkHistorySyncLastPlayedDate');
            config.HistorySyncFavorites = getChecked('chkHistorySyncFavorites');
        }, 'saveHistoryStatus', 'History settings saved.', 'Could not save the history settings.');
    }

    // A choice is saved by name and may be read back by name or by number.
    var PRECEDENCE = ['Newest', 'Rank'];

    function choiceOf(value, names) {
        if (typeof value === 'number') return names[value] || names[0];
        return names.indexOf(value) >= 0 ? value : names[0];
    }

    function loadMetadataSettings(config) {
        setChecked('chkEnableMetadataSync', config.EnableMetadataSync || false);
        setChecked('chkMetadataSyncMetadata', config.MetadataSyncMetadata !== false);
        setChecked('chkMetadataSyncGenres', config.MetadataSyncGenres !== false);
        setChecked('chkMetadataSyncTags', config.MetadataSyncTags !== false);
        setChecked('chkMetadataSyncStudios', config.MetadataSyncStudios !== false);
        setChecked('chkMetadataSyncPeople', config.MetadataSyncPeople === true);
        setChecked('chkMetadataSyncImages', config.MetadataSyncImages !== false);
        setChecked('chkMetadataSyncFolderItems', config.MetadataSyncFolderItems === true);
        setChecked('chkMetadataSyncProvider', config.MetadataSyncProvider !== false);
        setChecked('chkMetadataSyncSegmentProvider', config.MetadataSyncSegmentProvider === true);
        setChecked('chkMetadataSyncReplaceSegments', config.MetadataSyncReplaceSegments === true);
        setChecked('chkMetadataSyncRemoveSegments', config.MetadataSyncRemoveSegments === true);
        setChecked('chkMetadataSyncConsolidateIntros', config.MetadataSyncConsolidateIntros === true);
        setChecked('chkMetadataSyncConsolidateOutros', config.MetadataSyncConsolidateOutros === true);
        setChecked('chkMetadataSyncConsolidateOtherSegments', config.MetadataSyncConsolidateOtherSegments === true);
        setValue('selMetadataSyncPrecedence', choiceOf(config.MetadataSyncPrecedence, PRECEDENCE));
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
            config.MetadataSyncProvider = getChecked('chkMetadataSyncProvider');
            config.MetadataSyncSegmentProvider = getChecked('chkMetadataSyncSegmentProvider');
            config.MetadataSyncReplaceSegments = getChecked('chkMetadataSyncReplaceSegments');
            config.MetadataSyncRemoveSegments = getChecked('chkMetadataSyncRemoveSegments');
            config.MetadataSyncConsolidateIntros = getChecked('chkMetadataSyncConsolidateIntros');
            config.MetadataSyncConsolidateOutros = getChecked('chkMetadataSyncConsolidateOutros');
            config.MetadataSyncConsolidateOtherSegments = getChecked('chkMetadataSyncConsolidateOtherSegments');
            config.MetadataSyncPrecedence = getValue('selMetadataSyncPrecedence') || 'Newest';
        }, 'saveMetadataStatus', 'Metadata settings saved.', 'Could not save the metadata settings.');
    }

    function loadPeopleSettings(config) {
        setChecked('chkEnablePeopleSync', config.EnablePeopleSync === true);
        setChecked('chkPeopleSyncImages', config.PeopleSyncImages !== false);
        setChecked('chkPeopleSyncProvider', config.PeopleSyncProvider !== false);
        setValue('selPeopleSyncPrecedence', choiceOf(config.PeopleSyncPrecedence, PRECEDENCE));
    }

    function savePeopleSettings() {
        saveSection(function (config) {
            config.EnablePeopleSync = getChecked('chkEnablePeopleSync');
            config.PeopleSyncImages = getChecked('chkPeopleSyncImages');
            config.PeopleSyncProvider = getChecked('chkPeopleSyncProvider');
            config.PeopleSyncPrecedence = getValue('selPeopleSyncPrecedence') || 'Newest';
        }, 'savePeopleStatus', 'People settings saved.', 'Could not save the people settings.');
    }

    function loadUserSyncSettings(config) {
        needsUserMapping(config, 'chkEnableUserSync', 'userNeedsMapping', config.EnableUserSync === true);
        setChecked('chkUserSyncPolicy', config.UserSyncPolicy !== false);
        setChecked('chkUserSyncConfiguration', config.UserSyncConfiguration !== false);
        setChecked('chkUserSyncProfileImage', config.UserSyncProfileImage !== false);
        setValue('selUserSyncPrecedence', choiceOf(config.UserSyncPrecedence, PRECEDENCE));
        setValue('selUserSyncConflict', choiceOf(config.UserSyncConflict, ['Newest', 'Permissive', 'Restrictive']));
    }

    function saveUserSyncSettings() {
        saveSection(function (config) {
            config.EnableUserSync = hasUserMapping(config) && getChecked('chkEnableUserSync');
            config.UserSyncPolicy = getChecked('chkUserSyncPolicy');
            config.UserSyncConfiguration = getChecked('chkUserSyncConfiguration');
            config.UserSyncProfileImage = getChecked('chkUserSyncProfileImage');
            config.UserSyncPrecedence = getValue('selUserSyncPrecedence') || 'Newest';
            config.UserSyncConflict = getValue('selUserSyncConflict') || 'Newest';
        }, 'saveUserSyncStatus', 'User settings saved.', 'Could not save the user settings.');
    }

    function hasUserMapping(config) {
        var norm = function (id) { return (id || '').replace(/-/g, '').toLowerCase(); };
        return (config.Servers || []).some(function (server) {
            var self = server.AccessLevel === 'User' ? norm(server.AuthenticatedUserId) : '';
            return server.IsEnabled !== false && (server.UserMappings || []).some(function (m) {
                return m.IsEnabled !== false && m.SourceUserId && m.LocalUserId && (!self || norm(m.SourceUserId) === self);
            });
        });
    }

    function loadPlaylistSettings(config) {
        needsUserMapping(config, 'chkEnablePlaylistSync', 'playlistNeedsMapping', config.EnablePlaylistSync === true);
        setChecked('chkPlaylistSyncShares', config.PlaylistSyncShares !== false);
        setValue('selPlaylistSyncPrecedence', choiceOf(config.PlaylistSyncPrecedence, PRECEDENCE));
        setValue('selPlaylistRemovedAction', choiceOf(config.PlaylistRemovedAction, ['Delete', 'Keep']));
    }

    // History, playlists, and user settings all travel by user mapping. Without one there is nothing
    // to sync, so the switch is off and says what to do first.
    function needsUserMapping(config, toggleId, warningId, on) {
        var mapped = hasUserMapping(config);
        var toggle = getEl(toggleId);
        if (toggle) toggle.disabled = !mapped;
        setChecked(toggleId, mapped && on);
        setVisible(warningId, !mapped);
    }

    function savePlaylistSettings() {
        saveSection(function (config) {
            config.EnablePlaylistSync = hasUserMapping(config) && getChecked('chkEnablePlaylistSync');
            config.PlaylistSyncShares = getChecked('chkPlaylistSyncShares');
            config.PlaylistSyncPrecedence = getValue('selPlaylistSyncPrecedence') || 'Newest';
            config.PlaylistRemovedAction = getValue('selPlaylistRemovedAction') || 'Delete';
        }, 'savePlaylistStatus', 'Playlist settings saved.', 'Could not save the playlist settings.');
    }

    function loadProcessingSettings(config) {
        setValue('txtHintDebounceSeconds', config.HintDebounceSeconds || 60);
        setValue('txtHintRetrySeconds', config.HintRetrySeconds || 30);
        setValue('txtHintRetryMaxMinutes', config.HintRetryMaxMinutes || 60);
        setValue('txtHintMaxRetries', config.HintMaxRetries || 0);
        setValue('txtHintRefusedRetryMinutes', config.HintRefusedRetryMinutes || 15);
        setValue('txtHintRefusedMaxRetries', config.HintRefusedMaxRetries || 0);
        setValue('txtRefreshParallelism', config.RefreshParallelism || 8);
        setValue('txtFullScanIntervalHours', config.FullScanIntervalHours == null ? 24 : config.FullScanIntervalHours);
        setChecked('chkDeepImageVerification', config.DeepImageVerification === true);
        setValue('txtMaxRetryCount', config.MaxRetryCount || 3);

        // Changes are only sent to Push and Sync servers, so their timing shows only when one exists.
        var sends = (config.Servers || []).some(function (s) {
            return s.IsEnabled !== false && (s.Mode === 'Push' || s.Mode === 'Sync' || s.Mode === 1 || s.Mode === 2);
        });
        setVisible('hintSettings', sends);
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
            config.FullScanIntervalHours = Math.min(720, Math.max(0, getIntValue('txtFullScanIntervalHours', 24)));
            config.DeepImageVerification = getChecked('chkDeepImageVerification');
            config.MaxRetryCount = Math.min(10, Math.max(1, getIntValue('txtMaxRetryCount', 3)));
        }, 'saveProcessingStatus', 'Processing settings saved.', 'Could not save the processing settings.');
    }

    var grantUsers = [];
    var grantsLoaded = false;

    function fetchGrantUsers() {
        return ApiClient.fetch({ url: ApiClient.getUrl('Users'), type: 'GET', dataType: 'json' }).then(function (users) {
            grantUsers = (users || []).map(function (u) { return { Id: u.Id.replace(/-/g, '').toLowerCase(), Name: u.Name }; });
        }).catch(function () {
            grantUsers = [];
        });
    }

    // The Repeater that holds the whitelist rows, or null before the users have loaded.
    var grantRepeater = null;

    // grantChoices
    // Lists the choices of a row's User field: API Keys, every user here, and any saved user no longer here.
    //
    // Inputs
    // grants [object[]]: The saved rows.
    //
    // Output
    // [object[]]: The choices as { value, label }.
    function grantChoices(grants) {
        var choices = [{ value: '', label: 'Select a user' }, { value: 'apikey', label: 'API Keys' }];
        var known = { apikey: true };
        grantUsers.forEach(function (u) {
            known[u.Id] = true;
            choices.push({ value: u.Id, label: u.Name });
        });
        grants.forEach(function (g) {
            if (!g.UserId || known[g.UserId]) return;
            known[g.UserId] = true;
            choices.push({ value: g.UserId, label: g.UserName || g.UserId });
        });
        return choices;
    }

    // collectGrants
    // Reads the whitelist rows that name a user, with each row's locations split at its commas.
    //
    // Output
    // [object[]]: The rows as { UserId, UserName, Locations }.
    function collectGrants() {
        if (!grantRepeater) return [];
        var names = {};
        grantRepeater.choices.forEach(function (c) { names[c.value] = c.label; });
        return grantRepeater.handle.getValue().filter(function (row) { return !!row.UserId; }).map(function (row) {
            return {
                UserId: row.UserId,
                UserName: names[row.UserId] || '',
                Locations: String(row.Locations || '').split(',').map(function (l) { return l.trim(); }).filter(function (l) { return l.length > 0; })
            };
        });
    }

    // loadCallerAccessSettings
    // Loads the users and draws the whitelist as a Repeater of a user and their locations.
    //
    // Inputs
    // config [object]: The plugin configuration.
    function loadCallerAccessSettings(config) {
        grantsLoaded = false;
        fetchGrantUsers().then(function () {
            var container = getEl('callerGrants');
            if (!container) return;
            if (grantRepeater) grantRepeater.handle.destroy();
            container.innerHTML = '';
            var grants = (config.PeerCallerGrants || []).map(function (g) {
                return { UserId: (g.UserId || '').replace(/-/g, '').toLowerCase(), UserName: g.UserName, Locations: (g.Locations || []).join(', ') };
            });
            var choices = grantChoices(grants);
            var handle = serverSyncModule.createRepeater({
                fields: [
                    { key: 'UserId', label: 'User', type: 'select', options: choices, width: '1fr' },
                    { key: 'Locations', label: 'Locations', type: 'text', placeholder: 'Default', width: '2fr' }
                ],
                value: grants.map(function (g) { return { UserId: g.UserId, Locations: g.Locations }; }),
                addText: 'Add User',
                rowName: 'User',
                emptyText: 'No user is listed.'
            });
            grantRepeater = { handle: handle, choices: choices };
            container.appendChild(handle.element);
            grantsLoaded = true;
        });
    }

    function saveCallerAccessSettings() {
        if (!grantsLoaded) return;
        var grants = collectGrants();
        saveSection(function (config) {
            config.PeerCallerGrants = grants;
        }, 'saveCallerAccessStatus', 'Whitelist saved.', 'Could not save the whitelist.');
    }

    function loadConfig() {
        ServerSyncShared.getConfig().then(function(config) {
            currentConfig = config;

            loadContentSettings(config);
            loadBandwidthSettings(config);
            loadHistorySettings(config);
            loadMetadataSettings(config);
            loadPeopleSettings(config);
            loadUserSyncSettings(config);
            loadPlaylistSettings(config);
            loadProcessingSettings(config);

            // A sync type's options only show while it is on.
            [['chkEnableContentSync', 'contentSyncOptions'], ['chkEnableHistorySync', 'historySyncOptions'],
                ['chkEnableMetadataSync', 'metadataSyncOptions'], ['chkEnablePeopleSync', 'peopleSyncOptions'],
                ['chkEnableUserSync', 'userSyncOptions'], ['chkEnablePlaylistSync', 'playlistSyncOptions'],
                ['chkMetadataSyncSegmentProvider', 'segmentProviderOptions']].forEach(function (pair) {
                ServerSyncShared.bindReveal(pair[0], pair[1]);
            });
            loadCallerAccessSettings(config);
            fetchWatchedFilterUsers();
        }).catch(function() {
            ServerSyncShared.setStatus('settingsStatus', 'Could not load the settings. Reload the page.', true, { timeout: 0 });
        });
    }

    // confirmReset
    // Asks in the shared dialog before a reset. Each paragraph of the question is its own line.
    //
    // Inputs
    // title [string]: The dialog title.
    // paragraphs [string[]]: The question, then what it costs.
    //
    // Output
    // [Promise]: Resolves true when the reset is confirmed.
    function confirmReset(title, paragraphs) {
        return confirmDialog(paragraphs.map(function (p) { return '<p>' + escapeHtml(p) + '</p>'; }).join(''), {
            html: true,
            title: title,
            confirmText: 'Reset',
            destructive: true
        });
    }

    // resetTable
    // Asks first, then forgets one sync table's tracking. Media is untouched.
    //
    // Inputs
    // table [string]: The route name of the table.
    // tableName [string]: What the table is called in the question and the result, such as "content sync".
    function resetTable(table, tableName) {
        confirmReset('Reset Table', [
            'Reset the ' + tableName + ' table?',
            'This forgets all of its tracking data, and the next scan starts over. It cannot be undone.'
        ]).then(function (confirmed) {
            if (!confirmed) return;

            ServerSyncShared.apiRequest('Reset/' + table, 'POST').then(function() {
                ServerSyncShared.setStatus('resetStatus', tableName.charAt(0).toUpperCase() + tableName.slice(1) + ' table reset.', false);
            }).catch(function(err) {
                console.error('Reset ' + table + ' error:', err);
                ServerSyncShared.setStatus('resetStatus', 'Could not reset the ' + tableName + ' table.', true, { timeout: 10000 });
            });
        });
    }

    // resetEntireDatabase
    // Asks first, then forgets the tracking of every sync table. Media is untouched.
    function resetEntireDatabase() {
        confirmReset('Reset Database', [
            'Reset the entire sync database?',
            'This forgets all tracking data for every sync type, and everything starts over. It cannot be undone.'
        ]).then(function (confirmed) {
            if (!confirmed) return;

            ServerSyncShared.apiRequest('Reset/All', 'POST').then(function() {
                ServerSyncShared.setStatus('resetStatus', 'Database reset.', false);
            }).catch(function(err) {
                console.error('Reset All error:', err);
                ServerSyncShared.setStatus('resetStatus', 'Could not reset the database.', true, { timeout: 10000 });
            });
        });
    }

    // bindPage
    // Wires the page's buttons and controls. Base calls it once, the first time the page shows.
    function bindPage() {
        ServerSyncShared.initCollapsibles();

        bindClick('btnSaveProcessing', saveProcessingSettings);
        bindClick('btnSaveCallerAccess', saveCallerAccessSettings);

        bindClick('btnSaveContentSettings', saveContentSettings);
        bindClick('btnSaveBandwidth', saveBandwidthSettings);
        bindClick('btnClearLinkCache', clearLinkCache);
        bindClick('btnSaveHistorySettings', saveHistorySettings);
        bindClick('btnSaveMetadataSettings', saveMetadataSettings);
        bindClick('btnSavePeopleSettings', savePeopleSettings);
        bindClick('btnSaveUserSyncSettings', saveUserSyncSettings);
        bindClick('btnSavePlaylistSettings', savePlaylistSettings);

        bindClick('btnResetContentTable', function() { resetTable('Content', 'content sync'); });
        bindClick('btnResetHistoryTable', function() { resetTable('History', 'history sync'); });
        bindClick('btnResetMetadataTable', function() { resetTable('Metadata', 'metadata sync'); });
        bindClick('btnResetPeopleTable', function() { resetTable('People', 'people sync'); });
        bindClick('btnResetPlaylistTable', function() { resetTable('Playlists', 'playlist sync'); });
        bindClick('btnResetUserTable', function() { resetTable('Users', 'user sync'); });
        bindClick('btnResetEntireDatabase', resetEntireDatabase);
    }
}

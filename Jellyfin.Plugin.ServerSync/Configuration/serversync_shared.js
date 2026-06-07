// ============================================
// SERVER SYNC PLUGIN - SHARED MODULE (base shim)
// ============================================
// Thin wrapper over the JPKribs.Jellyfin.Base shared bundle.
// The base package embeds jpkribs_shared.js into this plugin's assembly and
// PluginBase.GetSharedPages("serversync") serves it as
// configurationpage?name=serversync_jpkribs_shared.js.
//
// This module re-exports the base paginated table unchanged and exposes
// createServerSyncShared(view) — the base shared helper bag (bound to this
// plugin's id + "ServerSync" controller prefix) plus the few ServerSync-domain
// helpers the pages rely on (item/user/person thumbnails, local server name).
//
// Note: getTabs() is defined locally in each page controller (sync, settings)
// because LibraryMenu.setTabs() must be called synchronously during the
// viewshow event.
// ============================================

import {
    createShared as baseCreateShared,
    createPaginatedTable,
    generateGuid
} from '/web/configurationpage?name=serversync_jpkribs_shared.js';

export { createPaginatedTable, generateGuid };

var PLUGIN_ID = 'ebd650b5-6f4c-4ccb-b10d-23dffb3a7286';

export function createServerSyncShared(view) {
    var shared = baseCreateShared(view, PLUGIN_ID, 'ServerSync');

    // Local server name (fetched once via fetchLocalServerName).
    shared.localServerName = null;

    // Item thumbnail. Starts as portrait (40×60); on load, if the image is
    // landscape (width > height), it swaps to the landscape class (106×60).
    shared.renderItemThumb = function (serverUrl, apiKey, itemId) {
        var imgUrl = shared.buildSourceImageUrl(serverUrl, apiKey, itemId, 'Primary', 80);
        if (!imgUrl) {
            return '<div class="jpk-table-row-thumb-placeholder"><span class="material-icons">movie</span></div>';
        }
        return '<img class="jpk-table-row-thumb jpk-table-row-thumb-portrait" src="' + shared.escapeHtml(imgUrl) +
            '" loading="lazy"' +
            ' onload="if(this.naturalWidth>this.naturalHeight){this.classList.remove(\'jpk-table-row-thumb-portrait\');this.classList.add(\'jpk-table-row-thumb-landscape\')}"' +
            ' onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'flex\'" />' +
            '<div class="jpk-table-row-thumb-placeholder" style="display:none"><span class="material-icons">movie</span></div>';
    };

    shared.renderUserThumb = function (serverUrl, apiKey, userId) {
        var imgUrl = shared.buildSourceUserImageUrl(serverUrl, apiKey, userId, 80);
        if (!imgUrl) {
            return '<div class="jpk-table-row-thumb-user-placeholder"><span class="material-icons">person</span></div>';
        }
        return '<img class="jpk-table-row-thumb-user" src="' + shared.escapeHtml(imgUrl) +
            '" loading="lazy" onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'flex\'" />' +
            '<div class="jpk-table-row-thumb-user-placeholder" style="display:none"><span class="material-icons">person</span></div>';
    };

    // Always portrait (no landscape auto-detection) since person images are headshots.
    shared.renderPersonThumb = function (serverUrl, apiKey, personId) {
        var imgUrl = shared.buildSourceImageUrl(serverUrl, apiKey, personId, 'Primary', 80);
        if (!imgUrl) {
            return '<div class="jpk-table-row-thumb-placeholder"><span class="material-icons">person</span></div>';
        }
        return '<img class="jpk-table-row-thumb jpk-table-row-thumb-portrait" src="' + shared.escapeHtml(imgUrl) +
            '" loading="lazy"' +
            ' onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'flex\'" />' +
            '<div class="jpk-table-row-thumb-placeholder" style="display:none"><span class="material-icons">person</span></div>';
    };

    shared.fetchLocalServerName = function () {
        return ApiClient.getPublicSystemInfo().then(function (info) {
            shared.localServerName = info.ServerName || 'Local';
            return shared.localServerName;
        }).catch(function () {
            shared.localServerName = 'Local';
            return shared.localServerName;
        });
    };

    return shared;
}

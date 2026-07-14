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
    generateGuid,
    SECRET_KEPT
} from '/web/configurationpage?name=serversync_jpkribs_shared.js';

export { createPaginatedTable, generateGuid, SECRET_KEPT };

var PLUGIN_ID = 'ebd650b5-6f4c-4ccb-b10d-23dffb3a7286';

export function createServerSyncShared(view) {
    var shared = baseCreateShared(view, PLUGIN_ID, 'ServerSync');

    shared.SECRET_KEPT = SECRET_KEPT;

    // Local server name (fetched once via fetchLocalServerName).
    shared.localServerName = null;

    // Source images go through the plugin's ImageProxy endpoint so the
    // source API key never reaches the browser. The image bytes are fetched
    // with ApiClient (session token in the Authorization header, never in
    // the URL — URLs land in proxy logs and browser caches) and attached as
    // a blob object-URL after the row is in the DOM.
    var thumbSeq = 0;

    shared.scheduleProxyImage = function (imgId, id, isUser, maxHeight) {
        var attempts = 0;
        function tryLoad() {
            var img = document.getElementById(imgId);
            if (!img) {
                // Caller inserts the rendered HTML in the same tick; one
                // deferred retry covers slower table renderers.
                if (++attempts <= 3) setTimeout(tryLoad, 100 * attempts);
                return;
            }
            var url = ApiClient.getUrl('ServerSync/ImageProxy', {
                itemId: id,
                user: isUser ? 'true' : 'false',
                maxHeight: maxHeight || 80
            });
            ApiClient.fetch({ url: url, type: 'GET' }).then(function (response) {
                if (!response || !response.ok) throw new Error('image fetch failed');
                return response.blob();
            }).then(function (blob) {
                var objectUrl = URL.createObjectURL(blob);
                img.addEventListener('load', function () { URL.revokeObjectURL(objectUrl); }, { once: true });
                img.src = objectUrl;
            }).catch(function () {
                img.style.display = 'none';
                if (img.nextElementSibling) img.nextElementSibling.style.display = 'flex';
            });
        }
        setTimeout(tryLoad, 0);
    };

    function renderProxyThumb(id, isUser, imgClass, extraAttrs, placeholderHtml) {
        if (!id) return placeholderHtml;
        var imgId = 'ss-thumb-' + (++thumbSeq);
        shared.scheduleProxyImage(imgId, id, isUser, 80);
        return '<img id="' + imgId + '" class="' + imgClass + '"' + extraAttrs + ' />' +
            placeholderHtml.replace('class="', 'style="display:none" class="');
    }

    // Item thumbnail. Starts as portrait (40×60); on load, if the image is
    // landscape (width > height), it swaps to the landscape class (106×60).
    shared.renderItemThumb = function (itemId) {
        return renderProxyThumb(itemId, false,
            'jpk-table-row-thumb jpk-table-row-thumb-portrait',
            ' onload="if(this.naturalWidth>this.naturalHeight){this.classList.remove(\'jpk-table-row-thumb-portrait\');this.classList.add(\'jpk-table-row-thumb-landscape\')}"',
            '<div class="jpk-table-row-thumb-placeholder"><span class="material-icons">movie</span></div>');
    };

    shared.renderUserThumb = function (userId) {
        return renderProxyThumb(userId, true,
            'jpk-table-row-thumb-user',
            '',
            '<div class="jpk-table-row-thumb-user-placeholder"><span class="material-icons">person</span></div>');
    };

    // Always portrait (no landscape auto-detection) since person images are headshots.
    shared.renderPersonThumb = function (personId) {
        return renderProxyThumb(personId, false,
            'jpk-table-row-thumb jpk-table-row-thumb-portrait',
            '',
            '<div class="jpk-table-row-thumb-placeholder"><span class="material-icons">person</span></div>');
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

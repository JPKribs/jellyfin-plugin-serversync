// serversync_shared.js
// Wraps the Base shared bundle for the Server Sync pages. It re-exports Base's table and dialog builders
// and adds createServerSyncShared(view), the helper bag with the Server Sync helpers the pages use.
//
// The Storage tab shows while content, history, or metadata sync is on. A page cannot wait for the config
// before it sets its tabs, so the last known answer is kept per browser under STORAGE_TAB_KEY.
// A missing answer shows the tab. When the answer changes, the page's onTabsChanged callback runs.

// The specifier is relative so it resolves against this module's own URL, and a server hosted under
// a base URL still loads the bundle.
import {
    createShared as baseCreateShared,
    createPaginatedTable,
    createSortableCardList,
    createChoiceGroup,
    createCheckboxList,
    createPieChart,
    createUsageMeter,
    createRepeater,
    generateGuid,
    bindDialog,
    confirmDialog,
    createConfigPage,
    setTabs,
    SECRET_KEPT
} from './configurationpage?name=serversync_jpkribs_shared.js';

// Re-exports
// The Base builders and the kept secret sentinel, so a page takes everything from this one module.
export { createPaginatedTable, createSortableCardList, createChoiceGroup, createCheckboxList, createPieChart, createUsageMeter, createRepeater, generateGuid, bindDialog, confirmDialog, SECRET_KEPT };

var PLUGIN_ID = 'ebd650b5-6f4c-4ccb-b10d-23dffb3a7286';

// STORAGE_TAB_KEY
// The browser storage key that holds whether the Storage tab shows. Each page controller reads it
// inline before this module loads, so a page sets its tabs without waiting for the config.
export var STORAGE_TAB_KEY = 'serversync.storageTab';

// readStorageTabFlag
// Reads the stored answer for whether the Storage tab shows.
//
// Output
// [string]: '1' or '0', or null when nothing is stored or storage is blocked.
function readStorageTabFlag() {
    try {
        return window.localStorage.getItem(STORAGE_TAB_KEY);
    } catch (e) {
        return null;
    }
}

// writeStorageTabFlag
// Stores whether the Storage tab shows. A blocked storage leaves the tab at its default of shown.
//
// Inputs
// value [string]: '1' to show the tab or '0' to hide it.
function writeStorageTabFlag(value) {
    try {
        window.localStorage.setItem(STORAGE_TAB_KEY, value);
    } catch (e) {
        // Storage blocked: the tab keeps its default of shown.
    }
}

// serverSyncTabs
// Lists the dashboard tabs in order. The Storage tab shows unless the last config read said no sync type uses it.
//
// Inputs
// alwaysStorage [boolean]: Whether to list Storage whatever is stored, as the Storage page does for itself.
//
// Output
// [object[]]: Each tab's href and name.
export function serverSyncTabs(alwaysStorage) {
    var tabs = [
        { href: 'configurationpage?name=serversync_sync', name: 'Sync' },
        { href: 'configurationpage?name=serversync_servers', name: 'Servers' }
    ];
    if (alwaysStorage || readStorageTabFlag() !== '0') tabs.push({ href: 'configurationpage?name=serversync_storage', name: 'Storage' });
    tabs.push({ href: 'configurationpage?name=serversync_settings', name: 'Settings' });
    return tabs;
}

// createServerSyncPage
// Wires a Server Sync page through Base's page lifecycle, with the Server Sync helper bag and the shared tabs.
//
// Inputs
// view [HTMLElement]: The page the dashboard passes to the controller.
// opts [object]: page is the tab's own page name. alwaysStorage lists the Storage tab whatever is stored.
//   bind, load, and onHide are Base's, and each is given the Server Sync bag and the page handle.
//
// Output
// [object]: shared is the Server Sync helper bag, and page is Base's page handle with addCleanup and createPoller.
export function createServerSyncPage(view, opts) {
    var bag = null;
    var tabs = function () { return serverSyncTabs(opts.alwaysStorage); };
    var tabIndex = function () {
        var href = 'configurationpage?name=' + opts.page;
        var index = tabs().findIndex(function (tab) { return tab.href === href; });
        return index < 0 ? 0 : index;
    };
    var pass = function (fn) { return fn ? function (shared, page) { fn(bag, page); } : undefined; };
    var page = createConfigPage(view, {
        pluginId: PLUGIN_ID,
        apiPrefix: 'Sync',
        tabsKey: 'serversync',
        get tabIndex() { return tabIndex(); },
        tabs: tabs,
        bind: pass(opts.bind),
        load: pass(opts.load),
        onHide: pass(opts.onHide)
    });
    bag = createServerSyncShared(view, page.shared);
    // A config load that turns a sync type on or off adds or drops the Storage tab. A hidden page leaves the tabs to the one on screen.
    bag.onTabsChanged = function () {
        if (!view.classList.contains('hide')) setTabs('serversync', tabIndex(), tabs);
    };
    return { shared: bag, page: page };
}

// createServerSyncShared
// Builds the helper bag a Server Sync page uses. It is Base's bag for the Sync route, plus the config
// readers, the thumbnail renderers, the save helpers, and the health banner. Loading it also checks the
// plugin's health and shows its banner when the plugin cannot run.
//
// Inputs
// view [HTMLElement]: The page the helpers work in.
// bag [object]: Base's bag to add the helpers to, such as a page handle's. A new one is built when left out.
//
// Output
// [object]: The helper bag, with every Base helper and the Server Sync helpers below.
export function createServerSyncShared(view, bag) {
    var shared = bag || baseCreateShared(view, PLUGIN_ID, 'Sync');

    shared.SECRET_KEPT = SECRET_KEPT;

    // Set by a page to set its tabs again when the Storage tab appears or disappears.
    shared.onTabsChanged = null;

    // rememberContentSync
    // Records whether the Storage tab shows, and tells the page when that changed the tab list. The tab
    // holds the library filter, which content, history, and metadata sync all apply.
    //
    // Inputs
    // config [object]: The plugin configuration. Anything without EnableContentSync is ignored.
    shared.rememberContentSync = function (config) {
        if (!config || typeof config !== 'object' || !('EnableContentSync' in config)) return;
        var value = config.EnableContentSync || config.EnableHistorySync || config.EnableMetadataSync ? '1' : '0';
        var before = readStorageTabFlag();
        if (before === value) return;
        writeStorageTabFlag(value);
        // A missing flag already showed the tab, so only a change of what shows reruns setTabs.
        var shownBefore = before !== '0';
        if (shownBefore !== (value === '1') && typeof shared.onTabsChanged === 'function') {
            shared.onTabsChanged();
        }
    };

    var baseGetConfig = shared.getConfig;
    var baseSaveConfig = shared.saveConfig;

    // getConfig
    // Reads the plugin configuration and remembers whether the Storage tab shows.
    //
    // Output
    // [Promise]: Resolves with the configuration.
    shared.getConfig = function () {
        return baseGetConfig.apply(shared, arguments).then(function (config) {
            shared.rememberContentSync(config);
            return config;
        });
    };

    // saveConfig
    // Saves the plugin configuration and remembers whether the Storage tab shows.
    //
    // Inputs
    // config [object]: The configuration to save.
    //
    // Output
    // [Promise]: Resolves when the save landed.
    shared.saveConfig = function (config) {
        return baseSaveConfig.apply(shared, arguments).then(function (result) {
            shared.rememberContentSync(config);
            return result;
        });
    };

    // This server's name, filled by fetchLocalServerName.
    shared.localServerName = null;

    // Source images go through the plugin's ImageProxy endpoint so the source API key never reaches the
    // browser. The image bytes are fetched with ApiClient, which keeps the session token in the
    // Authorization header and out of the URL, and attached as an object URL once the row is in the DOM.
    var thumbSeq = 0;

    // scheduleProxyImage
    // Loads a source image through the proxy into an image element that is already in the DOM, or soon
    // will be. When the load fails, the image hides and the placeholder after it shows.
    //
    // Inputs
    // imgId [string]: The id of the image element.
    // id [string]: The item, person, or user to show.
    // isUser [boolean]: Whether id is a user.
    // maxHeight [number]: The tallest image to ask for, in pixels. Defaults to 80.
    // serverKey [string]: The configured server that holds it. A row with none belongs to the first scan server.
    shared.scheduleProxyImage = function (imgId, id, isUser, maxHeight, serverKey) {
        var attempts = 0;
        function tryLoad() {
            var img = document.getElementById(imgId);
            if (!img) {
                // The caller inserts the rendered HTML in the same tick. Up to three deferred
                // retries, each waiting a little longer, cover slower table renderers.
                if (++attempts <= 3) setTimeout(tryLoad, 100 * attempts);
                return;
            }
            var params = {
                itemId: id,
                user: isUser ? 'true' : 'false',
                maxHeight: maxHeight || 80
            };
            if (serverKey) params.serverKey = serverKey;
            var url = ApiClient.getUrl('Sync/ImageProxy', params);
            ApiClient.fetch({ url: url, type: 'GET' }).then(function (response) {
                if (!response || !response.ok) throw new Error('image fetch failed');
                return response.blob();
            }).then(function (blob) {
                var objectUrl = URL.createObjectURL(blob);
                img.addEventListener('load', function () { URL.revokeObjectURL(objectUrl); }, { once: true });
                img.src = objectUrl;
            }).catch(function () {
                img.classList.add('hidden');
                if (img.nextElementSibling) img.nextElementSibling.classList.remove('hidden');
            });
        }
        setTimeout(tryLoad, 0);
    };

    // proxyImage
    // Gives a table column the loader for a thumbnail that is read through this server's image proxy.
    //
    // Inputs
    // id [string]: The item, person, or user to show.
    // isUser [boolean]: Whether id is a user.
    // serverKey [string]: The configured server that holds it.
    //
    // Output
    // [function]: A loader that resolves to the image, or null when there is no id.
    shared.proxyImage = function (id, isUser, serverKey) {
        if (!id) return null;
        return function () {
            var params = { itemId: id, user: isUser ? 'true' : 'false', maxHeight: 80 };
            if (serverKey) params.serverKey = serverKey;
            return ApiClient.fetch({ url: ApiClient.getUrl('Sync/ImageProxy', params), type: 'GET' }).then(function (response) {
                if (!response || !response.ok) throw new Error('image fetch failed');
                return response.blob();
            });
        };
    };

    // scanServers
    // Lists the configured servers that scan, in rank order. A server scans when it is on, has a Server
    // URL and API Key, and pulls. A server with no Mode is a Pull server, matching the server side default.
    // The mode arrives as the enum name or, from older writers, as its number, so both spellings count.
    //
    // Inputs
    // config [object]: The plugin configuration.
    //
    // Output
    // [object[]]: The servers that scan.
    shared.scanServers = function (config) {
        return ((config && config.Servers) || []).filter(function (s) {
            if (!s || s.IsEnabled === false || !s.Url || !s.ApiKey) return false;
            var mode = s.Mode === undefined || s.Mode === null || s.Mode === '' ? 'Pull' : s.Mode;
            return mode === 'Pull' || mode === 'Sync' || mode === 0 || mode === 2;
        });
    };

    // allLibraryMappings
    // Gathers the library mappings of every server that scans.
    //
    // Inputs
    // config [object]: The plugin configuration.
    //
    // Output
    // [object[]]: The mappings, in server rank order.
    shared.allLibraryMappings = function (config) {
        var out = [];
        shared.scanServers(config).forEach(function (s) { out = out.concat(s.LibraryMappings || []); });
        return out;
    };

    // allUserMappings
    // Gathers the user mappings of every server that scans.
    //
    // Inputs
    // config [object]: The plugin configuration.
    //
    // Output
    // [object[]]: The mappings, in server rank order.
    shared.allUserMappings = function (config) {
        var out = [];
        shared.scanServers(config).forEach(function (s) { out = out.concat(s.UserMappings || []); });
        return out;
    };

    // serverFor
    // Finds the configured server a row came from. A row with no key, or a key that no server holds,
    // belongs to the first scan server.
    //
    // Inputs
    // config [object]: The plugin configuration.
    // serverKey [string]: The key the row carries.
    //
    // Output
    // [object]: The server, or null when none scans.
    shared.serverFor = function (config, serverKey) {
        var servers = (config && config.Servers) || [];
        if (serverKey) {
            var match = servers.find(function (s) { return s.Key === serverKey; });
            if (match) return match;
        }
        return shared.scanServers(config)[0] || null;
    };

    // serverDisplayName
    // Names a server for a table or a message: its name, else the name it reported, else its Server URL.
    //
    // Inputs
    // server [object]: The configured server.
    //
    // Output
    // [string]: The name, or "Source" when there is none.
    shared.serverDisplayName = function (server) {
        if (!server) return 'Source';
        return server.Name || server.ServerName || server.Url || 'Source';
    };

    // serverNameFor
    // Names the server a row came from.
    //
    // Inputs
    // config [object]: The plugin configuration.
    // serverKey [string]: The key the row carries.
    //
    // Output
    // [string]: The server's display name.
    shared.serverNameFor = function (config, serverKey) {
        return shared.serverDisplayName(shared.serverFor(config, serverKey));
    };

    // requestFor
    // Builds the body of a request that names one configured server, for the endpoints that talk to it.
    // The server resolves a kept key sentinel against the stored entry with the same key.
    //
    // Inputs
    // server [object]: The configured server.
    // apiKeyOverride [string]: An API Key to send in place of the server's own.
    //
    // Output
    // [object]: The request body.
    shared.requestFor = function (server, apiKeyOverride) {
        return {
            ServerUrl: server.Url,
            ApiKey: apiKeyOverride || (server.ApiKey ? server.ApiKey : ''),
            ServerKey: server.Key,
            AllowPrivateNetwork: server.AllowPrivateNetwork !== false,
            AuthenticatedUserId: server.AuthenticatedUserId || null
        };
    };

    // saveSection
    // Saves one section of the config. It reads the config again, applies only that section's fields, and
    // saves, since posting a page load snapshot whole would overwrite what other writers changed since,
    // such as the timestamps and failure records the tasks write, or the other dashboard pages' sections.
    // The result shows on the status element.
    //
    // Inputs
    // mutator [function]: Called with the fresh config, and changes it in place.
    // statusId [string]: The id of the status element that reports the result.
    // successMessage [string]: What the status says when the save landed.
    // failureMessage [string]: What the status says when it did not.
    //
    // Output
    // [Promise]: Resolves with the saved config when the save landed and null when it did not. The caller
    // keeps its own copy of the config and updates it only from a save the server really kept.
    shared.saveSection = function (mutator, statusId, successMessage, failureMessage) {
        var pending = null;
        return shared.getConfig().then(function (config) {
            mutator(config);
            pending = config;
            return shared.saveConfig(config);
        }).then(function () {
            shared.setStatus(statusId, successMessage, false);
            return pending;
        }, function () {
            shared.setStatus(statusId, failureMessage, true, { timeout: 10000 });
            return null;
        });
    };

    // pageHelpers
    // Binds the helpers the Servers, Settings, and Storage pages call by bare name to this bag, so a page
    // takes the ones it uses in one destructuring assignment once the module has loaded.
    //
    // Output
    // [object]: The bound helpers.
    shared.pageHelpers = function () {
        return {
            escapeHtml: shared.escapeHtml.bind(shared),
            apiRequest: shared.apiRequest.bind(shared),
            setVisible: shared.setVisible.bind(shared),
            bindClick: shared.bindClick.bind(shared),
            getEl: shared.getEl.bind(shared),
            setChecked: shared.setChecked.bind(shared),
            getChecked: shared.getChecked.bind(shared),
            setValue: shared.setValue.bind(shared),
            getValue: shared.getValue.bind(shared),
            getIntValue: shared.getIntValue.bind(shared),
            requestFor: shared.requestFor
        };
    };

    // plural
    // Writes a count with the right form of a noun, such as "1 item" or "3 items".
    //
    // Inputs
    // count [number]: How many there are.
    // one [string]: The noun for exactly one.
    // many [string]: The noun for any other count.
    //
    // Output
    // [string]: The count, a space, and the noun.
    shared.plural = function (count, one, many) {
        return count + ' ' + (count === 1 ? one : many);
    };

    // syncStatus
    // Reports a result on the Sync tab's status line.
    //
    // Inputs
    // message [string]: What happened, as one sentence.
    // isError [boolean]: Whether it went wrong.
    shared.syncStatus = function (message, isError) {
        shared.setStatus('syncStatus', message, !!isError, { timeout: 10000 });
    };

    // fetchLocalServerName
    // Reads this server's name, once, and keeps it in localServerName.
    //
    // Output
    // [Promise]: Resolves with the name, or "Local" when it could not be read.
    shared.fetchLocalServerName = function () {
        return ApiClient.getPublicSystemInfo().then(function (info) {
            shared.localServerName = info.ServerName || 'Local';
            return shared.localServerName;
        }).catch(function () {
            shared.localServerName = 'Local';
            return shared.localServerName;
        });
    };

    // showHealth
    // Shows a banner at the top of the page while Server Sync is held for a database an older release
    // wrote, and removes it once the plugin is ready. A page cannot work while it is held, so each one
    // says so instead of only failing to load its data.
    //
    // Output
    // [Promise]: Resolves once the banner is in step with the health.
    shared.showHealth = function () {
        return shared.apiRequest('Health', 'GET').then(function (health) {
            var host = view.querySelector('.content-primary');
            var banner = view.querySelector('.serversync-health-banner');
            if (!health || health.Ready || !host) {
                if (banner) banner.remove();
                return;
            }
            if (!banner) {
                banner = document.createElement('div');
                banner.className = 'serversync-health-banner base-error';
                banner.setAttribute('role', 'alert');
                host.insertBefore(banner, host.firstChild);
            }
            banner.textContent = health.Message || 'Server Sync cannot run on this database.';
        }).catch(function () { });
    };
    shared.showHealth();

    return shared;
}

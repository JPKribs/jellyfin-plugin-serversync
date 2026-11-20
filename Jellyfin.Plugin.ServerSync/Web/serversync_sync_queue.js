// serversync_sync_queue.js
// The Change queue view. Built by serversync_sync.js from the page context once the shared
// module is in.

// createQueueModule
// Builds the Change queue view: the hint counts, the servers' delivery states, and the list of changes. It
// reloads itself every few seconds while it is the view on screen, and the page stops it on viewhide.
//
// Inputs
// ctx [object]: The page context from serversync_sync.js.
//
// Output
// [object]: A handle with init, load, startAutoRefresh, stopAutoRefresh, render, renderList, and the
// helpers that format a row.
export function createQueueModule(ctx) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;
    var confirmDialog = ctx.confirmDialog;
    // The page's strings, read through the bag so the loaded handle is always the one used.
    var t = {
        tr: function(key) { return ServerSyncShared.t.tr(key); },
        format: function() { return ServerSyncShared.t.format.apply(null, arguments); }
    };

    var DISCARD_BUTTON = function(lane, id) {
        var label = ServerSyncShared.escapeHtml(t.tr('ButtonDiscard'));
        return '<button type="button" class="base-row-btn serversync-queue-discard" data-lane="' + lane + '" data-discard="' + ServerSyncShared.escapeHtml(String(id)) +
            '" title="' + label + '" aria-label="' + label + '"><span aria-hidden="true">&times;</span></button>';
    };

    // keyed
    // Reads the string a map of keys names for a kind, or the fallback when the map has none.
    //
    // Inputs
    // keys [object]: String keys by kind.
    // kind [any]: The kind to name.
    // fallback [string]: The text when the map has no key for the kind.
    //
    // Output
    // [string]: The text in the viewer's language.
    function keyed(keys, kind, fallback) {
        return keys[kind] ? t.tr(keys[kind]) : fallback;
    }

    // Both hint queues and every peer's delivery state, read from
    // Sync/Hints. Rows are few and short lived, so a plain table that
    // reloads itself every few seconds fits better than the paginated table.

    var QueueModule = {
        _timer: null,
        _tick: null,
        // A poll still in flight is not started again, and a picture that has not changed is not rebuilt.
        _loading: false,
        _signature: null,
        // Numbers every load so only the newest reply renders.
        _loadSeq: 0,
        // Posters fetched through the image proxy, kept for the life of the page so a refresh does not
        // fetch every poster again.
        _proxyCache: {},
        _maxRows: 200,
        _bound: false,
        _config: null,
        _data: null,
        _states: { Gathering: true, Pending: true, Sent: true, Received: true, Failed: true },
        _dir: 'all',
        _peer: null,

        init: function() {
            var self = this;
            if (!this._bound) {
                this._bound = true;
                view.querySelector('#btnQueueRefresh').addEventListener('click', function() { self.load(true); });
                view.querySelector('#btnQueueRun').addEventListener('click', function() { self.runNow(); });
                view.querySelector('#btnQueueRetryFailed').addEventListener('click', function() { self.failedAction('Hints/Failed/Retry', 'POST', 'Retried', 'ToastFailedRetriedOne', 'ToastFailedRetriedMany'); });
                view.querySelector('#btnQueueDismissFailed').addEventListener('click', function() {
                    confirmDialog(t.tr('ConfirmDismissFailed'), {
                        title: t.tr('TitleDismissFailed'),
                        confirmText: t.tr('ButtonDismiss'),
                        destructive: true
                    }).then(function(confirmed) {
                        if (confirmed) self.failedAction('Hints/Failed', 'DELETE', 'Removed', 'ToastFailedDismissedOne', 'ToastFailedDismissedMany');
                    });
                });
                view.querySelector('#btnQueueUnmatchedDismiss').addEventListener('click', function() { self.dismissUnmatched(); });
                view.querySelector('#queueList').addEventListener('click', function(e) { self._onDiscard(e); });
                // An image error does not bubble, so the list catches it on the way down.
                view.querySelector('#queueList').addEventListener('error', function(e) { self._onImageError(e); }, true);
                view.querySelectorAll('.serversync-queue-cards .serversync-queue-card').forEach(function(card) {
                    card.addEventListener('click', function() {
                        self._states[card.dataset.state] = !self._states[card.dataset.state];
                        card.classList.toggle('serversync-on', self._states[card.dataset.state]);
                        self.renderList();
                    });
                });
                view.querySelector('#queueFilters').addEventListener('click', function(e) {
                    var btn = e.target.closest('.serversync-filter-pill');
                    if (!btn) return;
                    if (btn.dataset.dir) { self._dir = btn.dataset.dir; self._peer = null; }
                    else { self._peer = self._peer === btn.dataset.peer ? null : btn.dataset.peer; self._dir = 'all'; }
                    self._reflectFilters();
                    self.renderList();
                });
            }
            this.load();
        },

        startAutoRefresh: function() {
            var self = this;
            this.stopAutoRefresh();
            this._timer = setInterval(function() { self.load(); }, 8000);
            // Countdowns tick between refreshes.
            this._tick = setInterval(function() { self._tickChips(); }, 1000);
        },

        stopAutoRefresh: function() {
            if (this._timer) { clearInterval(this._timer); this._timer = null; }
            if (this._tick) { clearInterval(this._tick); this._tick = null; }
        },

        load: function(force) {
            var self = this;
            if (self._loading && !force) return Promise.resolve();
            self._loading = true;
            // A forced load can start while a poll is still in flight, and the poll's older reply must not
            // land last and put back a picture the forced load already replaced.
            var seq = ++self._loadSeq;
            return ServerSyncShared.apiRequest('Hints', 'GET').then(function(data) {
                if (seq !== self._loadSeq) return;
                var signature = JSON.stringify(data || {});
                if (!force && signature === self._signature) return;
                self._signature = signature;
                self._data = data || {};
                self.render();
            }).catch(function() {
                // Leave the last picture in place. The next tick tries again.
            }).then(function() {
                // Only the newest load clears the flag, so an older reply finishing first does not let the
                // next poll start while the newest load is still in flight.
                if (seq === self._loadSeq) self._loading = false;
            });
        },

        // Fetches a poster through the image proxy once per page and remembers it.
        _loadProxy: function(imgId, cacheKey, itemId, peerKey) {
            var self = this;
            var url = ApiClient.getUrl('Sync/ImageProxy', { itemId: itemId, user: 'false', maxHeight: 128, serverKey: peerKey });
            ApiClient.fetch({ url: url, type: 'GET' }).then(function(response) {
                if (!response || !response.ok) throw new Error('image fetch failed');
                return response.blob();
            }).then(function(blob) {
                var objectUrl = URL.createObjectURL(blob);
                self._proxyCache[cacheKey] = objectUrl;
                var img = document.getElementById(imgId);
                if (img) img.src = objectUrl;
            }).catch(function() {
                self._proxyCache[cacheKey] = '';
                var img = document.getElementById(imgId);
                if (img) {
                    img.classList.add('hidden');
                    if (img.nextElementSibling) img.nextElementSibling.classList.remove('hidden');
                }
            });
        },

        // Hides an image that failed to load and shows its placeholder, or only hides an avatar.
        _onImageError: function(e) {
            var img = e.target;
            if (!img || img.tagName !== 'IMG') return;
            if (img.classList.contains('serversync-queue-avatar')) {
                img.classList.add('serversync-broken');
                return;
            }
            img.classList.add('hidden');
            if (img.nextElementSibling) img.nextElementSibling.classList.remove('hidden');
        },

        runNow: function() {
            var self = this;
            var btn = view.querySelector('#btnQueueRun');
            btn.disabled = true;
            ServerSyncShared.apiRequest('Hints/Run', 'POST').then(function() {
                return self.load(true);
            }).then(function() {
                ServerSyncShared.syncStatus(t.tr('ToastQueueRan'), false);
            }).catch(function() {
                ServerSyncShared.syncStatus(t.tr('ErrorQueueRun'), true);
            }).then(function() {
                btn.disabled = false;
            });
        },

        // Every failed row at once: sent again, or discarded. doneOne and doneMany are the keys of the result.
        failedAction: function(route, method, countKey, doneOne, doneMany) {
            var self = this;
            var buttons = [view.querySelector('#btnQueueRetryFailed'), view.querySelector('#btnQueueDismissFailed')];
            buttons.forEach(function(b) { b.disabled = true; });
            ServerSyncShared.apiRequest(route, method).then(function(result) {
                return self.load(true).then(function() {
                    ServerSyncShared.syncStatus(ServerSyncShared.plural((result && result[countKey]) || 0, doneOne, doneMany), false);
                });
            }).catch(function() {
                ServerSyncShared.syncStatus(t.tr('ErrorQueueFailedAction'), true);
            }).then(function() {
                buttons.forEach(function(b) { b.disabled = false; });
            });
        },

        dismissUnmatched: function() {
            var self = this;
            var btn = view.querySelector('#btnQueueUnmatchedDismiss');
            btn.disabled = true;
            ServerSyncShared.apiRequest('Hints/Unmatched', 'DELETE').then(function() {
                return self.load(true);
            }).catch(function() {
                ServerSyncShared.syncStatus(t.tr('ErrorDismissWarning'), true);
            }).then(function() {
                btn.disabled = false;
            });
        },

        render: function() {
            var data = this._data || {};
            var outbound = data.Outbound || [];
            var inbound = data.Inbound || [];
            var peers = data.Peers || [];
            var counts = data.OutboundCounts || {};
            var count = function(state) {
                if (counts[state] !== undefined) return counts[state];
                return outbound.filter(function(r) { return QueueModule._stateName(r.State) === state; }).length;
            };
            view.querySelector('#queuePendingCount').textContent = count('Pending');
            view.querySelector('#queueSentCount').textContent = count('Sent');
            view.querySelector('#queueFailedCount').textContent = count('Failed');
            view.querySelector('#queueFailedActions').classList.toggle('hidden', !(count('Failed') > 0 || data.InboundFailingCount > 0));
            view.querySelector('#queueInboundCount').textContent = data.InboundCount !== undefined ? data.InboundCount : inbound.length;
            view.querySelector('#queueGatheringCount').textContent = (data.Gathering || []).length || data.Pending || 0;

            var peerRow = view.querySelector('#queuePeerRow');
            peerRow.innerHTML = peers.map(function(p) {
                var paused = p.PausedUntil && new Date(p.PausedUntil) > new Date();
                var state = paused ? t.format('QueuePeerPaused', QueueModule._time(p.PausedUntil))
                    : p.LastAttempt ? t.format('QueuePeerDelivered', QueueModule._ago(p.LastAttempt)) : t.tr('QueuePeerNothingSent');
                var sends = p.Sends === null || p.Sends === undefined ? t.tr('QueuePeerSendsAll')
                    : p.Sends.length === 0 ? t.tr('QueuePeerSendsNone')
                    : t.format('QueuePeerSends', p.Sends.map(QueueModule._kindLabel).join(', '));
                return '<div class="serversync-health-card serversync-queue-peer-card">' +
                    '<span class="serversync-health-label">' + ServerSyncShared.escapeHtml(p.Name || p.Key) + '</span>' +
                    '<span class="serversync-health-value' + (paused ? ' serversync-paused' : '') + '">' + ServerSyncShared.escapeHtml(state) + '</span>' +
                    '<div class="serversync-queue-peer-reason">' + ServerSyncShared.escapeHtml(sends) + '</div>' +
                    (p.Reason ? '<div class="serversync-queue-peer-reason">' + ServerSyncShared.escapeHtml(p.Reason) + '</div>' : '') +
                    '</div>';
            }).join('');
            view.querySelector('#queuePeers').classList.toggle('hidden', peers.length === 0);
            view.querySelector('#queueNoPeers').classList.toggle('hidden', peers.length > 0);

            var unmatchedEl = view.querySelector('#queueUnmatched');
            var unmatched = data.Unmatched || 0;
            unmatchedEl.classList.toggle('hidden', unmatched === 0);
            if (unmatched > 0) {
                unmatchedEl.querySelector('#queueUnmatchedText').textContent = data.LastUnmatched
                    ? ServerSyncShared.plural(unmatched, 'QueueUnmatchedLastOne', 'QueueUnmatchedLastMany', data.LastUnmatched)
                    : ServerSyncShared.plural(unmatched, 'QueueUnmatchedOne', 'QueueUnmatchedMany');
            }

            // Peer filters follow the peers that exist.
            var filters = view.querySelector('#queueFilters');
            filters.querySelectorAll('[data-peer]').forEach(function(b) { b.remove(); });
            peers.forEach(function(p) {
                var b = document.createElement('button');
                b.type = 'button'; b.className = 'serversync-filter-pill'; b.dataset.peer = p.Key; b.textContent = p.Name || p.Key;
                filters.appendChild(b);
            });
            this._reflectFilters();
            this.renderList();
        },

        _reflectFilters: function() {
            var self = this;
            view.querySelectorAll('#queueFilters .serversync-filter-pill').forEach(function(b) {
                b.classList.toggle('serversync-on', b.dataset.dir ? (self._peer === null && b.dataset.dir === self._dir) : b.dataset.peer === self._peer);
            });
        },

        // Every change as one row, whatever lane it is in, newest activity first.
        _rows: function() {
            var data = this._data || {};
            var rows = [];
            (data.Gathering || []).forEach(function(g) {
                rows.push({ lane: 'gathering', state: 'Gathering', dir: 'out', at: g.EditedAt, kind: g.Kind, recorded: g.Recorded,
                    title: g.Title || g.Change, subtitle: g.Subtitle, itemType: g.ItemType, itemId: g.ItemId, userId: g.UserId, userName: g.UserName, dueAt: g.DueAt, what: QueueModule._what(g.Kind) });
            });
            (data.Outbound || []).forEach(function(r) {
                var state = QueueModule._stateName(r.State);
                rows.push({ lane: 'out', state: state, dir: 'out', at: r.SentAt || r.CreatedAt, kind: r.Kind, recorded: r.Recorded, id: r.Id, peerKey: r.PeerKey, peerName: r.PeerName || r.PeerKey,
                    title: r.Title || QueueModule._fileName(r.ItemPath) || r.UserName || r.Key, subtitle: r.Subtitle, itemType: r.ItemType, itemId: r.ItemId, userId: r.UserId, userName: r.UserName,
                    attempts: r.Attempts, lastError: r.LastError, nextAttempt: r.NextAttempt, sentAt: r.SentAt, what: QueueModule._what(r.Kind) });
            });
            (data.Inbound || []).forEach(function(r) {
                var origin = QueueModule._origin(r.OriginServerId);
                rows.push({ lane: 'in', state: 'Received', dir: 'in', at: r.ReceivedAt, kind: r.Kind, recorded: r.Recorded, id: r.Id, peerKey: origin ? origin.Key : null, peerName: origin ? (origin.Name || origin.ServerName || origin.Url) : r.OriginServerId,
                    title: QueueModule._fileName(r.ItemPath) || r.UserName || r.Key, subtitle: null, itemType: r.ItemType, originItemId: r.ItemId, userName: r.UserName,
                    attempts: r.Attempts, lastError: r.LastError, receivedAt: r.ReceivedAt, parked: r.Parked === true, what: QueueModule._what(r.Kind) });
            });
            rows.sort(function(a, b) { return new Date(b.at || 0) - new Date(a.at || 0); });
            return rows;
        },

        renderList: function() {
            var self = this;
            var rows = this._rows().filter(function(r) {
                if (!self._states[r.state]) return false;
                if (self._peer) return r.peerKey === self._peer;
                return self._dir === 'all' || r.dir === self._dir;
            });
            var list = view.querySelector('#queueList');
            var shown = rows.slice(0, self._maxRows);
            list.innerHTML = shown.map(function(r) { return QueueModule._row(r); }).join('') +
                (rows.length > shown.length
                    ? '<div class="serversync-queue-more">' + ServerSyncShared.escapeHtml(t.format('QueueShowingLatest', shown.length, rows.length)) + '</div>'
                    : '');
            view.querySelector('#queueEmpty').classList.toggle('hidden', rows.length > 0);
            list.classList.toggle('hidden', rows.length === 0);
        },

        _row: function(r) {
            var esc = ServerSyncShared.escapeHtml;
            var isPerson = r.kind === 'People' || r.kind === 2;
            var isUser = r.kind === 'Users' || r.kind === 4;
            var isPlaylist = r.kind === 'Playlists' || r.kind === 5;
            // Jellyfin's own shapes: a person is round, an episode or a home video is wide, the rest are posters.
            var shape = isPerson || isUser ? ' serversync-round' : QueueModule._isWide(r.itemType) ? ' serversync-wide' : '';
            var icon = isPlaylist ? 'queue_music' : isUser || isPerson ? 'person' : QueueModule._isWide(r.itemType) ? 'tv' : 'movie';
            var holder = '<div class="serversync-queue-thumb-holder' + shape + '"><span class="material-icons">' + icon + '</span></div>';
            var hiddenHolder = holder.replace('class="', 'class="hidden ');
            var thumb;
            if (r.itemId && !isUser) {
                var url = ApiClient.getImageUrl(r.itemId, { type: 'Primary', maxHeight: 128 });
                thumb = '<img class="serversync-queue-thumb' + shape + '" loading="lazy" src="' + esc(url) + '" alt="" />' +
                    hiddenHolder;
            } else if (r.originItemId && r.peerKey && !isUser) {
                var cacheKey = r.peerKey + '|' + r.originItemId;
                var cached = QueueModule._proxyCache[cacheKey];
                if (cached) {
                    thumb = '<img class="serversync-queue-thumb' + shape + '" src="' + esc(cached) + '" alt="" />' + hiddenHolder;
                } else if (cached === '') {
                    thumb = holder;
                } else {
                    var id = 'ss-q-thumb-' + Math.random().toString(36).slice(2);
                    setTimeout(function() { QueueModule._loadProxy(id, cacheKey, r.originItemId, r.peerKey); }, 0);
                    thumb = '<img id="' + id + '" class="serversync-queue-thumb' + shape + '" alt="" />' + hiddenHolder;
                }
            } else {
                thumb = holder;
            }

            var who = '';
            if (r.userName && (r.kind === 'History' || r.kind === 0)) {
                var avatar = r.userId ? '<img class="serversync-queue-avatar" src="' + esc(ApiClient.getUserImageUrl(r.userId, { type: 'Primary', height: 32 })) + '" alt="" />' : '<i class="serversync-queue-avatar"></i>';
                who = '<span class="serversync-queue-who">' + avatar + esc(r.userName) + '</span>';
            }
            var where = r.dir === 'in' ? esc(t.format('QueueFrom', r.peerName || t.tr('QueueSomeServer'))) : (r.peerName ? esc(t.format('QueueTo', r.peerName)) : '');
            var sub = who + '<span>' + esc(r.what) + (where ? ' · ' + where : '') + '</span>';
            var badges = '<span class="base-badge ' + QueueModule._kindColor(r.kind) + '">' + esc(QueueModule._kindLabelTitle(r.kind)) + '</span>' +
                (r.recorded === false ? '<span class="base-badge gray">' + esc(t.tr('BadgeProvider')) + '</span>' : '');

            var chip, meta = '', err = false, act = '';
            if (r.state === 'Gathering') {
                chip = '<span class="serversync-queue-chip serversync-gathering" data-due="' + esc(r.dueAt || '') + '" data-edited="' + esc(r.at || '') + '"><i class="serversync-queue-ring"></i><span class="serversync-queue-chip-text">' + esc(t.format('QueueChipGathering', QueueModule._countdown(r.dueAt))) + '</span></span>';
                meta = t.format('QueueMetaEdited', QueueModule._ago(r.at));
            } else if (r.state === 'Pending') {
                chip = '<span class="serversync-queue-chip serversync-pending">' + esc(r.attempts > 0 ? t.format('QueueChipPendingRetry', QueueModule._time(r.nextAttempt)) : t.format('QueueChipPending', r.peerName)) + '</span>';
                if (r.attempts > 0) { meta = t.format('QueueMetaAttempt', r.lastError || t.tr('QueueMetaRetrying'), r.attempts); err = true; } else { meta = t.tr('QueueMetaWaiting'); }
                act = DISCARD_BUTTON('Outbound', r.id);
            } else if (r.state === 'Sent') {
                chip = '<span class="serversync-queue-chip serversync-sent">' + esc(t.format('QueueChipSent', r.peerName)) + '</span>';
                meta = t.format('QueueMetaAccepted', QueueModule._ago(r.sentAt));
                act = DISCARD_BUTTON('Outbound', r.id);
            } else if (r.state === 'Failed') {
                chip = '<span class="serversync-queue-chip serversync-failed">' + esc(t.format('QueueChipFailed', r.peerName)) + '</span>';
                meta = r.lastError || t.tr('QueueMetaRejected'); err = true;
                act = DISCARD_BUTTON('Outbound', r.id);
            } else {
                chip = '<span class="serversync-queue-chip serversync-received">' + esc(t.tr(r.parked ? 'QueueChipReceivedParked' : r.attempts > 0 ? 'QueueChipReceivedRetrying' : 'QueueChipReceivedNext')) + '</span>';
                if (r.parked) { meta = r.lastError || t.tr('QueueMetaParked'); } else if (r.attempts > 0) { meta = t.format('QueueMetaAttempt', r.lastError || t.tr('QueueMetaRetrying'), r.attempts); err = true; } else { meta = QueueModule._ago(r.receivedAt); }
                act = DISCARD_BUTTON('Inbound', r.id);
            }

            var title = esc(r.title || '') + (r.subtitle ? ' <span class="serversync-queue-episode"><i class="serversync-queue-separator">· </i>' + esc(r.subtitle) + '</span>' : '');
            return '<div class="serversync-queue-row">' +
                '<div class="serversync-queue-thumb-wrap">' + thumb + '</div>' +
                '<div class="serversync-queue-info"><div class="serversync-queue-title">' + title + '</div><div class="serversync-queue-sub">' + sub + '</div><div class="serversync-queue-badges">' + badges + '</div></div>' +
                '<div class="serversync-queue-right">' + chip + '<span class="serversync-queue-meta' + (err ? ' serversync-error' : '') + '">' + esc(meta) + '</span>' + act + '</div>' +
                '</div>';
        },

        // Episodes, home videos, trailers, and recordings carry a landscape primary image in Jellyfin.
        _isWide: function(itemType) {
            return itemType === 'Episode' || itemType === 'Video' || itemType === 'Trailer' || itemType === 'Recording' || itemType === 'MusicVideo';
        },

        // The gathering countdown moves every second without a round trip.
        _tickChips: function() {
            view.querySelectorAll('#queueList .serversync-queue-chip.serversync-gathering').forEach(function(chip) {
                var due = chip.getAttribute('data-due');
                var edited = chip.getAttribute('data-edited');
                if (!due) return;
                var text = chip.querySelector('.serversync-queue-chip-text');
                if (text) text.textContent = t.format('QueueChipGathering', QueueModule._countdown(due));
                var ring = chip.querySelector('.serversync-queue-ring');
                if (ring && edited) {
                    var total = new Date(due) - new Date(edited);
                    var done = Date.now() - new Date(edited);
                    ring.style.setProperty('--p', Math.max(0, Math.min(100, total > 0 ? 100 * done / total : 100)) + '%');
                }
            });
        },

        _countdown: function(iso) {
            if (!iso) return '';
            var s = Math.round((new Date(iso) - Date.now()) / 1000);
            if (s <= 0) return t.tr('QueueSendingNow');
            return s < 120 ? t.format('QueueSendsInSeconds', s) : t.format('QueueSendsInMinutes', Math.round(s / 60));
        },

        _origin: function(serverId) {
            var servers = (this._config && this._config.Servers) || [];
            return servers.find(function(s) { return s.ServerId === serverId; }) || null;
        },

        _what: function(kind) {
            return keyed({ 0: 'KindWhatHistory', 1: 'KindWhatMetadata', 2: 'KindWhatPeople', 3: 'KindWhatContent', 4: 'KindWhatUsers', 5: 'KindWhatPlaylists',
                History: 'KindWhatHistory', Metadata: 'KindWhatMetadata', People: 'KindWhatPeople', Content: 'KindWhatContent', Users: 'KindWhatUsers', Playlists: 'KindWhatPlaylists' }, kind, String(kind));
        },

        _kindLabel: function(kind) {
            return keyed({ History: 'KindWhatHistory', Metadata: 'KindWhatMetadata', People: 'KindPeopleLower', Content: 'KindFilesLower', Users: 'KindWhatUsers', Playlists: 'KindPlaylistsLower' }, kind, String(kind).toLowerCase());
        },

        _kindLabelTitle: function(kind) {
            return keyed({ 0: 'AccessWatchHistory', 1: 'LabelMetadata', 2: 'Person', 3: 'File', 4: 'HeaderUser', 5: 'LabelPlaylist',
                History: 'AccessWatchHistory', Metadata: 'LabelMetadata', People: 'Person', Content: 'File', Users: 'HeaderUser', Playlists: 'LabelPlaylist' }, kind, String(kind));
        },

        _kindColor: function(kind) {
            return { 0: 'purple', 1: 'blue', 2: 'green', 3: 'orange', 4: 'gray', 5: 'yellow', History: 'purple', Metadata: 'blue', People: 'green', Content: 'orange', Users: 'gray', Playlists: 'yellow' }[kind] || 'gray';
        },

        _stateName: function(state) {
            var names = { 0: 'Pending', 1: 'Sent', 2: 'Failed' };
            return names[state] || String(state);
        },

        _fileName: function(path) {
            if (!path) return '';
            var parts = path.split(/[\\/]/);
            var last = parts[parts.length - 1] || path;
            return last.replace(/\.[^.]+$/, '');
        },

        _ago: function(iso) {
            if (!iso) return '';
            var seconds = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
            if (seconds < 60) return t.format('AgoSeconds', seconds);
            if (seconds < 3600) return t.format('AgoMinutes', Math.round(seconds / 60));
            if (seconds < 86400) return t.format('AgoHours', Math.round(seconds / 3600));
            return t.format('AgoDays', Math.round(seconds / 86400));
        },

        _time: function(iso) {
            if (!iso) return '';
            var d = new Date(iso);
            var diff = d.getTime() - Date.now();
            if (diff > 0 && diff < 86400000) {
                var minutes = Math.round(diff / 60000);
                return minutes < 1 ? t.tr('InUnderMinute') : minutes < 60 ? t.format('InMinutes', minutes) : t.format('InHours', Math.round(minutes / 60));
            }
            return d.toLocaleString();
        },

        _onDiscard: function(e) {
            var btn = e.target.closest('[data-discard]');
            if (!btn) return;
            var self = this;
            btn.disabled = true;
            ServerSyncShared.apiRequest('Hints/' + btn.getAttribute('data-lane') + '/' + btn.getAttribute('data-discard'), 'DELETE').then(function() {
                ServerSyncShared.syncStatus(t.tr('ToastHintDiscarded'), false);
                return self.load(true);
            }).catch(function() {
                ServerSyncShared.syncStatus(t.tr('ErrorDiscardHint'), true);
                btn.disabled = false;
            });
        }
    };

    return QueueModule;
}

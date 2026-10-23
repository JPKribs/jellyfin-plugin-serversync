// ============================================
// SERVER SYNC PLUGIN - SYNC PAGE: CHANGE QUEUE VIEW
// ============================================
// The Change Queue view. Built by serversync_sync.js from the page context once the shared
// module is in.
// ============================================

export function createQueueModule(ctx) {
    'use strict';

    var view = ctx.view;
    var ServerSyncShared = ctx.shared;

    // ============================================
    // CHANGE QUEUE MODULE
    // ============================================
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
                view.querySelector('#btnQueueUnmatchedDismiss').addEventListener('click', function() { self.dismissUnmatched(); });
                view.querySelector('#queueList').addEventListener('click', function(e) { self._onDiscard(e); });
                view.querySelectorAll('.qCards .qCard').forEach(function(card) {
                    card.addEventListener('click', function() {
                        self._states[card.dataset.state] = !self._states[card.dataset.state];
                        card.classList.toggle('on', self._states[card.dataset.state]);
                        self.renderList();
                    });
                });
                view.querySelector('#queueFilters').addEventListener('click', function(e) {
                    var btn = e.target.closest('.qFilter');
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
            if (!ServerSyncShared || (self._loading && !force)) return Promise.resolve();
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
                    img.style.display = 'none';
                    if (img.nextElementSibling) img.nextElementSibling.style.display = 'flex';
                }
            });
        },

        runNow: function() {
            var self = this;
            var btn = view.querySelector('#btnQueueRun');
            btn.disabled = true;
            ServerSyncShared.apiRequest('Hints/Run', 'POST').then(function() {
                return self.load(true);
            }).then(function() {
                ServerSyncShared.showAlert('Delivered and applied what was due');
            }).catch(function() {
                ServerSyncShared.showAlert('The run failed. See the server log');
            }).then(function() {
                btn.disabled = false;
            });
        },

        dismissUnmatched: function() {
            var self = this;
            var btn = view.querySelector('#btnQueueUnmatchedDismiss');
            btn.disabled = true;
            ServerSyncShared.apiRequest('Hints/Unmatched', 'DELETE').then(function() {
                return self.load(true);
            }).catch(function() {
                ServerSyncShared.showAlert('Could not dismiss the warning');
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
            view.querySelector('#queueInboundCount').textContent = data.InboundCount !== undefined ? data.InboundCount : inbound.length;
            view.querySelector('#queueGatheringCount').textContent = (data.Gathering || []).length || data.Pending || 0;

            var peerRow = view.querySelector('#queuePeerRow');
            peerRow.innerHTML = peers.map(function(p) {
                var paused = p.PausedUntil && new Date(p.PausedUntil) > new Date();
                var state = paused ? 'Paused until ' + QueueModule._time(p.PausedUntil)
                    : p.LastAttempt ? 'Delivered ' + QueueModule._ago(p.LastAttempt) : 'Nothing sent yet';
                var sends = p.Sends === null || p.Sends === undefined ? 'Sends everything until the peer says what it applies'
                    : p.Sends.length === 0 ? 'Sends nothing: every module is off there'
                    : 'Sends ' + p.Sends.map(QueueModule._kindLabel).join(', ');
                return '<div class="healthCard queuePeerCard">' +
                    '<span class="healthLabel">' + ServerSyncShared.escapeHtml(p.Name || p.Key) + '</span>' +
                    '<span class="healthValue' + (paused ? ' paused' : '') + '">' + ServerSyncShared.escapeHtml(state) + '</span>' +
                    '<div class="queuePeerReason">' + ServerSyncShared.escapeHtml(sends) + '</div>' +
                    (p.Reason ? '<div class="queuePeerReason">' + ServerSyncShared.escapeHtml(p.Reason) + '</div>' : '') +
                    '</div>';
            }).join('');
            view.querySelector('#queuePeers').classList.toggle('hidden', peers.length === 0);
            view.querySelector('#queueNoPeers').classList.toggle('hidden', peers.length > 0);

            var unmatchedEl = view.querySelector('#queueUnmatched');
            var unmatched = data.Unmatched || 0;
            unmatchedEl.classList.toggle('hidden', unmatched === 0);
            if (unmatched > 0) {
                unmatchedEl.querySelector('#queueUnmatchedText').textContent = unmatched + ' local change' + (unmatched === 1 ? '' : 's') + ' matched no library or user mapping on any Push or Sync server, so nothing was sent for ' + (unmatched === 1 ? 'it' : 'them') +
                    (data.LastUnmatched ? '. Last: ' + data.LastUnmatched : '') + '. Saving the Libraries or Users step of those servers checks ' + (unmatched === 1 ? 'it' : 'them') + ' again.';
            }

            // Peer filters follow the peers that exist.
            var filters = view.querySelector('#queueFilters');
            filters.querySelectorAll('[data-peer]').forEach(function(b) { b.remove(); });
            peers.forEach(function(p) {
                var b = document.createElement('button');
                b.type = 'button'; b.className = 'qFilter'; b.dataset.peer = p.Key; b.textContent = p.Name || p.Key;
                filters.appendChild(b);
            });
            this._reflectFilters();
            this.renderList();
        },

        _reflectFilters: function() {
            var self = this;
            view.querySelectorAll('#queueFilters .qFilter').forEach(function(b) {
                b.classList.toggle('on', b.dataset.dir ? (self._peer === null && b.dataset.dir === self._dir) : b.dataset.peer === self._peer);
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
                    ? '<div class="qMore">Showing the latest ' + shown.length + ' of ' + rows.length + '. Filter by state or server to see others.</div>'
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
            var shape = isPerson || isUser ? ' round' : QueueModule._isWide(r.itemType) ? ' wide' : '';
            var icon = isPlaylist ? 'queue_music' : isUser || isPerson ? 'person' : QueueModule._isWide(r.itemType) ? 'tv' : 'movie';
            var holder = '<div class="qThumbHolder' + shape + '"><span class="material-icons">' + icon + '</span></div>';
            var thumb;
            if (r.itemId && !isUser) {
                var url = ApiClient.getImageUrl(r.itemId, { type: 'Primary', maxHeight: 128 });
                thumb = '<img class="qThumb' + shape + '" loading="lazy" src="' + esc(url) + '" alt="" onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'flex\'" />' +
                    holder.replace('<div ', '<div style="display:none" ');
            } else if (r.originItemId && r.peerKey && !isUser) {
                var cacheKey = r.peerKey + '|' + r.originItemId;
                var cached = QueueModule._proxyCache[cacheKey];
                if (cached) {
                    thumb = '<img class="qThumb' + shape + '" src="' + esc(cached) + '" alt="" />' + holder.replace('<div ', '<div style="display:none" ');
                } else if (cached === '') {
                    thumb = holder;
                } else {
                    var id = 'ss-q-thumb-' + Math.random().toString(36).slice(2);
                    setTimeout(function() { QueueModule._loadProxy(id, cacheKey, r.originItemId, r.peerKey); }, 0);
                    thumb = '<img id="' + id + '" class="qThumb' + shape + '" alt="" />' + holder.replace('<div ', '<div style="display:none" ');
                }
            } else {
                thumb = holder;
            }

            var who = '';
            if (r.userName && (r.kind === 'History' || r.kind === 0)) {
                var avatar = r.userId ? '<img class="qAvatar" src="' + esc(ApiClient.getUserImageUrl(r.userId, { type: 'Primary', height: 32 })) + '" alt="" onerror="this.style.visibility=\'hidden\'" />' : '<i class="qAvatar"></i>';
                who = '<span class="qWho">' + avatar + esc(r.userName) + '</span>';
            }
            var where = r.dir === 'in' ? 'from ' + esc(r.peerName || 'a peer') : (r.peerName ? 'to ' + esc(r.peerName) : '');
            var sub = who + '<span>' + esc(r.what) + (where ? ' · ' + where : '') + '</span>';
            var badges = '<span class="jpk-badge ' + QueueModule._kindColor(r.kind) + '">' + esc(QueueModule._kindLabelTitle(r.kind)) + '</span>' +
                (r.recorded === false ? '<span class="jpk-badge gray">provider</span>' : '');

            var chip, meta = '', err = false, act = '';
            if (r.state === 'Gathering') {
                chip = '<span class="qChip gathering" data-due="' + esc(r.dueAt || '') + '" data-edited="' + esc(r.at || '') + '"><i class="qRing"></i><span class="qChipText">Gathering · ' + QueueModule._countdown(r.dueAt) + '</span></span>';
                meta = 'edited ' + QueueModule._ago(r.at);
            } else if (r.state === 'Pending') {
                chip = '<span class="qChip pending">Pending' + (r.attempts > 0 ? ' · retry ' + QueueModule._time(r.nextAttempt) : ' · ' + esc(r.peerName)) + '</span>';
                if (r.attempts > 0) { meta = (r.lastError || 'retrying') + ', attempt ' + r.attempts; err = true; } else { meta = 'waiting for delivery'; }
                act = '<button type="button" class="qAct" data-lane="Outbound" data-discard="' + r.id + '" title="Discard this hint. The scheduled task still covers the change.">✕</button>';
            } else if (r.state === 'Sent') {
                chip = '<span class="qChip sent">Sent · waiting for ' + esc(r.peerName) + '</span>';
                meta = 'accepted ' + QueueModule._ago(r.sentAt);
                act = '<button type="button" class="qAct" data-lane="Outbound" data-discard="' + r.id + '" title="Discard this hint. The scheduled task still covers the change.">✕</button>';
            } else if (r.state === 'Failed') {
                chip = '<span class="qChip failed">Failed · ' + esc(r.peerName) + '</span>';
                meta = r.lastError || 'rejected'; err = true;
                act = '<button type="button" class="qAct" data-lane="Outbound" data-discard="' + r.id + '" title="Discard this hint.">✕</button>';
            } else {
                chip = '<span class="qChip received">Received · ' + (r.parked ? 'waiting for the item' : r.attempts > 0 ? 'retrying' : 'applying next pass') + '</span>';
                if (r.parked) { meta = r.lastError || 'waiting for the item to arrive here'; } else if (r.attempts > 0) { meta = (r.lastError || 'retrying') + ', attempt ' + r.attempts; err = true; } else { meta = QueueModule._ago(r.receivedAt); }
                act = '<button type="button" class="qAct" data-lane="Inbound" data-discard="' + r.id + '" title="Discard this hint. The scheduled task still covers the change.">✕</button>';
            }

            var title = esc(r.title || '') + (r.subtitle ? ' <span class="qEp"><i class="qSep">· </i>' + esc(r.subtitle) + '</span>' : '');
            return '<div class="qRow">' +
                '<div class="qThumbWrap">' + thumb + '</div>' +
                '<div class="qInfo"><div class="qTitle">' + title + '</div><div class="qSub">' + sub + '</div><div class="qBadges">' + badges + '</div></div>' +
                '<div class="qRight">' + chip + '<span class="qMeta' + (err ? ' err' : '') + '">' + esc(meta) + '</span>' + act + '</div>' +
                '</div>';
        },

        // Episodes, home videos, trailers, and recordings carry a landscape primary image in Jellyfin.
        _isWide: function(itemType) {
            return itemType === 'Episode' || itemType === 'Video' || itemType === 'Trailer' || itemType === 'Recording' || itemType === 'MusicVideo';
        },

        // The gathering countdown moves every second without a round trip.
        _tickChips: function() {
            view.querySelectorAll('#queueList .qChip.gathering').forEach(function(chip) {
                var due = chip.getAttribute('data-due');
                var edited = chip.getAttribute('data-edited');
                if (!due) return;
                var text = chip.querySelector('.qChipText');
                if (text) text.textContent = 'Gathering · ' + QueueModule._countdown(due);
                var ring = chip.querySelector('.qRing');
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
            if (s <= 0) return 'sending now';
            return s < 120 ? 'sends in ' + s + 's' : 'sends in ' + Math.round(s / 60) + 'm';
        },

        _origin: function(serverId) {
            var servers = (this._config && this._config.Servers) || [];
            return servers.find(function(s) { return s.ServerId === serverId; }) || null;
        },

        _what: function(kind) {
            return { 0: 'watch history', 1: 'metadata', 2: 'biography and images', 3: 'new file', 4: 'user settings', 5: 'playlist',
                History: 'watch history', Metadata: 'metadata', People: 'biography and images', Content: 'new file', Users: 'user settings', Playlists: 'playlist' }[kind] || String(kind);
        },

        _kindLabel: function(kind) {
            return { History: 'watch history', Metadata: 'metadata', People: 'people', Content: 'files', Users: 'user settings', Playlists: 'playlists' }[kind] || String(kind).toLowerCase();
        },

        _kindLabelTitle: function(kind) {
            return { 0: 'Watch history', 1: 'Metadata', 2: 'Person', 3: 'File', 4: 'User', 5: 'Playlist', History: 'Watch history', Metadata: 'Metadata', People: 'Person', Content: 'File', Users: 'User', Playlists: 'Playlist' }[kind] || String(kind);
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
            if (seconds < 60) return seconds + 's ago';
            if (seconds < 3600) return Math.round(seconds / 60) + 'm ago';
            if (seconds < 86400) return Math.round(seconds / 3600) + 'h ago';
            return Math.round(seconds / 86400) + 'd ago';
        },

        _time: function(iso) {
            if (!iso) return '';
            var d = new Date(iso);
            var diff = d.getTime() - Date.now();
            if (diff > 0 && diff < 86400000) {
                var minutes = Math.round(diff / 60000);
                return minutes < 1 ? 'in under a minute' : minutes < 60 ? 'in ' + minutes + 'm' : 'in ' + Math.round(minutes / 60) + 'h';
            }
            return d.toLocaleString();
        },

        _onDiscard: function(e) {
            var btn = e.target.closest('[data-discard]');
            if (!btn) return;
            var self = this;
            btn.disabled = true;
            ServerSyncShared.apiRequest('Hints/' + btn.getAttribute('data-lane') + '/' + btn.getAttribute('data-discard'), 'DELETE').then(function() {
                ServerSyncShared.showAlert('Hint discarded');
                return self.load(true);
            }).catch(function() {
                ServerSyncShared.showAlert('Could not discard the hint');
                btn.disabled = false;
            });
        }
    };

    return QueueModule;
}

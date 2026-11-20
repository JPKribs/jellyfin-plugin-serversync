// A stand-in ApiClient, LibraryMenu, and Dashboard with a generic pool for the Server Sync doc screenshots.
// This server is Living Room. It syncs with Basement, pulls from Cabin, and pushes to Office.
window.__errs = [];
window.addEventListener('error', function (e) { __errs.push(String(e.message)); });
window.addEventListener('unhandledrejection', function (e) { __errs.push('rejection: ' + String(e.reason && e.reason.stack || e.reason)); });
(function () { var ce = console.error; console.error = function () { __errs.push([].join.call(arguments, ' ')); ce.apply(console, arguments); }; })();

// The plugin's strings come from server.py, which serves them as the plugin's Translations route does.
(function () {
    var real = window.fetch;
    window.fetch = function (url, options) {
        var text = String(url);
        var at = text.indexOf('Sync/Translations');
        return real.call(window, at >= 0 ? '/' + text.slice(at) : url, options);
    };
})();

// The same strings for the answers the stub writes, such as the Check link rows the server builds.
var STRINGS = {};
var stringsReady = fetch('/Sync/Translations?language=en-us').then(function (r) { return r.json(); }).then(function (j) { STRINGS = j.strings; });
function tr(key) {
    var args = Array.prototype.slice.call(arguments, 1);
    return String(STRINGS[key] || key).replace(/\{(\d+)\}/g, function (m, i) { return args[i] === undefined ? m : args[i]; });
}

var KEPT = '__JPK_SECRET_KEPT__';
var VERSION = '12.4.20.0';
var now = Date.now();
function ago(min) { return new Date(now - min * 60000).toISOString(); }
function ahead(min) { return new Date(now + min * 60000).toISOString(); }
function clone(o) { return JSON.parse(JSON.stringify(o)); }
function id(n) { return ('00000000000000000000000000000000' + n.toString(16)).slice(-32); }

// This server's libraries and users.
var LOCAL_LIBRARIES = [
    { ItemId: 'a1000000000000000000000000000001', Name: 'Movies', CollectionType: 'movies', Locations: ['/media/movies'] },
    { ItemId: 'a1000000000000000000000000000002', Name: 'TV Shows', CollectionType: 'tvshows', Locations: ['/media/tv'] },
    { ItemId: 'a1000000000000000000000000000003', Name: 'Kids', CollectionType: 'movies', Locations: ['/media/kids'] },
    { ItemId: 'a1000000000000000000000000000004', Name: 'Music', CollectionType: 'music', Locations: ['/media/music'] }
];
var LOCAL_USERS = [
    { Id: 'c1000000000000000000000000000001', Name: 'alex', Policy: { IsAdministrator: true } },
    { Id: 'c1000000000000000000000000000002', Name: 'jordan', Policy: { IsAdministrator: false } },
    { Id: 'c1000000000000000000000000000003', Name: 'sam', Policy: { IsAdministrator: false } },
    { Id: 'c1000000000000000000000000000004', Name: 'kids', Policy: { IsAdministrator: false } }
];

// Each server's own libraries and users, as GetSourceLibraries and GetSourceUsers answer.
var SOURCE_LIBRARIES = {
    basement: [
        { Id: 'b2000000000000000000000000000001', Name: 'Movies', Locations: ['/data/movies'] },
        { Id: 'b2000000000000000000000000000002', Name: 'Shows', Locations: ['/data/shows'] },
        { Id: 'b2000000000000000000000000000003', Name: 'Family Movies', Locations: ['/data/family'] }
    ],
    cabin: [{ Id: 'b3000000000000000000000000000001', Name: 'Movies', Locations: ['/srv/movies'] }],
    office: [{ Id: 'b4000000000000000000000000000001', Name: 'Movies', Locations: ['/mnt/media/movies'] }]
};
var SOURCE_USERS = {
    basement: [
        { Id: 'd2000000000000000000000000000001', Name: 'alex' }, { Id: 'd2000000000000000000000000000002', Name: 'jordan' },
        { Id: 'd2000000000000000000000000000003', Name: 'sam' }, { Id: 'd2000000000000000000000000000004', Name: 'guest' }
    ],
    cabin: [{ Id: 'd3000000000000000000000000000001', Name: 'alex' }],
    office: [{ Id: 'd4000000000000000000000000000001', Name: 'alex' }]
};

function libraryMapping(server, source, local, extra) {
    var s = SOURCE_LIBRARIES[server][source], l = LOCAL_LIBRARIES[local];
    return Object.assign({ SourceLibraryId: s.Id, SourceLibraryName: s.Name, SourceRootPath: s.Locations[0], LocalLibraryId: l.ItemId, LocalLibraryName: l.Name,
        LocalRootPath: l.Locations[0], IsEnabled: true, FilterMode: 'AllowAll', FilteredItems: [], StorageMode: 'Copy', ExcludeFromLinkCache: false, NegotiateHistory: false, PermanentItems: [] }, extra || {});
}
function userMapping(server, source, local, extra) {
    var s = SOURCE_USERS[server][source], l = LOCAL_USERS[local];
    return Object.assign({ SourceUserId: s.Id, SourceUserName: s.Name, LocalUserId: l.Id, LocalUserName: l.Name, IsEnabled: true, LinkMaxStreams: null, LinkStreamSpeedMbps: null }, extra || {});
}
function server(o) {
    return Object.assign({ ExternalUrl: '', AllowPrivateNetwork: true, ApiKey: KEPT, AuthenticatedUser: '', AuthenticatedUserId: '', AccessLevel: 'Administrator', CanDownload: true,
        IsEnabled: true, AllowLinking: false, LinkMaxStreams: 0, LinkTakeMaxStreams: 0, LinkMaxStreamsPerUser: 0, LinkCountOwnSessions: false, LinkHonorBitrateLimit: false,
        LinkStreamSpeedMbps: 0, LinkScheduledStreamSpeedMbps: 0, LinkFallbackUserId: '', LibraryMappings: [], UserMappings: [] }, o);
}

var SERVERS = [
    server({ Key: 'e1000000000000000000000000000001', Name: 'Basement', Url: 'http://basement:8096', ServerName: 'Basement', ServerId: '3c9e5a1f7b2d48e09a6c4f1b8d2e7a50', Mode: 'Sync',
        AllowLinking: true, LinkMaxStreams: 4, LinkFallbackUserId: LOCAL_USERS[1].Id,
        LibraryMappings: [
            libraryMapping('basement', 0, 0, { NegotiateHistory: true }),
            libraryMapping('basement', 1, 1, { StorageMode: 'Link', NegotiateHistory: true,
                PermanentItems: [{ ItemId: '71000000000000000000000000000001', Name: 'The Lighthouse Keepers', Year: 2021, Path: '/data/shows/The Lighthouse Keepers', Type: 'Series' }] }),
            libraryMapping('basement', 2, 2, { FilterMode: 'Blacklist', FilteredItems: [{ ItemId: '72000000000000000000000000000001', Name: 'Midnight Creatures', Year: 2019, Path: '/data/family/Midnight Creatures (2019)', Type: 'Movie' }] })
        ],
        UserMappings: [userMapping('basement', 0, 0), userMapping('basement', 1, 1, { LinkMaxStreams: 2 }), userMapping('basement', 2, 2, { LinkMaxStreams: 1, LinkStreamSpeedMbps: 20 })] }),
    server({ Key: 'e1000000000000000000000000000002', Name: 'Cabin', Url: 'https://cabin.example.com', ServerName: 'Cabin', ServerId: '8b2f6d4a1e9c47b3a5d0c7e2f4b91d6a', Mode: 'Pull',
        AllowPrivateNetwork: false, LibraryMappings: [libraryMapping('cabin', 0, 0)], UserMappings: [userMapping('cabin', 0, 0)] }),
    server({ Key: 'e1000000000000000000000000000003', Name: 'Office', Url: 'https://office.example.com', ServerName: 'Office', ServerId: 'd47a1c9e3f6b42a8b0e5d2c7a9f4e113', Mode: 'Push',
        AllowPrivateNetwork: false, LibraryMappings: [libraryMapping('office', 0, 0)], UserMappings: [userMapping('office', 0, 0)] })
];
function serverKeyName(key) { return { e1000000000000000000000000000001: 'basement', e1000000000000000000000000000002: 'cabin', e1000000000000000000000000000003: 'office' }[key]; }

var config = {
    Servers: SERVERS,
    EnableContentSync: true, TempDownloadPath: '/cache/serversync', IncludeCompanionFiles: true, TakeLinkImagesFromSource: true, StreamLinksDirectly: true, LinkMaxStreamsTotal: 6,
    LinkCacheSizing: 'Fixed', LinkCacheSizeGb: 500, LinkCachePercent: 50, LinkCachePath: '/cache/links', LinkCacheRetentionDays: 30, LinkCachePreCacheEpisodes: 2, LinkCacheRemovalOrder: 'LastPlayed',
    MaxConcurrentDownloads: 2, MaxDownloadSpeed: 200, DownloadSpeedUnit: 'Mbps', DownloadNewContentMode: 'Enabled', ReplaceExistingContentMode: 'Enabled', DeleteMissingContentMode: 'Disabled',
    MissingContentAction: 'Keep', DetectUpdatedFiles: true, EnableBandwidthScheduling: true, ScheduledStartHour: 1, ScheduledEndHour: 7, ScheduledDownloadSpeed: 0, ScheduledDownloadSpeedUnit: 'Mbps',
    MinimumFreeDiskSpaceGb: 50, LastSyncStartTime: ago(52), LastSyncEndTime: ago(47), EnableRecyclingBin: true, RecyclingBinPath: '/media/.recycle', RecyclingBinRetentionDays: 7,
    RemoveEmptyFoldersOnDelete: true, MaxRetryCount: 3, SkipWatchedByAllUsers: false, WatchedFilterUserIds: [],
    EnableHistorySync: true, HistorySyncPlayedStatus: true, HistorySyncPlaybackPosition: true, HistorySyncPlayCount: true, HistorySyncLastPlayedDate: true, HistorySyncFavorites: true,
    LastHistorySyncTime: ago(46), HistorySyncNegotiate: true,
    EnableUserSync: true, UserSyncPolicy: true, UserSyncConfiguration: true, UserSyncProfileImage: true, UserSyncPrecedence: 'Newest', UserSyncConflict: 'Restrictive', LastUserSyncTime: ago(46),
    EnableMetadataSync: true, MetadataSyncPrecedence: 'Newest', MetadataSyncMetadata: true, MetadataSyncGenres: true, MetadataSyncTags: true, MetadataSyncStudios: true, MetadataSyncPeople: true,
    MetadataSyncImages: true, MetadataSyncFolderItems: true, MetadataSyncProvider: true, MetadataSyncSegmentProvider: true, MetadataSyncReplaceSegments: false, MetadataSyncRemoveSegments: true,
    MetadataSyncConsolidateIntros: true, MetadataSyncConsolidateOutros: false, MetadataSyncConsolidateOtherSegments: false, LastMetadataSyncTime: ago(46),
    EnablePeopleSync: true, PeopleSyncPrecedence: 'Rank', PeopleSyncImages: true, PeopleSyncProvider: true, LastPeopleSyncTime: ago(46),
    RefreshParallelism: 8, FullScanIntervalHours: 24, HintDebounceSeconds: 60, HintRetrySeconds: 30, HintRetryMaxMinutes: 60, HintMaxRetries: 0, HintRefusedRetryMinutes: 15, HintRefusedMaxRetries: 0,
    PeerCallerGrants: [], DeepImageVerification: false, MirrorSyncedCollections: true,
    EnablePlaylistSync: true, PlaylistSyncShares: true, PlaylistSyncPrecedence: 'Newest', PlaylistRemovedAction: 'Delete', LastRunFailures: []
};

// A poster drawn from a seed: a gradient and a few soft shapes, so rows have art without showing any real title.
var PALETTES = [['#2f4858', '#86bbd8'], ['#5b2a86', '#f49d6e'], ['#1b4332', '#95d5b2'], ['#7f1d1d', '#fcd34d'], ['#1e3a8a', '#f0abfc'], ['#3f3f46', '#fde68a'], ['#0f766e', '#fda4af'], ['#78350f', '#93c5fd']];
function posterSvg(seed, wide) {
    var n = 0;
    for (var i = 0; i < seed.length; i++) n = (n * 31 + seed.charCodeAt(i)) >>> 0;
    var p = PALETTES[n % PALETTES.length];
    var w = wide ? 320 : 200, h = wide ? 180 : 300;
    var cx = w * 0.3 + (n % 80), cy = h * 0.3 + ((n >> 5) % 60);
    return '<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h + '" viewBox="0 0 ' + w + ' ' + h + '"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">' +
        '<stop offset="0" stop-color="' + p[0] + '"/><stop offset="1" stop-color="' + p[1] + '"/></linearGradient></defs><rect width="100%" height="100%" fill="url(#g)"/>' +
        '<circle cx="' + cx + '" cy="' + cy + '" r="' + (30 + n % 40) + '" fill="#fff" fill-opacity=".18"/>' +
        '<path d="M0 ' + (h * 0.72) + ' Q ' + (w * 0.4) + ' ' + (h * 0.55) + ' ' + w + ' ' + (h * 0.78) + ' V ' + h + ' H 0 Z" fill="#000" fill-opacity=".25"/></svg>';
}
function posterUrl(seed, wide) { return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(posterSvg(seed, wide)); }

// What each of Basement's libraries holds, for the Always keep and Library filter pickers on the Storage tab.
var SOURCE_ITEMS = {
    b2000000000000000000000000000001: [
        { Id: '72000000000000000000000000000011', Name: 'Harbor Lights', Year: 2022, Type: 'Movie' },
        { Id: '72000000000000000000000000000012', Name: 'Paper Planets', Year: 2020, Type: 'Movie' }
    ],
    b2000000000000000000000000000002: [
        { Id: '72000000000000000000000000000021', Name: 'Garden Detectives', Year: 2019, Type: 'Series' },
        { Id: '71000000000000000000000000000001', Name: 'The Lighthouse Keepers', Year: 2021, Type: 'Series' },
        { Id: '72000000000000000000000000000022', Name: 'Small Town Bakers', Year: 2023, Type: 'Series' }
    ],
    b2000000000000000000000000000003: [
        { Id: '72000000000000000000000000000031', Name: 'The Brave Little Kite', Year: 2018, Type: 'Movie' },
        { Id: '72000000000000000000000000000001', Name: 'Midnight Creatures', Year: 2019, Type: 'Movie' },
        { Id: '72000000000000000000000000000032', Name: 'Night Garden', Year: 2017, Type: 'Movie' }
    ]
};

// A user's picture: their initial on a colored circle.
function avatarUrl(userId) {
    var user = LOCAL_USERS.find(function (u) { return u.Id === userId; });
    if (!user) return '';
    var color = ['#00a4dc', '#aa5cc3', '#f2a541', '#4caf50'][LOCAL_USERS.indexOf(user)];
    return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><circle cx="32" cy="32" r="32" fill="' + color + '"/>' +
        '<text x="32" y="43" font-family="sans-serif" font-size="32" fill="#fff" text-anchor="middle">' + user.Name.charAt(0).toUpperCase() + '</text></svg>');
}

// Content rows on the Sync tab.
function contentRow(n, path, libs, status, extra) {
    return Object.assign({ Id: n, SourceItemId: id(0x91000 + n), ServerKey: SERVERS[0].Key, SourcePath: path, SourceLibraryName: libs[0], LocalLibraryName: libs[1], Status: status,
        StorageMode: 'Copy', SourceSize: 4.2e9 + n * 3.1e8, SourceSizeFormatted: '', LastSyncTime: ago(60 * n), LocalPath: path.replace('/data/', '/media/') }, extra || {});
}
var CONTENT = [
    contentRow(1, '/data/movies/Harbor Lights (2022)/Harbor Lights (2022).mkv', ['Movies', 'Movies'], 'Queued', { SourceSizeFormatted: '6.8 GB' }),
    contentRow(2, '/data/shows/The Lighthouse Keepers/Season 01/The Lighthouse Keepers S01E04.mkv', ['Shows', 'TV Shows'], 'Synced', { StorageMode: 'Link', Cached: true }),
    contentRow(3, '/data/movies/Paper Planets (2020)/Paper Planets (2020).mkv', ['Movies', 'Movies'], 'Synced'),
    contentRow(4, '/data/family/The Brave Little Kite (2018)/The Brave Little Kite (2018).mp4', ['Family Movies', 'Kids'], 'Errored', { ErrorMessage: 'Not enough free space on /media/kids.' })
];

// Metadata rows on the Sync tab, and the detail of each.
function meta(o) {
    return Object.assign({ Name: '', OriginalTitle: null, Overview: '', OfficialRating: 'PG-13', CommunityRating: 7.4, PremiereDate: '2022-03-18T00:00:00Z', ProductionYear: 2022,
        Genres: ['Drama'], Tags: [], ProviderIds: { Tmdb: '100001', Imdb: 'tt0000001' }, LockData: false }, o);
}
var METADATA = [
    { Id: 501, SourceItemId: id(0x92001), LocalItemId: id(0x93001), ServerKey: SERVERS[0].Key, ItemName: 'Harbor Lights', SourceLibraryName: 'Movies', LocalLibraryName: 'Movies', Status: 'Queued',
      HasMetadataChanges: true, HasImagesChanges: true, HasSegmentsChanges: true, LastSyncTime: ago(46),
      SourcePath: '/data/movies/Harbor Lights (2022)/Harbor Lights (2022).mkv', LocalPath: '/media/movies/Harbor Lights (2022)/Harbor Lights (2022).mkv',
      MetadataChangesDetail: 'Overview, Tagline, Tags',
      ImagesChangesDetail: 'Backdrop',
      SourceMetadataValue: JSON.stringify(meta({ Name: 'Harbor Lights', Overview: 'A ferry captain and her daughter keep a small island town connected through one long winter.', Tagline: 'Every light leads home.', Genres: ['Drama', 'Family'], Tags: ['Winter', 'Islands'] })),
      LocalMetadataValue: JSON.stringify(meta({ Name: 'Harbor Lights', Overview: 'A ferry captain keeps a small island town connected.', Genres: ['Drama', 'Family'], Tags: ['Winter'] })),
      SourceImagesValue: JSON.stringify({ Primary: [{ Size: 412000 }], Backdrop: [{ Size: 1830000 }, { Size: 1720000 }], Logo: [{ Size: 64000 }] }),
      LocalImagesValue: JSON.stringify({ Primary: [{ Size: 412000 }], Backdrop: [{ Size: 1830000 }], Logo: [{ Size: 64000 }] }),
      SourcePeopleValue: JSON.stringify([{ Name: 'Mara Quinn' }, { Name: 'Theo Hart' }, { Name: 'June Okafor' }]),
      LocalPeopleValue: JSON.stringify([{ Name: 'Mara Quinn' }, { Name: 'Theo Hart' }, { Name: 'June Okafor' }]),
      SourceStudiosValue: JSON.stringify(['Northwind Pictures']), LocalStudiosValue: JSON.stringify(['Northwind Pictures']),
      SourceSegmentsValue: JSON.stringify({ Segments: [{ Type: 'Intro', StartTicks: 0, EndTicks: 860000000 }, { Type: 'Outro', StartTicks: 64350000000, EndTicks: 66120000000 }] }),
      LocalSegmentsValue: JSON.stringify({ Segments: [{ Type: 'Intro', StartTicks: 0, EndTicks: 860000000 }] }),
      Version: { ServerName: 'Basement', ServerId: SERVERS[0].ServerId, Timestamp: ago(14), IsThisServer: false } },
    { Id: 502, SourceItemId: id(0x92002), ServerKey: SERVERS[0].Key, ItemName: 'The Lighthouse Keepers', SourceLibraryName: 'Shows', LocalLibraryName: 'TV Shows', Status: 'Queued', HasPeopleChanges: true },
    { Id: 503, SourceItemId: id(0x92003), ServerKey: SERVERS[1].Key, ItemName: 'Paper Planets', SourceLibraryName: 'Movies', LocalLibraryName: 'Movies', Status: 'Queued', HasImagesChanges: true },
    { Id: 504, SourceItemId: id(0x92004), ServerKey: SERVERS[0].Key, ItemName: 'The Brave Little Kite', SourceLibraryName: 'Family Movies', LocalLibraryName: 'Kids', Status: 'Synced' },
    { Id: 505, SourceItemId: id(0x92005), ServerKey: SERVERS[0].Key, ItemName: 'Quiet Orbit', SourceLibraryName: 'Movies', LocalLibraryName: 'Movies', Status: 'Synced' },
    { Id: 506, SourceItemId: id(0x92006), ServerKey: SERVERS[1].Key, ItemName: 'Salt and Cedar', SourceLibraryName: 'Movies', LocalLibraryName: 'Movies', Status: 'Ignored' },
    { Id: 507, SourceItemId: id(0x92007), ServerKey: SERVERS[0].Key, ItemName: 'Station Eleven Road', SourceLibraryName: 'Movies', LocalLibraryName: 'Movies', Status: 'Errored', ErrorMessage: 'Basement did not answer in time.' },
    { Id: 508, SourceItemId: id(0x92008), ServerKey: SERVERS[0].Key, ItemName: 'Night Garden', SourceLibraryName: 'Family Movies', LocalLibraryName: 'Kids', Status: 'Synced' }
];

// The change queue: changes gathering here, sent to Basement and Office, and received from Basement.
var HINTS = {
    Pending: 0, Unmatched: 0,
    Gathering: [
        { Kind: 'History', Change: 'Played', Title: 'Harbor Lights', Subtitle: '2022', ItemType: 'Movie', ItemId: id(0x93001), UserId: LOCAL_USERS[1].Id, UserName: 'jordan', EditedAt: ago(0.4), DueAt: ahead(0.6) },
        { Kind: 'Metadata', Change: 'Edited', Title: 'The Lighthouse Keepers', Subtitle: 'S01E05 · The Fog Bell', ItemType: 'Episode', ItemId: id(0x93002), EditedAt: ago(0.2), DueAt: ahead(0.8) }
    ],
    Outbound: [
        { Id: 31, Title: 'Paper Planets', Subtitle: '2020', ItemType: 'Movie', ItemId: id(0x93003), PeerKey: SERVERS[0].Key, PeerName: 'Basement', Kind: 'History', UserId: LOCAL_USERS[2].Id, UserName: 'sam', State: 'Pending', Attempts: 0, NextAttempt: ahead(0.3), CreatedAt: ago(1.5) },
        { Id: 30, Title: 'Paper Planets', Subtitle: '2020', ItemType: 'Movie', ItemId: id(0x93003), PeerKey: SERVERS[2].Key, PeerName: 'Office', Kind: 'History', UserId: LOCAL_USERS[2].Id, UserName: 'sam', State: 'Sent', Attempts: 1, SentAt: ago(3), CreatedAt: ago(4) },
        { Id: 29, Title: 'Night Garden', Subtitle: '2017', ItemType: 'Movie', ItemId: id(0x93004), PeerKey: SERVERS[0].Key, PeerName: 'Basement', Kind: 'Metadata', State: 'Sent', Attempts: 1, SentAt: ago(9), CreatedAt: ago(10) },
        { Id: 28, Title: 'Road Trip Songs', ItemType: 'Playlist', ItemId: id(0x93005), PeerKey: SERVERS[0].Key, PeerName: 'Basement', Kind: 'Playlists', State: 'Sent', Attempts: 1, SentAt: ago(22), CreatedAt: ago(23) }
    ],
    OutboundCounts: { Pending: 1, Sent: 3, Failed: 0 },
    Inbound: [
        { Id: 12, HintId: 'h12', OriginServerId: SERVERS[0].ServerId, Kind: 'History', Key: 'k12', ItemPath: '/data/shows/The Lighthouse Keepers/Season 01/The Lighthouse Keepers S01E04.mkv', ItemId: id(0x92010), ItemType: 'Episode', UserName: 'alex', ReceivedAt: ago(0.1), Attempts: 0 }
    ],
    InboundCount: 1, InboundFailingCount: 0,
    Peers: [
        { Key: SERVERS[0].Key, Name: 'Basement', LastAttempt: ago(3), Sends: null },
        { Key: SERVERS[2].Key, Name: 'Office', LastAttempt: ago(3), Sends: ['History', 'Metadata', 'Content'] }
    ]
};

// The last ten scans of each sync type, and a month of link streams.
function runs(rows) {
    return rows.map(function (r, i) { return { FinishedAt: new Date(now - (rows.length - i) * 86400000 + 3 * 3600000).toISOString(), Added: r[0], Updated: r[1], Removed: r[2] }; });
}
function streamDays() {
    var days = [];
    for (var i = 29; i >= 0; i--) { var d = new Date(now - i * 86400000); days.push(d.getFullYear() + '-' + ('0' + (d.getMonth() + 1)).slice(-2) + '-' + ('0' + d.getDate()).slice(-2)); }
    return days;
}
var STATISTICS = {
    Types: [
        { Key: 'Content', Runs: runs([[14, 3, 0], [2, 1, 0], [0, 4, 1], [6, 0, 0], [1, 2, 0], [0, 0, 2], [9, 5, 0], [3, 1, 0], [0, 2, 0], [5, 3, 1]]) },
        { Key: 'History', Runs: runs([[22, 18, 0], [4, 31, 0], [2, 12, 1], [6, 25, 0], [3, 9, 0], [1, 16, 0], [8, 27, 2], [5, 14, 0], [2, 20, 0], [7, 11, 0]]) },
        { Key: 'Metadata', Runs: runs([[14, 6, 0], [2, 9, 0], [0, 3, 1], [6, 12, 0], [1, 4, 0], [0, 7, 2], [9, 5, 0], [3, 8, 0], [0, 2, 0], [5, 10, 1]]) }
    ],
    LinkStreams: {
        Days: streamDays(),
        Servers: [
            { Key: SERVERS[0].Key, Name: 'Basement', Streams: [2, 3, 1, 4, 2, 5, 3, 2, 1, 3, 4, 6, 2, 3, 1, 2, 4, 3, 5, 2, 1, 3, 2, 4, 3, 2, 5, 4, 3, 2] }
        ]
    }
};

// The server answers for each route a page calls, by path under Sync/ or Jellyfin's own.
function testConnection(body) {
    var s = SERVERS.find(function (x) { return x.Key === body.ServerKey; }) || SERVERS[0];
    return { Success: true, ServerName: s.ServerName, ServerId: s.ServerId, IsAdministrator: true, UserId: id(0x99), UserName: '', CanDownload: true, SyncAccess: 'Allowed', SyncAccessMessage: '' };
}
function joinWords(words) {
    return words.length === 2 ? tr('ListTwo', words[0], words[1]) : tr('ListLast', words.slice(0, -1).join(', '), words[words.length - 1]);
}
function checkPeer(body) {
    var s = SERVERS.find(function (x) { return x.Key === body.ServerKey; }) || SERVERS[0];
    var mode = body.Mode || s.Mode;
    var base = { Reachable: true, HasPlugin: true, ServerName: s.ServerName, ServerId: s.ServerId, Paired: true };
    if (mode === 'Pull') return Object.assign(base, { Severity: 'warn', Headline: tr('HeadlinePullScheduled'), Message: tr('VerdictPullScheduled', VERSION, tr('PeerNotListed')), Details: [] });
    var kinds = [tr('KindWhatHistory'), tr('KindWhatMetadata'), tr('KindPeopleLower'), tr('KindFilesLower'), tr('KindWhatUsers'), tr('KindPlaylistsLower')];
    var everything = joinWords(kinds);
    var details = [
        { Title: tr('DetailEntryTitle'), Text: tr('DetailListedRight', VERSION), Badge: tr(mode === 'Sync' ? 'TabSync' : 'ModePull'), State: 'ok' },
        { Title: tr('DetailChangesHere'), Text: tr('DetailPullsAnnounced'), Badge: tr('Live'), State: 'ok' }
    ];
    if (mode === 'Sync') details.push({ Title: tr('DetailChangesThere'), Text: tr('DetailPulledAndAnnounced'), Badge: tr('Live'), State: 'ok' });
    details.push({ Title: tr('DetailApplies'), Text: everything.charAt(0).toUpperCase() + everything.slice(1) + '.', Badge: tr('BadgeEverything'), State: 'ok' });
    details.push({ Title: tr('DetailReads'), Text: tr('DetailEveryLibrary'), Badge: tr('BadgeAdministrator'), State: 'ok' });
    details.push({ Title: tr('DetailPairing'), Text: tr('DetailPaired'), Badge: tr('BadgePaired'), State: 'ok' });
    return Object.assign(base, mode === 'Sync'
        ? { Severity: 'ok', Headline: tr('HeadlineSyncReady'), Message: tr('VerdictSyncReady', VERSION) }
        : { Severity: 'ok', Headline: tr('HeadlinePushReady'), Message: tr('VerdictPushReady', VERSION, tr('PeerListsAs', tr('ModePull'))) }, { Details: details });
}
function page(list, q) {
    var skip = +(q.get('skip') || q.get('Skip') || 0), take = +(q.get('take') || q.get('Take') || 50);
    return { Items: list.slice(skip, skip + take), TotalCount: list.length };
}
function filtered(list, q) {
    var status = q.get('status'), search = (q.get('search') || q.get('Search') || '').toLowerCase();
    return list.filter(function (r) {
        if (status && r.Status !== status) return false;
        if (search && JSON.stringify(r).toLowerCase().indexOf(search) < 0) return false;
        return true;
    });
}
function counts(list) {
    var c = { Synced: 0, Queued: 0, Errored: 0, Ignored: 0 };
    list.forEach(function (r) { c[r.Status] = (c[r.Status] || 0) + 1; });
    return c;
}
function route(path, q, method, body) {
    if (path === 'Health') return { Ready: true };
    if (path === 'TestConnection') return testConnection(body);
    if (path === 'Hints/CheckPeer') return checkPeer(body);
    if (path === 'GetSourceLibraries') return SOURCE_LIBRARIES[serverKeyName(body.ServerKey)] || [];
    if (path === 'GetSourceUsers') return SOURCE_USERS[serverKeyName(body.ServerKey)] || [];
    if (path === 'Library/VirtualFolders') return LOCAL_LIBRARIES;
    if (path === 'Users') return LOCAL_USERS;
    if (path === 'Statistics') return STATISTICS;
    if (path === 'Hints') return HINTS;
    if (path === 'Hints/Version') { var m = METADATA.find(function (r) { return r.LocalItemId === q.get('localItemId'); }); return m && m.Version || {}; }
    if (path === 'Status') return Object.assign(counts(CONTENT), { PendingDownload: 0, PendingReplacement: 0, PendingDeletion: 0, Deleting: 0 });
    if (path === 'PendingSize') return { TotalPendingBytes: 7.3e9 };
    if (path === 'Items') {
        var storage = q.get('storage');
        if (storage) return { Items: [], TotalCount: storage === 'Copy' ? 1284 : q.get('cached') ? 96 : 3410 };
        return page(filtered(CONTENT, q), q);
    }
    if (path === 'MetadataStatus') return counts(METADATA);
    if (path === 'MetadataItems') return page(filtered(METADATA, q), q);
    if (/^MetadataItems\/\d+$/.test(path)) return METADATA.find(function (r) { return String(r.Id) === path.split('/')[1]; }) || null;
    if (path === 'LinkCache') return { Enabled: true, LimitBytes: 500e9, UsedBytes: 318e9 };
    if (path === 'KeptCopies') return page([
        { Id: 1, SourceItemId: id(0x94001), ServerKey: SERVERS[0].Key, LocalPath: '/media/tv/The Lighthouse Keepers/Season 01/The Lighthouse Keepers S01E01.mkv', Kept: 'Pick', SourceSizeFormatted: '2.1 GB', Status: 'Synced' },
        { Id: 2, SourceItemId: id(0x94002), ServerKey: SERVERS[0].Key, LocalPath: '/media/tv/Garden Detectives/Season 02/Garden Detectives S02E07.mkv', Kept: 'Hand', SourceSizeFormatted: '1.4 GB', Status: 'Synced' },
        { Id: 3, SourceItemId: id(0x94003), ServerKey: SERVERS[0].Key, LocalPath: '/media/tv/Small Town Bakers/Season 01/Small Town Bakers S01E02.mkv', Kept: 'Own', SourceSizeFormatted: '980 MB', Status: 'Synced' }
    ], q);
    if (path === 'SourceLibraryItems') return page((SOURCE_ITEMS[q.get('libraryId')] || []).filter(function (i) { return !q.get('search') || i.Name.toLowerCase().indexOf(q.get('search').toLowerCase()) >= 0; }), q);
    if (path === 'UserStatus' || path === 'PeopleStatus' || path === 'HistoryStatus') return { Synced: 0, Queued: 0, Errored: 0, Ignored: 0 };
    window.__unrouted = (window.__unrouted || []).concat(method + ' ' + path);
    return {};
}

window.__saved = [];
window.ApiClient = {
    serverAddress: function () { return 'https://jellyfin.example.com'; },
    getUrl: function (p, q) { return 'https://jellyfin.example.com/' + String(p).replace(/^\//, '') + (q ? '?' + new URLSearchParams(q) : ''); },
    getCurrentUserId: function () { return LOCAL_USERS[0].Id; },
    getPluginConfiguration: function () { return Promise.resolve(clone(config)); },
    updatePluginConfiguration: function (pluginId, c) { window.__saved.push(clone(c)); config = clone(c); return Promise.resolve({}); },
    getUsers: function () { return Promise.resolve(clone(LOCAL_USERS)); },
    getVirtualFolders: function () { return Promise.resolve(clone(LOCAL_LIBRARIES)); },
    getSystemInfo: function () { return Promise.resolve({ ServerName: 'Living Room', Version: '12.4.0' }); },
    getPublicSystemInfo: function () { return Promise.resolve({ ServerName: 'Living Room', Version: '12.4.0' }); },
    getScheduledTasks: function () { return Promise.resolve([]); },
    getImageUrl: function (itemId, o) { return posterUrl(String(itemId), false); },
    getUserImageUrl: function (userId) { return avatarUrl(userId); },
    getJSON: function (url) { return ApiClient.fetch({ url: url, type: 'GET', dataType: 'json' }); },
    ajax: function (o) { return ApiClient.fetch(o); },
    fetch: function (o) {
        var url = new URL(o.url || String(o));
        var path = url.pathname.replace(/^\//, '').replace(/^Sync\//, '');
        var body = o.data ? JSON.parse(o.data) : {};
        return stringsReady.then(function () {
            if (path === 'ImageProxy') {
                var key = url.searchParams.get('itemId');
                return url.searchParams.get('user') === 'true' ? { ok: false } : new Response(new Blob([posterSvg(key, false)], { type: 'image/svg+xml' }));
            }
            return clone(route(path, url.searchParams, o.type || 'GET', body));
        });
    },
    sendMessage: function () {}, sendWebSocketMessage: function () {}
};
window.Dashboard = { alert: function (m) { console.error('Dashboard.alert used: ' + m); }, processPluginConfigurationUpdateResult: function () {}, showLoadingMsg: function () {}, hideLoadingMsg: function () {} };

// Draws the dashboard header and its tabs, as Jellyfin's LibraryMenu.setTabs does for a plugin page.
window.LibraryMenu = {
    setTabs: function (key, index, getTabs) {
        var tabs = typeof getTabs === 'function' ? getTabs() : getTabs;
        var host = document.getElementById('jfTabs');
        if (!host) return;
        host.innerHTML = (tabs || []).map(function (t, i) {
            return '<a class="jfTab' + (i === index ? ' jfTabActive' : '') + '">' + t.name + '</a>';
        }).join('');
    }
};

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using JPKribs.Jellyfin.Base;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ServerSync.Configuration;

/// <summary>
/// Configuration settings for the Server Sync plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the configured peer servers in rank order. Index zero wins whenever two servers
    /// offer the same item, and each later entry only adds what every earlier entry lacks.
    /// </summary>
    public List<SourceServer> Servers { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether Content sync is on.</summary>
    public bool EnableContentSync { get; set; }

    /// <summary>Gets or sets the folder files download into before they move to their library. Empty uses a folder under Jellyfin's cache path.</summary>
    public string? TempDownloadPath { get; set; }

    /// <summary>Gets or sets a value indicating whether subtitles and other companion files download with a file.</summary>
    public bool IncludeCompanionFiles { get; set; } = true;

    /// <summary>
    /// Whether a link takes the chapter images and trickplay tiles its source made. Jellyfin makes
    /// neither for a .strm, so without this a link has none.
    /// </summary>
    public bool TakeLinkImagesFromSource { get; set; } = true;

    /// <summary>
    /// Whether a player's direct play of a link is streamed from the source by this plugin, for the
    /// user who asked. Off, Jellyfin reads the link's own address instead, which works the same but
    /// cannot tell two viewers of one link apart.
    /// </summary>
    public bool StreamLinksDirectly { get; set; } = true;

    /// <summary>
    /// The most links this server streams from other servers at once, across all of them, or 0 for no
    /// limit. A link that plays from the link cache streams from nobody and is not counted.
    /// </summary>
    public int LinkMaxStreamsTotal { get; set; }

    /// <summary>
    /// How much disk the link cache may use. Unset, the cache is off at a size of 0 and holds up to
    /// <see cref="LinkCacheSizeGb"/> otherwise.
    /// </summary>
    public LinkCacheSizing? LinkCacheSizing { get; set; }

    /// <summary>
    /// The most disk the link cache may use, in gigabytes, when it is held to a fixed amount. At 0 a
    /// cache that is on uses all the room there is.
    /// </summary>
    public int LinkCacheSizeGb { get; set; }

    /// <summary>The share of the room on the cache's disk the cache may use, when it is held to a share.</summary>
    public int LinkCachePercent { get; set; } = 50;

    /// <summary>Gets how the cache is sized, with an unset choice read from the size and a fixed size of 0 read as all the room.</summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public LinkCacheSizing EffectiveLinkCacheSizing => (LinkCacheSizing ?? (LinkCacheSizeGb > 0 ? Models.ContentSync.Configuration.LinkCacheSizing.Fixed : Models.ContentSync.Configuration.LinkCacheSizing.Off)) switch
    {
        Models.ContentSync.Configuration.LinkCacheSizing.Fixed when LinkCacheSizeGb <= 0 => Models.ContentSync.Configuration.LinkCacheSizing.All,
        var sizing => sizing
    };

    /// <summary>Gets a value indicating whether the link cache is on.</summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LinkCacheOn => EffectiveLinkCacheSizing != Models.ContentSync.Configuration.LinkCacheSizing.Off;

    /// <summary>
    /// Where cached files are kept. Empty keeps them under Jellyfin's cache folder.
    /// </summary>
    public string LinkCachePath { get; set; } = string.Empty;

    /// <summary>
    /// How many days a cached file is kept after it was last played, or after it was cached when it
    /// was never played. 0 keeps files until the space is needed.
    /// </summary>
    public int LinkCacheRetentionDays { get; set; } = 30;

    /// <summary>
    /// How many episodes after the one a user is watching are cached ahead of them. 0 caches none ahead.
    /// </summary>
    public int LinkCachePreCacheEpisodes { get; set; } = 2;

    /// <summary>Which cached file is dropped first when the cache is full.</summary>
    public CacheRemovalOrder LinkCacheRemovalOrder { get; set; } = CacheRemovalOrder.LastPlayed;

    /// <summary>
    /// The most disk the link cache may use, in bytes, or 0 when the cache is off. The room on the
    /// cache's disk is what the cache holds now plus what is free, less the free space always kept.
    /// </summary>
    /// <param name="usedBytes">What the cache holds now.</param>
    /// <param name="freeBytes">The free space on the cache's disk.</param>
    /// <returns>The size in bytes.</returns>
    public long GetLinkCacheLimitBytes(long usedBytes, long freeBytes)
    {
        const long Gb = 1024L * 1024L * 1024L;
        var room = Math.Max(0, usedBytes) + Math.Max(0, freeBytes - (Math.Max(0, MinimumFreeDiskSpaceGb) * Gb));
        return EffectiveLinkCacheSizing switch
        {
            Models.ContentSync.Configuration.LinkCacheSizing.Off => 0,
            Models.ContentSync.Configuration.LinkCacheSizing.Fixed => LinkCacheSizeGb * Gb,
            Models.ContentSync.Configuration.LinkCacheSizing.Percent => (long)(room * (Math.Clamp(LinkCachePercent, 1, 100) / 100.0)),
            _ => room
        };
    }

    /// <summary>Gets or sets how many files download at once, clamped 1 to 10.</summary>
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>
    /// Gets or sets the most every download to this server together may use, in Mbps, like every other
    /// speed in the plugin. 0 is no limit.
    /// </summary>
    public int MaxDownloadSpeed { get; set; } = 0;

    /// <summary>
    /// Gets or sets the unit <see cref="MaxDownloadSpeed"/> is in. Always <see cref="Mbps"/> after
    /// <see cref="SanitizeValues"/>, which converts a speed kept in KB, MB, or GB per second.
    /// </summary>
    public string DownloadSpeedUnit { get; set; } = Mbps;

    /// <summary>The unit every speed is kept in.</summary>
    public const string Mbps = "Mbps";

    /// <summary>
    /// Calculates the max download speed in bytes per second.
    /// </summary>
    /// <returns>Speed in bytes per second.</returns>
    public long GetMaxDownloadSpeedBytes() => RateUtilities.MbpsToBytesPerSecond(MaxDownloadSpeed);

    /// <summary>
    /// Calculates the scheduled download speed in bytes per second.
    /// </summary>
    /// <returns>Speed in bytes per second.</returns>
    public long GetScheduledDownloadSpeedBytes() => RateUtilities.MbpsToBytesPerSecond(ScheduledDownloadSpeed);

    /// <summary>
    /// Returns the appropriate download speed based on current time and scheduling settings.
    /// </summary>
    /// <returns>Effective speed in bytes per second.</returns>
    public long GetEffectiveDownloadSpeedBytes() => GetEffectiveDownloadSpeedBytes(DateTime.Now.Hour);

    /// <summary>
    /// Returns the download speed for a given local hour, so the schedule can be checked at any hour
    /// rather than only the one it happens to be.
    /// </summary>
    /// <param name="currentHour">The local hour, 0 to 23.</param>
    /// <returns>Effective speed in bytes per second.</returns>
    public long GetEffectiveDownloadSpeedBytes(int currentHour)
        => IsInBandwidthSchedule(currentHour) ? GetScheduledDownloadSpeedBytes() : GetMaxDownloadSpeedBytes();

    /// <summary>Whether the bandwidth schedule's hours apply now. False while scheduling is off.</summary>
    /// <returns>True inside the scheduled hours.</returns>
    public bool IsInBandwidthSchedule() => IsInBandwidthSchedule(DateTime.Now.Hour);

    /// <summary>
    /// Whether the bandwidth schedule's hours apply at a given local hour. Downloads and link streams
    /// share the schedule, each with its own scheduled speed.
    /// </summary>
    /// <param name="currentHour">The local hour, 0 to 23.</param>
    /// <returns>True inside the scheduled hours.</returns>
    public bool IsInBandwidthSchedule(int currentHour)
    {
        if (!EnableBandwidthScheduling)
        {
            return false;
        }

        // Equal start and end means all day, since the same-day branch would never match.
        return ScheduledStartHour == ScheduledEndHour
            || (ScheduledStartHour < ScheduledEndHour
                ? currentHour >= ScheduledStartHour && currentHour < ScheduledEndHour
                : currentHour >= ScheduledStartHour || currentHour < ScheduledEndHour);
    }

    /// <summary>
    /// Controls how new content (items on the source that do not exist on this server) is handled.
    /// </summary>
    public ApprovalMode DownloadNewContentMode { get; set; } = ApprovalMode.Enabled;

    /// <summary>
    /// Controls how updated content (items that differ from this server's version) is handled.
    /// </summary>
    public ApprovalMode ReplaceExistingContentMode { get; set; } = ApprovalMode.Enabled;

    /// <summary>
    /// Controls how missing content (items on this server that do not exist on the source) is handled.
    /// </summary>
    public ApprovalMode DeleteMissingContentMode { get; set; } = ApprovalMode.Disabled;

    /// <summary>Gets or sets what happens to a local file once its item is gone from the source. Null reads the value from <see cref="DeleteMissingContentMode"/>.</summary>
    public MissingContentAction? MissingContentAction { get; set; }

    [XmlIgnore]
    [JsonIgnore]
    public MissingContentAction EffectiveMissingContentAction
        => MissingContentAction switch
        {
            null => DeleteMissingContentMode switch
            {
                ApprovalMode.Disabled => Models.ContentSync.Configuration.MissingContentAction.Keep,
                ApprovalMode.RequireApproval => Models.ContentSync.Configuration.MissingContentAction.DeleteAfterApproval,
                _ => Models.ContentSync.Configuration.MissingContentAction.Delete
            },
            Models.ContentSync.Configuration.MissingContentAction.Delete when DeleteMissingContentMode == ApprovalMode.RequireApproval
                => Models.ContentSync.Configuration.MissingContentAction.DeleteAfterApproval,
            { } action => action
        };

    /// <summary>
    /// Gets how a deletion of a removed file is approved, from <see cref="EffectiveMissingContentAction"/>:
    /// at once for Delete, after approval for Delete After Approval, and never otherwise.
    /// </summary>
    public ApprovalMode DeletionApproval => EffectiveMissingContentAction switch
    {
        Models.ContentSync.Configuration.MissingContentAction.Delete => ApprovalMode.Enabled,
        Models.ContentSync.Configuration.MissingContentAction.DeleteAfterApproval => ApprovalMode.RequireApproval,
        _ => ApprovalMode.Disabled
    };

    /// <summary>Gets a value indicating whether a removed file is deleted, at once or after approval.</summary>
    public bool DeletesMissing => DeletionApproval != ApprovalMode.Disabled;

    /// <summary>
    /// Re-queue files with size or date mismatches when enabled.
    /// </summary>
    public bool DetectUpdatedFiles { get; set; } = true;

    /// <summary>
    /// Enable time-based bandwidth scheduling with alternate speed.
    /// </summary>
    public bool EnableBandwidthScheduling { get; set; }

    /// <summary>
    /// Hour of day (0-23) when scheduled bandwidth starts.
    /// </summary>
    public int ScheduledStartHour { get; set; } = 0;

    /// <summary>
    /// Hour of day (0-24) when scheduled bandwidth ends.
    /// </summary>
    public int ScheduledEndHour { get; set; } = 6;

    /// <summary>
    /// Gets or sets the download speed during scheduled hours, in Mbps. 0 is no limit.
    /// </summary>
    public int ScheduledDownloadSpeed { get; set; } = 0;

    /// <summary>Gets or sets the unit of <see cref="ScheduledDownloadSpeed"/>, as <see cref="DownloadSpeedUnit"/>.</summary>
    public string ScheduledDownloadSpeedUnit { get; set; } = Mbps;

    /// <summary>
    /// Minimum free disk space required before downloads (in GB).
    /// </summary>
    public int MinimumFreeDiskSpaceGb { get; set; } = 10;

    /// <summary>
    /// Timestamp when the last sync started.
    /// </summary>
    public DateTime? LastSyncStartTime { get; set; }

    /// <summary>
    /// Timestamp when the last sync completed.
    /// </summary>
    public DateTime? LastSyncEndTime { get; set; }

    /// <summary>
    /// Move deleted/replaced files to a recycling bin instead of permanent deletion.
    /// </summary>
    public bool EnableRecyclingBin { get; set; }

    /// <summary>
    /// Path to the recycling bin directory for soft-deleted files.
    /// </summary>
    public string? RecyclingBinPath { get; set; }

    /// <summary>
    /// Number of days to keep files in the recycling bin before permanent deletion.
    /// </summary>
    public int RecyclingBinRetentionDays { get; set; } = 7;

    /// <summary>
    /// Remove empty parent folders after deleting content files.
    /// Only removes folders if they are completely empty after deletion.
    /// </summary>
    public bool RemoveEmptyFoldersOnDelete { get; set; }

    /// <summary>
    /// Maximum number of times to retry failed downloads before giving up.
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets a value indicating whether items watched by every user in
    /// <see cref="WatchedFilterUserIds"/> are skipped during content sync. It follows the list: on
    /// while any user is picked, off while none is. A list with the switch off is cleared.
    /// </summary>
    public bool SkipWatchedByAllUsers { get; set; }

    /// <summary>
    /// Source-server user IDs whose watched status decides whether an item is skipped. Empty skips
    /// nothing.
    /// </summary>
    public List<string> WatchedFilterUserIds { get; set; } = new();


    /// <summary>
    /// Enable watch history synchronization between servers.
    /// </summary>
    public bool EnableHistorySync { get; set; }

    /// <summary>
    /// Sync played/unplayed status.
    /// </summary>
    public bool HistorySyncPlayedStatus { get; set; } = true;

    /// <summary>
    /// Sync playback position (resume point).
    /// </summary>
    public bool HistorySyncPlaybackPosition { get; set; } = true;

    /// <summary>
    /// Sync play count.
    /// </summary>
    public bool HistorySyncPlayCount { get; set; } = true;

    /// <summary>
    /// Sync last played date.
    /// </summary>
    public bool HistorySyncLastPlayedDate { get; set; } = true;

    /// <summary>
    /// Sync favorite status.
    /// </summary>
    public bool HistorySyncFavorites { get; set; } = true;

    /// <summary>The parts of watch history the History settings allow to be copied.</summary>
    /// <returns>The enabled parts.</returns>
    public Models.HistorySync.HistoryFields GetHistoryFields()
        => (HistorySyncPlayedStatus ? Models.HistorySync.HistoryFields.Played : 0)
            | (HistorySyncPlaybackPosition ? Models.HistorySync.HistoryFields.Position : 0)
            | (HistorySyncPlayCount ? Models.HistorySync.HistoryFields.PlayCount : 0)
            | (HistorySyncLastPlayedDate ? Models.HistorySync.HistoryFields.LastPlayed : 0)
            | (HistorySyncFavorites ? Models.HistorySync.HistoryFields.Favorite : 0);

    /// <summary>
    /// Timestamp when the last history sync completed.
    /// </summary>
    public DateTime? LastHistorySyncTime { get; set; }

    /// <summary>
    /// Gets or sets a switch that turns history negotiation on for every server. <see cref="SanitizeValues"/>
    /// moves it onto every library mapping (see <see cref="Models.Configuration.LibraryMapping.NegotiateHistory"/>),
    /// which negotiates only for a Sync server, and sets it to false.
    /// </summary>
    public bool HistorySyncNegotiate { get; set; }

    /// <summary>
    /// Enable user settings synchronization between servers.
    /// </summary>
    public bool EnableUserSync { get; set; }

    /// <summary>
    /// Sync user policy (permissions, restrictions).
    /// </summary>
    public bool UserSyncPolicy { get; set; } = true;

    /// <summary>
    /// Sync user configuration (preferences, settings).
    /// </summary>
    public bool UserSyncConfiguration { get; set; } = true;

    /// <summary>
    /// Sync user profile images.
    /// </summary>
    public bool UserSyncProfileImage { get; set; } = true;

    /// <summary>
    /// Which server speaks for a user's settings when several map a user onto the same local user.
    /// </summary>
    public SyncPrecedence UserSyncPrecedence { get; set; } = SyncPrecedence.Newest;

    /// <summary>
    /// What a user's permissions become when this server and a Sync server both changed them since
    /// the two last agreed.
    /// </summary>
    public UserConflictRule UserSyncConflict { get; set; } = UserConflictRule.Newest;

    /// <summary>
    /// Timestamp when the last user sync completed.
    /// </summary>
    public DateTime? LastUserSyncTime { get; set; }

    /// <summary>
    /// Enable metadata synchronization between servers.
    /// </summary>
    public bool EnableMetadataSync { get; set; }

    /// <summary>
    /// Which server speaks for an item's metadata when several hold the item.
    /// </summary>
    public SyncPrecedence MetadataSyncPrecedence { get; set; } = SyncPrecedence.Newest;

    /// <summary>
    /// Sync core metadata fields (title, overview, ratings, dates, provider IDs).
    /// </summary>
    public bool MetadataSyncMetadata { get; set; } = true;

    /// <summary>
    /// Sync genre assignments from source to local items.
    /// </summary>
    public bool MetadataSyncGenres { get; set; } = true;

    /// <summary>
    /// Sync user-defined tags from source to local items.
    /// </summary>
    public bool MetadataSyncTags { get; set; } = true;

    /// <summary>
    /// Sync studio/production company assignments.
    /// </summary>
    public bool MetadataSyncStudios { get; set; } = true;

    /// <summary>
    /// Sync people associated with items (actors, directors, writers).
    /// Off by default as it can be resource-intensive.
    /// </summary>
    public bool MetadataSyncPeople { get; set; }

    /// <summary>
    /// Sync item images (Primary, Backdrop, Logo, Thumb, etc.).
    /// </summary>
    public bool MetadataSyncImages { get; set; } = true;

    /// <summary>
    /// Sync metadata for folder-type items (Series, Season, Album, Artist, BoxSet).
    /// When enabled, metadata for container items is synced in addition to leaf items.
    /// </summary>
    public bool MetadataSyncFolderItems { get; set; }

    /// <summary>
    /// Timestamp when the last metadata sync completed.
    /// </summary>
    public DateTime? LastMetadataSyncTime { get; set; }

    /// <summary>
    /// Enable people entity synchronization between servers.
    /// Syncs person metadata (biography, provider IDs, images) by matching people by name.
    /// </summary>
    public bool EnablePeopleSync { get; set; }

    /// <summary>
    /// Which server speaks for a person when several know the person.
    /// </summary>
    public SyncPrecedence PeopleSyncPrecedence { get; set; } = SyncPrecedence.Newest;

    /// <summary>
    /// Sync person images (Primary, etc.) from source to local.
    /// </summary>
    public bool PeopleSyncImages { get; set; } = true;

    /// <summary>
    /// Timestamp when the last people sync completed.
    /// </summary>
    public DateTime? LastPeopleSyncTime { get; set; }

    /// <summary>
    /// Concurrent items processed during the Metadata and People refresh
    /// build phases. Higher values finish refreshes faster but use more CPU
    /// for the duration. The build work is mostly CPU-bound (blob
    /// serialization, hashing, comparison). Default 8. Clamped 1 to 16.
    /// </summary>
    public int RefreshParallelism { get; set; } = 8;

    /// <summary>Gets or sets how many hours pass between full scans, clamped 0 to 720. At 0 every scan is a full scan.</summary>
    public int FullScanIntervalHours { get; set; } = 24;

    /// <summary>
    /// How long, in seconds, an object must go untouched after its last change before that change is
    /// sent to the servers in Push or Sync mode. Every edit to the same object restarts the wait, and
    /// one hint goes out at the end of it, so a poster changed twice a minute apart is sent once.
    /// Default 60. Clamped 1 to 3600.
    /// </summary>
    public int HintDebounceSeconds { get; set; } = 60;

    /// <summary>
    /// How long, in seconds, to wait before sending a change again after a peer could not be reached or
    /// answered with an error. Each further failure doubles the wait, up to
    /// <see cref="HintRetryMaxMinutes"/>. Default 30. Clamped 5 to 3600.
    /// </summary>
    public int HintRetrySeconds { get; set; } = 30;

    /// <summary>
    /// The longest wait, in minutes, between attempts to send a change to a peer that cannot be reached.
    /// Default 60. Clamped 1 to 1440.
    /// </summary>
    public int HintRetryMaxMinutes { get; set; } = 60;

    /// <summary>
    /// How many times a change is sent to a peer that cannot be reached before it is marked failed.
    /// Zero keeps trying for as long as the peer is configured. Default 0. Clamped 0 to 1000.
    /// </summary>
    public int HintMaxRetries { get; set; }

    /// <summary>
    /// How long, in minutes, hints to a peer are paused after it refused this server's key or answered
    /// that it lacks the plugin. Default 15. Clamped 1 to 1440.
    /// </summary>
    public int HintRefusedRetryMinutes { get; set; } = 15;

    /// <summary>
    /// How many times a change is sent to a peer that keeps refusing before it is marked failed. Zero
    /// keeps trying for as long as the peer is configured. Default 0. Clamped 0 to 1000.
    /// </summary>
    public int HintRefusedMaxRetries { get; set; }

    /// <summary>Gets or sets the Whitelist of users and locations that may call this server's sync routes.</summary>
    public List<PeerCallerGrant> PeerCallerGrants { get; set; } = new();

    /// <summary>
    /// Verify source image sizes with live HTTP calls on every refresh, even
    /// for images whose tag has not changed. Applies to all sync modules.
    /// Catches the rare case of an image file replaced on the source's disk
    /// without a metadata rescan, at the cost of one GET plus one HEAD per
    /// image per item per refresh. Off by default, unchanged tags reuse the
    /// previously measured sizes.
    /// </summary>
    public bool DeepImageVerification { get; set; }

    /// <summary>
    /// When enabled, whitelisted source collections are mirrored locally by
    /// the Sync Collections task: a matching local collection is created and
    /// its membership tracks the synced counterparts of the source
    /// collection's items. Defaults to on, since the whole point of whitelisting
    /// a collection is seeing it on this server.
    /// </summary>
    public bool MirrorSyncedCollections { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Playlists sync is on.</summary>
    public bool EnablePlaylistSync { get; set; }

    /// <summary>Gets or sets a value indicating whether a playlist's shares are copied with it.</summary>
    public bool PlaylistSyncShares { get; set; } = true;

    /// <summary>
    /// Which copy of a playlist wins between this server and a Sync server: the one edited last, or
    /// always the other server's when it ranks above this server's own edits.
    /// </summary>
    public SyncPrecedence PlaylistSyncPrecedence { get; set; } = SyncPrecedence.Newest;

    /// <summary>
    /// What happens to a copied playlist when the server it came from deletes its own.
    /// </summary>
    public PlaylistRemovedAction PlaylistRemovedAction { get; set; } = PlaylistRemovedAction.Delete;

    /// <summary>
    /// Clamps configuration values to valid ranges.
    /// </summary>
    public void SanitizeValues()
    {
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 10);
        MaxDownloadSpeed = Math.Max(0, MaxDownloadSpeed);

        LinkCacheSizeGb = Math.Clamp(LinkCacheSizeGb, 0, 1_000_000);
        LinkCachePercent = Math.Clamp(LinkCachePercent, 1, 100);
        LinkMaxStreamsTotal = Math.Clamp(LinkMaxStreamsTotal, 0, 1000);
        LinkCacheRetentionDays = Math.Clamp(LinkCacheRetentionDays, 0, 3650);
        LinkCachePreCacheEpisodes = Math.Clamp(LinkCachePreCacheEpisodes, 0, 50);
        LinkCachePath = (LinkCachePath ?? string.Empty).Trim();
        MinimumFreeDiskSpaceGb = Math.Clamp(MinimumFreeDiskSpaceGb, 0, 1000);
        ScheduledStartHour = Math.Clamp(ScheduledStartHour, 0, 23);
        ScheduledEndHour = Math.Clamp(ScheduledEndHour, 0, 24);
        ScheduledDownloadSpeed = Math.Max(0, ScheduledDownloadSpeed);
        RecyclingBinRetentionDays = Math.Clamp(RecyclingBinRetentionDays, 1, 365);
        MaxRetryCount = Math.Clamp(MaxRetryCount, 1, 10);
        RefreshParallelism = Math.Clamp(RefreshParallelism, 1, 16);
        HintDebounceSeconds = Math.Clamp(HintDebounceSeconds, 1, 3600);
        FullScanIntervalHours = Math.Clamp(FullScanIntervalHours, 0, 720);
        HintRetrySeconds = Math.Clamp(HintRetrySeconds, 5, 3600);
        HintRetryMaxMinutes = Math.Clamp(HintRetryMaxMinutes, 1, 1440);
        HintMaxRetries = Math.Clamp(HintMaxRetries, 0, 1000);
        HintRefusedRetryMinutes = Math.Clamp(HintRefusedRetryMinutes, 1, 1440);
        HintRefusedMaxRetries = Math.Clamp(HintRefusedMaxRetries, 0, 1000);

        // A hand edited or truncated XML file can deserialize a list as null or with null entries. Every
        // reader walks these lists without checking, so they are repaired here before anything else runs.
        Servers ??= new List<SourceServer>();
        Servers.RemoveAll(entry => entry is null);
        foreach (var server in Servers)
        {
            server.LinkMaxStreams = Math.Clamp(server.LinkMaxStreams, 0, 1000);
            server.LinkTakeMaxStreams = Math.Clamp(server.LinkTakeMaxStreams, 0, 1000);
            // The per user limit lives on each user mapping, so a server wide one is copied onto mappings that set none.
            if (server.LinkMaxStreamsPerUser > 0)
            {
                foreach (var mapping in server.UserMappings ?? new List<UserMapping>())
                {
                    mapping.LinkMaxStreams ??= Math.Min(server.LinkMaxStreamsPerUser, 1000);
                }

                server.LinkMaxStreamsPerUser = 0;
            }
            server.LinkStreamSpeedMbps = Math.Clamp(server.LinkStreamSpeedMbps, 0, 100_000);
            server.LinkScheduledStreamSpeedMbps = Math.Clamp(server.LinkScheduledStreamSpeedMbps, 0, 100_000);
        }
        LastRunFailures ??= new List<SyncRunFailure>();
        WatchedFilterUserIds ??= new List<string>();

        // Every speed is kept in Mbps, so one in KB, MB, or GB per second is converted.
        MaxDownloadSpeed = RateUtilities.ToMbps(MaxDownloadSpeed, DownloadSpeedUnit);
        ScheduledDownloadSpeed = RateUtilities.ToMbps(ScheduledDownloadSpeed, ScheduledDownloadSpeedUnit);
        DownloadSpeedUnit = Mbps;
        ScheduledDownloadSpeedUnit = Mbps;

        // Skipping watched items is on exactly while users are picked.
        if (!SkipWatchedByAllUsers)
        {
            WatchedFilterUserIds.Clear();
        }

        SkipWatchedByAllUsers = WatchedFilterUserIds.Count > 0;

        // History negotiation is chosen per library, so the server wide switch is copied onto each mapping.
        if (HistorySyncNegotiate)
        {
            foreach (var mapping in Servers.SelectMany(s => s.LibraryMappings ?? new List<LibraryMapping>()))
            {
                mapping.NegotiateHistory = true;
            }

            HistorySyncNegotiate = false;
        }

        // What happens to a removed file is one value, so it is derived from the action and the approval mode.
        MissingContentAction = EffectiveMissingContentAction;
        DeleteMissingContentMode = MissingContentAction switch
        {
            Models.ContentSync.Configuration.MissingContentAction.Delete => ApprovalMode.Enabled,
            Models.ContentSync.Configuration.MissingContentAction.DeleteAfterApproval => ApprovalMode.RequireApproval,
            _ => ApprovalMode.Disabled
        };

        PeerCallerGrants = (PeerCallerGrants ?? new List<PeerCallerGrant>())
            .Where(g => g is not null && !string.IsNullOrWhiteSpace(g.UserId))
            .Select(g => new PeerCallerGrant
            {
                UserId = g.UserId.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant(),
                UserName = g.UserName?.Trim() ?? string.Empty,
                Locations = (g.Locations ?? new List<string>())
                    .SelectMany(l => (l ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToList()
            })
            .Select(g => (Grant: g, Valid: g.Locations.Where(l => l.Length <= 255).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(x => x.Grant.Locations.Count == 0 || x.Valid.Count > 0)
            .GroupBy(x => x.Grant.UserId)
            .Select(g => new PeerCallerGrant
            {
                UserId = g.Key,
                UserName = g.First().Grant.UserName,
                Locations = g.Any(x => x.Valid.Count == 0)
                    ? new List<string>()
                    : g.SelectMany(x => x.Valid).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList()
            })
            .Take(200)
            .ToList();

        Servers ??= new List<SourceServer>();

        foreach (var server in Servers)
        {
            if (string.IsNullOrWhiteSpace(server.Key))
            {
                server.Key = SourceServer.NewKey();
            }

            server.Name = server.Name?.Trim() ?? string.Empty;
            server.Url = (server.Url ?? string.Empty).Trim().TrimEnd('/');
            server.ExternalUrl = (server.ExternalUrl ?? string.Empty).Trim().TrimEnd('/');
            server.LibraryMappings ??= new List<LibraryMapping>();
            server.UserMappings ??= new List<UserMapping>();
            server.LibraryMappings.RemoveAll(entry => entry is null);
            server.UserMappings.RemoveAll(entry => entry is null);
            foreach (var mapping in server.LibraryMappings)
            {
                mapping.LocalRootPath = NormalizePathOrNull(mapping.LocalRootPath) ?? string.Empty;
            }
        }

        // An unparseable path is dropped because a throw would fail every save, including the settings page's.
        TempDownloadPath = NormalizePathOrNull(TempDownloadPath);
        RecyclingBinPath = NormalizePathOrNull(RecyclingBinPath);
    }

    private static string? NormalizePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            // A relative path would resolve against the server's working directory, which is not a place
            // anyone chose, so it is treated as invalid rather than turned into a surprise location.
            if (!Path.IsPathFullyQualified(path))
            {
                return null;
            }

            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Most-recent run failure per module, surfaced in the dashboard.
    /// Stored as a list (not a dictionary) because Jellyfin's XML serialization
    /// does not round-trip <see cref="Dictionary{TKey, TValue}"/> reliably.
    /// </summary>
    public List<SyncRunFailure> LastRunFailures { get; set; } = new();
}

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
/// The settings of the Server Sync plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the server entries in Rank order. The first wins when two offer the same item, and each later one adds only what the earlier lack.
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
    /// Gets or sets a value indicating whether this plugin streams a link's direct play from the source for the user who asked.
    /// When off, Jellyfin reads the link's own address, which cannot tell two viewers of one link apart.
    /// </summary>
    public bool StreamLinksDirectly { get; set; } = true;

    /// <summary>
    /// The most links this server streams from other servers at once, across all of them, or 0 for no
    /// limit. A link that plays from the link cache streams from nobody and is not counted.
    /// </summary>
    public int LinkMaxStreamsTotal { get; set; }

    /// <summary>
    /// Gets or sets how the link cache is sized. Unset, the cache is off at a size of 0 and fixed at <see cref="LinkCacheSizeGb"/> otherwise.
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
    /// Reads the most disk the link cache may use, in bytes, or 0 when the cache is off.
    /// The room on the cache's disk is what the cache holds plus what is free, less the free space always kept.
    /// </summary>
    /// <param name="usedBytes">The bytes the cache holds.</param>
    /// <param name="freeBytes">The free space on the cache's disk.</param>
    /// <returns>The limit in bytes.</returns>
    public long GetLinkCacheLimitBytes(long usedBytes, long freeBytes)
    {
        var room = Math.Max(0, usedBytes) + Math.Max(0, freeBytes - (Math.Max(0, MinimumFreeDiskSpaceGb) * ByteSize.Gigabyte));
        return EffectiveLinkCacheSizing switch
        {
            Models.ContentSync.Configuration.LinkCacheSizing.Off => 0,
            Models.ContentSync.Configuration.LinkCacheSizing.Fixed => LinkCacheSizeGb * ByteSize.Gigabyte,
            Models.ContentSync.Configuration.LinkCacheSizing.Percent => (long)(room * (Math.Clamp(LinkCachePercent, 1, 100) / 100.0)),
            _ => room
        };
    }

    /// <summary>Gets or sets how many files download at once, clamped 1 to 10.</summary>
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>
    /// Gets or sets the most all downloads to this server together may use, in Mbps. 0 is no limit.
    /// </summary>
    public int MaxDownloadSpeed { get; set; } = 0;

    /// <summary>
    /// Gets or sets the unit <see cref="MaxDownloadSpeed"/> is in, which <see cref="SanitizeValues"/> always converts to <see cref="Mbps"/>.
    /// </summary>
    public string DownloadSpeedUnit { get; set; } = Mbps;

    /// <summary>The unit every speed is kept in.</summary>
    public const string Mbps = "Mbps";

    /// <summary>
    /// Reads the download speed limit in bytes per second.
    /// </summary>
    /// <returns>The speed in bytes per second, or 0 for no limit.</returns>
    public long GetMaxDownloadSpeedBytes() => RateUtilities.MbpsToBytesPerSecond(MaxDownloadSpeed);

    /// <summary>
    /// Reads the scheduled download speed limit in bytes per second.
    /// </summary>
    /// <returns>The speed in bytes per second, or 0 for no limit.</returns>
    public long GetScheduledDownloadSpeedBytes() => RateUtilities.MbpsToBytesPerSecond(ScheduledDownloadSpeed);

    /// <summary>
    /// Reads the download speed limit that applies at this hour, given the bandwidth schedule.
    /// </summary>
    /// <returns>The speed in bytes per second, or 0 for no limit.</returns>
    public long GetEffectiveDownloadSpeedBytes() => GetEffectiveDownloadSpeedBytes(DateTime.Now.Hour);

    /// <summary>
    /// Reads the download speed limit that applies at a given local hour, given the bandwidth schedule.
    /// </summary>
    /// <param name="currentHour">The local hour, 0 to 23.</param>
    /// <returns>The speed in bytes per second, or 0 for no limit.</returns>
    public long GetEffectiveDownloadSpeedBytes(int currentHour)
        => IsInBandwidthSchedule(currentHour) ? GetScheduledDownloadSpeedBytes() : GetMaxDownloadSpeedBytes();

    /// <summary>Whether the bandwidth schedule's hours apply at this hour, which is never the case while scheduling is off.</summary>
    /// <returns><c>true</c> inside the scheduled hours.</returns>
    public bool IsInBandwidthSchedule() => IsInBandwidthSchedule(DateTime.Now.Hour);

    /// <summary>
    /// Whether the bandwidth schedule's hours apply at a given local hour. Downloads and link streams share the schedule.
    /// </summary>
    /// <param name="currentHour">The local hour, 0 to 23.</param>
    /// <returns><c>true</c> inside the scheduled hours.</returns>
    public bool IsInBandwidthSchedule(int currentHour)
    {
        if (!EnableBandwidthScheduling)
        {
            return false;
        }

        // Equal start and end hours mean all day.
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

    /// <summary>Gets what happens to a removed file, read from <see cref="DeleteMissingContentMode"/> when no action is set.</summary>
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
    /// Gets how a deletion of a removed file is approved, from <see cref="EffectiveMissingContentAction"/>.
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
    /// Gets or sets the time the last Content run started.
    /// </summary>
    public DateTime? LastSyncStartTime { get; set; }

    /// <summary>
    /// Gets or sets the time the last Content run ended.
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
    /// Gets or sets a value indicating whether a folder left empty by a deletion is removed.
    /// </summary>
    public bool RemoveEmptyFoldersOnDelete { get; set; }

    /// <summary>
    /// Maximum number of times to retry failed downloads before giving up.
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets a value indicating whether items watched by every user in <see cref="WatchedFilterUserIds"/> are skipped by Content.
    /// It is on exactly while any user is picked.
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
    /// Gets or sets the time the last History run ended.
    /// </summary>
    public DateTime? LastHistorySyncTime { get; set; }

    /// <summary>
    /// Gets or sets a switch that turns History negotiation on for every server.
    /// <see cref="SanitizeValues"/> moves it onto every library mapping as <see cref="Models.Configuration.LibraryMapping.NegotiateHistory"/> and clears it.
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
    /// Gets or sets the time the last Users run ended.
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
    /// Gets or sets the time the last Metadata run ended.
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
    /// Gets or sets the time the last People run ended.
    /// </summary>
    public DateTime? LastPeopleSyncTime { get; set; }

    /// <summary>
    /// Gets or sets how many items a Metadata or People scan builds at once, clamped 1 to 16.
    /// A higher value ends a scan sooner and uses more CPU.
    /// </summary>
    public int RefreshParallelism { get; set; } = 8;

    /// <summary>Gets or sets how many hours pass between full scans, clamped 0 to 720. At 0 every scan is a full scan.</summary>
    public int FullScanIntervalHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets Send Changes After, the seconds an object must go untouched before its hint goes out, clamped 1 to 3600.
    /// Every edit to the same object restarts the wait, so several edits send one hint.
    /// </summary>
    public int HintDebounceSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the seconds to wait before sending a change again after a peer failed, clamped 5 to 3600.
    /// Each further failure doubles the wait, up to <see cref="HintRetryMaxMinutes"/>.
    /// </summary>
    public int HintRetrySeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the longest wait, in minutes, between attempts to send a change to a peer that cannot be reached, clamped 1 to 1440.
    /// </summary>
    public int HintRetryMaxMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets how many times a change is sent to a peer that cannot be reached before it is marked failed, clamped 0 to 1000.
    /// Zero keeps trying for as long as the peer has a server entry.
    /// </summary>
    public int HintMaxRetries { get; set; }

    /// <summary>
    /// Gets or sets how many minutes hints to a peer are paused after it refused this server's key or lacks the plugin, clamped 1 to 1440.
    /// </summary>
    public int HintRefusedRetryMinutes { get; set; } = 15;

    /// <summary>
    /// Gets or sets how many times a change is sent to a peer that keeps refusing before it is marked failed, clamped 0 to 1000.
    /// Zero keeps trying for as long as the peer has a server entry.
    /// </summary>
    public int HintRefusedMaxRetries { get; set; }

    /// <summary>Gets or sets the Whitelist of users and locations that may call this server's sync routes.</summary>
    public List<PeerCallerGrant> PeerCallerGrants { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether every scan measures source images again, even those whose tag is unchanged.
    /// It catches an image replaced on the source's disk under the same tag, at the cost of two calls per image.
    /// </summary>
    public bool DeepImageVerification { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether collections a Whitelist lists are mirrored here, each holding the synced copies of the source collection's items.
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
    /// Clamps every setting to its valid range, repairs lists a damaged file left null, and moves each server wide value onto the mappings that hold it.
    /// A path that cannot be parsed is dropped, since a throw would fail every save.
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

        Servers ??= new List<SourceServer>();
        Servers.RemoveAll(entry => entry is null);
        foreach (var server in Servers)
        {
            server.LinkMaxStreams = Math.Clamp(server.LinkMaxStreams, 0, 1000);
            server.LinkTakeMaxStreams = Math.Clamp(server.LinkTakeMaxStreams, 0, 1000);
            // The per user limit lives on each user mapping.
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

        // History negotiation is chosen per library mapping.
        if (HistorySyncNegotiate)
        {
            foreach (var mapping in Servers.SelectMany(s => s.LibraryMappings ?? new List<LibraryMapping>()))
            {
                mapping.NegotiateHistory = true;
            }

            HistorySyncNegotiate = false;
        }

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

        TempDownloadPath = NormalizePathOrNull(TempDownloadPath);
        RecyclingBinPath = NormalizePathOrNull(RecyclingBinPath);
    }

    /// <summary>Returns the full form of a path, or null when it is relative or cannot be parsed.</summary>
    private static string? NormalizePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            // A relative path would resolve against a folder nobody chose.
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
    /// Gets or sets the last run failure of each sync type, shown on the dashboard.
    /// It is a list because Jellyfin's XML serialization does not round trip a dictionary.
    /// </summary>
    public List<SyncRunFailure> LastRunFailures { get; set; } = new();
}

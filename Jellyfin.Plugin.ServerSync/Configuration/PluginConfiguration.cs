using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ServerSync.Configuration;

/// <summary>
/// Configuration settings for the Server Sync plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ===== Servers =====

    /// <summary>
    /// Gets or sets the configured peer servers in priority order. Index zero wins whenever two servers
    /// offer the same item, and each later entry only adds what every earlier entry lacks.
    /// </summary>
    public List<SourceServer> Servers { get; set; } = new();

    // ===== Legacy single source =====
    // The fields below carried the single source before servers became a list. They are still read so an
    // older configuration file loads, then MigrateLegacyServer moves them into the first entry of Servers
    // and they are never written again.

    public string SourceServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// When true (default), the source-server URL is allowed to point at
    /// loopback, RFC1918, or IPv6 ULA addresses — typical for home Jellyfin
    /// installs where the source server runs on the same LAN. When false, the
    /// URL must resolve to a public address; loopback/private ranges are
    /// rejected. Cloud-metadata endpoints (169.254.0.0/16, IPv6 link-local,
    /// IPv6 site-local, 0.0.0.0) are always blocked regardless of this flag.
    /// </summary>
    public bool AllowSourceServerOnPrivateNetwork { get; set; } = true;

    /// <summary>
    /// Optional external URL for the source server, used only for image display in the UI.
    /// When set, image thumbnails in sync tables and filter browsers use this URL instead
    /// of SourceServerUrl. Useful when the sync connection uses an internal/VPN address
    /// but the browser needs a public URL to load images.
    /// </summary>
    public string SourceServerExternalUrl { get; set; } = string.Empty;

    /// <summary>
    /// API key or access token for authenticating with the source server.
    /// Can be either a manually entered API key or a token generated from username/password.
    /// </summary>
    public string SourceServerApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Username that was used to generate the access token.
    /// Empty if using a manually entered API key.
    /// </summary>
    public string SourceServerAuthenticatedUser { get; set; } = string.Empty;

    /// <summary>
    /// User ID of the authenticated user on the source server.
    /// Used for user-scoped API fallbacks when the user is not an admin.
    /// Empty if using a manually entered API key.
    /// </summary>
    public string SourceServerAuthenticatedUserId { get; set; } = string.Empty;

    public string SourceServerName { get; set; } = string.Empty;

    public string SourceServerId { get; set; } = string.Empty;

    /// <summary>
    /// Library mappings between source and local servers.
    /// Shared by ContentSync and HistorySync features.
    /// </summary>
    public List<LibraryMapping> LibraryMappings { get; set; } = new();

    /// <summary>
    /// User mappings between source and local servers.
    /// Used by HistorySync and UserSync features.
    /// </summary>
    public List<UserMapping> UserMappings { get; set; } = new();

    /// <summary>Suppresses the legacy element. See <see cref="MigrateLegacyServer"/>.</summary>
    public bool ShouldSerializeSourceServerUrl() => false;

    /// <summary>Suppresses the legacy element.</summary>
    public bool ShouldSerializeAllowSourceServerOnPrivateNetwork() => false;

    /// <summary>Suppresses the legacy element.</summary>
    public bool ShouldSerializeSourceServerExternalUrl() => false;

    /// <summary>Suppresses the legacy element.</summary>
    public bool ShouldSerializeSourceServerApiKey() => false;

    /// <summary>Suppresses the legacy element.</summary>
    public bool ShouldSerializeSourceServerAuthenticatedUser() => false;

    /// <summary>Suppresses the legacy element.</summary>
    public bool ShouldSerializeSourceServerAuthenticatedUserId() => false;

    /// <summary>Suppresses the legacy element.</summary>
    public bool ShouldSerializeSourceServerName() => false;

    /// <summary>Suppresses the legacy element.</summary>
    public bool ShouldSerializeSourceServerId() => false;

    /// <summary>Suppresses the legacy element.</summary>
    public bool ShouldSerializeLibraryMappings() => false;

    /// <summary>Suppresses the legacy element.</summary>
    public bool ShouldSerializeUserMappings() => false;

    /// <summary>
    /// Moves a single source written by an older version into the first entry of <see cref="Servers"/>.
    /// Runs on every load and save and does nothing once the list has an entry or the legacy fields are
    /// empty, so it is safe to call repeatedly.
    /// </summary>
    /// <returns><c>true</c> when an entry was created.</returns>
    public bool MigrateLegacyServer()
    {
        var hasLegacyData = !string.IsNullOrWhiteSpace(SourceServerUrl)
            || !string.IsNullOrWhiteSpace(SourceServerApiKey)
            || (LibraryMappings?.Count ?? 0) > 0
            || (UserMappings?.Count ?? 0) > 0;
        if (Servers.Count > 0 || !hasLegacyData)
        {
            ClearLegacyServer();
            return false;
        }

        Servers.Add(new SourceServer
        {
            Name = SourceServerName,
            Url = SourceServerUrl,
            ExternalUrl = SourceServerExternalUrl,
            AllowPrivateNetwork = AllowSourceServerOnPrivateNetwork,
            ApiKey = SourceServerApiKey,
            AuthenticatedUser = SourceServerAuthenticatedUser,
            AuthenticatedUserId = SourceServerAuthenticatedUserId,
            ServerName = SourceServerName,
            ServerId = SourceServerId,
            Mode = ServerMode.Pull,
            IsEnabled = true,
            LibraryMappings = LibraryMappings ?? new List<LibraryMapping>(),
            UserMappings = UserMappings ?? new List<UserMapping>()
        });

        ClearLegacyServer();
        return true;
    }

    private void ClearLegacyServer()
    {
        SourceServerUrl = string.Empty;
        SourceServerExternalUrl = string.Empty;
        SourceServerApiKey = string.Empty;
        SourceServerAuthenticatedUser = string.Empty;
        SourceServerAuthenticatedUserId = string.Empty;
        SourceServerName = string.Empty;
        SourceServerId = string.Empty;
        LibraryMappings = new List<LibraryMapping>();
        UserMappings = new List<UserMapping>();
    }

    // ===== Content Sync Configuration =====

    public bool EnableContentSync { get; set; }

    public string? TempDownloadPath { get; set; }

    public bool IncludeCompanionFiles { get; set; } = true;

    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>
    /// Maximum download speed value (0 = unlimited).
    /// </summary>
    public int MaxDownloadSpeed { get; set; } = 0;

    /// <summary>
    /// Unit for MaxDownloadSpeed (KB, MB, GB).
    /// </summary>
    public string DownloadSpeedUnit { get; set; } = "MB";

    /// <summary>
    /// Calculates the max download speed in bytes per second.
    /// </summary>
    /// <returns>Speed in bytes per second.</returns>
    public long GetMaxDownloadSpeedBytes()
    {
        if (MaxDownloadSpeed == 0) return 0;

        return DownloadSpeedUnit switch
        {
            "KB" => MaxDownloadSpeed * 1024L,
            "MB" => MaxDownloadSpeed * 1024L * 1024L,
            "GB" => MaxDownloadSpeed * 1024L * 1024L * 1024L,
            _ => MaxDownloadSpeed * 1024L * 1024L // Default to MB
        };
    }

    /// <summary>
    /// Calculates the scheduled download speed in bytes per second.
    /// </summary>
    /// <returns>Speed in bytes per second.</returns>
    public long GetScheduledDownloadSpeedBytes()
    {
        if (ScheduledDownloadSpeed == 0) return 0;

        return ScheduledDownloadSpeedUnit switch
        {
            "KB" => ScheduledDownloadSpeed * 1024L,
            "MB" => ScheduledDownloadSpeed * 1024L * 1024L,
            "GB" => ScheduledDownloadSpeed * 1024L * 1024L * 1024L,
            _ => ScheduledDownloadSpeed * 1024L * 1024L // Default to MB
        };
    }

    /// <summary>
    /// Returns the appropriate download speed based on current time and scheduling settings.
    /// </summary>
    /// <returns>Effective speed in bytes per second.</returns>
    public long GetEffectiveDownloadSpeedBytes()
    {
        if (!EnableBandwidthScheduling)
        {
            return GetMaxDownloadSpeedBytes();
        }

        var currentHour = DateTime.Now.Hour;

        // Equal start and end reads as "all day", not "never". The general
        // same-day branch evaluates to `hour >= X && hour < X`, which is always
        // false, so a schedule of 3 to 3 silently did nothing.
        var isInScheduledWindow = ScheduledStartHour == ScheduledEndHour
            || (ScheduledStartHour < ScheduledEndHour
                ? currentHour >= ScheduledStartHour && currentHour < ScheduledEndHour
                : currentHour >= ScheduledStartHour || currentHour < ScheduledEndHour);

        return isInScheduledWindow ? GetScheduledDownloadSpeedBytes() : GetMaxDownloadSpeedBytes();
    }

    /// <summary>
    /// Controls how new content (items on source that don't exist locally) is handled.
    /// </summary>
    public ApprovalMode DownloadNewContentMode { get; set; } = ApprovalMode.Enabled;

    /// <summary>
    /// Controls how updated content (items that differ from local version) is handled.
    /// </summary>
    public ApprovalMode ReplaceExistingContentMode { get; set; } = ApprovalMode.Enabled;

    /// <summary>
    /// Controls how missing content (items on local that don't exist on source) is handled.
    /// </summary>
    public ApprovalMode DeleteMissingContentMode { get; set; } = ApprovalMode.Disabled;

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
    /// Download speed during scheduled hours.
    /// </summary>
    public int ScheduledDownloadSpeed { get; set; } = 0;

    /// <summary>
    /// Unit for scheduled download speed (KB, MB, GB).
    /// </summary>
    public string ScheduledDownloadSpeedUnit { get; set; } = "MB";

    /// <summary>
    /// Minimum free disk space required before downloads (in GB).
    /// </summary>
    public int MinimumFreeDiskSpaceGb { get; set; } = 10;

    /// <summary>
    /// Timestamp of last successful connection check.
    /// </summary>
    public DateTime? LastConnectionCheck { get; set; }

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
    /// When enabled, items watched by every user in <see cref="WatchedFilterUserIds"/>
    /// are skipped during content sync (not queued for download). If at least one selected
    /// user has not watched the item, it is still eligible to sync.
    /// Has no effect when <see cref="WatchedFilterUserIds"/> is empty.
    /// </summary>
    public bool SkipWatchedByAllUsers { get; set; }

    /// <summary>
    /// Source-server user IDs whose watched status determines whether an item is skipped
    /// when <see cref="SkipWatchedByAllUsers"/> is enabled.
    /// </summary>
    public List<string> WatchedFilterUserIds { get; set; } = new();

    /// <summary>
    /// Tolerance in bytes for treating a local file as matching the source's recorded size.
    /// 0 (default) means strict equality. Non-zero values allow minor drift between Jellyfin's
    /// MediaSources.Size and the actual on-disk file size (e.g., after tag rewrites or remux)
    /// without re-queueing the file for download.
    /// Post-download integrity is always validated against the HTTP Content-Length, so this
    /// setting only affects skip decisions on existing local files.
    /// </summary>
    public long SizeMatchToleranceBytes { get; set; }

    // ===== History Sync Configuration =====

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

    /// <summary>
    /// Timestamp when the last history sync completed.
    /// </summary>
    public DateTime? LastHistorySyncTime { get; set; }

    // ===== User Sync Configuration =====

    /// <summary>
    /// Negotiate watch history with the source server instead of only pulling it. The merged state is
    /// written to both servers through the Server Sync plugin on the source, which must be installed
    /// there. Requires an API key the source accepts, the same one the other modules use.
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
    /// Timestamp when the last user sync completed.
    /// </summary>
    public DateTime? LastUserSyncTime { get; set; }

    // ===== Metadata Sync Configuration =====

    /// <summary>
    /// Enable metadata synchronization between servers.
    /// </summary>
    public bool EnableMetadataSync { get; set; }

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

    // ===== People Sync Configuration =====

    /// <summary>
    /// Enable people entity synchronization between servers.
    /// Syncs person metadata (biography, provider IDs, images) by matching people by name.
    /// </summary>
    public bool EnablePeopleSync { get; set; }

    /// <summary>
    /// Sync person images (Primary, etc.) from source to local.
    /// </summary>
    public bool PeopleSyncImages { get; set; } = true;

    /// <summary>
    /// Timestamp when the last people sync completed.
    /// </summary>
    public DateTime? LastPeopleSyncTime { get; set; }

    // ===== Processing Configuration =====

    /// <summary>
    /// Concurrent items processed during the Metadata and People refresh
    /// build phases. Higher values finish refreshes faster but use more CPU
    /// for the duration; the build work is mostly CPU-bound (blob
    /// serialization, hashing, comparison) now that image sizes carry
    /// forward. Default 8 — the historical behavior. Clamped 1–16.
    /// </summary>
    public int RefreshParallelism { get; set; } = 8;

    /// <summary>
    /// Verify source image sizes with live HTTP calls on every refresh, even
    /// for images whose tag hasn't changed. Applies to all sync modules.
    /// Catches the rare case of an image file replaced on the source's disk
    /// without a metadata rescan, at the cost of one GET plus one HEAD per
    /// image per item per refresh. Off by default — unchanged tags reuse the
    /// previously measured sizes. Replaces the per-module
    /// MetadataSyncDeepImageVerification / PeopleSyncDeepImageVerification
    /// settings from 10.11.64.0.
    /// </summary>
    public bool DeepImageVerification { get; set; }

    /// <summary>
    /// When enabled, whitelisted source collections are mirrored locally by
    /// the Sync Collections task: a matching local collection is created and
    /// its membership tracks the synced counterparts of the source
    /// collection's items. Defaults to on — the whole point of whitelisting
    /// a collection is seeing it on this server.
    /// </summary>
    public bool MirrorSyncedCollections { get; set; } = true;

    /// <summary>
    /// Legacy 10.11.64.0 element. XML deserialization drops unknown elements,
    /// so without this shim a user who enabled per-module deep verification
    /// would have the feature silently reset to off on upgrade. Reads map onto
    /// <see cref="DeepImageVerification"/>; never serialized back out.
    /// </summary>
    [System.Xml.Serialization.XmlElement("MetadataSyncDeepImageVerification")]
    [System.ComponentModel.Browsable(false)]
    public bool LegacyMetadataSyncDeepImageVerification
    {
        get => false;
        set => DeepImageVerification |= value;
    }

    /// <summary>
    /// Legacy 10.11.64.0 element; see <see cref="LegacyMetadataSyncDeepImageVerification"/>.
    /// </summary>
    [System.Xml.Serialization.XmlElement("PeopleSyncDeepImageVerification")]
    [System.ComponentModel.Browsable(false)]
    public bool LegacyPeopleSyncDeepImageVerification
    {
        get => false;
        set => DeepImageVerification |= value;
    }

    /// <summary>
    /// Suppresses serialization of the legacy metadata element.
    /// </summary>
    public bool ShouldSerializeLegacyMetadataSyncDeepImageVerification() => false;

    /// <summary>
    /// Suppresses serialization of the legacy people element.
    /// </summary>
    public bool ShouldSerializeLegacyPeopleSyncDeepImageVerification() => false;

    /// <summary>
    /// Validates configuration values and returns a list of validation errors.
    /// </summary>
    /// <returns>List of validation error messages.</returns>
    public List<string> ValidateConfiguration()
    {
        var errors = new List<string>();

        // Validate each server entry
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in Servers)
        {
            var label = server.DisplayName;
            if (string.IsNullOrWhiteSpace(server.Key) || !seenKeys.Add(server.Key))
            {
                errors.Add($"Server '{label}' has a missing or duplicate key");
            }

            if (!string.IsNullOrWhiteSpace(server.Url)
                && (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")))
            {
                errors.Add($"Server '{label}' URL must be a valid HTTP or HTTPS URL");
            }

            if (server.IsEnabled && string.IsNullOrWhiteSpace(server.Url))
            {
                errors.Add($"Server '{label}' is enabled but has no URL");
            }

            if (server.IsEnabled && string.IsNullOrWhiteSpace(server.ApiKey))
            {
                errors.Add($"Server '{label}' is enabled but has no API key");
            }
        }

        var scanServers = this.GetPullServers();
        if (EnableContentSync && scanServers.Count == 0)
        {
            errors.Add("At least one enabled server in Pull or Sync mode is required when content sync is enabled");
        }

        // Validate numeric ranges
        if (MaxConcurrentDownloads < 1 || MaxConcurrentDownloads > 10)
        {
            errors.Add("Max concurrent downloads must be between 1 and 10");
        }

        if (MaxDownloadSpeed < 0)
        {
            errors.Add("Max download speed cannot be negative");
        }

        // Validate speed units
        var validUnits = new[] { "KB", "MB", "GB" };
        if (!string.IsNullOrEmpty(DownloadSpeedUnit) &&
            !Array.Exists(validUnits, u => u.Equals(DownloadSpeedUnit, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Download speed unit must be KB, MB, or GB");
        }

        if (!string.IsNullOrEmpty(ScheduledDownloadSpeedUnit) &&
            !Array.Exists(validUnits, u => u.Equals(ScheduledDownloadSpeedUnit, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Scheduled download speed unit must be KB, MB, or GB");
        }

        if (MinimumFreeDiskSpaceGb < 0 || MinimumFreeDiskSpaceGb > 1000)
        {
            errors.Add("Minimum free disk space must be between 0 and 1000 GB");
        }

        if (SizeMatchToleranceBytes < 0)
        {
            errors.Add("Size match tolerance cannot be negative");
        }

        // Validate bandwidth scheduling
        if (EnableBandwidthScheduling)
        {
            if (ScheduledStartHour < 0 || ScheduledStartHour > 23)
            {
                errors.Add("Scheduled start hour must be between 0 and 23");
            }

            if (ScheduledEndHour < 0 || ScheduledEndHour > 24)
            {
                errors.Add("Scheduled end hour must be between 0 and 24");
            }

            if (ScheduledDownloadSpeed < 0)
            {
                errors.Add("Scheduled download speed cannot be negative");
            }
        }

        // Validate library mappings
        foreach (var mapping in this.GetEnabledLibraryMappings())
        {
            if (string.IsNullOrWhiteSpace(mapping.SourceLibraryId))
            {
                errors.Add($"Library mapping '{mapping.SourceLibraryName}' is missing source library ID");
            }

            if (string.IsNullOrWhiteSpace(mapping.LocalRootPath))
            {
                errors.Add($"Library mapping '{mapping.SourceLibraryName}' is missing local root path");
            }
            else if (mapping.LocalRootPath.Contains("..", StringComparison.Ordinal))
            {
                errors.Add($"Library mapping '{mapping.SourceLibraryName}' local root path must not contain path traversal sequences (..)");
            }
        }

        // Validate user mappings
        foreach (var mapping in this.GetEnabledUserMappings())
        {
            if (string.IsNullOrWhiteSpace(mapping.SourceUserId))
            {
                errors.Add($"User mapping '{mapping.SourceUserName}' is missing source user ID");
            }

            if (string.IsNullOrWhiteSpace(mapping.LocalUserId))
            {
                errors.Add($"User mapping '{mapping.SourceUserName}' is missing local user ID");
            }
        }

        // Validate path safety
        if (!string.IsNullOrWhiteSpace(TempDownloadPath))
        {
            var normalizedTemp = System.IO.Path.GetFullPath(TempDownloadPath);
            if (normalizedTemp != TempDownloadPath && TempDownloadPath.Contains("..", StringComparison.Ordinal))
            {
                errors.Add("Temp download path must not contain path traversal sequences (..)");
            }
        }

        // Validate recycling bin settings
        if (EnableRecyclingBin)
        {
            if (string.IsNullOrWhiteSpace(RecyclingBinPath))
            {
                errors.Add("Recycling bin path is required when recycling bin is enabled");
            }
            else
            {
                var normalizedBin = System.IO.Path.GetFullPath(RecyclingBinPath);
                if (normalizedBin != RecyclingBinPath && RecyclingBinPath.Contains("..", StringComparison.Ordinal))
                {
                    errors.Add("Recycling bin path must not contain path traversal sequences (..)");
                }
            }

            if (RecyclingBinRetentionDays < 1 || RecyclingBinRetentionDays > 365)
            {
                errors.Add("Recycling bin retention must be between 1 and 365 days");
            }
        }

        // Validate history sync settings. History is the one module that also travels as hints, so a
        // server that only sends is a valid setup for it.
        if (EnableHistorySync)
        {
            var activeServers = this.GetActiveServers();
            if (activeServers.Count == 0)
            {
                errors.Add("At least one enabled server in Pull, Push, or Sync mode is required when history sync is enabled");
            }

            if (activeServers.All(s => s.GetEnabledUserMappings().Count == 0))
            {
                errors.Add("At least one user mapping must be enabled for history sync");
            }

            if (activeServers.All(s => s.GetEnabledLibraryMappings().Count == 0))
            {
                errors.Add("At least one library mapping must be enabled for history sync");
            }
        }

        // Validate user sync settings
        if (EnableUserSync)
        {
            if (scanServers.Count == 0)
            {
                errors.Add("At least one enabled server in Pull or Sync mode is required when user sync is enabled");
            }

            var enabledUserMappings = this.GetEnabledUserMappings();
            if (enabledUserMappings.Count == 0)
            {
                errors.Add("At least one user mapping must be enabled for user sync");
            }

            if (!UserSyncPolicy && !UserSyncConfiguration && !UserSyncProfileImage)
            {
                errors.Add("At least one user sync option (Policy, Configuration, or Profile Image) must be enabled");
            }
        }

        // Validate metadata sync settings
        if (EnableMetadataSync)
        {
            if (scanServers.Count == 0)
            {
                errors.Add("At least one enabled server in Pull or Sync mode is required when metadata sync is enabled");
            }

            var enabledLibraryMappings = this.GetEnabledLibraryMappings();
            if (enabledLibraryMappings.Count == 0)
            {
                errors.Add("At least one library mapping must be enabled for metadata sync");
            }

            if (!MetadataSyncMetadata && !MetadataSyncImages && !MetadataSyncPeople && !MetadataSyncStudios && !MetadataSyncGenres && !MetadataSyncTags)
            {
                errors.Add("At least one metadata sync option (Metadata, Images, People, Studios, Genres, or Tags) must be enabled");
            }
        }

        return errors;
    }

    /// <summary>
    /// Returns true if the configuration passes validation.
    /// </summary>
    /// <returns>True if valid.</returns>
    public bool IsValid()
    {
        return ValidateConfiguration().Count == 0;
    }

    /// <summary>
    /// Clamps configuration values to valid ranges.
    /// </summary>
    public void SanitizeValues()
    {
        MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 10);
        MaxDownloadSpeed = Math.Max(0, MaxDownloadSpeed);
        MinimumFreeDiskSpaceGb = Math.Clamp(MinimumFreeDiskSpaceGb, 0, 1000);
        ScheduledStartHour = Math.Clamp(ScheduledStartHour, 0, 23);
        ScheduledEndHour = Math.Clamp(ScheduledEndHour, 0, 24);
        ScheduledDownloadSpeed = Math.Max(0, ScheduledDownloadSpeed);
        RecyclingBinRetentionDays = Math.Clamp(RecyclingBinRetentionDays, 1, 365);
        MaxRetryCount = Math.Clamp(MaxRetryCount, 1, 10);
        SizeMatchToleranceBytes = Math.Max(0, SizeMatchToleranceBytes);
        RefreshParallelism = Math.Clamp(RefreshParallelism, 1, 16);

        MigrateLegacyServer();

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
            foreach (var mapping in server.LibraryMappings)
            {
                mapping.LocalRootPath = NormalizePathOrNull(mapping.LocalRootPath) ?? string.Empty;
            }
        }

        // Normalize and validate speed units
        DownloadSpeedUnit = NormalizeSpeedUnit(DownloadSpeedUnit);
        ScheduledDownloadSpeedUnit = NormalizeSpeedUnit(ScheduledDownloadSpeedUnit);

        // Normalize filesystem paths to remove traversal sequences. An
        // unparseable path (embedded NUL, absurd length) is dropped rather
        // than allowed to throw — SanitizeValues runs inside every save,
        // including the settings page's, and a throw would 500 the save.
        TempDownloadPath = NormalizePathOrNull(TempDownloadPath);
        RecyclingBinPath = NormalizePathOrNull(RecyclingBinPath);

        foreach (var mapping in LibraryMappings)
        {
            mapping.LocalRootPath = NormalizePathOrNull(mapping.LocalRootPath) ?? string.Empty;
        }
    }

    private static string? NormalizePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.PathTooLongException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a speed unit string to one of the valid values (KB, MB, GB).
    /// Returns "MB" for unrecognized values.
    /// </summary>
    private static string NormalizeSpeedUnit(string unit)
    {
        return unit?.Trim().ToUpperInvariant() switch
        {
            "KB" => "KB",
            "MB" => "MB",
            "GB" => "GB",
            _ => "MB"
        };
    }

    /// <summary>
    /// Most-recent run failure per module, surfaced in the dashboard.
    /// Stored as a list (not a dictionary) because Jellyfin's XML serialization
    /// doesn't round-trip <see cref="Dictionary{TKey, TValue}"/> reliably.
    /// </summary>
    public List<SyncRunFailure> LastRunFailures { get; set; } = new();
}

/// <summary>
/// Failure record for a sync run that aborted before normal completion.
/// </summary>
public sealed class SyncRunFailure
{
    /// <summary>
    /// Module mutex key — "Content", "History", "Metadata", "People", or "User".
    /// </summary>
    public string ModuleKey { get; set; } = string.Empty;

    /// <summary>"Refresh" or "Sync".</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>One-line human-readable reason; surfaced in the UI.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the failure was recorded.</summary>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Helpers over the server list: which servers scan, which entry a row belongs to, and the mappings
/// across every scan server in priority order.
/// </summary>
public static class PluginConfigurationExtensions
{
    /// <summary>
    /// Returns the servers this installation scans, in priority order: enabled, with a URL and key, and in
    /// Pull or Sync mode. Never returns null.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The scan servers, highest priority first.</returns>
    public static List<SourceServer> GetPullServers(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.Where(s => s.Pulls).ToList() ?? new List<SourceServer>();
    }

    /// <summary>
    /// Returns the servers this installation sends hints to: enabled, with a URL and key, and in Send or
    /// Both mode. Never returns null.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The send servers, in list order.</returns>
    public static List<SourceServer> GetPushServers(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.Where(s => s.Pushes).ToList() ?? new List<SourceServer>();
    }

    /// <summary>Returns every server that takes part in sync in any direction. Never returns null.</summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The active servers, in list order.</returns>
    public static List<SourceServer> GetActiveServers(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.Where(s => s.Pulls || s.Pushes).ToList() ?? new List<SourceServer>();
    }

    /// <summary>Finds a server entry by key, or null.</summary>
    /// <param name="config">The configuration.</param>
    /// <param name="key">The entry key carried on a sync row.</param>
    /// <returns>The entry, or null when no entry has that key.</returns>
    public static SourceServer? FindServer(this PluginConfiguration config, string? key)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return config.Servers?.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the server a sync row came from. Rows written before servers became a list carry no key
    /// and belong to the first scan server, which is where the single source migrated to.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="key">The entry key carried on the row, or null.</param>
    /// <returns>The entry, or null when none applies.</returns>
    public static SourceServer? ResolveServer(this PluginConfiguration config, string? key)
        => config.FindServer(key) ?? config.GetPullServers().FirstOrDefault();

    /// <summary>
    /// Returns the enabled library mappings of every scan server, in priority order. Never returns null.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The enabled mappings across all scan servers.</returns>
    public static List<LibraryMapping> GetEnabledLibraryMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GetPullServers().SelectMany(s => s.GetEnabledLibraryMappings()).ToList();
    }

    /// <summary>
    /// Returns the enabled user mappings of every scan server, in priority order. Never returns null.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The enabled mappings across all scan servers.</returns>
    public static List<UserMapping> GetEnabledUserMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.GetPullServers().SelectMany(s => s.GetEnabledUserMappings()).ToList();
    }

    /// <summary>
    /// Returns every library mapping of every server, enabled or not, in priority order. For code that
    /// only cares about local folders, such as disk space and recycling, where a disabled mapping's
    /// folder still exists.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>All library mappings.</returns>
    public static List<LibraryMapping> GetAllLibraryMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Servers?.SelectMany(s => s.LibraryMappings ?? new List<LibraryMapping>()).ToList() ?? new List<LibraryMapping>();
    }
}

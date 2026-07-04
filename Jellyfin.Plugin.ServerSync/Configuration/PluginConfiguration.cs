using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ServerSync.Configuration;

/// <summary>
/// Configuration settings for the Server Sync plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ===== Source Server Configuration =====

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
        var isInScheduledWindow = ScheduledStartHour <= ScheduledEndHour
            ? currentHour >= ScheduledStartHour && currentHour < ScheduledEndHour
            : currentHour >= ScheduledStartHour || currentHour < ScheduledEndHour;

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
    /// Validates configuration values and returns a list of validation errors.
    /// </summary>
    /// <returns>List of validation error messages.</returns>
    public List<string> ValidateConfiguration()
    {
        var errors = new List<string>();

        // Validate URL
        if (!string.IsNullOrWhiteSpace(SourceServerUrl))
        {
            if (!Uri.TryCreate(SourceServerUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                errors.Add("Source server URL must be a valid HTTP or HTTPS URL");
            }
        }

        // Validate authentication
        if (EnableContentSync)
        {
            if (string.IsNullOrWhiteSpace(SourceServerUrl))
            {
                errors.Add("Source server URL is required when content sync is enabled");
            }

            if (string.IsNullOrWhiteSpace(SourceServerApiKey))
            {
                errors.Add("API key is required for authentication");
            }
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
        foreach (var mapping in LibraryMappings.Where(m => m.IsEnabled))
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
        foreach (var mapping in UserMappings.Where(m => m.IsEnabled))
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

        // Validate history sync settings
        if (EnableHistorySync)
        {
            if (string.IsNullOrWhiteSpace(SourceServerUrl))
            {
                errors.Add("Source server URL is required when history sync is enabled");
            }

            if (string.IsNullOrWhiteSpace(SourceServerApiKey))
            {
                errors.Add("API key is required when history sync is enabled");
            }

            var enabledUserMappings = UserMappings?.Where(m => m.IsEnabled).ToList() ?? new List<UserMapping>();
            if (enabledUserMappings.Count == 0)
            {
                errors.Add("At least one user mapping must be enabled for history sync");
            }

            var enabledLibraryMappings = LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();
            if (enabledLibraryMappings.Count == 0)
            {
                errors.Add("At least one library mapping must be enabled for history sync");
            }
        }

        // Validate user sync settings
        if (EnableUserSync)
        {
            if (string.IsNullOrWhiteSpace(SourceServerUrl))
            {
                errors.Add("Source server URL is required when user sync is enabled");
            }

            if (string.IsNullOrWhiteSpace(SourceServerApiKey))
            {
                errors.Add("API key is required when user sync is enabled");
            }

            var enabledUserMappings = UserMappings?.Where(m => m.IsEnabled).ToList() ?? new List<UserMapping>();
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
            if (string.IsNullOrWhiteSpace(SourceServerUrl))
            {
                errors.Add("Source server URL is required when metadata sync is enabled");
            }

            if (string.IsNullOrWhiteSpace(SourceServerApiKey))
            {
                errors.Add("API key is required when metadata sync is enabled");
            }

            var enabledLibraryMappings = LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();
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

        // Normalize URLs
        if (!string.IsNullOrWhiteSpace(SourceServerUrl))
        {
            SourceServerUrl = SourceServerUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(SourceServerExternalUrl))
        {
            SourceServerExternalUrl = SourceServerExternalUrl.TrimEnd('/');
        }

        // Normalize and validate speed units
        DownloadSpeedUnit = NormalizeSpeedUnit(DownloadSpeedUnit);
        ScheduledDownloadSpeedUnit = NormalizeSpeedUnit(ScheduledDownloadSpeedUnit);

        // Normalize filesystem paths to remove traversal sequences
        if (!string.IsNullOrWhiteSpace(TempDownloadPath))
        {
            TempDownloadPath = System.IO.Path.GetFullPath(TempDownloadPath);
        }

        if (!string.IsNullOrWhiteSpace(RecyclingBinPath))
        {
            RecyclingBinPath = System.IO.Path.GetFullPath(RecyclingBinPath);
        }

        // Normalize library mapping local root paths
        foreach (var mapping in LibraryMappings)
        {
            if (!string.IsNullOrWhiteSpace(mapping.LocalRootPath))
            {
                mapping.LocalRootPath = System.IO.Path.GetFullPath(mapping.LocalRootPath);
            }
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
/// Convenience helpers over <see cref="PluginConfiguration"/>'s mapping
/// collections. Centralizes the
/// <c>(config.LibraryMappings ?? new()).Where(m =&gt; m.IsEnabled).ToList()</c>
/// pattern so refresh tasks share one enabled-mappings accessor.
/// </summary>
public static class PluginConfigurationExtensions
{
    /// <summary>
    /// Returns enabled library mappings as a fresh list. Never returns null.
    /// </summary>
    public static List<LibraryMapping> GetEnabledLibraryMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();
    }

    /// <summary>
    /// Returns enabled user mappings as a fresh list. Never returns null.
    /// </summary>
    public static List<UserMapping> GetEnabledUserMappings(this PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.UserMappings?.Where(m => m.IsEnabled).ToList() ?? new List<UserMapping>();
    }
}

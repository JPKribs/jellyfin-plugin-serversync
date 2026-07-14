using System;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Sync item information for API responses.
/// </summary>
public class SyncItemDto
{
    public long Id { get; set; }

    public string SourceItemId { get; set; } = string.Empty;

    public string SourceLibraryId { get; set; } = string.Empty;

    public string? SourceLibraryName { get; set; }

    public string LocalLibraryId { get; set; } = string.Empty;

    public string? LocalLibraryName { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string? LocalPath { get; set; }

    public string? LocalItemId { get; set; }

    public long SourceSize { get; set; }

    public string? SourceSizeFormatted { get; set; }

    public DateTime SourceCreateDate { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? PendingType { get; set; }

    public DateTime StatusDate { get; set; }

    public DateTime? LastSyncTime { get; set; }

    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; }

    public string? SourceServerUrl { get; set; }

    public string? SourceServerId { get; set; }

    public string? CompanionFiles { get; set; }
}

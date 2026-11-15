using System;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Sync item information for API responses.
/// </summary>
public class SyncItemDto
{
    /// <summary>Gets or sets the row id.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the item's id on the source.</summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the id of the item's library on the source.</summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the item's library on the source.</summary>
    public string? SourceLibraryName { get; set; }

    /// <summary>Gets or sets the id of the library here that the item is mapped to.</summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the library here that the item is mapped to.</summary>
    public string? LocalLibraryName { get; set; }

    /// <summary>Gets or sets the file's path on the source.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's path here, or null before one is chosen.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Gets or sets the item's id here, or null before Jellyfin has it.</summary>
    public string? LocalItemId { get; set; }

    /// <summary>Gets or sets the file's size on the source, in bytes.</summary>
    public long SourceSize { get; set; }

    /// <summary>Gets or sets <see cref="SourceSize"/> as text, such as 1.2 GB.</summary>
    public string? SourceSizeFormatted { get; set; }

    /// <summary>Gets or sets when the file was created on the source.</summary>
    public DateTime SourceCreateDate { get; set; }

    /// <summary>Gets or sets the item status, such as Synced or Queued.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets what a Pending row waits for approval to do, which is a download, a replacement, or a deletion.</summary>
    public string? PendingType { get; set; }

    /// <summary>Gets or sets when <see cref="Status"/> last changed.</summary>
    public DateTime StatusDate { get; set; }

    /// <summary>Gets or sets when the item last finished syncing.</summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>Gets or sets why the last attempt failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets how many times the item has been retried.</summary>
    public int RetryCount { get; set; }

    /// <summary>Gets or sets the Server URL of the row's server.</summary>
    public string? SourceServerUrl { get; set; }

    /// <summary>Gets or sets the Jellyfin server id of the row's server.</summary>
    public string? SourceServerId { get; set; }

    /// <summary>Gets or sets the configured server entry the row came from.</summary>
    public string? ServerKey { get; set; }

    /// <summary>Gets or sets that entry's display name.</summary>
    public string? ServerName { get; set; }

    /// <summary>Gets or sets the companion files saved with the item, as JSON.</summary>
    public string? CompanionFiles { get; set; }

    /// <summary>Gets or sets how the file is held here: Copy, or Link for a .strm that streams from the source.</summary>
    public string StorageMode { get; set; } = "Copy";

    /// <summary>Gets or sets a value indicating whether a link's file is held in the link cache.</summary>
    public bool Cached { get; set; }

    /// <summary>Gets or sets why a copy in a Link mapping is a copy: Pick, Hand, or Unknown.</summary>
    public string? Kept { get; set; }
}

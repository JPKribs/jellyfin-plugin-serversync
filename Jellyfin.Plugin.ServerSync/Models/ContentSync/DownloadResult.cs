namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Result of a download operation.
/// </summary>
/// <param name="Success">Whether the file arrived.</param>
/// <param name="ErrorMessage">Why the download failed, when it did.</param>
/// <param name="CompanionFiles">The companion files saved with the file, as JSON, or null.</param>
public record DownloadResult(bool Success, string? ErrorMessage = null, string? CompanionFiles = null);

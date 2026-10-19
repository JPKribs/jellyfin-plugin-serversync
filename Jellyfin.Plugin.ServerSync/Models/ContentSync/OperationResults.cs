namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Result of a download operation.
/// </summary>
public record DownloadResult(bool Success, string? ErrorMessage = null, string? CompanionFiles = null);

/// <summary>
/// Result of a deletion operation. AlreadyGone means the file was not on disk any more, so only the
/// stale library entry was removed.
/// </summary>
public record DeletionResult(bool Success, string? ErrorMessage = null, bool AlreadyGone = false);

/// <summary>
/// Result of a path validation check.
/// </summary>
public record PathValidationResult(bool IsValid, string? ErrorMessage);

/// <summary>
/// Result of a sync state transition operation.
/// </summary>
public record TransitionResult(bool Changed, string? Message = null);

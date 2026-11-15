namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Result of a deletion operation. AlreadyGone means the file was not on disk any more, so only the
/// stale library entry was removed.
/// </summary>
/// <param name="Success">Whether the file is gone.</param>
/// <param name="ErrorMessage">Why the deletion failed, when it did.</param>
/// <param name="AlreadyGone">Whether the file was already off the disk.</param>
public record DeletionResult(bool Success, string? ErrorMessage = null, bool AlreadyGone = false);

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Result of a sync state transition operation.
/// </summary>
/// <param name="Changed">Whether the row's state changed.</param>
/// <param name="Message">Why it did not change, when it did not.</param>
public record TransitionResult(bool Changed, string? Message = null);

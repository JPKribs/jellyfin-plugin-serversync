namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Result of a path validation check.
/// </summary>
/// <param name="IsValid">Whether the path is acceptable.</param>
/// <param name="ErrorMessage">Why the path was refused, when it was.</param>
public record PathValidationResult(bool IsValid, string? ErrorMessage);

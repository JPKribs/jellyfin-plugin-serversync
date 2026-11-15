namespace Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;

/// <summary>
/// What happens to a local file once its item is gone from the source.
/// </summary>
public enum MissingContentAction
{
    /// <summary>Leaves the local file.</summary>
    Keep = 0,

    /// <summary>Deletes the local file at once.</summary>
    Delete = 1,

    /// <summary>Leaves the local file and hides an emptied title folder with a .ignore file.</summary>
    Ignore = 2,

    /// <summary>Deletes the local file once an administrator approves.</summary>
    DeleteAfterApproval = 3
}

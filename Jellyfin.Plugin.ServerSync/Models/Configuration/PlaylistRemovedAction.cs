namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// What happens to this server's copy of a playlist when the server it came from deletes its own.
/// </summary>
public enum PlaylistRemovedAction
{
    /// <summary>The copy is deleted too.</summary>
    Delete = 0,

    /// <summary>The copy stays and becomes this server's own playlist.</summary>
    Keep = 1
}

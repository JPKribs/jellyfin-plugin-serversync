namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

/// <summary>What applying a source playlist did.</summary>
public enum PlaylistMirrorOutcome
{
    /// <summary>A local playlist was created.</summary>
    Created,

    /// <summary>The local playlist was changed to match.</summary>
    Updated,

    /// <summary>The local playlist already matched.</summary>
    Unchanged,

    /// <summary>None of the playlist's items are here yet, so nothing was changed.</summary>
    Waiting,

    /// <summary>The local playlist belongs to another user, so it was left alone.</summary>
    Skipped
}

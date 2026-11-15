namespace Jellyfin.Plugin.ServerSync.Services.Playlists;

/// <summary>What applying a source playlist did.</summary>
public enum PlaylistMirrorOutcome
{
    Created,
    Updated,
    Unchanged,
    Waiting,
    Skipped
}

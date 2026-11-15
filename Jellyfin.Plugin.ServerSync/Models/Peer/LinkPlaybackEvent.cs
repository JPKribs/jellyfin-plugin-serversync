namespace Jellyfin.Plugin.ServerSync.Models.Peer;

/// <summary>What happened to playback of a link, sent to the source so it shows there as a session.</summary>
public enum LinkPlaybackEvent
{
    /// <summary>Playback started.</summary>
    Start = 0,

    /// <summary>Playback went on, or paused or resumed.</summary>
    Progress = 1,

    /// <summary>Playback stopped.</summary>
    Stop = 2
}

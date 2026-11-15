namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// The limits Server Sync sets on the streams it copies and sends.
/// </summary>
public static class StreamCaps
{
    /// <summary>The largest image taken from another server, in bytes.</summary>
    public const long MaxSyncedImageBytes = 64L * 1024 * 1024;

    /// <summary>How many seconds of a speed limit may be sent at once after a stream has sat idle, so a player fills its first buffer.</summary>
    public const double BurstSeconds = 3;
}

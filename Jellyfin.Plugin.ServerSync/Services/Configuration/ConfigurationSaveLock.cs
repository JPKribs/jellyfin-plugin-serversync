namespace Jellyfin.Plugin.ServerSync.Services.Configuration;

/// <summary>
/// Single lock serializing every plugin-configuration mutation-and-save.
/// XmlSerializer enumerates <c>LastRunFailures</c> while writing the config
/// file; a concurrent module recording a failure would throw
/// "collection was modified" mid-write and could truncate the XML. All save
/// paths — task completion stamps, RunFailureLog, the config manager, and
/// the settings-page save (<c>Plugin.UpdateConfiguration</c>) — take this
/// lock.
/// </summary>
public static class ConfigurationSaveLock
{
    /// <summary>
    /// Gets the lock object.
    /// </summary>
    public static object Sync { get; } = new();
}

namespace Jellyfin.Plugin.ServerSync.Services.Configuration;

/// <summary>
/// Holds the one lock every change and save of the plugin configuration takes.
/// A save racing a change to <c>LastRunFailures</c> would throw while writing and could truncate the file.
/// </summary>
public static class ConfigurationSaveLock
{
    /// <summary>
    /// Gets the object to lock on.
    /// </summary>
    public static object Sync { get; } = new();
}

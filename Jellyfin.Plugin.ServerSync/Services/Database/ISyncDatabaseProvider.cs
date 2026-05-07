namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Provides access to the shared <see cref="SyncDatabase"/> singleton.
/// </summary>
public interface ISyncDatabaseProvider
{
    /// <summary>
    /// Gets the <see cref="SyncDatabase"/> instance.
    /// </summary>
    SyncDatabase Database { get; }
}

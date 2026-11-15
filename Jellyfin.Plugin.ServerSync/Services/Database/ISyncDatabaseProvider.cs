namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// Gives every store and sync table the one shared <see cref="SyncDatabase"/>.
/// </summary>
public interface ISyncDatabaseProvider
{
    /// <summary>
    /// Gets the sync database, opening it on first use.
    /// </summary>
    SyncDatabase Database { get; }
}

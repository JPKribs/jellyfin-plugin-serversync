using Jellyfin.Plugin.ServerSync.Services;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Hands a store the one sync database a test opened, in place of the plugin's own provider.
/// </summary>
public sealed class FixedProvider : ISyncDatabaseProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FixedProvider"/> class.
    /// </summary>
    /// <param name="db">The database every caller receives.</param>
    public FixedProvider(SyncDatabase db) => Database = db;

    /// <inheritdoc/>
    public SyncDatabase Database { get; }
}

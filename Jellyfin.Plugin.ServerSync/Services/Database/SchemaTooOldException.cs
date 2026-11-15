using System;

namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// The sync database was written by a release this one cannot read. It is left exactly as it is, so
/// installing <see cref="SyncSchema.OldestUpgradableRelease"/> and starting it once brings it up to date.
/// </summary>
public sealed class SchemaTooOldException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaTooOldException"/> class.
    /// </summary>
    /// <param name="foundVersion">The schema version the database holds.</param>
    public SchemaTooOldException(int foundVersion)
        : base($"The Server Sync database is schema v{foundVersion}, written by a release before {SyncSchema.OldestUpgradableRelease}, and this release reads v{SyncSchema.CurrentSchemaVersion}. It was left untouched. Install Server Sync {SyncSchema.OldestUpgradableRelease}, start Jellyfin once and leave it running for a day, then update again.")
    {
        FoundVersion = foundVersion;
    }

    /// <summary>Gets the schema version the database holds.</summary>
    public int FoundVersion { get; }
}

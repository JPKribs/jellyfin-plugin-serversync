using System;
using JPKribs.Jellyfin.Base;

namespace Jellyfin.Plugin.ServerSync.Services.Database;

/// <summary>
/// Thrown when the sync database was written by a release this one cannot read, and the database is left as it is.
/// </summary>
public sealed class SchemaTooOldException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaTooOldException"/> class.
    /// </summary>
    /// <param name="foundVersion">The schema version the database holds.</param>
    public SchemaTooOldException(int foundVersion)
        : base(Describe(foundVersion, null))
    {
        FoundVersion = foundVersion;
    }

    /// <summary>
    /// Says why a database of this schema version is held and what to install, in a language.
    /// </summary>
    /// <param name="foundVersion">The schema version the database holds.</param>
    /// <param name="language">The language, or <c>null</c> for US English, as the log reads it.</param>
    /// <returns>The reason.</returns>
    public static string Describe(int foundVersion, string? language)
        => BaseTranslations.Format(TranslationKeys.ErrorSchemaTooOld, language, foundVersion, SyncSchema.OldestUpgradableRelease, SyncSchema.CurrentSchemaVersion);

    /// <summary>Gets the schema version the database holds.</summary>
    public int FoundVersion { get; }
}

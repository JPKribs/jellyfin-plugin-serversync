using System;
using System.Globalization;
using Jellyfin.Plugin.ServerSync.Services.Database;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Services.Database;

/// <summary>
/// The exception must carry the version it found and tell the operator which release to install first.
/// </summary>
public sealed class SchemaTooOldExceptionTests
{
    /// <summary>
    /// The found version is kept, at either end of the range.
    /// True: the hold message and the log name the version the file really holds.
    /// False: the operator would be told the wrong version.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(int.MaxValue)]
    public void Constructor_FoundVersion_IsKept(int found)
    {
        var exception = new SchemaTooOldException(found);

        Assert.Equal(found, exception.FoundVersion);
    }

    /// <summary>
    /// The message names the found version, the version this release reads, the release to install first, and that nothing was changed.
    /// True: the operator can fix it from the message alone.
    /// False: the operator would see a bare failure and might delete the database.
    /// </summary>
    [Fact]
    public void Message_NamesBothVersionsAndTheReleaseToInstall()
    {
        var exception = new SchemaTooOldException(20);

        Assert.Contains("schema v20,", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reads v" + SyncSchema.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture) + ".", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Install Server Sync " + SyncSchema.OldestUpgradableRelease, exception.Message, StringComparison.Ordinal);
        Assert.Contains("left untouched", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exception is an invalid operation.
    /// True: callers that catch a failed database open catch this one too.
    /// False: it would slip past them and stop Jellyfin's start.
    /// </summary>
    [Fact]
    public void Type_IsAnInvalidOperation()
    {
        Assert.IsAssignableFrom<InvalidOperationException>(new SchemaTooOldException(1));
    }
}
